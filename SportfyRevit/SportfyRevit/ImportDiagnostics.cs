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
        private static int _floorTypesCreated;
        private static int _floorTypesReused;
        private static int _floorsDrawn;
        private static int _plantsPlaced;

        public static void Begin()
        {
            Lines.Clear();
            _explicitResolved = _keywordMatched = _generated = _placeholder = 0;
            _floorTypesCreated = _floorTypesReused = _floorsDrawn = _plantsPlaced = 0;
        }

        public static void ExplicitResolved(string family, string type) { _explicitResolved++; Lines.Add($"OK   {family} → type \"{type}\""); }
        public static void KeywordMatched(string label) { _keywordMatched++; Lines.Add($"~    {label} → matched by keyword (no explicit family reference)"); }
        public static void Generated(string label) { _generated++; Lines.Add($"GEN  {label} → generated family"); }
        public static void Placeholder(string label) { _placeholder++; Lines.Add($"BOX  {label} → placeholder box"); }

        public static void FloorTypeCreated(string name, int layerCount, double totalThicknessM)
        {
            _floorTypesCreated++;
            Lines.Add($"NEW  floor type \"{name}\" — {layerCount} layers, {totalThicknessM * 1000:0} mm");
        }

        public static void FloorTypeReused(string name) { _floorTypesReused++; }

        public static void PlantPlaced(string species, double crownM, double heightM)
        {
            _plantsPlaced++;
            Lines.Add($"PLT  {species} — {crownM:0.#} m crown, {heightM:0.#} m tall");
        }

        public static void FloorCreated(string label, string typeName, double areaM2)
        {
            _floorsDrawn++;
            Lines.Add($"FLR  {label} → floor \"{typeName}\", {areaM2:0} m2");
        }

        public static void FloorFailed(string label, string reason) => Lines.Add($"FAIL floor for {label} → {reason}");

        public static void FloorTypeFailed(string name, string reason) => Lines.Add($"FAIL floor type \"{name}\" → {reason}");

        /// <summary>The important one: an explicit reference the user made, that could not be honored.</summary>
        public static void ExplicitFailed(string family, string reason) => Lines.Add($"FAIL {family} → {reason}");

        public static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Placed: {_explicitResolved} by family reference, {_keywordMatched} by keyword, {_generated} generated, {_placeholder} placeholder.");
            if (_floorTypesCreated + _floorTypesReused > 0)
                sb.AppendLine($"Build-up systems: {_floorTypesCreated} floor type(s) created, {_floorTypesReused} reused, {_floorsDrawn} floor(s) drawn.");
            if (_plantsPlaced > 0) sb.AppendLine($"Planting: {_plantsPlaced} plant(s) placed.");
            if (Lines.Count > 0)
            {
                sb.AppendLine();
                foreach (var line in Lines) sb.AppendLine(line);
            }
            return sb.ToString();
        }
    }
}
