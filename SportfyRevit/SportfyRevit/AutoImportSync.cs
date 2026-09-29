using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// Live-sync half of the Combine-to-Revit import, toggled on/off from the ribbon (ToggleAutoImportCommand). Revit API calls are only valid
    /// from the main UI thread inside a proper API context, so this can't use a background timer — UIControlledApplication.Idling (fires
    /// repeatedly whenever Revit is otherwise idle, already on that thread) is the standard way to run recurring API work like this.
    ///
    /// It runs exactly the import the manual command runs (LayoutImporter), so every sync REPLACES what the previous import of the active project
    /// created (ImportLedger, kept in the project itself). It used to keep a static list of ElementIds in memory and delete them by number in
    /// whichever document was active: with two projects open that deleted unrelated elements of the second one, and after a restart it replaced
    /// nothing. A push that fails is now reported (once, and in the log) instead of being marked applied in silence.
    /// </summary>
    internal static class AutoImportSync
    {
        private const int PollIntervalMs = 2000;

        public static bool IsEnabled { get; private set; }
        public static PushButton? ToggleButton { get; set; }
        /// <summary>Set by ToggleAutoImportCommand when it turns Auto Import on, if the person chose to also clear a previous "Import Iterations as Design Options" run on every sync.</summary>
        public static bool ClearIterationsToo { get; set; }

        // Keyed by roof id, not one number: this now watches whichever roof is active in the web app, and that can change between
        // polls (RoofBoundaryServer.SetActiveRoof) — one shared "already applied" version would otherwise mean something different
        // every time the designer switches roofs, importing a roof again (or missing a real change) for no reason.
        private static readonly Dictionary<long, int> _lastAppliedVersionByRoof = new();
        // ... and the identity of the layout last imported for it: the board's drafts are imported too now (AutoImportDecision), and the same layout never twice.
        private static readonly Dictionary<long, string> _lastImportedIdByRoof = new();

        /// <summary>A layout imported by another way (Sync with Revit's import-now) is in the model: Auto Import does not import it again.</summary>
        internal static void MarkImported(long roofId, string? layoutId, int version)
        {
            _lastAppliedVersionByRoof[roofId] = version;
            if (!string.IsNullOrEmpty(layoutId)) _lastImportedIdByRoof[roofId] = layoutId!;
        }
        private static DateTime _lastPollUtc = DateTime.MinValue;

        public static void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            SportifyLog.Info("auto-import", enabled ? "turned on" : "turned off");
            if (!enabled)
            {
                // Turning off doesn't remove the last-synced geometry — it just stops watching for new pushes, same as pausing a live link
                // elsewhere in this app.
                return;
            }
            // Force the next Idling tick to check immediately rather than waiting out whatever's left of the previous poll interval.
            _lastPollUtc = DateTime.MinValue;

            UpdateButtonLabel();
        }

        public static void UpdateButtonLabel()
        {
            if (ToggleButton == null) return;
            ToggleButton.ItemText = IsEnabled ? "Auto Import:\nON" : "Auto Import:\nOFF";
            ToggleButton.ToolTip = IsEnabled
                ? "Live sync is ON — the model follows the web app's board, like Sync with Revit: a change is imported a few seconds after the board stops changing (a Sync with Revit or an export at once), replacing the previous import. Click to turn off."
                : "Keep the model in step with the web app's board, like Sync with Revit but by itself: every change is imported a few seconds after the board stops changing. Click to turn on.";
        }

        public static void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (!IsEnabled) return;
            if ((DateTime.UtcNow - _lastPollUtc).TotalMilliseconds < PollIntervalMs) return;
            _lastPollUtc = DateTime.UtcNow;

            if (!RoofBoundaryServer.TryGetLatestLayoutState(out var json, out var version, out var roofId, out var layoutId, out var changedUtc) || json == null) return;
            var lastApplied = _lastAppliedVersionByRoof.TryGetValue(roofId, out var v) ? v : -1;
            var lastId = _lastImportedIdByRoof.TryGetValue(roofId, out var li) ? li : null;
            // An export at once, the board's draft once it has settled, and never the layout that is already in the model (AutoImportDecision).
            var action = AutoImportDecision.Decide(version != lastApplied, layoutId, lastId, (DateTime.UtcNow - changedUtc).TotalMilliseconds);
            if (action == AutoImportDecision.Action.Skip) { _lastAppliedVersionByRoof[roofId] = version; return; }
            if (action == AutoImportDecision.Action.Wait) return;

            var uiApp = sender as UIApplication;
            var doc = uiApp?.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) return;

            SportifyLayout? layout;
            try
            {
                layout = JsonSerializer.Deserialize<SportifyLayout>(json);
            }
            catch (Exception ex)
            {
                // A malformed push: wait for the next one rather than crash the idling loop — but say so.
                MarkImported(roofId, layoutId, version);
                SportifyLog.Error("auto-import", "push " + version + " is not a readable layout", ex);
                return;
            }
            if (layout?.Placements == null)
            {
                MarkImported(roofId, layoutId, version);
                SportifyLog.Warn("auto-import", "push " + version + " has no placements array; ignored");
                return;
            }

            // Whatever the outcome, this push is dealt with: a failing one must not be retried every two seconds.
            MarkImported(roofId, layoutId, version);
            SportifyLog.Info("auto-import", $"importing layout {layoutId} (version {version}{(version != lastApplied ? ", an export" : ", the board's draft")})");
            var outcome = LayoutImporter.Run(doc, layout, ImportSource.Auto, ClearIterationsToo, roofId.ToString());
            if (outcome.Cancelled) return;

            if (!outcome.Succeeded)
            {
                TaskDialog.Show("Sportify — Auto Import",
                    "The layout pushed from the web app could not be imported into \"" + doc.Title + "\":\n" + outcome.Error +
                    "\n\nThe project is as it was before. Details: " + SportifyLog.CurrentFile);
            }
        }
    }
}
