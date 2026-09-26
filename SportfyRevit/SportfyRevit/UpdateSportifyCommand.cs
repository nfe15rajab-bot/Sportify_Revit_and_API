using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "Update": for switching to a different roof mid-session. A plain "Push to Sportify" push already replaces what an
    /// earlier push of a DIFFERENT roof brought (RoofPushMerge) — but the elements an earlier IMPORT built in this
    /// project stay on screen until the next import replaces them, which needs a fresh layout sent back from the web
    /// app first. That gap is confusing: pieces from the old roof, still on screen, overlapping whatever gets configured
    /// next. Update closes it in one click: clear this project's previous import and iterations right away, then push
    /// everything about the roof now selected (the same as "Push to Sportify" → Everything) — the model is clean on the
    /// new roof immediately, without waiting on the web app's own re-send.
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
                removedImport = ImportLedger.RemovePrevious(doc);
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
