using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// What actually happened to each placement during an import, and WHY.
    ///
    /// The placement pipeline is deliberately forgiving: every failure falls through to the next strategy so a bad family can never stop
    /// geometry appearing. That is the right behaviour and, before this, the wrong diagnostic: an import that quietly placed the wrong family
    /// looked identical to one that worked, and a placeholder box never said what had gone wrong. Now every piece has one line that names
    /// the family it got and how (a family reference, a family already in the project with exactly its quality_key, a generated family,
    /// a specified-sport builder), or the reason it became a placeholder box. The same text goes to the log (SportifyLog).
    ///
    /// Static and single-threaded on purpose: Revit API work happens on one thread, and both import paths (the manual command and
    /// AutoImportSync) go through LayoutImporter, which is a Begin/Report pair around exactly one run.
    /// </summary>
    internal static class ImportDiagnostics
    {
        private static readonly List<string> Lines = new();
        private static int _specified;
        private static int _plants;
        private static int _explicitResolved;
        private static int _matched;
        private static int _generated;
        private static int _placeholder;
        private static int _floorTypesCreated;
        private static int _floorTypesReused;
        private static int _floorsDrawn;
        private static int _plantsPlaced;

        public static void Begin()
        {
            Lines.Clear();
            _specified = _plants = _explicitResolved = _matched = _generated = _placeholder = 0;
            _floorTypesCreated = _floorTypesReused = _floorsDrawn = _plantsPlaced = 0;
        }

        // ── one line per placement ───────────────────────────────────────────────────────────────────────────────────────

        public const string HowGenerated = "generated family";
        public const string HowMatched = "already in the project, exact quality_key";
        public const string HowReference = "family reference";
        public const string HowSpecified = "specified-sport builder";
        public const string HowPlant = "species family";

        /// <summary>A placement got a family. <paramref name="how"/> says how (one of the How... constants).</summary>
        public static void Placed(string label, string family, string type, string how)
        {
            switch (how)
            {
                case HowGenerated: _generated++; break;
                case HowMatched: _matched++; break;
                case HowReference: _explicitResolved++; break;
                case HowSpecified: _specified++; break;
                case HowPlant: _plants++; break;
            }
            Lines.Add($"{Tag(how)} {label} → family \"{family}\", type \"{type}\" ({how})");
        }

        /// <summary>A placement became a box, and this is why.</summary>
        public static void PlaceholderBecause(string label, string reason)
        {
            _placeholder++;
            Lines.Add($"BOX  {label} → placeholder box: {reason}");
        }

        private static string Tag(string how) => how switch
        {
            HowGenerated => "GEN ",
            HowSpecified => "SPT ",
            HowPlant => "PLT ",
            _ => "OK  ",
        };

        /// <summary>RevitFamilyResolver reports the type it resolved; the piece's own line (Placed) is written by the import, so this only goes to the log.</summary>
        public static void ExplicitResolved(string family, string type) => SportifyLog.Info("families", $"family reference resolved: \"{family}\" → type \"{type}\"");

        // ── what the specified-sport builders and the floors report (unchanged in meaning) ───────────────────────────────

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

        public static void PadelCourtBuilt(string courtType, string wallSystem, string surface, double weightKg)
            => Lines.Add($"PDL  Padel court — {courtType}, {wallSystem}, {surface} — {weightKg:0} kg on the deck");

        public static void BasketballCourtBuilt(string variant, int hoops, string mounting, string surface, double weightKg)
            => Lines.Add($"BBL  Basketball court — {variant}, {hoops} basket(s), {mounting}, {surface} — {weightKg:0} kg on the deck");

        public static void VolleyballCourtBuilt(string playType, double netM, string surface, double sandM3, double weightKg)
        {
            // The sand is called out because it is the number that decides whether a beach court can be on this roof at all.
            string sand = sandM3 > 0 ? $", {sandM3:0} m3 of sand" : "";
            Lines.Add($"VBL  Volleyball court — {playType}, net {netM:0.00} m, {surface}{sand} — {weightKg:0} kg on the deck");
        }

        /// <summary>A remark that is neither a success nor a failure of one piece.</summary>
        public static void Note(string message) => Lines.Add($"NOTE {message}");

        public static void FloorCreated(string label, string typeName, double areaM2)
        {
            _floorsDrawn++;
            Lines.Add($"FLR  {label} → floor \"{typeName}\", {areaM2:0} m2");
        }

        public static void FloorFailed(string label, string reason) => Lines.Add($"FAIL floor for {label} → {reason}");

        public static void FloorTypeFailed(string name, string reason) => Lines.Add($"FAIL floor type \"{name}\" → {reason}");

        /// <summary>An explicit reference (or a family that had to be built) that could not be honoured.</summary>
        public static void ExplicitFailed(string family, string reason) => Lines.Add($"FAIL {family} → {reason}");

        /// <summary>True when something failed outright (as opposed to falling back to a box): the caller may want to say so.</summary>
        public static bool AnyFailure => Lines.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal));

        public static string Report()
        {
            var sb = new StringBuilder();
            int pieces = _specified + _plants + _explicitResolved + _matched + _generated + _placeholder;
            sb.AppendLine($"Placed {pieces} piece(s): {_specified} by a specified-sport builder, {_plants} plant(s), {_explicitResolved} by family reference, " +
                          $"{_matched} by a family already in the project (exact quality_key), {_generated} by a generated family, {_placeholder} as placeholder box(es).");
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
