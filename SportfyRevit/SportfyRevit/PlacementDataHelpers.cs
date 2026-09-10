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
    }
}
