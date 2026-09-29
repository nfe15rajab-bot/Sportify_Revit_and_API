using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace SportfyRevit
{
    /// <summary>
    /// Cleans one roof on demand — what every import now also does on its own roof (ImportLedger.RemoveOnRoof, DuplicateCleanup), for a project
    /// that already carries several imports on top of each other. The roof: the one pushed last, if it is a building roof; else the selection, or
    /// a pick (a click on one of Sportify's own floors is taken as the roof under it). Two ways, asked each time:
    ///   Keep the newest — every earlier import on this roof goes (whatever id it was recorded under), with untracked leftovers and repeated pieces.
    ///   Clear this roof — everything Sportify put on it goes, the newest import too: before importing the iterations into design options, say.
    /// Other roofs, the contents of design options and what "Import Iterations" put on its worksets are never touched.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RemoveDuplicatesCommand : IExternalCommand
    {
        const string Title = "Sportify — Remove Duplicates";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null) { TaskDialog.Show("Sportify", "Open a Revit project first."); return Result.Cancelled; }
            if (DesignOption.GetActiveDesignOptionId(doc) != ElementId.InvalidElementId)
            {
                TaskDialog.Show(Title, "A design option is being edited. Switch the Design Options toolbar back to the Main Model first: this cleans the main model's Sportify content of a roof.");
                return Result.Cancelled;
            }

            var roof = RoofIdentity.RoofOfKey(doc, RoofBoundaryServer.ActiveRoofId?.ToString());
            if (roof == null)
                foreach (var id in uidoc!.Selection.GetElementIds())
                    if (doc.GetElement(id) is Element sel && new RoofOrFloorSelectionFilter().AllowElement(sel)) { roof = RoofIdentity.Resolve(doc, sel, out _); break; }
            if (roof == null)
            {
                try
                {
                    var picked = doc.GetElement(uidoc!.Selection.PickObject(ObjectType.Element, new RoofOrFloorSelectionFilter(), "Select the roof to clean"));
                    roof = RoofIdentity.Resolve(doc, picked, out _);
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            }
            var roofKey = roof.Id.Value.ToString();

            var ask = new TaskDialog(Title)
            {
                MainInstruction = $"Clean \"{roof.Name}\" (id {roofKey})?",
                MainContent = "Only this roof, only the main model: other roofs, design options and the worksets of \"Import Iterations\" keep what they have.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Keep the newest import",
                "Removes every earlier import on this roof (whatever roof id it was recorded under), untracked Sportify leftovers, and repeated copies of a piece.");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Clear this roof",
                "Removes everything Sportify put on this roof, the newest import too — for example before importing the iterations into design options.");
            ask.DefaultButton = TaskDialogResult.CommandLink1;
            var answer = ask.Show();
            if (answer != TaskDialogResult.CommandLink1 && answer != TaskDialogResult.CommandLink2) return Result.Cancelled;
            var clearAll = answer == TaskDialogResult.CommandLink2;

            int removed, duplicates = 0;
            try
            {
                using var t = new Transaction(doc, clearAll ? "Sportify: clear the roof" : "Sportify: remove duplicates");
                t.Start();
                removed = ImportLedger.RemoveOnRoof(doc, roofKey, roof, leftovers: true, keepNewest: !clearAll);
                if (!clearAll) duplicates = DuplicateCleanup.RemoveForRoof(doc, roofKey, roof);
                t.Commit();
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the roof could not be cleaned", ex, ref message);
            }

            SportifyLog.Info("duplicates", $"roof {roofKey}: {(clearAll ? "cleared" : "kept the newest import")}, {removed} element(s) of earlier imports/leftovers and {duplicates} repeated piece(s) removed");
            TaskDialog.Show(Title,
                removed + duplicates == 0
                    ? $"Nothing to remove on \"{roof.Name}\": {(clearAll ? "Sportify has nothing on it in the main model." : "only one import stands on it and no piece is repeated.")}"
                    : clearAll
                        ? $"Cleared \"{roof.Name}\": {removed} Sportify element(s) removed from the main model."
                        : $"Removed {removed} element(s) of earlier imports and leftovers" + (duplicates > 0 ? $" and {duplicates} repeated piece(s)" : "") +
                          $" on \"{roof.Name}\"; the newest import stays.");
            return Result.Succeeded;
        }
    }
}
