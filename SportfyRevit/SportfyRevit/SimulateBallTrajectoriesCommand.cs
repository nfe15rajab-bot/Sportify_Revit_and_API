using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Takes whatever the current Sportify layout is — a live Combine-tab push, or
    /// the last manual "Import Configuration" (both go through
    /// RoofBoundaryServer.TryGetLatestCombinedLayout now, see
    /// ImportSportifyLayoutCommand) — and hands it to the Sportify.Simulation
    /// Unity project as a headless run. Unity flies four stray shots per court
    /// and records them as an MP4, and separately sweeps a fan of ~400 shots per
    /// court to work out what share leave the roof and where fences should go;
    /// this command then shows that and publishes it. No file picker, no
    /// switching to Unity yourself: see Sportify.Simulation/Assets/Scripts/Editor/
    /// BatchRunner.cs for the other half of this.
    /// Falls back to the Unity project's own bundled Goldbeck default layout
    /// if nothing has been imported into this Revit session yet, so this
    /// always produces something rather than a dead end on a fresh session.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateBallTrajectoriesCommand : IExternalCommand
    {
        // Unity start-up, a 1080p slow-motion render and the H.264 encode: roughly
        // 30-60 s on a warm project, several minutes on the very first open.
        const int TimeoutMs = 300_000;
        const string DialogTitle = "Sportify — Ball Trajectory Simulation";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var layoutJson, out _);
            var usingBundledDefault = layoutJson == null;

            if (!UnityHeadlessRunner.TryLocate(out var unity, out var problem))
            {
                message = problem;
                return Result.Failed;
            }

            var run = AnalysisMedia.RunUnity(unity!, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunCollisionAnalysis",
                LayoutJson = layoutJson,
                ResultsFileName = "collision_results.json",
                VideoFileStem = "ball_trajectories",
                LogFileName = "unity_batch.log",
                TimeoutMs = TimeoutMs,
            }, "Simulating the ball trajectories and recording the video");
            if (!run.Ok)
            {
                if (run.Cancelled) return Result.Cancelled;
                message = run.Message;
                return Result.Failed;
            }

            CollisionResults? results;
            try
            {
                results = System.Text.Json.JsonSerializer.Deserialize<CollisionResults>(run.ResultsJson!);
            }
            catch (Exception ex)
            {
                message = $"Couldn't parse Unity's results file: {ex.Message}";
                return Result.Failed;
            }

            if (results == null)
            {
                message = "Unity's results file was empty.";
                return Result.Failed;
            }

            if (!string.IsNullOrEmpty(results.Error))
            {
                message = $"Unity simulation failed: {results.Error}";
                return Result.Failed;
            }

            var recordedVideo = !string.IsNullOrEmpty(results.Video?.FilePath) && File.Exists(results.Video!.FilePath)
                ? results.Video.FilePath
                : null;
            if (recordedVideo != null) recordedVideo = SportifyWorkspace.Adopt("videos", recordedVideo);     // the workspace holds the video, not just Unity's Recordings folder

            // Merged into the session's analysis payload (like every other Analyze*
            // command) rather than replacing it, so the Fire Safety / Water / ...
            // results published earlier survive for the PDF report.
            var violations = results.Violations ?? new List<ViolationRecord>();
            AnalysisResultPublisher.PublishBallTrajectory(BuildPublishedResult(results, violations.Count, recordedVideo));

            ShowSummary(results, violations, usingBundledDefault, recordedVideo);
            return Result.Succeeded;
        }

        static BallTrajectoryResultDto BuildPublishedResult(CollisionResults results, int crossings, string? videoPath)
        {
            var dto = new BallTrajectoryResultDto
            {
                CaseStudy = results.CaseStudy,
                ShotsSimulated = results.ShotsSimulated,
                CrossingCount = crossings,
                VideoPath = videoPath,
            };

            var exit = results.RoofExit;
            if (exit is { Ran: true })
            {
                dto.SweptShots = exit.ShotsSwept;
                dto.PercentLeavingRoof = Math.Round(exit.PercentLeavingRoof, 2);
                dto.PercentLeavingAfterFences = Math.Round(exit.PercentLeavingAfterFences, 2);
                dto.Fences = new List<RoofFenceDto>();
                foreach (var f in exit.Fences ?? new List<FenceInfo>())
                {
                    dto.Fences.Add(new RoofFenceDto
                    {
                        Edge = f.Edge,
                        FromM = Math.Round(f.FromM, 2),
                        ToM = Math.Round(f.ToM, 2),
                        HeightM = Math.Round(f.HeightM, 2),
                        FullHeightM = Math.Round(f.FullHeightM, 2),
                        StopsPercentOfExits = Math.Round(f.StopsPercentOfExits, 1),
                    });
                }
            }

            return dto;
        }

        static void ShowSummary(CollisionResults results, List<ViolationRecord> violations, bool usingBundledDefault, string? videoPath)
        {
            var body = new StringBuilder();
            if (usingBundledDefault)
                body.AppendLine("No layout imported into this Revit session yet — ran Unity's bundled Goldbeck default instead.").AppendLine();
            body.AppendLine(results.CaseStudy);
            body.AppendLine($"{results.ShotsSimulated} shot(s) simulated, {violations.Count} boundary crossing(s) found.");
            body.AppendLine();
            body.AppendLine(AnalysisMedia.SeeReport);

            body.AppendLine();
            if (videoPath != null)
            {
                var v = results.Video!;
                body.AppendLine($"Video: {Path.GetFileName(videoPath)} ({v.DurationS:0.#} s, {v.Width}×{v.Height})");
            }
            else
            {
                body.AppendLine("Video: not recorded" + (string.IsNullOrEmpty(results.VideoError) ? "." : $" — {results.VideoError}"));
            }

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = violations.Count == 0 ? "No boundary crossings found" : $"{violations.Count} boundary crossing(s) found",
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

        // ---- what Sportify.Simulation writes to Recordings/collision_results.json (camelCase, Unity JsonUtility) ----

        internal class ViolationRecord
        {
            [JsonPropertyName("courtIndex")] public int CourtIndex { get; set; }
            [JsonPropertyName("shotLabel")] public string? ShotLabel { get; set; }
            [JsonPropertyName("zoneType")] public string? ZoneType { get; set; }
            [JsonPropertyName("zoneLabel")] public string? ZoneLabel { get; set; }
            [JsonPropertyName("simTime")] public float SimTime { get; set; }
        }

        internal class VideoInfo
        {
            // Named FilePath rather than Path so it can't be confused with System.IO.Path in this file.
            [JsonPropertyName("path")] public string? FilePath { get; set; }
            [JsonPropertyName("durationS")] public float DurationS { get; set; }
            [JsonPropertyName("width")] public int Width { get; set; }
            [JsonPropertyName("height")] public int Height { get; set; }
        }

        internal class FenceInfo
        {
            [JsonPropertyName("edge")] public string? Edge { get; set; }
            [JsonPropertyName("fromM")] public double FromM { get; set; }
            [JsonPropertyName("toM")] public double ToM { get; set; }
            [JsonPropertyName("lengthM")] public double LengthM { get; set; }
            [JsonPropertyName("heightM")] public double HeightM { get; set; }
            [JsonPropertyName("fullHeightM")] public double FullHeightM { get; set; }
            [JsonPropertyName("stopsPercentOfExits")] public double StopsPercentOfExits { get; set; }
        }

        internal class RoofExitInfo
        {
            [JsonPropertyName("ran")] public bool Ran { get; set; }
            [JsonPropertyName("shotsSwept")] public int ShotsSwept { get; set; }
            [JsonPropertyName("shotsLeavingRoof")] public int ShotsLeavingRoof { get; set; }
            [JsonPropertyName("percentLeavingRoof")] public double PercentLeavingRoof { get; set; }
            [JsonPropertyName("percentLeavingAfterFences")] public double PercentLeavingAfterFences { get; set; }
            [JsonPropertyName("fences")] public List<FenceInfo>? Fences { get; set; }
        }

        internal class CollisionResults
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("shotsSimulated")] public int ShotsSimulated { get; set; }
            [JsonPropertyName("violations")] public List<ViolationRecord>? Violations { get; set; }
            [JsonPropertyName("roofExit")] public RoofExitInfo? RoofExit { get; set; }
            [JsonPropertyName("video")] public VideoInfo? Video { get; set; }
            [JsonPropertyName("videoError")] public string? VideoError { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
