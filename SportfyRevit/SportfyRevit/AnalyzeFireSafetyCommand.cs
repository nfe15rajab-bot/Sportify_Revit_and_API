using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeFireSafetyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Fire Safety Analysis",
                "Will check evacuation routes, travel distances and exit/entry counts from the Combine layout against fire-safety norms.");
    }
}
