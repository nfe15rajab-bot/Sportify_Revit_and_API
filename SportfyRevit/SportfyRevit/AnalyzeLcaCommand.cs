using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeLcaCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "LCA Analysis",
                "Will run a Life Cycle Assessment (phases A-D) using material/provider data from the Sportify reference database.");
    }
}
