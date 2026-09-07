using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeWaterManagementCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Water Management Analysis",
                "Will estimate rainwater absorption/retention across the roof's garden coverage, and the runoff the drainage design would need to handle.");
    }
}
