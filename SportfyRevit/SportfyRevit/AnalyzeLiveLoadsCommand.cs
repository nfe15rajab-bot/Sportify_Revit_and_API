using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Was genuinely open-ended ("scope still being defined" per the
    /// project's own notes) — scoped down to a simple, clearly-labeled
    /// estimate: convert each sport field's seated spectator capacity
    /// into a distributed load and compare against a generic fixed-
    /// seating assembly-area reference value. Not a check against this
    /// project's actual building structure — no structural model exists
    /// to check against, only a rule-of-thumb reference figure.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeLiveLoadsCommand : IExternalCommand
    {
        private const double GravityMPerS2 = 9.81;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Live Loads Analysis";

            var layout = AnalysisLayoutSource.GetLayout("Live Loads Analysis");
            if (layout == null) return Result.Cancelled;

            var fields = (layout.Placements ?? new List<PlacementDto>())
                .Where(p => string.Equals(p.Category, "field", StringComparison.OrdinalIgnoreCase)
                    && (p.Parameters?.Field?.Capacity?.Seats ?? 0) > 0
                    && p.BoundingBox != null && p.BoundingBox.WidthM * p.BoundingBox.HeightM > 0)
                .ToList();

            if (fields.Count == 0)
            {
                TaskDialog.Show(title, "No sport field with spectator capacity in this layout yet — set a capacity in Sport mode, push it, and sync before checking this.");
                return Result.Succeeded;
            }

            double massPerPersonKg = AnalysisReferenceData.GetParam("Live Loads", "assumed_load_per_person_kg");
            double referenceKnPerM2 = AnalysisReferenceData.GetParam("Live Loads", "reference_capacity_kn_per_m2");

            var lines = new List<string>();
            double worstKnPerM2 = 0;

            foreach (var f in fields)
            {
                var bb = f.BoundingBox!;
                double areaM2 = bb.WidthM * bb.HeightM;
                int seats = f.Parameters!.Field!.Capacity!.Seats;

                double loadKnPerM2 = seats * massPerPersonKg * GravityMPerS2 / areaM2 / 1000.0;
                worstKnPerM2 = Math.Max(worstKnPerM2, loadKnPerM2);

                lines.Add($"{f.Label ?? "Field"}: {seats} seats over {areaM2:0.#} m² -> {loadKnPerM2:0.00} kN/m²");
            }

            bool withinReference = worstKnPerM2 <= referenceKnPerM2;

            AnalysisResultPublisher.PublishLiveLoads(new LiveLoadsResultDto
            {
                WorstCaseKnPerM2 = worstKnPerM2,
                ReferenceKnPerM2 = referenceKnPerM2,
                WithinReference = withinReference,
            });

            TaskDialog.Show(title,
                $"{(withinReference ? "Within reference" : "OVER REFERENCE")} — worst-case spectator load: {worstKnPerM2:0.00} kN/m² " +
                $"(reference: {referenceKnPerM2:0.#} kN/m², DIN EN 1991-1-1 Category C3).\n\n" +
                string.Join("\n", lines) +
                "\n\nIllustrative only — not checked against this project's actual structural model.");

            return Result.Succeeded;
        }
    }
}
