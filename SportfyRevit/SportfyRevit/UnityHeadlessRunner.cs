using System;
using System.Diagnostics;
using System.IO;

namespace SportfyRevit
{
    /// <summary>
    /// Finds the Sportify.Simulation Unity project and Unity itself, and runs one of its analyses
    /// headless: the layout goes in as a file, a results file and an MP4 come out. Shared by every
    /// Unity-based command (ball trajectories, wind and erosion), so they all locate Unity, guard
    /// against an already-open project, and explain a silent failure the same way.
    ///
    /// Revit-free on purpose (no Revit types here), so the results contract can be tested outside Revit.
    /// </summary>
    internal static class UnityHeadlessRunner
    {
        internal sealed record UnityInstall(string ProjectDir, string ExePath);

        internal sealed class Request
        {
            /// <summary>The static method to run, e.g. Sportify.Simulation.Editor.BatchRunner.RunWindAnalysis.</summary>
            public string ExecuteMethod = "";

            /// <summary>The layout to analyse, or null for the project's own bundled sample.</summary>
            public string? LayoutJson;

            /// <summary>The file Unity writes under Recordings/ when it is done, e.g. wind_results.json.</summary>
            public string ResultsFileName = "";

            /// <summary>Start of the MP4's name; a timestamp is added so an older video that is still open in a player never blocks a new run.</summary>
            public string VideoFileStem = "";

            public string LogFileName = "unity_batch.log";
            public int TimeoutMs = 300_000;
        }

        internal sealed class RunOutcome
        {
            public bool Ok;
            /// <summary>The run was cancelled by the person (Unity was stopped).</summary>
            public bool Cancelled;
            public string Message = "";
            public string? ResultsJson;
            public string? VideoPath;
            public string LogPath = "";
        }

