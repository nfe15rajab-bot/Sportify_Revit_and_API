namespace SportfyRevit
{
    /// <summary>
    /// Shared per-placement field lookups used by LCA and Schedules —
    /// sport/activity keep their materials block at the top level of
    /// "parameters", garden nests its own copy one level deeper (see
    /// ParametersDto/GardenParametersDto).
    /// </summary>
    internal static class PlacementDataHelpers
    {
        public static MaterialsRefDto? GetMaterials(PlacementDto p) =>
            string.Equals(p.Category, "garden", System.StringComparison.OrdinalIgnoreCase)
                ? p.Parameters?.Garden?.Materials
                : p.Parameters?.Materials;

        public static string? GetReferenceMaterialName(PlacementDto p) => GetMaterials(p)?.ReferenceMaterial;
        public static string? GetReferenceProviderName(PlacementDto p) => GetMaterials(p)?.ReferenceProvider;
        public static string? GetQualityLevel(PlacementDto p) => GetMaterials(p)?.QualityLevel;

        /// <summary>
        /// The "unified parameter contract" fields (TypeId/Variant/Norm/
        /// dimensions) worked out for Moamen's eventual sportified
        /// families, pulled from whichever category's nested block this
        /// placement actually has. Garden has no norm field of its own in
        /// the export — FLL Guideline governs green-roof buildup generally,
        /// so that's used as a constant rather than left blank.
        /// </summary>
        public static (string? TypeId, string? Variant, string? Norm, double? LengthM, double? WidthM) GetUnifiedFields(PlacementDto p)
        {
            if (string.Equals(p.Category, "field", System.StringComparison.OrdinalIgnoreCase) && p.Parameters?.Field != null)
            {
                var f = p.Parameters.Field;
                return (f.Sport, f.Variant, f.Norm, f.Dimensions?.LengthM, f.Dimensions?.WidthM);
            }
            if (string.Equals(p.Category, "garden", System.StringComparison.OrdinalIgnoreCase) && p.Parameters?.Garden != null)
            {
                var g = p.Parameters.Garden;
                return (g.TypeId, g.Theme, "FLL Guideline", g.Dimensions?.LengthM, g.Dimensions?.WidthM);
            }
            if (string.Equals(p.Category, "activity", System.StringComparison.OrdinalIgnoreCase) && p.Parameters?.Activity != null)
            {
                var a = p.Parameters.Activity;
                return (a.TypeId, null, a.Norm, a.Dimensions?.LengthM, a.Dimensions?.WidthM);
            }
            return (null, null, null, null, null);
        }
    }
}
