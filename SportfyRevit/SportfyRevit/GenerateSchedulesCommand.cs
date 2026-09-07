using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class GenerateSchedulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Generate Schedules",
                "Will export an XLS schedule of the families in the model — materials, provider, quality, quantity and description — letting the user toggle which components/families to include.");
    }
}
