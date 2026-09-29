namespace SportfyRevit
{
    /// <summary>
    /// An explicit quality_key -> family table, checked by FamilyPreparation (step 6) before falling back to
    /// SportifyFamilyGenerator's generated box: a real family a person sent, found and loaded from the library the
    /// same way SportifyPlanterFamilyBuilder.FamilyFor's design-team kit items are ("the project's own copy wins,
    /// else load ours" — SportifyPlanterFamilyBuilder.FindOrLoad does that part). File and FamilyName are kept apart
    /// like FamilyFor's, because a file's name on disk (Sportify_BalanceLogs, this project's own convention) need
    /// not be the family's own internal Revit name (whatever its author saved it as). Adding a family here is two
    /// lines: the .rfa in Library\Families, and the entry below.
    /// </summary>
    internal static class FamilyMatchRules
    {
        /// <summary>quality_key (activitiesData.js's getActivityQualityKey: "ACTIVITY_" + the activity's id, upper case) -> the .rfa's file name in Library\Families (no extension) and the family's own Revit name.</summary>
        internal static readonly Dictionary<string, (string File, string FamilyName)> Lookup = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ACTIVITY_BALANCE_LOGS"] = ("Sportify_BalanceLogs", "Balance Logs"),
            ["ACTIVITY_MINIGOLF_LANE"] = ("Sportify_MiniGolfLane", "Mini Golf Lane"),
            ["ACTIVITY_MODULAR_TOWER_SLIDE"] = ("Sportify_ModularTowerSlide", "Modular Tower Slide"),
        };
    }
}
