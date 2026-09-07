using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class GenerateAnalysisReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Generate Analysis Report",
                "Will generate a report from the analyses above, letting the user toggle which sections to include.");
    }
}
