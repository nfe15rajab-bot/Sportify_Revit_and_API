using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// A rule/geometry check against accessibility standards, not a physics
    /// simulation — same category as AnalyzeFireSafetyCommand, reusing the
    /// same circulation engine that already computes travel distances.
    /// Three separate user groups map to three already-existing pieces of
    /// frontend data: wheelchair route width against DIN 18040 accessible-
    /// route minimums (computed below); tactile/contrast guidance at
    /// decision points for blind users (DIN 32984 tactile paving); and,
    /// for children, the "mini" variants already in FIELDS (data.js) plus
    /// impact-attenuating surfacing under playground-category activity
    /// items (DIN EN 1176/1177) — the latter two need a visual walkthrough
    /// rather than being computable from the layout JSON alone, so this
    /// command says so explicitly rather than silently skipping them.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeAccessibilityCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Accessibility Analysis";

            var layout = AnalysisLayoutSource.GetLayout("Accessibility Analysis");
            if (layout == null) return Result.Cancelled;

            if (layout.Placements == null || layout.Placements.Count == 0)
            {
                TaskDialog.Show(title, "Push a sport, activity, or garden piece to Combine and sync it before checking this.");
                return Result.Succeeded;
            }

            // The decision is SafetyAnalysis.Access: the same as the web app's analyzeAccessibility() (Tools/AnalysisParity runs both on every fixture layout).
            double minWidth = AnalysisReferenceData.GetParam("Accessibility", "min_circulation_width_m");
            var outcome = SafetyAnalysis.Access(layout, minWidth);
            double currentWidth = outcome.CurrentWidthM;
            bool widthOk = outcome.WidthOk;
            bool reachOk = outcome.ReachOk;

            AnalysisResultPublisher.PublishAccessibility(new AccessibilityResultDto
            {
                WidthOk = widthOk,
                ReachOk = reachOk,
                CurrentWidthM = currentWidth,
                MinWidthM = minWidth,
            });

            TaskDialog.Show(title,
                $"Circulation width {currentWidth:0.0} m ({(widthOk ? "meets" : "BELOW")} the {minWidth:0.#} m reference); " +
                $"every piece reachable: {(reachOk ? "yes" : "no")}.\n\n" +
                AnalysisMedia.SeeReport);

            return Result.Succeeded;
        }
    }
}
