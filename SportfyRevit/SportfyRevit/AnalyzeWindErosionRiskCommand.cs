using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    /// <summary>
    /// What a storm does to the roof garden: whether trees would be blown over, whether a build-up would
    /// lift off the roof, and whether growing medium would blow away. Maps to the two wind concerns of the
    /// FLL green-roof guideline (uplift, erosion) using the concepts of EN 1991-1-4 (roof zones F/G/H/I,
    /// peak velocity pressure). It is a screening model, not a structural design: every constant that is a
    /// judgement call is listed among the assumptions in the results.
    ///
    /// The numbers come from WindAnalysisCore.cs, compiled into this add-in from the Unity project's
    /// sources, so they need no Unity and appear at once. Unity only adds the video (a 3D film of the wind
    /// crossing the roof, the lifting build-ups, the blown substrate and the fixes) and is offered as an
    /// extra: it needs the Unity Editor installed, which is exactly what a user without it must not be forced into.
    /// When Unity does run, its numbers are compared with these (they come from the same source, through a
    /// second reader of the layout), so a disagreement between the two readers cannot go unnoticed.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeWindErosionRiskCommand : IExternalCommand
    {
        const string DialogTitle = "Sportify — Wind & Erosion Analysis";
        const string BundledSampleFile = "sample_layout_roofgarden.json";

        // Unity start-up, the 52 s 1080p render and the H.264 encode: about a minute on a warm project.
        const int VideoTimeoutMs = 300_000;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var layoutJson, out _);
            var usingBundledSample = false;

            var haveUnity = UnityHeadlessRunner.TryLocate(out var unity, out _);

            if (layoutJson == null)
            {
                // Nothing imported into this Revit session yet: fall back to the Unity project's own sample,
                // so the command always shows what it does rather than ending in a dead end.
                if (haveUnity) layoutJson = UnityHeadlessRunner.ReadBundledSample(unity!, BundledSampleFile);
                usingBundledSample = layoutJson != null;
                if (layoutJson == null)
                {
                    message = "No layout to analyse yet. Import or push a layout from the Sportify web app first (Combine tab).";
                    return Result.Failed;
                }
            }

            WindInputs inputs;
            try
            {
                var layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson)
                             ?? throw new InvalidOperationException("The layout was empty.");
                inputs = WindLayoutAdapter.ToInputs(layout);
            }
            catch (Exception ex)
            {
                message = $"Couldn't read the layout: {ex.Message}";
                return Result.Failed;
            }

            if (inputs.Zones.Count == 0 && inputs.Plants.Count == 0)
            {
                TaskDialog.Show(DialogTitle,
                    "The layout has no green-roof zones or plants to analyse. Draw a green roof zone (and place some plants) in the Combine tab first.");
                return Result.Cancelled;
            }

            var report = WindModel.Analyse(inputs);
            var caseStudy = WindModel.CaseStudy(inputs);

            // Merged into the session's analysis payload like every other Analyze* command, so the
            // results published earlier survive for the PDF report.
            AnalysisResultPublisher.PublishWindErosion(BuildPublishedResult(report, caseStudy, null));

            var unityFree = haveUnity && !UnityHeadlessRunner.IsProjectOpenInUnity(unity!.ProjectDir);
            if (!ShowSummary(report, caseStudy, usingBundledSample, haveUnity, unityFree))
                return Result.Succeeded;

            RenderVideo(unity!, layoutJson, report, caseStudy);
            return Result.Succeeded;
        }

        // ------------------------------------------------------------------ dialogs

        /// <summary>Shows the numbers. Returns true if the user asked for the Unity video.</summary>
        static bool ShowSummary(WindReport report, string caseStudy, bool usingBundledSample, bool haveUnity, bool unityFree)
        {
            var s = report.summary;
            var site = report.site;
            var problems = s.plantsFailing + s.zonesUpliftFlagged;

            var body = new StringBuilder();
            if (usingBundledSample)
                body.AppendLine("No layout imported into this Revit session yet — analysed Unity's bundled roof-garden sample instead.").AppendLine();
            body.AppendLine(caseStudy);
            body.AppendLine($"Design wind: zone {site.windZone} ({site.basicWindSpeedMs:0.#} m/s) — {site.windZoneSource}.");
            body.AppendLine($"Terrain {site.terrainCategory}; roof {site.roofElevationM:0.#} m above ground — {site.roofHeightSource}; peak pressure {site.peakPressureAtRoofPa:0} Pa.");
            if (site.hasNorth && site.prevailingDirectionIndex >= 0)
            {
                var prevailing = report.directions[site.prevailingDirectionIndex];
                body.AppendLine($"Prevailing wind ({prevailing.compassLabel}, generic for Germany): {prevailing.plantsFailing} tree(s) at risk, {prevailing.zonesUpliftFlagged} zone(s) lift.");
            }
            body.AppendLine();

            if (s.plantsChecked > 0)
            {
                body.AppendLine($"Trees: {s.plantsFailing} of {s.plantsChecked} would be blown over, {s.plantsMarginal} marginal.");
                foreach (var p in report.plants.Where(p => p.status == "fails").OrderByDescending(p => p.utilisation).Take(4))
                    body.AppendLine($"  • {p.species} at ({p.xM:0.#}, {p.yM:0.#}): {p.utilisation:0.0}× its limit ({p.worstDirectionLabel})");
            }
            else
            {
                body.AppendLine("Trees: none in the layout.");
            }

            body.AppendLine($"Uplift: {s.zonesUpliftFlagged} of {s.zonesChecked} zones need ballast ({s.percentPlantedAreaUpliftFlagged:0.#}% of the planted area).");
            foreach (var z in report.zones.Where(z => z.upliftStatus == "fails").Take(4))
                body.AppendLine($"  • {z.label} ({z.system}): +{z.requiredBallastMm:0} mm gravel in the {z.flaggedDepthM:0.#} m band along the {z.worstEdge} edge");

            var closed = report.zones.Where(z => z.erosionRisk != "n/a").Select(z => z.erosionOnsetEstablishedMs).DefaultIfEmpty(0f).Min();
            body.AppendLine($"Erosion: bare substrate starts to move at {s.lowestBareOnsetMs:0.#} m/s; closed planting holds to {closed:0.#} m/s.");

            body.AppendLine();
            body.AppendLine("A screening estimate after EN 1991-1-4 and the FLL guideline, not a structural design. The assumptions are in the PDF report.");

            if (!haveUnity)
                body.AppendLine().AppendLine("3D video: needs the Unity Editor, which wasn't found. The numbers above don't.");
            else if (!unityFree)
                body.AppendLine().AppendLine("3D video: close the Unity Editor (it has Sportify.Simulation open) to render it.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = problems == 0 ? "No wind problems found" : $"{problems} wind problem(s) found",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };

            if (unityFree)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Render the 3D video with Unity",
                    "About a minute; Revit is unresponsive while it renders.");

            return dialog.Show() == TaskDialogResult.CommandLink1;
        }

        static void RenderVideo(UnityHeadlessRunner.UnityInstall unity, string layoutJson, WindReport report, string caseStudy)
        {
            var run = UnityHeadlessRunner.Run(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunWindAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "wind_results.json",
                VideoFileStem = "wind_erosion",
                LogFileName = "unity_wind_batch.log",
                TimeoutMs = VideoTimeoutMs,
            });

            if (!run.Ok)
            {
                TaskDialog.Show(DialogTitle, "The numbers above stand, but the video couldn't be rendered.\n\n" + run.Message);
                return;
            }

            WindResultsFile? results;
            try
            {
                results = JsonSerializer.Deserialize<WindResultsFile>(run.ResultsJson!, new JsonSerializerOptions { IncludeFields = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(DialogTitle, $"The video was rendered, but Unity's results file couldn't be read: {ex.Message}");
                return;
            }

            if (results == null || !string.IsNullOrEmpty(results.Error))
            {
                TaskDialog.Show(DialogTitle, $"Unity's wind analysis failed: {results?.Error ?? "empty results file"}");
                return;
            }

            var video = results.Video;
            var videoPath = video != null && !string.IsNullOrEmpty(video.FilePath) && File.Exists(video.FilePath)
                ? video.FilePath
                : null;

            var disagreement = results.Analysis != null ? Compare(report, results.Analysis) : "Unity's results carried no analysis to compare.";

            AnalysisResultPublisher.PublishWindErosion(BuildPublishedResult(report, caseStudy, videoPath));

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
                if (choice == TaskDialogResult.CommandLink1)
                    Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
                else if (choice == TaskDialogResult.CommandLink2)
                    Process.Start("explorer.exe", $"/select,\"{videoPath}\"");
            }
            catch (Exception)
            {
                // Best-effort — the video is on disk either way.
            }
        }

        // ------------------------------------------------------------------ result plumbing

        internal static WindErosionResultDto BuildPublishedResult(WindReport report, string caseStudy, string? videoPath)
        {
            var s = report.summary;
            var site = report.site;
            return new WindErosionResultDto
            {
                CaseStudy = caseStudy,
                VideoPath = videoPath,
                WindZone = site.windZone,
                WindZoneSource = site.windZoneSource,
                RoofHeightSource = site.roofHeightSource,
                NorthDeg = site.hasNorth ? Math.Round(site.northDeg, 1) : null,
                BasicWindSpeedMs = Math.Round(site.basicWindSpeedMs, 1),
                TerrainCategory = site.terrainCategory,
                RoofHeightM = Math.Round(site.roofElevationM, 1),
                RoofHeightAssumed = site.roofElevationAssumed,
                PeakPressurePa = Math.Round(site.peakPressureAtRoofPa),
                TreesChecked = s.plantsChecked,
                TreesFailing = s.plantsFailing,
                TreesMarginal = s.plantsMarginal,
                ZonesChecked = s.zonesChecked,
                ZonesUpliftFlagged = s.zonesUpliftFlagged,
                PercentPlantedAreaUpliftFlagged = Math.Round(s.percentPlantedAreaUpliftFlagged, 1),
                PercentPlantedAreaErosionFlagged = Math.Round(s.percentPlantedAreaErosionFlagged, 1),
                LowestBareOnsetMs = Math.Round(s.lowestBareOnsetMs, 1),
                Findings = report.recommendations
                    .Select(r => new WindFindingDto { Kind = r.kind, Target = r.target, Edge = r.edge, Text = r.text })
                    .ToList(),
                Assumptions = new List<string>(report.assumptions),
            };
        }

        /// <summary>
        /// Unity reads the same layout through its own reader and runs the same core: the two must agree.
        /// Returns a sentence describing a disagreement, or null.
        /// </summary>
        internal static string? Compare(WindReport addin, WindReport unity)
        {
            var problems = new List<string>();
            if (Math.Abs(addin.site.peakPressureAtRoofPa - unity.site.peakPressureAtRoofPa) > 0.5f)
                problems.Add($"peak pressure {addin.site.peakPressureAtRoofPa:0} vs {unity.site.peakPressureAtRoofPa:0} Pa");
            if (addin.summary.plantsFailing != unity.summary.plantsFailing)
                problems.Add($"failing trees {addin.summary.plantsFailing} vs {unity.summary.plantsFailing}");
            if (addin.summary.zonesUpliftFlagged != unity.summary.zonesUpliftFlagged)
                problems.Add($"lifting zones {addin.summary.zonesUpliftFlagged} vs {unity.summary.zonesUpliftFlagged}");
            if (Math.Abs(addin.summary.percentPlantedAreaUpliftFlagged - unity.summary.percentPlantedAreaUpliftFlagged) > 0.05f)
                problems.Add($"uplift area {addin.summary.percentPlantedAreaUpliftFlagged:0.#}% vs {unity.summary.percentPlantedAreaUpliftFlagged:0.#}%");

            return problems.Count == 0
                ? null
                : "Unity's run differs from the add-in's (" + string.Join(", ", problems) + ").";
        }

        // ---- what Sportify.Simulation writes to Recordings/wind_results.json (camelCase, Unity JsonUtility) ----

        internal class WindVideoInfo
        {
            [JsonPropertyName("path")] public string? FilePath { get; set; }
            [JsonPropertyName("durationS")] public float DurationS { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
        }

        internal class WindResultsFile
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("message")] public string? Message { get; set; }
            [JsonPropertyName("analysis")] public WindReport? Analysis { get; set; }
            [JsonPropertyName("video")] public WindVideoInfo? Video { get; set; }
            [JsonPropertyName("videoError")] public string? VideoError { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
