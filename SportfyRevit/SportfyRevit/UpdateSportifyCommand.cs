using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "Update (switch to a different roof)": pushes everything about the roof now selected and makes it the active one. It no longer clears
    /// anything: it used to delete the previous import of EVERY roof and every iteration first, so switching to a second roof emptied the one
    /// just left (user, 2026-09-29: "if we switch to another roof … the previous roof shouldn't be emptied"). Each roof keeps its own content
    /// now, and the next sync of a layout for this roof replaces what earlier imports put on THIS roof only (ImportLedger.RemoveOnRoof); clearing
    /// a roof on purpose is Remove Duplicates → "Clear this roof".
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class UpdateSportifyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show("Sportify — Update", "Open a Revit project first."); return Result.Cancelled; }
            return PushRoofCommandBase.Run(commandData, RoofPushScope.All, ref message);
        }
    }
}
