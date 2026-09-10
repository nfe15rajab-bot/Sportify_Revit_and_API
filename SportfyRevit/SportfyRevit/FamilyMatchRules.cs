namespace SportfyRevit
{
    /// <summary>
    /// Placeholder for an explicit family-name lookup, to replace
    /// FamilyPlacementBuilder's generic keyword scoring once a real family
    /// library exists — keyword matching works with zero bundled content,
    /// but once family names are known and stable, an explicit table is
    /// more predictable than scoring shared words.
    ///
    /// Not wired into FamilyPlacementBuilder yet — TODO once this has real
    /// entries: in FindMatchingFamilySymbol, check FamilyMatchRules.Lookup
    /// for an exact match on quality_key before falling back to keyword
    /// scoring.
    /// </summary>
    internal static class FamilyMatchRules
    {
        /// <summary>quality_key (e.g. "BASKETBALL_STANDARD_HIGH") -> exact Revit family name to place.</summary>
        internal static readonly Dictionary<string, string> Lookup = new(StringComparer.OrdinalIgnoreCase)
        {
            // TODO: fill in as real families are loaded, e.g.:
            // ["BASKETBALL_STANDARD_HIGH"] = "Basketball Court - Standard",
        };
    }
}
