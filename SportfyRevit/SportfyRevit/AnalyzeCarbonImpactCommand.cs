using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Illustrative kinetic-to-electrical energy-harvesting estimate
    /// across active playing surface (field + activity pieces — garden
    /// doesn't see comparable foot traffic). A theoretical ceiling
    /// assuming piezoelectric-capable flooring throughout, not a
    /// prediction for whichever material was actually picked — the
    /// reference database doesn't track that property per material yet.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeCarbonImpactCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Carbon Impact Analysis";

            var layout = AnalysisLayoutSource.GetLayout("Carbon Impact Analysis");
            if (layout == null) return Result.Cancelled;

            var activeSurfaces = (layout.Placements ?? new List<PlacementDto>())
                .Where(p => (string.Equals(p.Category, "field", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Category, "activity", StringComparison.OrdinalIgnoreCase))
                    && p.BoundingBox != null)
                .ToList();

            if (activeSurfaces.Count == 0)
            {
                TaskDialog.Show(title, "No sport field or activity piece in this layout yet — this check doesn't apply to garden-only layouts.");
                return Result.Succeeded;
            }

            double totalAreaM2 = activeSurfaces.Sum(p => p.BoundingBox!.WidthM * p.BoundingBox!.HeightM);

            double densityWhPerM2PerHour = AnalysisReferenceData.GetParam("Carbon Impact", "energy_density_wh_per_m2_per_hour");
            double dailyHours = AnalysisReferenceData.GetParam("Carbon Impact", "assumed_daily_usage_hours");

            double dailyWh = totalAreaM2 * densityWhPerM2PerHour * dailyHours;

            AnalysisResultPublisher.PublishCarbonImpact(new CarbonImpactResultDto
            {
                ActiveSurfaceAreaM2 = totalAreaM2,
                EstimatedDailyWh = dailyWh,
            });

            TaskDialog.Show(title,
                $"{totalAreaM2:0.#} m² of active playing surface, assuming {dailyHours:0.#} hours/day of use.\n" +
                $"Theoretical kinetic-energy-harvesting ceiling: ~{dailyWh:0.#} Wh/day ({dailyWh / 1000:0.##} kWh/day).\n\n" +
                "Assumes piezoelectric-capable flooring throughout — today's material data doesn't track that property, " +
                "so treat this as an upper bound, not a prediction for the materials actually picked.");

            return Result.Succeeded;
        }
    }
}
