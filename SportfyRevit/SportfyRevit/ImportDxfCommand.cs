using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class ImportDxfCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Import DXF",
                "Will import a DXF export (e.g. the Sport tab's \"Export DXF\" button) as reference geometry.");
    }
}
