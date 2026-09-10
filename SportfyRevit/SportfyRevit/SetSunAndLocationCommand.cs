using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Sets the project's real-world Site Location (lat/lon/place name +
    /// time zone) and configures the active Sun Settings as a "Still" study
    /// at the Site tab's chosen date/time — so Revit's own, already-built-in
    /// sun path/shadow tools ("revit does already") reflect the actual site
    /// instead of the template's generic default location. Prefers whatever
    /// the frontend last pushed live (same channel AutoImportSync reads) so
    /// this needs no file picker in the common case; falls back to picking
    /// a JSON file when nothing has been pushed yet.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SetSunAndLocationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument.Document;

            SportifyLayout? layout;
            if (RoofBoundaryServer.TryGetLatestCombinedLayout(out var liveJson, out _) && liveJson != null)
            {
                layout = TryParse(liveJson, ref message);
                if (layout == null) return Result.Failed;
            }
            else
            {
                var fod = new FileOpenDialog("Sportify layout JSON (*.json)|*.json");
                fod.Title = "No live data from the web app yet — select a Combine export instead";
                if (fod.Show() != ItemSelectionDialogResult.Confirmed)
                    return Result.Cancelled;

                string jsonPath;
                try
                {
                    jsonPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
                }
                catch (Exception ex)
                {
                    message = "Couldn't resolve the selected file: " + ex.Message;
                    return Result.Failed;
                }

                string text;
                try { text = File.ReadAllText(jsonPath); }
                catch (Exception ex)
                {
                    message = "Couldn't read that JSON file: " + ex.Message;
                    return Result.Failed;
                }

                layout = TryParse(text, ref message);
                if (layout == null) return Result.Failed;
            }

            var loc = layout.SiteLocation;
            if (loc == null)
            {
                TaskDialog.Show(
                    "Sportify",
                    "That Combine export has no site location — search an address or use \"Use my location\" " +
                    "on the web app's Site tab, then export/push again.");
                return Result.Failed;
            }

            using (var t = new Transaction(doc, "Set Sportify site location and sun settings"))
            {
                t.Start();

                var siteLocation = doc.SiteLocation;
                double latRad = loc.LatitudeDeg * Math.PI / 180.0;
                double lonRad = loc.LongitudeDeg * Math.PI / 180.0;
                siteLocation.Latitude = latRad;
                siteLocation.Longitude = lonRad;
                siteLocation.TimeZone = SunAndShadowSettings.CalculateTimeZone(latRad, lonRad);
                if (!string.IsNullOrWhiteSpace(loc.PlaceName))
                    siteLocation.PlaceName = loc.PlaceName;

                var sunElement = SunAndShadowSettings.GetActiveSunAndShadowSettings(doc);
                if (sunElement is SunAndShadowSettings sun)
                {
                    var when = ParseDateTime(loc.Date, loc.Time);
                    sun.SunAndShadowType = SunAndShadowType.StillImage;
                    sun.StartDateAndTime = when;
                    sun.EndDateAndTime = when;
                }

                t.Commit();

                TaskDialog.Show(
                    "Sportify",
                    $"Site location set to {loc.LatitudeDeg:0.####}, {loc.LongitudeDeg:0.####}" +
                    (string.IsNullOrWhiteSpace(loc.PlaceName) ? "" : $" ({loc.PlaceName})") +
                    $".\nSun settings set to a still study at {(loc.Date ?? "today")} {(loc.Time ?? "")}.".TrimEnd() +
                    "\n\nOpen a 3D view's Sun Path / Shading to see it.");
            }

            return Result.Succeeded;
        }

        private static SportifyLayout? TryParse(string json, ref string message)
        {
            try
            {
                return JsonSerializer.Deserialize<SportifyLayout>(json);
            }
            catch (Exception ex)
            {
                message = "Couldn't parse that Combine export: " + ex.Message;
                return null;
            }
        }

        private static DateTime ParseDateTime(string? date, string? time)
        {
            if (!string.IsNullOrWhiteSpace(date) && !string.IsNullOrWhiteSpace(time) &&
                DateTime.TryParse($"{date}T{time}:00", out var parsed))
            {
                return parsed;
            }
            return DateTime.Now;
        }
    }
}
