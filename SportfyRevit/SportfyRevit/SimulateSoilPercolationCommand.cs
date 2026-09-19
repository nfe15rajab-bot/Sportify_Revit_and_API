using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    /// <summary>
    /// What rain does inside the roof garden: how much of a storm each build-up keeps, how much runs off and how much
    /// later, and whether any build-up fills up. Replaces the old Water Management command's one-line retention estimate
    /// (a rule of thumb: 30% plus 2% per cm, retired so the report has one answer) with a model that steps rain through the real layers of each build-up.
    /// It is a screening model, not a hydrological design: the rain events are generic, and every constant that is a
    /// judgement call is listed among the assumptions in the results.
    ///
    /// The numbers come from PercolationCore.cs, compiled into this add-in from the Unity project's sources, so they
    /// need no Unity and appear at once. Unity only adds the video (rain falling on each build-up in section, water
    /// soaking through its layers, the roof's outflow curve) and is offered afterwards, when the Editor is installed.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateSoilPercolationCommand : IExternalCommand
    {
        const string DialogTitle = "Sportify — Rain & Soil Percolation";
        const string BundledSampleFile = "sample_layout_roofgarden.json";
        const int VideoTimeoutMs = 300_000;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var layoutJson, out _);
            var usingBundledSample = false;

            var haveUnity = UnityHeadlessRunner.TryLocate(out var unity, out _);

            if (layoutJson == null)
            {
                if (haveUnity) layoutJson = UnityHeadlessRunner.ReadBundledSample(unity!, BundledSampleFile);
                usingBundledSample = layoutJson != null;
                if (layoutJson == null)
                {
                    message = "No layout to analyse yet. Import or push a layout from the Sportify web app first (Combine tab).";
                    return Result.Failed;
                }
            }

            WaterInputs inputs;
            try
            {
                var layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson)
                             ?? throw new InvalidOperationException("The layout was empty.");
                var zones = WindLayoutAdapter.ToInputs(layout);
                inputs = new WaterInputs { RoofLength = zones.RoofLength, RoofWidth = zones.RoofWidth, Zones = zones.Zones };
            }
            catch (Exception ex)
            {
                message = $"Couldn't read the layout: {ex.Message}";
                return Result.Failed;
            }

            if (inputs.Zones.Count == 0)
            {
                TaskDialog.Show(DialogTitle, "The layout has no green-roof zones to analyse. Draw a green roof zone in the Combine tab first.");
                return Result.Cancelled;
            }

            var report = PercolationModel.Analyse(inputs);
            var caseStudy = CaseStudy(inputs);
            AnalysisResultPublisher.PublishSoilPercolation(BuildPublishedResult(report, caseStudy, null));

            var unityFree = haveUnity && !UnityHeadlessRunner.IsProjectOpenInUnity(unity!.ProjectDir);
            if (!ShowSummary(report, caseStudy, usingBundledSample, haveUnity, unityFree))
                return Result.Succeeded;

            RenderVideo(unity!, layoutJson, report, caseStudy);
            return Result.Succeeded;
        }

        internal static string CaseStudy(WaterInputs inputs)
        {
            return $"{inputs.Zones.Count} green-roof zone{(inputs.Zones.Count == 1 ? "" : "s")} on a {inputs.RoofLength:0.#} x {inputs.RoofWidth:0.#} m roof";
        }

        // ------------------------------------------------------------------ dialogs

        static bool ShowSummary(PercolationReport report, string caseStudy, bool usingBundledSample, bool haveUnity, bool unityFree)
        {
            var s = report.summary;
            var body = new StringBuilder();
            if (usingBundledSample)
                body.AppendLine("No layout imported into this Revit session yet — analysed Unity's bundled roof-garden sample instead.").AppendLine();
            body.AppendLine(caseStudy);
            body.AppendLine($"{s.greenAreaM2:0} m² of build-ups, {s.otherAreaM2:0} m² of other roof (treated as bare).");
            body.AppendLine();
            body.AppendLine("Share of the rain kept, against the same rain on a bare roof (steady 10 mm/h, shower 40 mm/h, cloudburst 108 mm/h):");
            foreach (var z in report.zones)
            {
                body.AppendLine(z.substrateMm <= 0
                    ? $"  • {z.label} ({z.system}): no substrate, keeps nothing"
                    : $"  • {z.label} ({z.system}, {z.substrateMm:0} mm): {z.scenarios[0].retainedPercent:0}% / {z.scenarios[1].retainedPercent:0}% / {z.scenarios[2].retainedPercent:0}%" +
                      (z.scenarios[2].saturated ? "  — fills up in the cloudburst" : ""));
            }

            var roof = report.roof;
            body.AppendLine();
            body.AppendLine($"Whole roof: {roof[0].retainedPercent:0}% / {roof[1].retainedPercent:0}% / {roof[2].retainedPercent:0}% kept.");
            body.AppendLine($"Cloudburst peak {roof[2].peakFlowLps:0} l/s, against {roof[2].referencePeakLps:0} l/s with no green layers ({roof[2].peakReductionPercent:0}% lower).");

            var advice = report.recommendations.Where(r => r.kind == "more-storage").Take(3).ToList();
            if (advice.Count > 0)
            {
                body.AppendLine();
                foreach (var r in advice) body.AppendLine("  • " + r.text);
            }

            body.AppendLine();
            body.AppendLine("A screening estimate with generic rain events (not the site's design rainfall), not a hydrological design. The assumptions are in the PDF report.");

            if (!haveUnity)
                body.AppendLine().AppendLine("3D video: needs the Unity Editor, which wasn't found. The numbers above don't.");
            else if (!unityFree)
                body.AppendLine().AppendLine("3D video: close the Unity Editor (it has Sportify.Simulation open) to render it.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = s.zonesBelowTarget == 0
                    ? "Every build-up keeps most of a heavy shower"
                    : $"{s.zonesBelowTarget} of {s.zonesChecked} build-up(s) keep under {PercolationModel.TargetRetentionPercent:0}% of a heavy shower",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };

            if (unityFree)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Render the 3D video with Unity",
                    "About a minute; Revit is unresponsive while it renders.");

            return dialog.Show() == TaskDialogResult.CommandLink1;
        }

        static void RenderVideo(UnityHeadlessRunner.UnityInstall unity, string layoutJson, PercolationReport report, string caseStudy)
        {
            var run = UnityHeadlessRunner.Run(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunPercolationAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "percolation_results.json",
                VideoFileStem = "rain_percolation",
                LogFileName = "unity_percolation_batch.log",
                TimeoutMs = VideoTimeoutMs,
            });

            if (!run.Ok)
            {
                TaskDialog.Show(DialogTitle, "The numbers above stand, but the video couldn't be rendered.\n\n" + run.Message);
                return;
            }

            PercolationResultsFile? results;
            try
            {
                results = JsonSerializer.Deserialize<PercolationResultsFile>(run.ResultsJson!, new JsonSerializerOptions { IncludeFields = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(DialogTitle, $"The video was rendered, but Unity's results file couldn't be read: {ex.Message}");
                return;
            }

            if (results == null || !string.IsNullOrEmpty(results.Error))
            {
                TaskDialog.Show(DialogTitle, $"Unity's percolation analysis failed: {results?.Error ?? "empty results file"}");
                return;
            }

            var video = results.Video;
            var videoPath = video != null && !string.IsNullOrEmpty(video.FilePath) && File.Exists(video.FilePath) ? video.FilePath : null;
            var disagreement = results.Analysis != null ? Compare(report, results.Analysis) : "Unity's results carried no analysis to compare.";

            AnalysisResultPublisher.PublishSoilPercolation(BuildPublishedResult(report, caseStudy, videoPath));

            var body = new StringBuilder();
            body.AppendLine(videoPath != null
                ? $"Video: {Path.GetFileName(videoPath)} ({video!.DurationS:0.#} s, {video.Width}×{video.Height})"
                : "Video: not recorded" + (string.IsNullOrEmpty(results.VideoError) ? "." : $" — {results.VideoError}"));
            if (disagreement != null)
                body.AppendLine().AppendLine("Note: " + disagreement + " The add-in's numbers are the ones published.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = videoPath != null ? "The 3D video is ready" : "No video was recorded",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };
            if (videoPath != null)
            {
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Play the video", videoPath);
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Show the video in its folder");
            }

            var choice = dialog.Show();
            if (videoPath == null) return;
            try
            {
                if (choice == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
                else if (choice == TaskDialogResult.CommandLink2) Process.Start("explorer.exe", $"/select,\"{videoPath}\"");
            }
            catch (Exception)
            {
                // Best-effort — the video is on disk either way.
            }
        }

        // ------------------------------------------------------------------ result plumbing

        internal static SoilPercolationResultDto BuildPublishedResult(PercolationReport report, string caseStudy, string? videoPath)
        {
            var s = report.summary;
            var roof = report.roof;
            return new SoilPercolationResultDto
            {
                CaseStudy = caseStudy,
                VideoPath = videoPath,
                ZonesChecked = s.zonesChecked,
                GreenAreaM2 = Math.Round(s.greenAreaM2, 1),
                SteadyRetainedPercent = Math.Round(roof[0].retainedPercent, 1),
                HeavyShowerRetainedPercent = Math.Round(roof[1].retainedPercent, 1),
                HeavyShowerPeakReductionPercent = Math.Round(roof[1].peakReductionPercent, 1),
                CloudburstRetainedPercent = Math.Round(roof[2].retainedPercent, 1),
                CloudburstPeakReductionPercent = Math.Round(roof[2].peakReductionPercent, 1),
                CloudburstPeakLps = Math.Round(roof[2].peakFlowLps, 1),
                CloudburstReferencePeakLps = Math.Round(roof[2].referencePeakLps, 1),
                ZonesBelowTarget = s.zonesBelowTarget,
                ZonesSaturatedInCloudburst = s.zonesSaturatedInCloudburst,
                Zones = report.zones.Select(z => new SoilPercolationZoneDto
                {
                    Label = z.label,
                    System = z.system,
                    SubstrateMm = Math.Round(z.substrateMm, 0),
                    RetainedSteadyPercent = Math.Round(z.scenarios[0].retainedPercent, 1),
                    RetainedHeavyShowerPercent = Math.Round(z.scenarios[1].retainedPercent, 1),
                    RetainedCloudburstPercent = Math.Round(z.scenarios[2].retainedPercent, 1),
                }).ToList(),
                Findings = report.recommendations.Select(r => new WindFindingDto { Kind = r.kind, Target = r.target, Text = r.text }).ToList(),
                Assumptions = new List<string>(report.assumptions),
            };
        }

        /// <summary>Unity runs the same core on its own reading of the layout: the two must agree. Returns a description of a disagreement, or null.</summary>
        internal static string? Compare(PercolationReport addin, PercolationReport unity)
        {
            var problems = new List<string>();
            if (addin.roof.Count != unity.roof.Count || addin.zones.Count != unity.zones.Count)
                return "Unity's run has a different number of zones or events than the add-in's.";
            for (var k = 0; k < addin.roof.Count; k++)
            {
                if (Math.Abs(addin.roof[k].retainedPercent - unity.roof[k].retainedPercent) > 0.05f)
                    problems.Add($"{addin.roof[k].scenario} retained {addin.roof[k].retainedPercent:0.#}% vs {unity.roof[k].retainedPercent:0.#}%");
                if (Math.Abs(addin.roof[k].peakFlowLps - unity.roof[k].peakFlowLps) > 0.05f)
                    problems.Add($"{addin.roof[k].scenario} peak {addin.roof[k].peakFlowLps:0.#} vs {unity.roof[k].peakFlowLps:0.#} l/s");
            }
            if (addin.summary.zonesBelowTarget != unity.summary.zonesBelowTarget)
                problems.Add($"zones below the aim {addin.summary.zonesBelowTarget} vs {unity.summary.zonesBelowTarget}");
            return problems.Count == 0 ? null : "Unity's run differs from the add-in's (" + string.Join(", ", problems) + ").";
        }

        // ---- what Sportify.Simulation writes to Recordings/percolation_results.json (camelCase, Unity JsonUtility) ----

        internal class PercolationVideoInfo
        {
            [JsonPropertyName("path")] public string? FilePath { get; set; }
            [JsonPropertyName("durationS")] public float DurationS { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
        }

        internal class PercolationResultsFile
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("message")] public string? Message { get; set; }
            [JsonPropertyName("analysis")] public PercolationReport? Analysis { get; set; }
            [JsonPropertyName("video")] public PercolationVideoInfo? Video { get; set; }
            [JsonPropertyName("videoError")] public string? VideoError { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
