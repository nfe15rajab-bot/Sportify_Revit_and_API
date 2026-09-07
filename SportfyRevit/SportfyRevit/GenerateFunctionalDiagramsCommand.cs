using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    [Transaction(TransactionMode.ReadOnly)]
    public class GenerateFunctionalDiagramsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Generate Functional Diagrams",
                "Will generate template-based diagrams: a circulation diagram, a bubble diagram, and a box-like 3D axonometric.");
    }
}
