using System.IO;
using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// The add-in's log: <c>%APPDATA%\Sportify\logs\addin-YYYYMMDD.log</c>, one file a day, the last two weeks kept.
    ///
    /// Until this existed the add-in had no record of anything: 87 of its 151 catch blocks discarded the exception, a family that could not be
    /// generated left only "placeholder box" in a dialog that was gone a moment later, and a failed Auto Import was silent. A tester who says "it
    /// did not import" can now be answered from a file. It is also where the import report goes (the dialog is only shown for a manual import).
    ///
    /// Free of Revit types on purpose (AddinCheck tests it), and it never throws: logging must not be the thing that breaks an import.
    /// UTC timestamps, no user name, no file contents; paths are logged because they are what one needs to find the problem.
    /// </summary>
    internal static class SportifyLog
    {
        private const int KeepDays = 14;
        private static readonly object Gate = new();
        private static string? _directory;
        private static string _prunedOn = "";

        /// <summary>The folder the log is written to. SPORTIFY_LOG_DIR overrides it (tests, or a machine where AppData is not writable).</summary>
        internal static string Folder
        {
            get
            {
                if (_directory != null) return _directory;
                var fromEnv = Environment.GetEnvironmentVariable("SPORTIFY_LOG_DIR");
                return !string.IsNullOrWhiteSpace(fromEnv)
                    ? fromEnv
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "logs");
            }
        }

        /// <summary>Points the log somewhere else (a check writes into a scratch folder).</summary>
        internal static void UseDirectory(string? directory) { lock (Gate) { _directory = directory; _prunedOn = ""; } }

        /// <summary>Today's file.</summary>
        internal static string CurrentFile => FileFor(DateTime.UtcNow);

        private static string FileFor(DateTime utc) => Path.Combine(Folder, $"addin-{utc:yyyyMMdd}.log");

        public static void Info(string area, string message) => Write("INFO ", area, message);
        public static void Warn(string area, string message) => Write("WARN ", area, message);

        public static void Error(string area, string message, Exception? ex = null) =>
            Write("ERROR", area, ex == null ? message : message + " :: " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex);

        /// <summary>A multi-line text (the import report) under one heading, every line indented so that the file stays one entry per timestamp.</summary>
        public static void Block(string area, string title, string text)
        {
            var sb = new StringBuilder(title);
            foreach (var line in (text ?? "").Split('\n')) sb.Append(Environment.NewLine).Append("        ").Append(line.TrimEnd('\r'));
            Write("INFO ", area, sb.ToString());
        }

        private static void Write(string level, string area, string message)
        {
            try
            {
                var now = DateTime.UtcNow;
                var line = $"{now:yyyy-MM-ddTHH:mm:ss.fffZ} {level} [{area}] {message}{Environment.NewLine}";
                lock (Gate)
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(FileFor(now), line, Encoding.UTF8);
                    PruneOnce(now);
                }
            }
            catch (Exception)
            {
                // A log that cannot be written (read-only profile, full disk) is not a reason to fail an import.
            }
        }

        /// <summary>Old days are removed once a day, on the first write of that day.</summary>
        private static void PruneOnce(DateTime now)
        {
            var today = now.ToString("yyyyMMdd");
            if (_prunedOn == today) return;
            _prunedOn = today;
            foreach (var file in Directory.GetFiles(Folder, "addin-*.log"))
            {
                try { if (File.GetLastWriteTimeUtc(file) < now.AddDays(-KeepDays)) File.Delete(file); }
                catch (Exception) { /* in use: next time */ }
            }
        }
    }
}
