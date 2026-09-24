using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sportify.Simulation.Structure;

namespace SportfyRevit
{
    /// <summary>
    /// The static half of the structural analysis: where the weight and the people will be on the roof, which bays of the roof's
    /// structural grid are most loaded against the deck's capacity, and whether the load sits to one side, with the move or lightening
    /// that would balance it. (Crowds moving through the day, and water, wind and snow acting on the roof, are the dynamic half:
    /// AnalyzeDynamicLoadsCommand.)
    ///
    /// The grid and columns come from the Revit model with the roof push (PushRoofBoundaryCommand) and travel with the layout. The
    /// numbers come from StructuralLoadCore.cs, compiled into this add-in from the Unity project's sources, so they need no Unity and
    /// appear at once; Unity only adds the video, offered afterwards when the Editor is installed. A screening model, not a structural
    /// verification: the deck's capacity is an input (the layout carries the engineer's figure when it has been entered, else a
    /// placeholder that the results say is one), and every constant that is a judgement call is listed among the assumptions.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeStructuralLoadsCommand : IExternalCommand
    {
        const string DialogTitle = "Sportify — Structural Loads";
        const string BundledSampleFile = "sample_layout_roofgarden.json";
        const int VideoTimeoutMs = 300_000;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var baseJson, out _);
            var usingBundledSample = false;

            var haveUnity = UnityHeadlessRunner.TryLocate(out var unity, out _);

            if (baseJson == null)
            {
                if (haveUnity) baseJson = UnityHeadlessRunner.ReadBundledSample(unity!, BundledSampleFile);
                usingBundledSample = baseJson != null;
                if (baseJson == null)
                {
                    message = "No layout to analyse yet. Import or push a layout from the Sportify web app first (Combine tab).";
                    return Result.Failed;
                }
            }

