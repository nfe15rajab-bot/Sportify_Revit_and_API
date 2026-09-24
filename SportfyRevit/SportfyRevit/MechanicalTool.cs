using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FontFamily = System.Windows.Media.FontFamily;

namespace SportfyRevit
{
    /// <summary>
    /// Sportify.Mechanical: the tool that drives SOLIDWORKS (its own COM API, hidden) to build a kinetic unit as a real assembly, run it through its states and record the
    /// frames as an MP4. It is a separate program, run like Unity is: on a worker thread behind a progress window with Cancel, so Revit never waits on it. It talks in lines:
    /// "PROGRESS &lt;percent&gt; &lt;text&gt;" while it works and "RESULT &lt;path to result.json&gt;" at the end; a file named by --cancel-file stops it.
    /// </summary>
    internal static class MechanicalTool
    {
        internal sealed class Outcome
        {
            public bool Ok, Cancelled;
            public string Message = "";
            public string? VideoPath, AssemblyPath, ReportPath, StepPath;
            public JsonElement Result;
            public List<string> Lines = new();
        }

        internal static bool SolidWorksInstalled() => Type.GetTypeFromProgID("SldWorks.Application") != null;

        /// <summary>Where Sportify.Mechanical.exe is: SPORTIFY_MECHANICAL_EXE, else the "mechanical" folder next to the add-in, else the development build beside the source.</summary>
        internal static string? Locate(out string problem)
        {
            problem = "";
            var candidates = new List<string>();
            var env = Environment.GetEnvironmentVariable("SPORTIFY_MECHANICAL_EXE");
            if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);
            var here = Path.GetDirectoryName(typeof(MechanicalTool).Assembly.Location) ?? "";
            candidates.Add(Path.Combine(here, "mechanical", "Sportify.Mechanical.exe"));
            // a development machine: the tool built in the repository the add-in was built from (bin\Release\net8.0-windows10.0.19041.0 inside Sportify.Mechanical)
            for (var dir = new DirectoryInfo(here); dir != null; dir = dir.Parent)
            {
                var repoTool = Path.Combine(dir.FullName, "Sportify.Mechanical");
                if (!Directory.Exists(repoTool)) continue;
                foreach (var exe in Directory.EnumerateFiles(repoTool, "Sportify.Mechanical.exe", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc)) candidates.Add(exe);
                break;
            }
            var found = candidates.FirstOrDefault(File.Exists);
            if (found == null) problem = "The SOLIDWORKS tool (Sportify.Mechanical.exe) was not found next to the add-in (a \"mechanical\" folder) or in the source tree. Set SPORTIFY_MECHANICAL_EXE to its path.";
            return found;
        }

