using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sportify.Simulation.Dynamics;

namespace SportfyRevit
{
    /// <summary>
    /// The dynamic half of the structural analysis, in three scenarios that follow one another: how a day's crowd moves over the roof,
    /// what weather (a cloudburst, snow, wind) does to the load on the structure, and whether the deck resonates with rhythmic crowd
    /// movement. It builds on the static Structural Loads analysis (same pieces, same grid, same capacity).
    ///
    /// The numbers come from DynamicLoadCore.cs, compiled into this add-in from the Unity project's sources, so they need no Unity
    /// and appear at once; Unity only adds the video, offered afterwards when the Editor is installed. A screening model, not a
    /// structural verification or a vibration design: the deck's natural frequency is estimated from the spans unless the engineer's
    /// figure is entered, the snow zone is assumed unless set, and the day's schedule is a generic one; the results say so.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeDynamicLoadsCommand : IExternalCommand
    {
        const string DialogTitle = "Sportify — Dynamic Analysis";
        const string BundledSampleFile = "sample_layout_roofgarden.json";
        const int VideoTimeoutMs = 600_000;

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

            DynamicInputs inputs;
            try
            {
                var layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson)
                             ?? throw new InvalidOperationException("The layout was empty.");
                inputs = DynamicLayoutAdapter.ToInputs(layout);
            }
            catch (Exception ex)
            {
                message = $"Couldn't read the layout: {ex.Message}";
                return Result.Failed;
            }

            if (!inputs.Structure.Items.Any(i => i.Persons > 0))
            {
                TaskDialog.Show(DialogTitle, "The layout has nowhere for people to be yet (no sports field, activity or accessible garden). Place something in the Combine tab first.");
                return Result.Cancelled;
            }

            var report = DynamicModel.Analyse(inputs);
            var caseStudy = DynamicModel.CaseStudy(inputs, report);
            AnalysisResultPublisher.PublishDynamicAnalysis(BuildPublishedResult(report, caseStudy, null));

            var unityFree = haveUnity && !UnityHeadlessRunner.IsProjectOpenInUnity(unity!.ProjectDir);
            if (!ShowSummary(report, caseStudy, usingBundledSample, haveUnity, unityFree))
                return Result.Succeeded;

            RenderVideo(unity!, layoutJson, report, caseStudy);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ dialogs

        static bool ShowSummary(DynamicReport report, string caseStudy, bool usingBundledSample, bool haveUnity, bool unityFree)
        {
            var c = report.crowd;
            var w = report.weather;
            var r = report.resonance;
            var s = report.summary;
            var body = new StringBuilder();
            if (usingBundledSample)
                body.AppendLine("No layout imported into this Revit session yet — analysed Unity's bundled roof-garden sample instead.").AppendLine();
            body.AppendLine(caseStudy);
            body.AppendLine();

            body.AppendLine($"1  CROWDS ({c.scheduleName}): busiest at {DynamicModel.Clock(c.peakAtHour)} with about {c.peakPersons:0} people ({c.peakCrowdKn:0} kN), {c.arrivals} arrivals over the day; static analysis assumed {c.staticExpectedPersons:0} at once.");
            body.AppendLine($"    Most crowded: {c.busiestBay}, {c.busiestBayPeakDensity:0.00} people/m². The crowd moves the load's centre by at most {c.maxShiftPercent:0.##}%.");
            body.AppendLine();

            body.AppendLine($"2  WEATHER: snow zone {w.snow.zone}{(w.snow.zoneAssumed ? " (ASSUMED)" : "")}, sk {w.snow.skKnM2:0.00} kN/m²; a {w.rain.intensityMmH:0} mm/h cloudburst adds up to {w.rain.peakAddedKn:0} kN to the build-ups.");
            foreach (var lc in w.cases)
                body.AppendLine($"    • {lc.name}: busiest bay {lc.worstBay} at {lc.peakUtilisation * 100:0}% ({lc.baysOver} over capacity)");
            body.AppendLine();

            body.AppendLine($"3  RESONANCE: deck frequency {r.lowestFrequencyHz:0.0} to {r.highestFrequencyHz:0.0} Hz ({(r.estimated ? "ESTIMATED from the spans" : "the engineer's figure")}).");
            body.AppendLine($"    Worst: {r.worstBay}, {(r.worstActivity ?? "").ToLowerInvariant()}: {r.worstAccelerationG:0.000} g against a comfort limit of {r.worstLimitG:0.00} g. {r.baysExceeding} of {r.bays.Count} bays exceed under some activity.");

            var advice = report.recommendations.Where(x => x.kind is "over-capacity" or "resonance" or "snow-input" or "frequency-input" or "capacity-input").Take(4).ToList();
            if (advice.Count > 0)
            {
                body.AppendLine();
                foreach (var a in advice) body.AppendLine("  • " + a.text);
            }

            body.AppendLine();
            body.AppendLine("A screening estimate, not a structural verification or a vibration design. The assumptions are in the PDF report.");

            if (!haveUnity)
                body.AppendLine().AppendLine("3D video: needs the Unity Editor, which wasn't found. The numbers above don't.");
            else if (!unityFree)
                body.AppendLine().AppendLine("3D video: close the Unity Editor (it has Sportify.Simulation open) to render it.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = r.worstRatio > 1.0 && r.estimated ? $"Resonance is likely: {r.baysExceeding} bay(s) exceed the comfort limit"
                                : s.baysOverCapacity > 0 ? $"{s.baysOverCapacity} bay(s) exceed the deck capacity in some weather"
                                : "No bay exceeds the deck capacity in any weather",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };

            if (unityFree)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Render the 3D video with Unity",
                    "About two minutes; Revit is unresponsive while it renders.");

            return dialog.Show() == TaskDialogResult.CommandLink1;
        }

