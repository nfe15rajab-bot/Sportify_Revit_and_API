using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "IFC Worksets by Class": puts every element an IFC import made on a workset by its IFC class, from a table the person can edit (%APPDATA%\Sportify\ifc-worksets.json, written with the defaults the first
    /// time). It says what it would do first (the worksets, how many elements on each, which have no class, which phase they are in) and only then does it, in one transaction. The roof is the
    /// selection at the time: the selected elements go on "Sportify Existing Roof Base". Nothing Sportify made is touched. The rules are IfcWorksetRules, the work IfcWorksetService.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class AssignIfcWorksetsCommand : IExternalCommand
    {
        const string Title = "Sportify — IFC Worksets by Class";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }
                if (doc.IsFamilyDocument) { TaskDialog.Show(Title, "This works on a project (.rvt), not on a family."); return Result.Cancelled; }
                if (!EnsureWorkshared(doc)) return Result.Cancelled;

                var path = IfcWorksetRules.DefaultPath;
                var wrote = IfcWorksetRules.EnsureFile(path);
                var table = IfcWorksetRules.Load(path);
                var roof = new HashSet<ElementId>(uidoc!.Selection.GetElementIds());
                var plan = IfcWorksetService.Plan(doc, table, roof);
                SportifyLog.Info("ifc-worksets", "plan: " + plan.Items.Count + " IFC element(s), " + plan.ToMove + " to move, " + plan.WithoutClass + " without a class, " + plan.SkippedSportify + " of Sportify's left alone, roof " + plan.RoofCount + (wrote ? "; the default table was written to " + path : ""));

                if (plan.Items.Count == 0)
                {
                    TaskDialog.Show(Title, "No element from an IFC import was found in this project.\n\nAn element counts as coming from the IFC when it is a DirectShape (what an IFC import makes of what Revit has no family for) or carries the parameter IfcSpatialContainer. "
                                           + (plan.SkippedSportify > 0 ? plan.SkippedSportify + " Sportify element(s) were left out on purpose." : ""));
                    return Result.Succeeded;
                }

                var phases = string.Join(", ", plan.Phases.OrderByDescending(p => p.Value).Select(p => "\"" + p.Key + "\" " + p.Value));
                var content = plan.Items.Count + " element(s) come from the IFC. By their IFC class they go on:\n" + IfcWorksetRules.Lines(plan.ByWorkset)
                              + (plan.AlreadyThere > 0 ? "\n\n" + plan.AlreadyThere + " are on their workset already, so " + plan.ToMove + " would move." : "")
                              + (plan.WithoutClass > 0 ? "\n" + plan.WithoutClass + " more have no IFC class (not even by their category) and stay where they are: " + string.Join(", ", plan.Unclassified.OrderByDescending(u => u.Value).Select(u => u.Key + " " + u.Value)) + "." : "")
                              + (plan.RoofCount > 0 ? "\nThe " + plan.RoofCount + " selected element(s) are the roof: \"" + table.Roof + "\"." : "\nNothing is selected, so no element goes to the roof workset: select the roof first if you want it on \"" + table.Roof + "\".")
                              + "\n\nPhase Created of these elements: " + phases + " (Set Up Phases & Worksets in the same drop-down names the phases)."
                              + "\nSportify's own elements (" + plan.SkippedSportify + ") are not touched."
                              + "\n\nThe table: " + path + (wrote ? " (just made, with the defaults)" : "");
                var ask = new TaskDialog(Title) { MainInstruction = plan.ToMove > 0 ? "Put " + plan.ToMove + " IFC element(s) on worksets by their class?" : "The IFC elements are on their worksets already", MainContent = content, CommonButtons = TaskDialogCommonButtons.Cancel };
                if (plan.ToMove > 0) ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Organize the worksets now", "One step in one transaction; Undo takes it back.");
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the table to edit it", "Change the workset names, then run this again.");
                ask.DefaultButton = plan.ToMove > 0 ? TaskDialogResult.CommandLink1 : TaskDialogResult.CommandLink2;      // after the links exist: Revit throws otherwise
                var answer = ask.Show();
                if (answer == TaskDialogResult.CommandLink2)
                {
                    try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex) { SportifyLog.Warn("ifc-worksets", "the table could not be opened: " + ex.Message); TaskDialog.Show(Title, "The table could not be opened here. It is the file\n" + path); }
                    return Result.Succeeded;
                }
                if (answer != TaskDialogResult.CommandLink1) return Result.Cancelled;

                var result = IfcWorksetService.Apply(doc, plan);
                var total = result.Moved.Values.Sum();
                var text = total + " IFC element(s) moved:\n" + IfcWorksetRules.Lines(result.Moved);
                if (result.Made.Count > 0) text += "\n\nWorksets made: " + string.Join(", ", result.Made) + ".";
                if (result.Already > 0) text += "\n" + result.Already + " were on their workset already.";
                if (result.OwnedByOthers > 0) text += "\n" + result.OwnedByOthers + " are checked out by someone else and were left.";
                if (result.Refused > 0) text += "\n" + result.Refused + " could not be changed.";
                SportifyLog.Info("ifc-worksets", "moved " + total + " (" + string.Join(", ", result.Moved.Select(k => k.Key + " " + k.Value)) + "), made " + string.Join(", ", result.Made) + ", " + result.Already + " already, " + result.OwnedByOthers + " owned by others, " + result.Refused + " refused");
                TaskDialog.Show(Title, text);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the worksets could not be organized", ex, ref message);
            }
        }

        /// <summary>Worksets need worksharing (which cannot be turned off again): a project that has it is fine; one that does not is asked, and one that cannot have it is told why.</summary>
        static bool EnsureWorkshared(Document doc)
        {
            if (doc.IsWorkshared) return true;
            if (!doc.CanEnableWorksharing())
            {
                TaskDialog.Show(Title, "Revit does not allow worksharing to be turned on in this document, so there are no worksets to put the IFC elements on.\n\nSave the project as an .rvt (File > Save As > Project), open that, and run this again.");
                return false;
            }
            var ask = new TaskDialog(Title)
            {
                MainInstruction = "Turn worksharing on to put the IFC elements on worksets?",
                MainContent = "This project is not workshared, so it has no worksets. Worksharing makes it a central-model project (it has to be saved as one) and cannot be turned off again.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Turn worksharing on and continue", "Cannot be undone.");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Leave the project as it is", "Nothing changes.");
            ask.DefaultButton = TaskDialogResult.CommandLink2;
            if (ask.Show() != TaskDialogResult.CommandLink1) return false;
            doc.EnableWorksharing("Shared Levels and Grids", "Workset1");     // outside any transaction: Revit refuses it inside one
            SportifyLog.Info("worksharing", "worksharing enabled by IFC Worksets by Class, with the person's consent");
            return true;
        }
    }
}