        /// <summary>Runs the tool on the request behind a progress window. The tool's output lines drive the bar; Cancel stops it and the SOLIDWORKS it started.</summary>
        internal static Outcome Run(string exe, string requestFile, string outDir, string name, string what)
        {
            var outcome = new Outcome();
            var cancel = false;
            var cancelFile = Path.Combine(Path.GetTempPath(), "sportify-mechanical-cancel-" + Guid.NewGuid().ToString("N") + ".flag");
            var swBefore = Process.GetProcessesByName("SLDWORKS").Select(p => p.Id).ToHashSet();
            var window = new ToolProgressWindow("Sportify — SOLIDWORKS", what,
                "SOLIDWORKS starts hidden, builds the parts and the assembly, and records each frame of the motion; the first start can take a minute or two. Revit is only waiting, not frozen.", () => { cancel = true; try { File.WriteAllText(cancelFile, "cancel"); } catch (Exception) { } });
            Process? process = null;
            window.Loaded += (_, _) =>
            {
                Task.Run(() =>
                {
                    try
                    {
                        var args = "simulate --request \"" + requestFile + "\" --out \"" + outDir + "\" --name \"" + name + "\" --cancel-file \"" + cancelFile + "\"";
                        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
                        process = Process.Start(psi) ?? throw new InvalidOperationException("the tool did not start");
                        var stderr = new StringBuilder();
                        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                        process.OutputDataReceived += (_, e) =>
                        {
                            if (e.Data == null) return;
                            outcome.Lines.Add(e.Data);
                            if (e.Data.StartsWith("PROGRESS "))
                            {
                                var rest = e.Data.Substring(9).Trim();
                                var space = rest.IndexOf(' ');
                                if (int.TryParse(space > 0 ? rest.Substring(0, space) : rest, out var pct)) window.Dispatcher.BeginInvoke(new Action(() => window.SetProgress(pct, space > 0 ? rest.Substring(space + 1) : "")));
                            }
                            else if (e.Data.StartsWith("RESULT ")) outcome.ReportPath = e.Data.Substring(7).Trim();
                        };
                        process.BeginOutputReadLine(); process.BeginErrorReadLine();
                        var started = DateTime.UtcNow;
                        while (!process.WaitForExit(500))
                        {
                            if (cancel && (DateTime.UtcNow - started).TotalSeconds > 0 && !process.WaitForExit(20000)) { try { process.Kill(true); } catch (Exception) { } break; }
                        }
                        process.WaitForExit();
                        outcome.Cancelled = cancel;
                        outcome.Ok = !cancel && process.ExitCode == 0 && outcome.ReportPath != null && File.Exists(outcome.ReportPath);
                        outcome.Message = outcome.Cancelled ? "Cancelled: SOLIDWORKS was stopped and no video was made."
                            : outcome.Ok ? "" : "The SOLIDWORKS tool stopped (exit code " + process.ExitCode + "). " + (stderr.Length > 0 ? stderr.ToString().Trim() : string.Join("\n", outcome.Lines.TakeLast(6)));
                        if (outcome.Ok)
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(outcome.ReportPath!));
                            outcome.Result = doc.RootElement.Clone();
                            string? Str(string p) => outcome.Result.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                            outcome.VideoPath = Str("videoFile"); outcome.AssemblyPath = Str("assemblyFile"); outcome.StepPath = Str("stepFile");
                        }
                    }
                    catch (Exception ex) { outcome.Message = "The SOLIDWORKS tool could not be run: " + ex.Message; }
                    finally
                    {
                        // a SOLIDWORKS this run started and did not close (a hidden instance does not always exit when asked) is stopped; one the person already had open is left alone
                        try { foreach (var p in Process.GetProcessesByName("SLDWORKS").Where(p => !swBefore.Contains(p.Id))) p.Kill(); } catch (Exception) { }
                        try { if (File.Exists(cancelFile)) File.Delete(cancelFile); } catch (Exception) { }
                        window.Dispatcher.BeginInvoke(new Action(() => window.Finish()));
                    }
                });
            };
            var owner = Process.GetCurrentProcess().MainWindowHandle;
            if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
            window.ShowDialog();
            if (!outcome.Ok && !outcome.Cancelled && outcome.Message.Length == 0) outcome.Message = "Cancelled.";
            return outcome;
        }
    }

    /// <summary>The window shown while SOLIDWORKS works: what it is doing, a bar, the time so far, and Cancel.</summary>
    internal sealed class ToolProgressWindow : Window
    {
        readonly Action _onCancel;
        readonly TextBlock _status, _elapsed;
        readonly ProgressBar _bar;
        readonly DateTime _start = DateTime.Now;
        readonly DispatcherTimer _timer;
        bool _finished;

        public ToolProgressWindow(string title, string what, string hint, Action onCancel)
        {
            _onCancel = onCancel;
            Title = title;
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock { Text = what, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10), Opacity = 0.75 });
            _bar = new ProgressBar { IsIndeterminate = true, Height = 8, Minimum = 0, Maximum = 100 };
            stack.Children.Add(_bar);
            _status = new TextBlock { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, Text = "Starting..." };
            stack.Children.Add(_status);
            _elapsed = new TextBlock { Margin = new Thickness(0, 4, 0, 12), Opacity = 0.75, Text = "0:00" };
            stack.Children.Add(_elapsed);
            var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
            cancel.Click += (_, _) => { _onCancel(); cancel.IsEnabled = false; cancel.Content = "Stopping..."; };
            stack.Children.Add(cancel);
            Content = stack;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => { var t = DateTime.Now - _start; _elapsed.Text = (int)t.TotalMinutes + ":" + t.Seconds.ToString("00"); };
            _timer.Start();
            Closing += (_, e) => { if (!_finished) { e.Cancel = true; _onCancel(); } };
        }

        public void SetProgress(int percent, string text)
        {
            _bar.IsIndeterminate = false;
            _bar.Value = Math.Max(0, Math.Min(100, percent));
            if (!string.IsNullOrEmpty(text)) _status.Text = text;
        }

        public void Finish()
        {
            _finished = true;
            _timer.Stop();
            Close();
        }
    }
}
