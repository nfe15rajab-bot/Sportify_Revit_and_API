using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateCrowdsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Simulate Crowds",
                "Will simulate spectator/participant flow across the placed layout, using the circulation paths and entry points already computed on the web app's Combine tab.");
    }
}
