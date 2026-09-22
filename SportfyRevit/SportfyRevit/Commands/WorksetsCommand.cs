using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "Worksets" in BIM &amp; Documentation: sorts the MODEL onto the Sportify worksets that mirror the "Push to Sportify" drop-down (PushWorksets): Sportify Roof, Structure, Entries, Openings,
    /// Edge, Drains, Equipment, Slab. Each existing element that the push reads (roofs and floors, grids, structural columns and beams, bearing walls, stairs, ramps, doors, lifts, shafts,
    /// roof windows, railings, parapets, drains, equipment) is recognised by its category and family names and goes to the workset of its drop-down item, so a model sorted this way can be
    /// pushed by workset (the push offers "By workset") and the configurator reads what is meant.
    ///
    /// It is optional, and it asks: the checkbox is the toggle that activates worksets (turns worksharing on, which Revit cannot turn off again); an element in a default workset is assigned,
    /// an element in another workset than its kind belongs in is FLAGGED (listed, and selected in the model) and only moved when the designer chooses that. Sportify's own elements are not
    /// touched (they have Sports, Gardens and Combine, see "Organize Multi-Worksets").
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class WorksetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Worksets";
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (uidoc == null || doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                // what the model holds, before anything is asked: by kind, and (when worksets are on) where it sits now
                var preview = PushWorksetAssigner.Sort(doc, apply: false, moveWrong: false);
                var kinds = preview.Plan.Where(d => d.Verdict != PushWorksets.Verdict.NotSportify).GroupBy(d => PushWorksets.WorksetFor(d.Item.Kind)!).OrderBy(g => g.Key).ToList();
                if (kinds.Count == 0)
                {
                    TaskDialog.Show(title, "Nothing here for the Sportify worksets: no roof, floor, grid, column, beam, bearing wall, stair, ramp, door, lift, shaft, railing, drain or equipment was found (Sportify's own elements are not sorted here).");
                    return Result.Succeeded;
                }

                var toMove = preview.Plan.Count(d => d.Verdict == PushWorksets.Verdict.Assign);
                var wrong = preview.Plan.Where(d => d.Verdict == PushWorksets.Verdict.Wrong).ToList();
                var lines = string.Join("\n", kinds.Select(g => $"• {g.Count()} for {g.Key}"));
                var state = doc.IsWorkshared
                    ? $"{toMove} element(s) are in a default workset and would move; {preview.Right} are on the right workset already" + (wrong.Count > 0 ? $"; {wrong.Count} are on another workset than their kind belongs on (they would be flagged)." : ".")
                    : "Worksets are off in this project, so none of this has a workset yet.";

                if (!doc.IsWorkshared && !doc.CanEnableWorksharing())
                {
                    TaskDialog.Show(title, "Worksets are off in this project and Revit does not allow them here (a template file, a read-only document or a project that is not saved). What would be sorted:\n" + lines +
                                           "\n\nSave the project as an .rvt and run this again.");
                    return Result.Cancelled;
                }

                var dialog = new TaskDialog(title)
                {
                    MainInstruction = "Sort this model onto the Sportify worksets?",
                    MainContent = "The worksets mirror the Push to Sportify drop-down, so the model can be pushed by workset:\n" + lines + "\n\n" + state +
                                  "\n\nSportify's own elements are not touched. Elements on your own worksets are flagged, not moved, unless you choose that.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                };
                if (!doc.IsWorkshared) dialog.ExtraCheckBoxText = "Activate worksets first (turns worksharing on; Revit cannot turn it off again)";
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Sort onto the worksets and flag the wrong ones", "Elements in a default workset move; elements on another workset are listed and selected, not moved.");
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Sort and move the wrong ones too", "Every element goes to the workset of its kind, wherever it is now.");
                dialog.DefaultButton = TaskDialogResult.CommandLink1;       // after the links exist: Revit throws otherwise
                var answer = dialog.Show();
                if (answer != TaskDialogResult.CommandLink1 && answer != TaskDialogResult.CommandLink2) return Result.Cancelled;
                var moveWrong = answer == TaskDialogResult.CommandLink2;

                if (!doc.IsWorkshared)
                {
                    if (!dialog.WasExtraCheckBoxChecked())
                    {
                        TaskDialog.Show(title, "Worksets are off, so nothing was sorted. Run this again with \"Activate worksets first\" ticked to turn them on.");
                        return Result.Cancelled;
                    }
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");     // outside any transaction: Revit refuses it inside one
                    SportifyLog.Info("worksharing", "worksharing enabled by Worksets, with the person's consent (the activate box)");
                }

                PushWorksetAssigner.SortReport report;
                using (var t = new Transaction(doc, "Sportify: sort onto worksets"))
                {
                    t.Start();
                    report = PushWorksetAssigner.Sort(doc, apply: true, moveWrong);
                    t.Commit();
                }

                var flagged = report.Plan.Where(d => d.Verdict == PushWorksets.Verdict.Wrong && !moveWrong).ToList();
                int moved = report.Moved.Values.Sum();
                var text = moved > 0
                    ? $"{moved} element(s) moved: " + string.Join(", ", report.Moved.OrderBy(k => k.Key).Select(k => $"{k.Value} to {k.Key}")) + "."
                    : "No element needed to move.";
                if (report.Created.Count > 0) text += "\nWorksets made: " + string.Join(", ", report.Created) + ".";
                if (report.Right > 0) text += $"\n{report.Right} were on the right workset already.";
                if (report.OwnedByOthers > 0) text += $"\n{report.OwnedByOthers} are checked out by someone else and were left.";
                if (report.Refused > 0) text += $"\n{report.Refused} could not be changed.";
                if (flagged.Count > 0)
                {
                    text += $"\n\nFLAGGED: {flagged.Count} element(s) are on a different workset than their kind belongs on (they are selected in the model):\n" +
                            string.Join("\n", flagged.Take(8).Select(d => "• " + PushWorksets.Describe(d))) + (flagged.Count > 8 ? $"\n… and {flagged.Count - 8} more" : "") +
                            "\nRun this again and choose \"Sort and move the wrong ones too\" to move them.";
                    uidoc.Selection.SetElementIds(flagged.Select(d => new ElementId(d.Item.Id)).ToList());
                }
                SportifyLog.Info("worksets", $"sorted: {moved} moved ({string.Join(", ", report.Moved.Select(k => k.Key + " " + k.Value))}), {report.Right} right, {flagged.Count} flagged, {report.Created.Count} worksets made, {report.OwnedByOthers} owned by others, {report.Refused} refused");
                foreach (var d in flagged.Take(20)) SportifyLog.Info("worksets", "flagged: " + PushWorksets.Describe(d));
                TaskDialog.Show(title, text);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "the model could not be sorted onto the Sportify worksets", ex, ref message);
            }
        }
    }
}
