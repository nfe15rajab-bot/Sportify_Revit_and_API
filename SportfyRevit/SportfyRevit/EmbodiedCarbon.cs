namespace SportfyRevit
{
    /// <summary>What the embodied-carbon sum found: the total of the pieces that could be counted, and how many could not.</summary>
    internal sealed class EmbodiedCarbonResult
    {
        public double TotalKg { get; init; }
        public int CoveredCount { get; init; }
        public int MissingCount { get; init; }
        public int TotalCount { get; init; }
    }

    /// <summary>
    /// Embodied carbon of a layout: each piece's footprint area times its reference material's kg CO2e per m2. A piece with no material, or a material with no carbon figure,
    /// is counted as missing, never as zero. No Revit in this file: the LCA command calls it, and Tools/AnalysisParity runs it on every fixture layout against the web app's
    /// carbon.js (embodiedCarbon), which is the same function; they fail together or not at all. (There used to be four copies; the web app's three were merged into carbon.js.)
    /// </summary>
    internal static class EmbodiedCarbon
    {
        /// <summary>
        /// Which catalog material a quality tier means, for a piece that has none picked (a layout saved before the tier default existed, a prebuilt session). The same table
        /// as data.js's QUALITY_REFERENCE_MATERIAL: Tools/AnalysisParity compares them.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> QualityReferenceMaterial = new Dictionary<string, string>
        {
            ["low"] = "PVC sheet, single-layer (economy)",
            ["medium"] = "Sports vinyl / PVC flooring",
            ["high"] = "Wood sprung floor / parquet",
        };

        /// <summary>The material a piece is made of: the one picked, else the one its quality tier means (carbon.js referenceMaterialName).</summary>
        public static string? ReferenceMaterialName(PlacementDto p)
        {
            var picked = PlacementDataHelpers.GetReferenceMaterialName(p);
            if (!string.IsNullOrEmpty(picked)) return picked;
            var tier = PlacementDataHelpers.GetQualityLevel(p);
            return tier != null && QualityReferenceMaterial.TryGetValue(tier, out var name) ? name : null;
        }

        public static EmbodiedCarbonResult Compute(IReadOnlyList<PlacementDto> items, IReadOnlyList<MaterialEntry> materials)
        {
            double totalKg = 0;
            int covered = 0;
            foreach (var it in items)
            {
                var bb = it.BoundingBox;
                if (bb == null) continue;
                var name = ReferenceMaterialName(it);
                var material = name != null ? materials.FirstOrDefault(m => m.Name == name) : null;
                if (material?.EmbodiedCarbonValue is double perM2)
                {
                    totalKg += perM2 * bb.WidthM * bb.HeightM;
                    covered++;
                }
            }
            return new EmbodiedCarbonResult { TotalKg = totalKg, CoveredCount = covered, MissingCount = items.Count - covered, TotalCount = items.Count };
        }
    }
}
