using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Rooftop wind exposure is meaningfully higher than ground level and
    /// concentrated at edges/corners (a simple, well-known aerodynamic
    /// pattern — no real CFD needed to get the qualitative picture right).
    /// Maps to two real FLL Guideline concerns already cited in the
    /// reference database: wind-uplift stress on tall vegetation (e.g. the
    /// roof_trees garden item) and erosion/scour protection for exposed
    /// growing medium at the roof perimeter.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeWindErosionRiskCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Wind & Erosion Risk Analysis",
                "Will apply a simplified wind force field (stronger at roof edges/corners) to placed vegetation and growing-medium zones, flagging wind-uplift stress on tall plants and erosion/scour risk on exposed substrate — the FLL Guideline concern this maps to is already in the Sportify reference database.");
    }
}
