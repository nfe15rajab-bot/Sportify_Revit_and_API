using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// The "All Buttons" toggle: shows the full Sportify ribbon even when the person's view is Simple, and puts the ribbon back to their view when clicked again. Like Auto Import it is a plain
    /// push button whose own words are rewritten to show the state (RibbonApplier does it). The choice is kept in the settings file, so it is still on when Revit is started again; the
    /// web app's Profile tab (the view itself) is not touched. It is one of the buttons that are never hidden, so there is always a way back.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ToggleShowAllCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var turningOn = !SportifyProfile.ShowAllButtons();
            try { SportifyProfile.SetShowAllButtons(turningOn); }
            catch (Exception ex)
            {
                SportifyLog.Error("ribbon", "the All Buttons choice could not be saved", ex);
                message = "The choice could not be saved (" + ex.Message + "), so the ribbon stays as it is.";
                return Result.Failed;
            }
            SportifyLog.Info("ribbon", "All Buttons turned " + (turningOn ? "on" : "off"));
            RibbonApplier.Apply();     // this command runs on Revit's own thread, the one the ribbon may be changed from
            return Result.Succeeded;
        }
    }
}
