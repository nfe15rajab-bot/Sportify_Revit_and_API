using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Shows the dockable pane SportifyDockablePaneProvider registered in
    /// OnStartup — a real WebView2-hosted browser docked inside Revit's
    /// own window, not a separate external browser window. The pane
    /// itself (SportifyBrowserPane) handles navigation and surfaces its
    /// own errors (dev server not running, WebView2 Runtime missing)
    /// inline, so this command only needs to make it visible.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenSportifyAppCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var pane = commandData.Application.GetDockablePane(SportifyDockablePaneProvider.PaneId);
            if (!pane.IsShown())
                pane.Show();

            return Result.Succeeded;
        }
    }
}
