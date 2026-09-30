using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Revit's WARNINGS during an import are written to the log and dismissed, instead of stopping the import on a dialog: an automatic import (Auto
    /// Import, Sync with Revit, Import Iterations) has nobody to click OK. Found in the live test 2026-09-30: the green roof tray of a very narrow garden
    /// pocket (1.1 m) came out "Extrusion is too thin", Revit resolved it itself ("automatically resolved"), and the import then waited for a click on a
    /// warning dialog, holding up everything after it. ERRORS are left to Revit exactly as before (it rolls the transaction back and says why).
    /// </summary>
    internal sealed class ImportWarnings : IFailuresPreprocessor
    {
        private readonly List<string> _seen = new();

        /// <summary>The warnings dismissed so far, each once, in the order they came.</summary>
        internal IReadOnlyList<string> Seen => _seen;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failures)
        {
            foreach (var message in failures.GetFailureMessages())
            {
                if (message.GetSeverity() != FailureSeverity.Warning) continue;
                var text = message.GetDescriptionText() ?? "";
                if (!_seen.Contains(text)) _seen.Add(text);
                failures.DeleteWarning(message);
            }
            return FailureProcessingResult.Continue;
        }

        /// <summary>Puts a new ImportWarnings on the transaction (call after Start) and returns it, to log what it dismissed after Commit.</summary>
        internal static ImportWarnings On(Transaction transaction)
        {
            var warnings = new ImportWarnings();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(warnings);
            transaction.SetFailureHandlingOptions(options);
            return warnings;
        }

        /// <summary>One log line for what was dismissed (nothing when there was nothing).</summary>
        internal void Log(string area, string what)
        {
            if (_seen.Count == 0) return;
            SportifyLog.Warn(area, $"{what}: {_seen.Count} Revit warning(s) dismissed so the import did not wait for a click: " + string.Join(" | ", _seen.Select(s => s.Replace("\r", " ").Replace("\n", " "))));
        }
    }
}
