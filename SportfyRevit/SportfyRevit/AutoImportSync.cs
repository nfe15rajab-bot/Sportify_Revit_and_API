using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// Live-sync half of the Combine-to-Revit import, toggled on/off from
    /// the ribbon (ToggleAutoImportCommand). Revit API calls are only valid
    /// from the main UI thread inside a proper API context, so this can't
    /// use a background timer — UIControlledApplication.Idling (fires
    /// repeatedly whenever Revit is otherwise idle, already on that thread)
    /// is the standard way to run recurring API work like this. Each sync
    /// deletes the elements the PREVIOUS auto-sync pass created before
    /// rebuilding, so toggling this on doesn't pile up a fresh copy of the
    /// whole layout every few seconds — unlike the manual Import command,
    /// which is expected to be run occasionally and deliberately.
    /// </summary>
    internal static class AutoImportSync
    {
        private const int PollIntervalMs = 2000;

        public static bool IsEnabled { get; private set; }
        public static PushButton? ToggleButton { get; set; }

        private static int _lastAppliedVersion = -1;
        private static DateTime _lastPollUtc = DateTime.MinValue;
        private static List<ElementId> _lastCreatedIds = new();

        public static void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            if (!enabled)
            {
                // Turning off doesn't remove the last-synced geometry — it
                // just stops watching for new pushes, same as pausing a live
                // link elsewhere in this app.
                return;
            }
            // Force the next Idling tick to check immediately rather than
            // waiting out whatever's left of the previous poll interval.
            _lastPollUtc = DateTime.MinValue;

            UpdateButtonLabel();
        }

        public static void UpdateButtonLabel()
        {
            if (ToggleButton == null) return;
            ToggleButton.ItemText = IsEnabled ? "Auto Import:\nON" : "Auto Import:\nOFF";
            ToggleButton.ToolTip = IsEnabled
                ? "Live sync is ON — every Combine export from the web app is applied automatically. Click to turn off."
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
            if (doc == null) return;

            SportifyLayout? layout;
            try
            {
                layout = JsonSerializer.Deserialize<SportifyLayout>(json);
            }
            catch (Exception)
            {
                return; // malformed push — wait for the next one rather than crash the idling loop
            }
            if (layout?.Placements == null) return;

            using (var t = new Transaction(doc, "Sportify auto-import"))
            {
                t.Start();

                var stillValid = _lastCreatedIds.Where(id => doc.GetElement(id) != null).ToList();
                if (stillValid.Count > 0)
                    doc.Delete(stillValid);

                var summary = SportifyLayoutBuilder.BuildGeometry(doc, layout);
                _lastCreatedIds = summary.CreatedIds;

                t.Commit();
            }

            _lastAppliedVersion = version;
        }
    }
}
