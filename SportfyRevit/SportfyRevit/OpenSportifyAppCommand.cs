using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// A toggle, not just a show: the dockable pane (SportifyDockablePaneProvider, a real WebView2-hosted browser
    /// docked inside Revit's own window) is cramped at the widths a docked panel actually gets — a full desktop
    /// layout squeezed into a narrow strip. Clicking this while the pane is already shown hides it and opens the
    /// same web app in the user's own system browser instead (a real window, resizable, not fighting Revit for
    /// space); clicking it again while hidden re-shows the docked pane. The pane itself (SportifyBrowserPane)
    /// handles navigation and surfaces its own errors (dev server not running, WebView2 Runtime missing) inline.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class OpenSportifyAppCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var pane = commandData.Application.GetDockablePane(SportifyDockablePaneProvider.PaneId);
            if (pane.IsShown())
            {
                pane.Hide();
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SportifyBrowserPane.DefaultUrl) { UseShellExecute = true }); }
                catch (System.Exception ex) { SportifyLog.Warn("app", "could not open the system browser: " + ex.Message); }
            }
            else
            {
                pane.Show();
            }

            return Result.Succeeded;
        }
    }
}
