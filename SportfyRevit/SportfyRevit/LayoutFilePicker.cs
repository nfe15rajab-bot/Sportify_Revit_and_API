using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// How a command asks for a Combine export. It first offers the newest export in the Sportify folder's Layouts subfolder (the web app saves them there), so the usual
    /// case is one click and no file dialog at all; only when there is none, or the person wants another file, does a file dialog open.
    /// The dialog is the Windows one with Revit's main window as its owner, not Revit's own FileOpenDialog: on 2026-09-26 that one initialised and then never came back
    /// once (Revit sat frozen for minutes), and its journal line "Initialize AFileNavDialog" was the last thing it wrote. An owned Windows dialog cannot end up behind
    /// Revit's window, and the folder it opens in is the one the exports are in.
    /// </summary>
    internal static class LayoutFilePicker
    {
        public const string ExportPattern = "sportify_combined_revit*.json";

        /// <summary>The path the person chose, or null when they cancelled. "what" names the command in the dialog's title; "why" is the sentence over the file dialog.</summary>
        public static string? Pick(IntPtr revitWindow, string what, string why)
        {
            var folder = LayoutsFolder();
            var newest = NewestExport(folder);
            SportifyLog.Info("import", what + ": asking for a layout file" + (newest == null ? " (no export in " + folder + ")" : " (newest export: " + Path.GetFileName(newest) + ")"));

            if (newest != null)
            {
                var ask = new TaskDialog("Sportify — " + what)
                {
                    MainInstruction = "Which Combine export?",
                    MainContent = "The newest export in your Sportify folder is ready. Use it, or pick another file.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                };
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Use the newest export", Path.GetFileName(newest) + "\n" + Age(File.GetLastWriteTime(newest)));
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Choose another file…", "Opens a file dialog in " + folder);
                ask.DefaultButton = TaskDialogResult.CommandLink1;       // after the links exist: Revit throws otherwise
                var answer = ask.Show();
                if (answer == TaskDialogResult.CommandLink1)
                {
                    SportifyLog.Info("import", what + ": using the newest export " + newest);
                    return newest;
                }
                if (answer != TaskDialogResult.CommandLink2)
                {
                    SportifyLog.Info("import", what + ": cancelled at the choice");
                    return null;
                }
            }

            return PickFile(revitWindow, what, why, "Sportify layout JSON (*.json)|*.json", folder);
        }

        /// <summary>A file dialog owned by Revit's main window (ShowDialog MUST be given it, see LoadFamiliesCommand: without one Revit's window stays disabled after the dialog closes). Null when cancelled.</summary>
        public static string? PickFile(IntPtr revitWindow, string what, string title, string filter, string folder)
        {
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                RestoreDirectory = true,
                InitialDirectory = Directory.Exists(folder) ? folder : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            };
            SportifyLog.Info("import", what + ": file dialog opening");
            var result = dialog.ShowDialog(new Owner(revitWindow));
            SportifyLog.Info("import", what + ": file dialog closed (" + result + ")");
            return result == System.Windows.Forms.DialogResult.OK ? dialog.FileName : null;
        }

        /// <summary>Revit's main window for code that has no UIApplication at hand.</summary>
        public static IntPtr CurrentRevitWindow()
        {
            try { return Process.GetCurrentProcess().MainWindowHandle; }
            catch (Exception) { return IntPtr.Zero; }
        }

        public static string SportFolder()
        {
            try { return SportifyWorkspace.PathFor("sport"); }
            catch (Exception) { return Path.Combine(SportifyWorkspace.Folder, "Sport fields"); }
        }

        public static string LayoutsFolder()
        {
            try { return SportifyWorkspace.PathFor("layouts"); }
            catch (Exception) { return Path.Combine(SportifyWorkspace.Folder, "Layouts"); }
        }

        /// <summary>The most recently written export in the folder; null when there is none or the folder cannot be read.</summary>
        public static string? NewestExport(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return null;
                return Directory.GetFiles(folder, ExportPattern)
                    .Where(f => new FileInfo(f).Length > 2)             // the empty "{}" a test leaves is not an export
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch (Exception) { return null; }
        }

        static string Age(DateTime local)
        {
            var span = DateTime.Now - local;
            var text = span.TotalMinutes < 1 ? "just now"
                : span.TotalMinutes < 90 ? (int)span.TotalMinutes + " minute(s) ago"
                : span.TotalHours < 48 ? (int)span.TotalHours + " hour(s) ago"
                : (int)span.TotalDays + " day(s) ago";
            return "Saved " + text + " (" + local.ToString("yyyy-MM-dd HH:mm") + ")";
        }

        /// <summary>Revit hands out an HWND, not an IWin32Window.</summary>
        sealed class Owner : System.Windows.Forms.IWin32Window
        {
            public Owner(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }
    }
}
