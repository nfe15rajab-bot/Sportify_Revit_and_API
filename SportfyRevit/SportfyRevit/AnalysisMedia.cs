using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using Button = System.Windows.Controls.Button;
using FontFamily = System.Windows.Media.FontFamily;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace SportfyRevit
{
    /// <summary>
    /// What an analysis offers after its numbers: a 3D video (which needs the Unity Editor on this computer) or, for everybody else, the same results drawn as charts
    /// in a PDF. The dialogs of the Unity-based commands no longer ask "do you have Unity?": the add-in knows (SportifyCapabilities). With Unity they offer "Render the 3D
    /// video" (Unity runs headless, with a progress window and a Cancel button, so Revit is not left frozen) and "Charts as a PDF"; without it only the PDF, which needs
    /// nothing installed and writes to the Physical analysis folder of the Sportify workspace. Choosing the video when Unity turns out to be unusable still says why and offers the PDF.
    /// </summary>
    internal static class AnalysisMedia
    {
        /// <summary>The command-link ids of the two answers; the "review the assumptions" link, where a command has one, is CommandLink3.</summary>
        public const TaskDialogCommandLinkId VideoLink = TaskDialogCommandLinkId.CommandLink1;
        public const TaskDialogCommandLinkId PdfLink = TaskDialogCommandLinkId.CommandLink2;

        /// <summary>
        /// What every Analyze*/Simulate* command's result dialog now points to instead of repeating its own numbers, charts and recommendations as a
        /// wall of text: the one combined report (AnalysisReportPdfBuilder) that already draws all of that, richer, from the same published result —
        /// the dialog only needs to say what happened, not restate it.
        /// </summary>
        public const string SeeReport = "See the Analysis Report (BIM & Documentation → Generate Analysis Report) for the numbers, chart and recommendations.";

        /// <summary>
        /// Adds what the result dialog offers next. Nobody is asked whether they have Unity: the add-in knows (SportifyCapabilities). With Unity, the 3D video and the PDF are both
        /// offered; without it only the PDF is, with the reason, so there is no link that can only fail. The ids stay the same (VideoLink, PdfLink), so <see cref="Read"/> is unchanged.
        /// </summary>
        public static void AddLinks(TaskDialog dialog, bool haveUnity, bool unityFree, string videoTakes)
        {
            const string PdfText = "The same results drawn as plans, bars and curves, saved as a PDF in the Physical analysis folder of your Sportify folder. Needs no Unity; takes a few seconds.";
            if (!haveUnity)
            {
                dialog.AddCommandLink(PdfLink, "Charts as a PDF", "Unity was not found on this computer, so there is no 3D video here. " + PdfText);
                return;
            }
            var video = unityFree ? $"Unity was found. {videoTakes} A progress window shows it, and you can cancel."
                                  : "Unity was found, but its Editor has the Sportify.Simulation project open: close it first (you will be asked). Or choose the PDF.";
            dialog.AddCommandLink(VideoLink, "Render the 3D video with Unity", video);
            dialog.AddCommandLink(PdfLink, "Charts as a PDF instead", PdfText);
        }

        public static SummaryChoice Read(TaskDialogResult result)
        {
            return result == TaskDialogResult.CommandLink1 ? SummaryChoice.Video : result == TaskDialogResult.CommandLink2 ? SummaryChoice.Pdf : SummaryChoice.Close;
        }

        /// <summary>
        /// The Unity install to render with, or null when there is none to use: then it says why and offers the PDF instead (and writes it when asked).
        /// </summary>
        public static UnityHeadlessRunner.UnityInstall? EnsureUnity(string title, UnityHeadlessRunner.UnityInstall? install, string layoutJson, string analysisKey, string projectName)
        {
            string? why = null;
            if (install == null)
            {
                UnityHeadlessRunner.TryLocate(out _, out var problem);
                why = string.IsNullOrEmpty(problem) ? "Unity, or the Sportify.Simulation project it renders with, was not found on this computer." : problem;
            }
            else if (UnityHeadlessRunner.IsProjectOpenInUnity(install.ProjectDir))
                why = "The Unity Editor has the Sportify.Simulation project open, and a project can only be open once. Close the Editor (or that project in it) and try again.";
            if (why == null) return install;

            var dialog = new TaskDialog(title)
            {
                MainInstruction = "The 3D video can't be made here",
                MainContent = why,
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Charts as a PDF instead", "Needs no Unity: the same results as plans, bars and curves.");
            if (dialog.Show() == TaskDialogResult.CommandLink1) ExportPdf(title, layoutJson, new[] { analysisKey }, projectName);
            return null;
        }

        /// <summary>Draws the charts of the given analyses (null = all five) into the Physical Analysis folder, then offers to open the PDF.</summary>
        public static void ExportPdf(string title, string layoutJson, IEnumerable<string>? keys, string projectName)
        {
            PdfExport pdf;
            try { pdf = PhysicalAnalysisPdf.Export(layoutJson, PhysicalAnalysisPdf.DefaultFolder(), projectName, keys); }
            catch (Exception ex) { TaskDialog.Show(title, "The PDF could not be made: " + ex.Message); return; }

            if (!pdf.Ok)
            {
                TaskDialog.Show(title, pdf.Error ?? "The PDF could not be made.");
                return;
            }

            var body = new System.Text.StringBuilder();
            body.AppendLine(pdf.Path!);
            body.AppendLine();
            body.AppendLine("Drawn: " + string.Join(", ", pdf.Sections.Select(s => s.Title)) + ".");
            foreach (var s in pdf.Skipped) body.AppendLine($"Not drawn, {s.Title}: {s.Reason}.");
            if (pdf.Sections.Any(s => s.Preliminary)) body.AppendLine().AppendLine("Some inputs are still unconfirmed, so the report says PRELIMINARY where that applies.");

            var dialog = new TaskDialog(title)
            {
                MainInstruction = "The charts are ready as a PDF",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the PDF", Path.GetFileName(pdf.Path!));
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Show it in its folder", Path.GetDirectoryName(pdf.Path!));
            dialog.DefaultButton = TaskDialogResult.CommandLink1;       // after the links exist: Revit throws otherwise
            var result = dialog.Show();
            try
            {
                if (result == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo(pdf.Path!) { UseShellExecute = true });
                else if (result == TaskDialogResult.CommandLink2) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{pdf.Path}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { TaskDialog.Show(title, "Couldn't open it: " + ex.Message); }
        }

        /// <summary>The Revit project's name, for the report's cover; empty when there is none.</summary>
        public static string ProjectTitle(ExternalCommandData data)
        {
            try { return data.Application.ActiveUIDocument?.Document?.Title ?? ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// Runs Unity headless with a progress window in front of Revit. Unity runs on a worker thread while the window (modal, with a Cancel button and the time so far)
        /// keeps the message loop going, so Revit does not go "not responding". Closing or cancelling stops Unity.
        /// </summary>
        public static UnityHeadlessRunner.RunOutcome RunUnity(UnityHeadlessRunner.UnityInstall install, UnityHeadlessRunner.Request request, string what)
        {
            var cancel = false;
            UnityHeadlessRunner.RunOutcome? outcome = null;
            var window = new UnityProgressWindow(what, () => cancel = true);
            window.Loaded += (_, _) =>
            {
                System.Threading.Tasks.Task.Run(() =>
                {
                    try { outcome = UnityHeadlessRunner.Run(install, request, () => cancel); }
                    catch (Exception ex) { outcome = new UnityHeadlessRunner.RunOutcome { Message = "Unity could not be run: " + ex.Message }; }
                    window.Dispatcher.BeginInvoke(new Action(() => window.Finish()));
                });
            };
            var owner = Process.GetCurrentProcess().MainWindowHandle;
            if (owner != IntPtr.Zero) new WindowInteropHelper(window) { Owner = owner };
            window.ShowDialog();
            return outcome ?? new UnityHeadlessRunner.RunOutcome { Cancelled = true, Message = "Cancelled." };
        }
    }

    /// <summary>The window shown while Unity renders: what it is doing, the time so far, and Cancel.</summary>
    internal sealed class UnityProgressWindow : Window
    {
        readonly Action _onCancel;
        readonly TextBlock _elapsed;
        readonly DateTime _start = DateTime.Now;
        readonly DispatcherTimer _timer;
        bool _finished;

        public UnityProgressWindow(string what, Action onCancel)
        {
            _onCancel = onCancel;
            Title = "Sportify — Unity";
            Width = 440;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;

            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock { Text = what, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock
            {
                Text = "Unity is starting, running the analysis and recording the video. This takes one to two minutes; Revit is only waiting, not frozen.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10), Opacity = 0.75,
            });
            stack.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 8 });
            _elapsed = new TextBlock { Margin = new Thickness(0, 8, 0, 12), Opacity = 0.75, Text = "0:00" };
            stack.Children.Add(_elapsed);
            var cancel = new Button { Content = "Cancel", IsCancel = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, MinWidth = 90, Padding = new Thickness(10, 4, 10, 4) };
            cancel.Click += (_, _) => { _onCancel(); cancel.IsEnabled = false; cancel.Content = "Stopping..."; };
            stack.Children.Add(cancel);
            Content = stack;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => { var t = DateTime.Now - _start; _elapsed.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}"; };
            _timer.Start();
            Closing += (_, e) => { if (!_finished) { e.Cancel = true; _onCancel(); } };     // closing the window is a cancel; it closes when Unity has stopped
        }

        public void Finish()
        {
            _finished = true;
            _timer.Stop();
            Close();
        }
    }
}
