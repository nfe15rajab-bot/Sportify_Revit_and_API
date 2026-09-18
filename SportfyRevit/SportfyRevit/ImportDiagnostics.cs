using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// What actually happened to each placement during an import.
    ///
    /// The placement pipeline is deliberately forgiving — every failure falls
    /// through to the next strategy so a bad family reference can never stop
    /// geometry appearing. That is the right behavior and the wrong diagnostic:
    /// an import that quietly places the wrong-sized family reports success and
    /// looks identical to one that worked. This makes the fallback chain
    /// visible instead.
    ///
    /// Static and single-threaded on purpose: Revit API work happens on one
    /// thread inside one transaction, and both import paths (the manual command
    /// and AutoImportSync) are a Begin/Report pair around exactly one run.
    /// </summary>
    internal static class ImportDiagnostics
    {
        private static readonly List<string> Lines = new();
        private static int _explicitResolved;
        private static int _keywordMatched;
        private static int _generated;
        private static int _placeholder;

        public static void Begin()
        {
            Lines.Clear();
            _explicitResolved = _keywordMatched = _generated = _placeholder = 0;
        }

        public static void ExplicitResolved(string family, string type) { _explicitResolved++; Lines.Add($"OK   {family} → type \"{type}\""); }
        public static void KeywordMatched(string label) { _keywordMatched++; Lines.Add($"~    {label} → matched by keyword (no explicit family reference)"); }
        public static void Generated(string label) { _generated++; Lines.Add($"GEN  {label} → generated family"); }
        public static void Placeholder(string label) { _placeholder++; Lines.Add($"BOX  {label} → placeholder box"); }

        /// <summary>The important one: an explicit reference the user made, that could not be honored.</summary>
        public static void ExplicitFailed(string family, string reason) => Lines.Add($"FAIL {family} → {reason}");

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Placed: {_explicitResolved} by family reference, {_keywordMatched} by keyword, {_generated} generated, {_placeholder} placeholder.");
            if (Lines.Count > 0)
            {
                sb.AppendLine();
                foreach (var line in Lines) sb.AppendLine(line);
            }
            return sb.ToString();
        }
    }
}