            var review = false;
            while (true)
            {
                // The values the analysis rests on that the layout cannot know: entered, accepted or left unconfirmed (which the results then say).
                var prepared = AnalysisAssumptionsDialog.Prepare(baseJson, "structural", DialogTitle, commandData.Application.MainWindowHandle, review);
                if (prepared.Cancelled) return Result.Cancelled;
                var layoutJson = prepared.Json;

                StructureInputs inputs;
                try
                {
                    var layout = JsonSerializer.Deserialize<SportifyLayout>(layoutJson)
                                 ?? throw new InvalidOperationException("The layout was empty.");
                    inputs = StructureLayoutAdapter.ToInputs(layout);
                }
                catch (Exception ex)
                {
                    message = $"Couldn't read the layout: {ex.Message}";
                    return Result.Failed;
                }

                if (inputs.Items.Count == 0)
                {
                    TaskDialog.Show(DialogTitle, "The layout has nothing that weighs on the roof yet (no sports field, activity, green-roof zone or tree). Place something in the Combine tab first.");
                    return Result.Cancelled;
                }

                var report = StructureModel.Analyse(inputs);
                var caseStudy = CaseStudy(inputs, report);
                AnalysisResultPublisher.PublishStructuralLoads(BuildPublishedResult(report, caseStudy, null));

                var unityFree = haveUnity && !UnityHeadlessRunner.IsProjectOpenInUnity(unity!.ProjectDir);
                var choice = ShowSummary(report, caseStudy, usingBundledSample, haveUnity, unityFree);
                if (choice == SummaryChoice.Review) { review = true; continue; }
                if (choice == SummaryChoice.Video)
                {
                    var install = AnalysisMedia.EnsureUnity(DialogTitle, haveUnity ? unity : null, layoutJson, "structural_loads", AnalysisMedia.ProjectTitle(commandData));
                    if (install != null) RenderVideo(install, layoutJson, report, caseStudy);
                }
                else if (choice == SummaryChoice.Pdf)
                    AnalysisMedia.ExportPdf(DialogTitle, layoutJson, new[] { "structural_loads" }, AnalysisMedia.ProjectTitle(commandData));
                return Result.Succeeded;
            }
        }

        internal static string CaseStudy(StructureInputs inputs, StructureReport report)
        {
            return StructureModel.CaseStudy(inputs, report);
        }

        // ------------------------------------------------------------------ dialogs

        static SummaryChoice ShowSummary(StructureReport report, string caseStudy, bool usingBundledSample, bool haveUnity, bool unityFree)
        {
            var s = report.summary;
            var b = report.balance;
            var body = new StringBuilder();
            if (usingBundledSample)
                body.AppendLine("No layout imported into this Revit session yet — analysed Unity's bundled roof-garden sample instead.").AppendLine();
            if (s.preliminary)
                body.AppendLine(s.preliminaryNote).AppendLine();
            body.AppendLine(caseStudy);
            body.AppendLine(AnalysisMedia.SeeReport);

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = (s.preliminary ? "PRELIMINARY: " : "") +
                                  (s.baysOver > 0 ? $"{s.baysOver} bay(s) carry more than the {(s.capacityAssumed ? "assumed " : "")}deck capacity"
                                  : b.status != "balanced" ? "The load sits to one side of the roof"
                                  : "No bay is over capacity and the load is balanced"),
                MainContent = body.ToString(),
                ExpandedContent = "Inputs used:\n" + AnalysisAssumptionsPatcher.DescribeInputs(report.assumptionUses),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };

            AnalysisMedia.AddLinks(dialog, haveUnity, unityFree, "About a minute.");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Review the assumptions, then run again",
                s.preliminary ? "Enter the deck capacity, or accept the built-in value, to lift the PRELIMINARY mark." : "Change the deck capacity or how the built-in value is treated.");

            var result = dialog.Show();
            return result == TaskDialogResult.CommandLink3 ? SummaryChoice.Review : AnalysisMedia.Read(result);
        }

        static void RenderVideo(UnityHeadlessRunner.UnityInstall unity, string layoutJson, StructureReport report, string caseStudy)
        {
            var run = AnalysisMedia.RunUnity(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunStructuralAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "structure_results.json",
                VideoFileStem = "structural_loads",
                LogFileName = "unity_structure_batch.log",
                TimeoutMs = VideoTimeoutMs,
            }, "Rendering the structural loads video");

            if (!run.Ok)
            {
                if (!run.Cancelled) TaskDialog.Show(DialogTitle, "The numbers above stand, but the video couldn't be rendered.\n\n" + run.Message);
                return;
            }

            StructureResultsFile? results;
            try
            {
                results = JsonSerializer.Deserialize<StructureResultsFile>(run.ResultsJson!, new JsonSerializerOptions { IncludeFields = true });
            }
            catch (Exception ex)
            {
                TaskDialog.Show(DialogTitle, $"The video was rendered, but Unity's results file couldn't be read: {ex.Message}");
                return;
            }

            if (results == null || !string.IsNullOrEmpty(results.Error))
            {
                TaskDialog.Show(DialogTitle, $"Unity's structural analysis failed: {results?.Error ?? "empty results file"}");
                return;
            }

            var video = results.Video;
            var videoPath = video != null && !string.IsNullOrEmpty(video.FilePath) && File.Exists(video.FilePath) ? video.FilePath : null;
            if (videoPath != null) videoPath = SportifyWorkspace.Adopt("videos", videoPath);     // the workspace holds the video, not just Unity's Recordings folder
            var disagreement = results.Analysis != null ? Compare(report, results.Analysis) : "Unity's results carried no analysis to compare.";

            AnalysisResultPublisher.PublishStructuralLoads(BuildPublishedResult(report, caseStudy, videoPath));

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

        internal static StructuralLoadsResultDto BuildPublishedResult(StructureReport report, string caseStudy, string? videoPath)
        {
            var s = report.summary;
            var b = report.balance;
            return new StructuralLoadsResultDto
            {
                CaseStudy = caseStudy,
                VideoPath = videoPath,
                GridSource = s.gridSource,
                GridAssumed = s.gridAssumed,
                DeckCapacityKnM2 = Math.Round(s.capacityKnM2, 2),
                DeckCapacityAssumed = s.capacityAssumed,
                Preliminary = s.preliminary,
                PreliminaryNote = s.preliminaryNote,
                Inputs = AssumptionUseDto.From(report.assumptionUses),
                RoofAreaM2 = Math.Round(s.roofAreaM2, 1),
                PermanentLoadKn = Math.Round(s.deadKn, 1),
                ImposedLoadKn = Math.Round(s.liveKn, 1),
                MeanLoadKnM2 = Math.Round(s.meanKnM2, 2),
                PeakBayLoadKnM2 = Math.Round(s.peakBayKnM2, 2),
                PeakUtilisationPercent = Math.Round(s.peakUtilisation * 100.0, 1),
                WorstBay = s.worstBay,
                BaysChecked = s.baysChecked,
                BaysOverCapacity = s.baysOver,
                BaysMarginal = s.baysMarginal,
                ColumnsChecked = s.columnsChecked,
                ColumnsHigh = s.columnsHigh,
                ExpectedPersons = Math.Round(s.expectedPersons, 0),
                BalanceStatus = b.status,
                HeavySide = b.heavySide,
                LoadCentreOffsetXPercent = Math.Round(b.totalEccentricityX * 100.0, 1),
                LoadCentreOffsetYPercent = Math.Round(b.totalEccentricityY * 100.0, 1),
                Bays = report.bays.Select(x => new StructuralBayDto
                {
                    Label = x.label,
                    GridNames = x.gridNames,
                    X0M = Math.Round(x.x0, 2), X1M = Math.Round(x.x1, 2), Y0M = Math.Round(x.y0, 2), Y1M = Math.Round(x.y1, 2),
                    PolygonM = x.polygon == null || x.polygon.Length < 6 ? null
                        : Enumerable.Range(0, x.polygon.Length / 2).Select(i => new PointDto { XM = Math.Round(x.polygon[2 * i], 2), YM = Math.Round(x.polygon[2 * i + 1], 2) }).ToList(),
                    AreaM2 = Math.Round(x.areaM2, 2),
                    LoadKnM2 = Math.Round(x.totalKnM2, 2),
                    UtilisationPercent = Math.Round(x.utilisation * 100.0, 1),
                    Status = x.status,
                    Persons = Math.Round(x.persons, 1),
                }).ToList(),
                Findings = report.recommendations.Select(r => new WindFindingDto { Kind = r.kind, Target = r.target, Text = r.text }).ToList(),
                Assumptions = new List<string>(report.assumptions),
            };
        }

        /// <summary>Unity runs the same core on its own reading of the layout: the two must agree. Returns a description of a disagreement, or null.</summary>
        internal static string? Compare(StructureReport addin, StructureReport unity)
        {
            if (addin.bays.Count != unity.bays.Count)
                return $"Unity's run has {unity.bays.Count} bays, the add-in's {addin.bays.Count}.";

            var problems = new List<string>();
            if (Math.Abs(addin.summary.totalKn - unity.summary.totalKn) > Math.Max(0.5, 0.001 * addin.summary.totalKn))
                problems.Add($"total load {addin.summary.totalKn:0.#} vs {unity.summary.totalKn:0.#} kN");
            if (Math.Abs(addin.summary.peakUtilisation - unity.summary.peakUtilisation) > 0.005f)
                problems.Add($"busiest bay {addin.summary.peakUtilisation * 100:0.#}% vs {unity.summary.peakUtilisation * 100:0.#}%");
            if (Math.Abs(addin.balance.totalEccentricityX - unity.balance.totalEccentricityX) > 0.0005f || Math.Abs(addin.balance.totalEccentricityY - unity.balance.totalEccentricityY) > 0.0005f)
                problems.Add("balance of the load");
            if (addin.summary.baysOver != unity.summary.baysOver)
                problems.Add($"bays over capacity {addin.summary.baysOver} vs {unity.summary.baysOver}");
            return problems.Count == 0 ? null : "Unity's run differs from the add-in's (" + string.Join(", ", problems) + ").";
        }

        // ---- what Sportify.Simulation writes to Recordings/structure_results.json (camelCase, Unity JsonUtility) ----

        internal class StructureVideoInfo
        {
            [JsonPropertyName("path")] public string? FilePath { get; set; }
            [JsonPropertyName("durationS")] public float DurationS { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
        }

        internal class StructureResultsFile
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("message")] public string? Message { get; set; }
            [JsonPropertyName("analysis")] public StructureReport? Analysis { get; set; }
            [JsonPropertyName("video")] public StructureVideoInfo? Video { get; set; }
            [JsonPropertyName("videoError")] public string? VideoError { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
