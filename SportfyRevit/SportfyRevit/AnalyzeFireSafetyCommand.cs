using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Full-BIM-fidelity port of the web app's analyzeFireSafety(): the
    /// same synced Combine layout AutoImportSync already consumes, the
    /// same circulation-engine math (CirculationEngine — a line-for-line
    /// port of rules.js's computeCirculation), and the same
    /// max_travel_distance_m reference figure from Sportify.Api — so this
    /// number can never disagree with the web app's own Analysis tab for
    /// the same layout.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeFireSafetyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Fire Safety Analysis";

            var layout = AnalysisLayoutSource.GetLayout("Fire Safety Analysis");
            if (layout == null) return Result.Cancelled;

            if (layout.Placements == null || layout.Placements.Count == 0)
            {
                TaskDialog.Show(title, "Push a sport, activity, or garden piece to Combine and sync it before checking this.");
                return Result.Succeeded;
            }
            if (layout.EntryPoints == null || layout.EntryPoints.Count == 0)
            {
                TaskDialog.Show(title, "This layout has no entry points yet — add one on the Combine board, then sync again.");
                return Result.Succeeded;
            }

            var (distancesM, unreachable) = CirculationEngine.ComputeTravelDistances(layout);
            double maxTravelDistance = AnalysisReferenceData.GetParam("Fire Safety", "max_travel_distance_m");

            if (unreachable.Count > 0)
            {
                AnalysisResultPublisher.PublishFireSafety(new FireSafetyResultDto
                {
                    MaxDistM = 0,
                    MaxTravelDistanceM = maxTravelDistance,
                    WithinLimit = false,
                    UnreachableCount = unreachable.Count,
                });

                TaskDialog.Show(title,
                    $"{unreachable.Count} of {layout.Placements.Count} piece(s) have no walkable route to any entry point " +
                    "— blocked by clearance/circulation width. Fix circulation before a travel-distance figure is meaningful.");
                return Result.Succeeded;
            }

            double maxDist = distancesM.Count > 0 ? distancesM.Values.Max() : 0;
            bool withinLimit = maxDist <= maxTravelDistance;

            AnalysisResultPublisher.PublishFireSafety(new FireSafetyResultDto
            {
                MaxDistM = maxDist,
                MaxTravelDistanceM = maxTravelDistance,
                WithinLimit = withinLimit,
                UnreachableCount = 0,
            });

            TaskDialog.Show(title,
                $"{(withinLimit ? "Within limit" : "OVER LIMIT")} — longest route from a piece to its nearest entry point: " +
                $"{maxDist:0.0} m (max. travel distance reference: {maxTravelDistance:0.#} m, MBO §35).");

            return Result.Succeeded;
        }
    }
}
