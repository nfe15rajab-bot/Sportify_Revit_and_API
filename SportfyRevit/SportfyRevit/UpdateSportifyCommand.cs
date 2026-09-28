using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "Update": an explicit, deliberate full reset, for when every roof Sportify has ever built in this project should be cleared and
    /// started over — not the everyday way to switch roofs any more. Each roof pushed and imported now keeps its own import (RoofId on
    /// ImportLedger's entries, see RoofBoundaryServer): importing or syncing one roof never deletes what an earlier import of a DIFFERENT
    /// roof built, so simply picking another roof and pushing/importing it no longer needs this first. Update still clears the previous
    /// import and iterations of EVERY roof at once, then pushes everything about the roof now selected (the same as "Push to Sportify" →
    /// Everything) — useful when a roof's own import is in a state worth starting fresh from, or the model should hold only one roof's
    /// worth of Sportify content again.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class UpdateSportifyCommand : IExternalCommand
    {
        private const string Title = "Sportify — Update";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }

            int removedImport, removedIterations;
            try
            {
                using var t = new Transaction(doc, "Sportify: clear the previous import");
                t.Start();
                removedImport = ImportLedger.RemoveAll(doc);
                removedIterations = IterationLedger.RemovePrevious(doc);
                t.Commit();
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the previous import could not be cleared", ex, ref message);
            }

            if (removedImport > 0 || removedIterations > 0)
                SportifyLog.Info("update", $"cleared {removedImport} element(s) of the previous import" +
                                            (removedIterations > 0 ? $" and {removedIterations} element(s) of earlier iterations" : "") +
                                            " ahead of pushing a (possibly different) roof");

            var pushResult = PushRoofCommandBase.Run(commandData, RoofPushScope.All, ref message);

            if (pushResult == Result.Succeeded && (removedImport > 0 || removedIterations > 0))
                TaskDialog.Show(Title,
                    $"Cleared {removedImport} element(s) of the previous import" +
                    (removedIterations > 0 ? $" and {removedIterations} element(s) of earlier iterations" : "") +
                    " before pushing the roof.\n\nThe model is clean on the roof just pushed — send the layout from the Sportify app (or turn Auto Import on) to build it here.");

            return pushResult;
        }
    }
}
