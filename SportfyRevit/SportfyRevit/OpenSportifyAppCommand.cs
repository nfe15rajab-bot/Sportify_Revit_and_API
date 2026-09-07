using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Placeholder for opening the Sportify web app inside Revit. The ask
    /// was a browser pane docked inside Revit itself, not an external
    /// browser — that needs a WPF dockable pane hosting a WebView2 control
    /// (a new dependency, plus registering the pane in SportfyRevitApp.
    /// OnStartup via RegisterDockablePane), which hasn't been built yet, so
    /// this is a placeholder like the rest of the new ribbon for now.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenSportifyAppCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            PlaceholderCommand.Show(
                "Open Sportify App",
                "Will open the Sportify web app in a browser pane docked inside Revit (not a separate external browser window) — needs a WebView2-hosted dockable pane, not built yet.");
    }
}
