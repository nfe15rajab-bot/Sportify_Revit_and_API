using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Distinct from SetSunAndLocationCommand (which just sets this Revit
    /// project's built-in Site Location/Sun Settings from the web app's
    /// Site tab) — this is meant to be an actual shading analysis over the
    /// placed layout. Deliberately doesn't reimplement solar-position
    /// astronomy here: Revit's own Sun Path/shadow rendering is already a
    /// mature, verified implementation of exactly that math, and silently
    /// re-deriving azimuth/altitude by hand risks a confidently-wrong
    /// number with no way to cross-check it from inside this codebase.
    /// This is a baseline, not the final feature (that's Sukriti's, per
    /// the project notes, using native Revit sun/shadow tools): confirm
    /// the site data genuinely reached Revit, then point at Revit's own
    /// render rather than approximate it.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeSunAndShadingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Sun & Shading Analysis";
            var doc = commandData.Application.ActiveUIDocument.Document;

            var layout = AnalysisLayoutSource.GetLayout("Sun & Shading Analysis");
            if (layout == null) return Result.Cancelled;

            var loc = layout.SiteLocation;
            if (loc == null)
            {
                TaskDialog.Show(title,
                    "This layout has no site location yet — search an address or use \"Use my location\" on the web app's " +
                    "Site tab, sync again, then run \"Set Sun + Location\" before checking this.");
                return Result.Succeeded;
            }

            double docLatDeg = doc.SiteLocation.Latitude * 180.0 / Math.PI;
            double docLonDeg = doc.SiteLocation.Longitude * 180.0 / Math.PI;
            bool locationMatches = Math.Abs(docLatDeg - loc.LatitudeDeg) < 0.01 && Math.Abs(docLonDeg - loc.LongitudeDeg) < 0.01;

            if (!locationMatches)
            {
                TaskDialog.Show(title,
                    $"The synced layout's site ({loc.LatitudeDeg:0.####}, {loc.LongitudeDeg:0.####}" +
                    (string.IsNullOrWhiteSpace(loc.PlaceName) ? "" : $", {loc.PlaceName}") +
                    $") doesn't match this Revit project's current Site Location ({docLatDeg:0.####}, {docLonDeg:0.####}).\n\n" +
                    "Run \"Set Sun + Location\" first so Revit's own Sun Path/shadow rendering uses the right site, then check this again.");
                return Result.Succeeded;
            }

            var sunElement = SunAndShadowSettings.GetActiveSunAndShadowSettings(doc);
            string whenText = sunElement is SunAndShadowSettings sun
                ? sun.StartDateAndTime.ToString("yyyy-MM-dd HH:mm")
                : "not set";

            AnalysisResultPublisher.PublishSunAndShading(new SunAndShadingResultDto
            {
                LocationConfigured = true,
                ConfiguredForDateTime = whenText,
            });

            TaskDialog.Show(title,
                $"Site Location matches the synced layout ({loc.LatitudeDeg:0.####}, {loc.LongitudeDeg:0.####}" +
                (string.IsNullOrWhiteSpace(loc.PlaceName) ? "" : $", {loc.PlaceName}") + $").\n" +
                $"Sun Settings are configured for {whenText}.\n\n" +
                "Open a 3D view and turn on Shadows (Graphic Display Options) or the Sun Path tool to see Revit's own " +
                "accurate shadow rendering for this exact site and time — that's real solar geometry computed by Revit " +
                "itself, not reproduced here.");

            return Result.Succeeded;
        }
    }
}
