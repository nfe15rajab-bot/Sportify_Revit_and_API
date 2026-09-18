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
    /// Real implementation, replacing the PlaceholderCommand stub: takes
    /// whatever the current Sportify layout is — a live Combine-tab push, or
    /// the last manual "Import Configuration" (both go through
    /// RoofBoundaryServer.TryGetLatestCombinedLayout now, see
    /// ImportSportifyLayoutCommand) — and hands it to the Sportify.Simulation
    /// Unity project as a headless run, then shows what it found. No file
    /// picker, no switching to Unity yourself: see Sportify.Simulation/
    /// Assets/Scripts/Editor/BatchRunner.cs for the other half of this.
    /// Falls back to the Unity project's own bundled Goldbeck default layout
    /// if nothing has been imported into this Revit session yet, so this
    /// always produces something rather than a dead end on a fresh session.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateBallTrajectoriesCommand : IExternalCommand
    {
        const int TimeoutMs = 120_000;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var layoutJson, out _);
            var usingBundledDefault = layoutJson == null;

            string unityProjectDir;
            string unityExePath;
            try
            {
                unityProjectDir = ResolveUnityProjectDir();
                unityExePath = ResolveUnityExePath(unityProjectDir);
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "Sportify");
            Directory.CreateDirectory(tempDir);
            var logFilePath = Path.Combine(tempDir, "unity_batch.log");

            string? layoutFilePath = null;
            if (layoutJson != null)
            {
                layoutFilePath = Path.Combine(tempDir, "layout_for_unity.json");
                File.WriteAllText(layoutFilePath, layoutJson);
            }

            var resultsPath = Path.Combine(unityProjectDir, "Recordings", "collision_results.json");
            try { if (File.Exists(resultsPath)) File.Delete(resultsPath); } catch { /* best-effort */ }

            var psi = new ProcessStartInfo
            {
                FileName = unityExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-batchmode");
            psi.ArgumentList.Add("-projectPath");
            psi.ArgumentList.Add(unityProjectDir);
            psi.ArgumentList.Add("-executeMethod");
            psi.ArgumentList.Add("Sportify.Simulation.Editor.BatchRunner.RunCollisionAnalysis");
            if (layoutFilePath != null)
                psi.ArgumentList.Add($"-layoutFile={layoutFilePath}");
            psi.ArgumentList.Add("-logFile");
            psi.ArgumentList.Add(logFilePath);

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex)
            {
                message = $"Couldn't launch Unity at \"{unityExePath}\": {ex.Message}";
                return Result.Failed;
            }

            // Revit's UI thread blocks here for the simulation's real-world
            // duration (Unity startup plus an ~8s in-sim window) — a
            // synchronous wait is the simplest correct v1 for a one-click
            // school-project command; see the ribbon tooltip for the heads-up
            // shown before this runs.
            var exited = process.WaitForExit(TimeoutMs);
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                message = $"Unity didn't finish within {TimeoutMs / 1000}s — killed it. Log: {logFilePath}";
                return Result.Failed;
            }

            if (!File.Exists(resultsPath))
            {
                message = $"Unity exited (code {process.ExitCode}) but wrote no results file. Check the log: {logFilePath}";
                return Result.Failed;
            }

            CollisionResults? results;
            string resultsJson;
            try
            {
                resultsJson = File.ReadAllText(resultsPath);
                results = System.Text.Json.JsonSerializer.Deserialize<CollisionResults>(resultsJson);
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

            RoofBoundaryServer.PublishAnalysisResults(resultsJson);
            ShowSummary(results, usingBundledDefault);
            return Result.Succeeded;
        }

        static void ShowSummary(CollisionResults results, bool usingBundledDefault)
        {
            var violations = results.Violations ?? new List<ViolationRecord>();
            var body = new StringBuilder();
            if (usingBundledDefault)
                body.AppendLine("No layout imported into this Revit session yet — ran Unity's bundled Goldbeck default instead.").AppendLine();
            body.AppendLine(results.CaseStudy);
            body.AppendLine($"{results.ShotsSimulated} shot(s) simulated, {violations.Count} boundary crossing(s) found.");

            if (violations.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("First few:");
                foreach (var v in violations.GetRange(0, Math.Min(8, violations.Count)))
                    body.AppendLine($"  • Court {v.CourtIndex} \"{v.ShotLabel}\" → {v.ZoneType} ({v.ZoneLabel}) at t={v.SimTime:F2}s");
                if (violations.Count > 8)
                    body.AppendLine($"  … and {violations.Count - 8} more.");
            }

            TaskDialog.Show("Sportify — Ball Trajectory Simulation", body.ToString());
        }

        static string ResolveUnityProjectDir()
        {
            var overridePath = Environment.GetEnvironmentVariable("SPORTIFY_UNITY_PROJECT");
            if (!string.IsNullOrEmpty(overridePath) && Directory.Exists(overridePath))
                return overridePath;

            var defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Sportify_Revit_and_API", "Sportify.Simulation");
            if (Directory.Exists(defaultPath) && File.Exists(Path.Combine(defaultPath, "ProjectSettings", "ProjectVersion.txt")))
                return defaultPath;

            throw new InvalidOperationException(
                $"Can't find the Sportify.Simulation Unity project. Looked at \"{defaultPath}\" " +
                "— set the SPORTIFY_UNITY_PROJECT environment variable to its folder if it's somewhere else.");
        }

        static string ResolveUnityExePath(string unityProjectDir)
        {
            var overridePath = Environment.GetEnvironmentVariable("SPORTIFY_UNITY_EXE");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                return overridePath;

            var versionFile = Path.Combine(unityProjectDir, "ProjectSettings", "ProjectVersion.txt");
            var version = ParseEditorVersion(File.ReadAllText(versionFile));

            var candidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Unity", "Hub", "Editor", version, "Editor", "Unity.exe");
            if (File.Exists(candidate)) return candidate;

            throw new InvalidOperationException(
                $"Can't find Unity {version} at \"{candidate}\" (Unity Hub's default install layout). " +
                "Install it via Unity Hub, or set the SPORTIFY_UNITY_EXE environment variable to Unity.exe's own path.");
        }

        static string ParseEditorVersion(string projectVersionTxt)
        {
            foreach (var rawLine in projectVersionTxt.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("m_EditorVersion:") && !line.StartsWith("m_EditorVersionWithRevision:"))
                    return line.Substring("m_EditorVersion:".Length).Trim();
            }
            throw new InvalidOperationException("Couldn't find m_EditorVersion in ProjectVersion.txt.");
        }

        internal class ViolationRecord
        {
            [JsonPropertyName("courtIndex")] public int CourtIndex { get; set; }
            [JsonPropertyName("shotLabel")] public string? ShotLabel { get; set; }
            [JsonPropertyName("zoneType")] public string? ZoneType { get; set; }
            [JsonPropertyName("zoneLabel")] public string? ZoneLabel { get; set; }
            [JsonPropertyName("simTime")] public float SimTime { get; set; }
        }

        internal class CollisionResults
        {
            [JsonPropertyName("caseStudy")] public string? CaseStudy { get; set; }
            [JsonPropertyName("shotsSimulated")] public int ShotsSimulated { get; set; }
            [JsonPropertyName("violations")] public List<ViolationRecord>? Violations { get; set; }
            [JsonPropertyName("error")] public string? Error { get; set; }
        }
    }
}
