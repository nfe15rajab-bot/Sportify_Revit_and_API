using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeCarbonImpactCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Carbon Impact Analysis",
                "Will estimate the kinetic-to-electrical energy generation potential of a configuration, based on player activity and the materials assigned to each family.");
    }
}
