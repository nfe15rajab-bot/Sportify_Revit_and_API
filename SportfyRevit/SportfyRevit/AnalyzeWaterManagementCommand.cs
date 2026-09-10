using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Full-BIM-fidelity port of the web app's analyzeWaterManagement():
    /// area-weighted average buildup depth across garden pieces, run
    /// through the same illustrative retention formula (not a certified
    /// hydrology calculation there either) using the same AnalysisParameter
    /// coefficients from Sportify.Api. Depth comes straight from each
    /// placement's parameters.garden.layers — already resolved by the web
    /// app's buildGardenPayload() at export time — so, unlike the web
    /// app's own analyzeWaterManagement(), no GARDEN_THEMES lookup is
    /// needed on this side at all.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeWaterManagementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Water Management Analysis";

            var layout = AnalysisLayoutSource.GetLayout("Water Management Analysis");
            if (layout == null) return Result.Cancelled;

            var gardenItems = (layout.Placements ?? new List<PlacementDto>())
                .Where(p => string.Equals(p.Category, "garden", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (gardenItems.Count == 0)
            {
                TaskDialog.Show(title, "No garden pieces in this layout yet — push one from Garden mode to check this.");
                return Result.Succeeded;
            }

            double totalAreaM2 = 0, weightedDepthCm = 0;
            int missingDepthCount = 0;

            foreach (var it in gardenItems)
            {
                var bb = it.BoundingBox;
                if (bb == null) continue;

                var layers = it.Parameters?.Garden?.Layers;
                if (layers == null || layers.Count == 0) { missingDepthCount++; continue; }

                double area = bb.WidthM * bb.HeightM;
                double depthCm = layers.Sum(l => l.ThicknessM * 100.0);
                totalAreaM2 += area;
                weightedDepthCm += area * depthCm;
            }

            if (totalAreaM2 <= 0)
            {
                TaskDialog.Show(title, "None of the garden pieces in this layout carry buildup-depth data yet — re-export from the web app's Combine tab.");
                return Result.Succeeded;
            }

            double avgDepthCm = weightedDepthCm / totalAreaM2;
            double basePercent = AnalysisReferenceData.GetParam("Water Management", "retention_base_percent");
            double coeff = AnalysisReferenceData.GetParam("Water Management", "retention_depth_coefficient_percent_per_cm");
            double maxPercent = AnalysisReferenceData.GetParam("Water Management", "retention_max_percent");
            double retentionPercent = Math.Min(maxPercent, Math.Round(basePercent + avgDepthCm * coeff));

            AnalysisResultPublisher.PublishWaterManagement(new WaterManagementResultDto
            {
                TotalAreaM2 = totalAreaM2,
                AvgDepthCm = avgDepthCm,
                RetentionPercent = retentionPercent,
            });

            string missingNote = missingDepthCount > 0
                ? $"\n\n({missingDepthCount} garden piece(s) skipped — no buildup-depth data in the sync.)"
                : "";

            TaskDialog.Show(title,
                $"{totalAreaM2:0.0} m² of garden coverage, {avgDepthCm:0} cm average buildup depth.\n" +
                $"Estimated rainfall retention: ~{retentionPercent:0}% (illustrative — not a certified hydrology figure)." +
                missingNote);

            return Result.Succeeded;
        }
    }
}
