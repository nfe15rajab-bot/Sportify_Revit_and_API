using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Distinct from SetSunAndLocationCommand (which just sets this Revit
    /// project's built-in Site Location/Sun Settings from the web app's
    /// Site tab, and still exists but isn't wired to this ribbon) — this is
    /// meant to be an actual shading analysis over the placed layout.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class AnalyzeSunAndShadingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Sun & Shading Analysis",
                "Will analyze shading across the layout using the site's sun position data already captured on the web app's Site tab.");
    }
}
