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

            var materials = AnalysisReferenceData.GetMaterials();
            double totalKg = 0;
            int coveredCount = 0;

            foreach (var it in items)
            {
                var bb = it.BoundingBox;
                if (bb == null) continue;

                string? materialName = PlacementDataHelpers.GetReferenceMaterialName(it);
                var material = materialName != null
                    ? materials.FirstOrDefault(m => m.Name == materialName)
                    : null;

                if (material?.EmbodiedCarbonValue is double carbonPerM2)
                {
                    totalKg += carbonPerM2 * bb.WidthM * bb.HeightM;
                    coveredCount++;
                }
            }

            int missingCount = items.Count - coveredCount;

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
