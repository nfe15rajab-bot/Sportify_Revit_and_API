using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Complementary to AnalyzeWaterManagementCommand's plan-view
    /// rain/drainage animation: this is a cross-section view of water
    /// actually filtering down through one garden buildup's real layers
    /// (substrate -> drainage board, using the thicknesses/materials
    /// already defined per theme in GARDEN_THEMES) — a more physically
    /// grounded use of a particle/fluid system than a surface animation,
    /// since flow through a porous medium is closer to what those systems
    /// actually model.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateSoilPercolationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Simulate Soil Percolation",
                "Will show water filtering down through one garden buildup's actual layers (substrate then drainage board, using the real thicknesses/materials per theme) in cross-section — a diagnostic complement to the plan-view rain/drainage animation planned for Water Management.");
    }
}
