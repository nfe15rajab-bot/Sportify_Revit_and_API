namespace SportfyRevit
{
    /// <summary>
    /// An explicit quality_key -> family name table, checked by FamilyPreparation (step 6) before falling back to
    /// SportifyFamilyGenerator's generated box: a real family a person sent, whose Revit family name is the same as
    /// its file name in Library\Families (SportifyPlanterFamilyBuilder.FindOrLoad does the "project's own copy wins,
    /// else load ours" part). Adding a family here is two lines: the .rfa in Library\Families, and the entry below.
    /// </summary>
    internal static class FamilyMatchRules
    {
        /// <summary>quality_key (activitiesData.js's getActivityQualityKey: "ACTIVITY_" + the activity's id, upper case) -> exact Revit family name to place, which is also the .rfa's file name.</summary>
        internal static readonly Dictionary<string, string> Lookup = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ACTIVITY_BALANCE_LOGS"] = "Balance Logs",
            ["ACTIVITY_MINIGOLF_LANE"] = "Mini Golf Lane",
            ["ACTIVITY_MODULAR_TOWER_SLIDE"] = "Modular Tower Slide",
            ["ACTIVITY_TRX_FRAME"] = "TRX Suspension Frame",
        };
    }
}
