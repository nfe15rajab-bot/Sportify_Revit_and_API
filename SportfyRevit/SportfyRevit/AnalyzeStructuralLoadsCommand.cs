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
            if (!ShowSummary(report, caseStudy, usingBundledSample, haveUnity, unityFree))
                return Result.Succeeded;

            RenderVideo(unity!, layoutJson, report, caseStudy);
            return Result.Succeeded;
        }

        internal static string CaseStudy(StructureInputs inputs, StructureReport report)
        {
            return StructureModel.CaseStudy(inputs, report);
        }

        // ------------------------------------------------------------------ dialogs

        static bool ShowSummary(StructureReport report, string caseStudy, bool usingBundledSample, bool haveUnity, bool unityFree)
        {
            var s = report.summary;
            var b = report.balance;
            var body = new StringBuilder();
            if (usingBundledSample)
                body.AppendLine("No layout imported into this Revit session yet — analysed Unity's bundled roof-garden sample instead.").AppendLine();
            body.AppendLine(caseStudy);
            body.AppendLine($"{s.deadKn:0} kN permanent + {s.liveKn:0} kN imposed = {s.totalKn:0} kN on {s.roofAreaM2:0} m² ({s.meanKnM2:0.0} kN/m² on average, {s.peakBayKnM2:0.0} in the most loaded bay).");
            body.AppendLine($"Deck capacity {s.capacityKnM2:0.#} kN/m²" + (s.capacityAssumed ? " — a PLACEHOLDER: enter the structural engineer's figure in the Combine tab." : "."));
            body.AppendLine();
            body.AppendLine($"Bays: {s.baysOver} of {s.baysChecked} over capacity, {s.baysMarginal} marginal; most loaded {s.worstBay} at {s.peakUtilisation * 100:0}%.");
            if (s.columnsChecked > 0)
                body.AppendLine($"Columns: {s.columnsHigh} of {s.columnsChecked} take more than {StructureModel.ColumnHighFactor:0.#}× the average.");
            body.AppendLine($"People expected: about {s.expectedPersons:0}; {s.busiestBaysSharePercent:0}% of them in the busiest fifth of the bays.");
            body.AppendLine($"Balance: the load's centre is {Math.Abs(b.totalEccentricityX) * 100:0.#}% of the length and {Math.Abs(b.totalEccentricityY) * 100:0.#}% of the width off {b.centreBasis} ({b.status}" +
                            (string.IsNullOrEmpty(b.heavySide) ? "" : $", heavy on the {b.heavySide}") + $"; {b.leftSharePercent:0}% left / {b.rightSharePercent:0}% right).");

            var advice = report.recommendations.Where(r => r.kind != "vulnerable" && r.kind != "grid").Take(4).ToList();
            if (advice.Count > 0)
            {
                body.AppendLine();
                foreach (var r in advice) body.AppendLine("  • " + r.text);
            }

            body.AppendLine();
            body.AppendLine("A screening estimate, not a structural verification. The assumptions are in the PDF report; crowds in motion and wind, rain and snow come with the dynamic analysis.");

            if (!haveUnity)
                body.AppendLine().AppendLine("3D video: needs the Unity Editor, which wasn't found. The numbers above don't.");
            else if (!unityFree)
                body.AppendLine().AppendLine("3D video: close the Unity Editor (it has Sportify.Simulation open) to render it.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = s.baysOver > 0 ? $"{s.baysOver} bay(s) carry more than the deck capacity"
                                : b.status != "balanced" ? "The load sits to one side of the roof"
                                : "No bay is over capacity and the load is balanced",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };

            if (unityFree)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Render the 3D video with Unity",
                    "About a minute; Revit is unresponsive while it renders.");

            return dialog.Show() == TaskDialogResult.CommandLink1;
        }

        static void RenderVideo(UnityHeadlessRunner.UnityInstall unity, string layoutJson, StructureReport report, string caseStudy)
        {
            var run = UnityHeadlessRunner.Run(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunStructuralAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "structure_results.json",
                VideoFileStem = "structural_loads",
                LogFileName = "unity_structure_batch.log",
                TimeoutMs = VideoTimeoutMs,
            });

            if (!run.Ok)
            {
                TaskDialog.Show(DialogTitle, "The numbers above stand, but the video couldn't be rendered.\n\n" + run.Message);
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