        /// <summary>
        /// The Unity project and the editor that opens it, or a sentence saying what is missing.
        /// </summary>
        public static bool TryLocate(out UnityInstall? install, out string problem)
        {
            install = null;
            problem = "";
            try
            {
                var projectDir = ResolveUnityProjectDir();
                install = new UnityInstall(projectDir, ResolveUnityExePath(projectDir));
                return true;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Runs the request and waits for Unity. <paramref name="cancelled"/> is polled while it runs (a few times a second): when it says true Unity is stopped
        /// and the outcome says so. Call it from a worker thread with a progress window on the UI thread (AnalysisMedia.RunUnity), so Revit does not freeze.
        /// </summary>
        public static RunOutcome Run(UnityInstall install, Request request, Func<bool>? cancelled = null)
        {
            var outcome = new RunOutcome();
            var tempDir = Path.Combine(Path.GetTempPath(), "Sportify");
            Directory.CreateDirectory(tempDir);
            outcome.LogPath = Path.Combine(tempDir, request.LogFileName);

            // A Unity project can only be open once. If the Editor has it open, a batch run
            // would exit at once, saying nothing useful — so check first and say so.
            if (IsProjectOpenInUnity(install.ProjectDir))
            {
                outcome.Message = "Unity already has the Sportify.Simulation project open, and a project can only be open once. " +
                                  "Close the Unity Editor (or that project in it) and run this again.";
                return outcome;
            }

            string? layoutFilePath = null;
            if (request.LayoutJson != null)
            {
                layoutFilePath = Path.Combine(tempDir, "layout_for_unity.json");
                File.WriteAllText(layoutFilePath, request.LayoutJson);
            }

            var recordingsDir = Path.Combine(install.ProjectDir, "Recordings");
            Directory.CreateDirectory(recordingsDir);
            var resultsPath = Path.Combine(recordingsDir, request.ResultsFileName);
            var videoPath = Path.Combine(recordingsDir, $"{request.VideoFileStem}_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            try { if (File.Exists(resultsPath)) File.Delete(resultsPath); } catch { /* best-effort */ }

            var psi = new ProcessStartInfo
            {
                FileName = install.ExePath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-batchmode");
            psi.ArgumentList.Add("-projectPath");
            psi.ArgumentList.Add(install.ProjectDir);
            psi.ArgumentList.Add("-executeMethod");
            psi.ArgumentList.Add(request.ExecuteMethod);
            if (layoutFilePath != null)
                psi.ArgumentList.Add($"-layoutFile={layoutFilePath}");
            psi.ArgumentList.Add($"-videoFile={videoPath}");
            psi.ArgumentList.Add("-logFile");
            psi.ArgumentList.Add(outcome.LogPath);

            Process process;
            try
            {
                process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            }
            catch (Exception ex)
            {
                outcome.Message = $"Couldn't launch Unity at \"{install.ExePath}\": {ex.Message}";
                return outcome;
            }

            // The wait is in slices so a cancel is noticed within a fraction of a second. (Unity startup, the analysis, then rendering and encoding the
            // video take a minute or two; the caller shows progress, see AnalysisMedia.RunUnity.)
            var started = DateTime.UtcNow;
            while (!process.WaitForExit(250))
            {
                if (cancelled != null && cancelled())
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    outcome.Cancelled = true;
                    outcome.Message = "Cancelled: Unity was stopped and no video was made.";
                    return outcome;
                }
                if ((DateTime.UtcNow - started).TotalMilliseconds > request.TimeoutMs)
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                    outcome.Message = $"Unity didn't finish within {request.TimeoutMs / 1000}s — killed it. Log: {outcome.LogPath}";
                    return outcome;
                }
            }

            if (!File.Exists(resultsPath))
            {
                outcome.Message = ExplainMissingResults(process.ExitCode, outcome.LogPath);
                return outcome;
            }

            try
            {
                outcome.ResultsJson = File.ReadAllText(resultsPath);
            }
            catch (Exception ex)
            {
                outcome.Message = $"Couldn't read Unity's results file: {ex.Message}";
                return outcome;
            }

            outcome.VideoPath = videoPath;
            outcome.Ok = true;
            return outcome;
        }

        /// <summary>The bundled sample layout of the Unity project, for when nothing has been imported yet.</summary>
        public static string? ReadBundledSample(UnityInstall install, string fileName)
        {
            var path = Path.Combine(install.ProjectDir, "Assets", "StreamingAssets", fileName);
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// While a project is open Unity holds Temp/UnityLockfile open exclusively, so
        /// failing to open it for exclusive access means another Unity instance (most
        /// likely the Editor) has the project. A stale file left by a crashed session
        /// isn't held and opens fine, so it doesn't count.
        /// </summary>
        public static bool IsProjectOpenInUnity(string unityProjectDir)
        {
            var lockFile = Path.Combine(unityProjectDir, "Temp", "UnityLockfile");
            if (!File.Exists(lockFile)) return false;

            try
            {
                using var stream = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;   // can't tell — let Unity try and report
            }
        }

        /// <summary>
        /// Turns "Unity ran but wrote nothing" into something a person can act on.
        /// </summary>
        public static string ExplainMissingResults(int exitCode, string logFilePath)
        {
            var log = "";
            try { if (File.Exists(logFilePath)) log = File.ReadAllText(logFilePath); } catch { /* fall through */ }

            if (log.Contains("error CS", StringComparison.Ordinal))
                return $"The Sportify.Simulation scripts didn't compile in Unity. Log: {logFilePath}";

            if (log.Contains("No valid Unity Editor license", StringComparison.OrdinalIgnoreCase))
                return "Unity couldn't find a valid license. Open Unity Hub, sign in, then run this again.";

            // A run that opened the project always logs the engine version. Without it,
            // Unity exited before loading the project — in batch mode, silently.
            if (!log.Contains("Initialize engine version", StringComparison.Ordinal))
                return "Unity couldn't open the Sportify.Simulation project — most likely it is open in the Unity Editor right now. " +
                       $"Close the Editor and run this again. Log: {logFilePath}";

            return $"Unity exited (code {exitCode}) but wrote no results file. Check the log: {logFilePath}";
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
    }
}
