using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// A manual safety net for DuplicateCleanup, which every import already runs on its own roof: this button runs
    /// it on demand, for a project that picked up overlapping families before that automatic pass existed (an
    /// earlier add-in version, or a ledger entry that never landed), without needing a fresh import to trigger it.
    /// Only the active roof's placements are checked — the same scope an import itself would clean.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class RemoveDuplicatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show("Sportify", "Open a Revit project first."); return Result.Cancelled; }

            var roofId = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();
            int removed;
            using (var t = new Transaction(doc, "Sportify: remove duplicates"))
            {
                t.Start();
                removed = DuplicateCleanup.RemoveForRoof(doc, roofId);
                t.Commit();
            }

            TaskDialog.Show("Sportify — Remove Duplicates",
                removed > 0
                    ? $"Removed {removed} duplicate element(s) on the active roof: the older copy of each repeated placement, keeping the newest."
                    : "No duplicates found on the active roof.");
            return Result.Succeeded;
        }
    }
}