        static void RenderVideo(UnityHeadlessRunner.UnityInstall unity, string layoutJson, DynamicReport report, string caseStudy)
        {
            var run = UnityHeadlessRunner.Run(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunDynamicAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "dynamic_results.json",
                VideoFileStem = "dynamic_analysis",
                LogFileName = "unity_dynamic_batch.log",
                TimeoutMs = VideoTimeoutMs,
            });

            if (!run.Ok)
            {
                TaskDialog.Show(DialogTitle, "The numbers above stand, but the video couldn't be rendered.\n\n" + run.Message);
                return;
            }

            DynamicResultsFile? results;
            try
            {
                results = JsonSerializer.Deserialize<DynamicResultsFile>(run.ResultsJson!, new JsonSerializerOptions { IncludeFields = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(DialogTitle, $"The video was rendered, but Unity's results file couldn't be read: {ex.Message}");
                return;
            }

            if (results == null || !string.IsNullOrEmpty(results.Error))
            {
                TaskDialog.Show(DialogTitle, $"Unity's dynamic analysis failed: {results?.Error ?? "empty results file"}");
                return;
            }

            var video = results.Video;
            var videoPath = video != null && !string.IsNullOrEmpty(video.FilePath) && File.Exists(video.FilePath) ? video.FilePath : null;
            var disagreement = results.Analysis != null ? Compare(report, results.Analysis) : "Unity's results carried no analysis to compare.";

            AnalysisResultPublisher.PublishDynamicAnalysis(BuildPublishedResult(report, caseStudy, videoPath));

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

        internal static DynamicAnalysisResultDto BuildPublishedResult(DynamicReport report, string caseStudy, string? videoPath)
        {
            var c = report.crowd;
            var w = report.weather;
            var r = report.resonance;
            var s = report.summary;
            return new DynamicAnalysisResultDto
            {
                CaseStudy = caseStudy,
                VideoPath = videoPath,
                Schedule = c.scheduleName,
                PeakPersons = c.peakPersons,
                PeakAtHour = Math.Round(c.peakAtHour, 2),
                PeakCrowdKn = Math.Round(c.peakCrowdKn, 1),
                CrowdShareOfLoadPercent = Math.Round(s.crowdSharePercent, 2),
                MaxLoadCentreShiftPercent = Math.Round(c.maxShiftPercent, 3),
                BusiestBay = c.busiestBay,
                BusiestBayPeakDensity = Math.Round(c.busiestBayPeakDensity, 3),
                SnowZone = w.snow.zone,
                SnowAssumed = w.snow.zoneAssumed || w.snow.altitudeAssumed,
                SnowSkKnM2 = Math.Round(w.snow.skKnM2, 3),
                RainPeakAddedKn = Math.Round(w.rain.peakAddedKn, 1),
                RainSaturatedKn = Math.Round(w.rain.saturatedKn, 1),
                WorstCase = w.worstCase,
                WorstCaseBay = w.worstBay,
                WorstCaseUtilisationPercent = Math.Round(w.peakUtilisation * 100.0, 1),
                DeckCapacityKnM2 = Math.Round(s.capacityKnM2, 2),
                Cases = w.cases.Select(x => new DynamicCaseDto
                {
                    Name = x.name, TotalKn = Math.Round(x.totalKn, 1), PeakUtilisationPercent = Math.Round(x.peakUtilisation * 100.0, 1), WorstBay = x.worstBay, BaysOverCapacity = x.baysOver,
                }).ToList(),
                FrequencyEstimated = r.estimated,
                LowestFrequencyHz = Math.Round(r.lowestFrequencyHz, 2),
                HighestFrequencyHz = Math.Round(r.highestFrequencyHz, 2),
                WorstResonanceBay = r.worstBay,
                WorstResonanceActivity = r.worstActivity,
                WorstAccelerationG = Math.Round(r.worstAccelerationG, 4),
                WorstLimitG = Math.Round(r.worstLimitG, 3),
                BaysExceedingComfort = r.baysExceeding,
                BaysChecked = r.bays.Count,
                Findings = report.recommendations.Select(x => new WindFindingDto { Kind = x.kind, Target = x.target, Text = x.text }).ToList(),
                Assumptions = new List<string>(report.assumptions),
            };
        }

        /// <summary>Unity runs the same core on its own reading of the layout: the two must agree. Returns a description of a disagreement, or null.</summary>
        internal static string? Compare(DynamicReport addin, DynamicReport unity)
        {
            if (addin.resonance.bays.Count != unity.resonance.bays.Count || addin.weather.cases.Count != unity.weather.cases.Count)
                return "Unity's run has a different number of bays or load cases than the add-in's.";

            var problems = new List<string>();
            if (Math.Abs(addin.crowd.peakPersons - unity.crowd.peakPersons) > 1.5f) problems.Add($"peak crowd {addin.crowd.peakPersons:0} vs {unity.crowd.peakPersons:0}");
            for (var i = 0; i < addin.weather.cases.Count; i++)
                if (Math.Abs(addin.weather.cases[i].peakUtilisation - unity.weather.cases[i].peakUtilisation) > 0.005f)
                    problems.Add($"{addin.weather.cases[i].name} {addin.weather.cases[i].peakUtilisation * 100:0.#}% vs {unity.weather.cases[i].peakUtilisation * 100:0.#}%");
            if (Math.Abs(addin.resonance.worstAccelerationG - unity.resonance.worstAccelerationG) > 0.002f + 0.01f * addin.resonance.worstAccelerationG)
                problems.Add($"worst acceleration {addin.resonance.worstAccelerationG:0.000} vs {unity.resonance.worstAccelerationG:0.000} g");
            if (addin.resonance.baysExceeding != unity.resonance.baysExceeding)
                problems.Add($"bays exceeding {addin.resonance.baysExceeding} vs {unity.resonance.baysExceeding}");
            return problems.Count == 0 ? null : "Unity's run differs from the add-in's (" + string.Join(", ", problems) + ").";
        }

        // ---- what Sportify.Simulation writes to Recordings/dynamic_results.json (camelCase, Unity JsonUtility) ----

        internal class DynamicVideoInfo
        {
            [JsonPropertyName("path")] public string? FilePath { get; set; }
            [JsonPropertyName("durationS")] public float DurationS { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
        }

        internal class DynamicResultsFile
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("message")] public string? Message { get; set; }
            [JsonPropertyName("analysis")] public DynamicReport? Analysis { get; set; }
            [JsonPropertyName("video")] public DynamicVideoInfo? Video { get; set; }
            [JsonPropertyName("videoError")] public string? VideoError { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
