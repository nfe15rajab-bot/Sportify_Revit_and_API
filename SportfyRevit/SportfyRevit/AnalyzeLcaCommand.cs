using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Port of the web app's analyzeLCA(): sums embodied carbon (area x
    /// material's kg CO2e/m2) across every placement with both a picked
    /// reference material and that material's carbon figure filled in
    /// (Sportify.Api's Materials table) — pieces missing either are
    /// counted separately, never silently assumed zero, same honesty as
    /// the web version and the Material model's own seeding philosophy.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeLcaCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — LCA Analysis";

            var layout = AnalysisLayoutSource.GetLayout("LCA Analysis");
            if (layout == null) return Result.Cancelled;

            var items = layout.Placements ?? new List<PlacementDto>();
            if (items.Count == 0)
            {
                TaskDialog.Show(title, "Push a sport, activity, or garden piece to Combine and sync it before checking this.");
                return Result.Succeeded;
            }

            // The sum is EmbodiedCarbon.Compute: the same function as the web app's carbon.js (Tools/AnalysisParity runs both on every fixture layout).
            var carbon = EmbodiedCarbon.Compute(items, AnalysisReferenceData.GetMaterials());
            double totalKg = carbon.TotalKg;
            int coveredCount = carbon.CoveredCount;
            int missingCount = carbon.MissingCount;

            AnalysisResultPublisher.PublishLca(new LcaResultDto
            {
                TotalKg = totalKg,
                CoveredCount = coveredCount,
                MissingCount = missingCount,
                TotalCount = items.Count,
            });

            string missingNote = missingCount > 0
                ? $"\n\n{missingCount} of {items.Count} piece(s) don't have both a reference material and an embodied-carbon " +
                  "figure filled in yet (Data tab -> Materials admin edit form) — excluded, not assumed zero."
                : "";

            TaskDialog.Show(title,
                $"Estimated embodied carbon: ~{totalKg:0.#} kg CO2e across {coveredCount} of {items.Count} piece(s)." +
                missingNote);

            return Result.Succeeded;
        }
    }
}
