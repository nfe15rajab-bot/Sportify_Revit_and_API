namespace SportfyRevit
{
    /// <summary>
    /// When Auto Import (ribbon: Auto Import ON) imports what the web app has (Revit-free, so Tools/AddinCheck checks it).
    ///
    /// Auto Import used to import only an EXPORT (a bumped version): the board's live drafts (POST /combined-layout?draft=1, every few seconds while the
    /// designer works) never reached the model, so "Auto Import ON" did nothing while "Sync with Revit" worked (user, 2026-09-29: "make sure the auto import
    /// on command works like the sync with revit in the app"). Now, with it ON, the model follows the board: an export (Sync with Revit, Export Combined
    /// JSON) is imported at once, a draft once the board has stopped changing for SettleMs - not in the middle of a drag - and a layout that is already in
    /// the model (the one Sync with Revit's import-now just brought in) is not imported a second time.
    /// </summary>
    internal static class AutoImportDecision
    {
        /// <summary>How long the board must stay the same before its draft is imported.</summary>
        internal const double SettleMs = 3000;

        internal enum Action { Import, Wait, Skip }

        /// <param name="exported">the version moved since the last import (an export, not just a draft)</param>
        /// <param name="layoutId">the identity of the layout the web app has now</param>
        /// <param name="lastImportedId">the identity of the layout last imported into the model for this roof (null: none)</param>
        /// <param name="sinceChangeMs">how long ago the layout became this one</param>
        internal static Action Decide(bool exported, string? layoutId, string? lastImportedId, double sinceChangeMs)
        {
            if (string.IsNullOrEmpty(layoutId)) return exported ? Action.Import : Action.Skip;
            if (layoutId == lastImportedId) return Action.Skip;            // already in the model
            if (exported) return Action.Import;                             // a deliberate send: at once
            return sinceChangeMs >= SettleMs ? Action.Import : Action.Wait;  // a draft: once the board has settled
        }
    }
}
