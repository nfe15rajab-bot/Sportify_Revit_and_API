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

        private static int _lastAppliedVersion = -1;
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
                ? "Live sync is ON — every Combine export from the web app is applied automatically, replacing the previous import. Click to turn off."
                : "Automatically apply every Combine export pushed from the web app, without opening a file picker. Click to turn on.";
        }

        public static void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (!IsEnabled) return;
            if ((DateTime.UtcNow - _lastPollUtc).TotalMilliseconds < PollIntervalMs) return;
            _lastPollUtc = DateTime.UtcNow;

            if (!RoofBoundaryServer.TryGetLatestCombinedLayout(out var json, out var version)) return;
            if (version == _lastAppliedVersion || json == null) return;

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
                _lastAppliedVersion = version;
                SportifyLog.Error("auto-import", "push " + version + " is not a readable layout", ex);
                return;
            }
            if (layout?.Placements == null)
            {
                _lastAppliedVersion = version;
                SportifyLog.Warn("auto-import", "push " + version + " has no placements array; ignored");
                return;
            }

            // Whatever the outcome, this push is dealt with: a failing one must not be retried every two seconds.
            _lastAppliedVersion = version;
            var outcome = LayoutImporter.Run(doc, layout, ImportSource.Auto);
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
