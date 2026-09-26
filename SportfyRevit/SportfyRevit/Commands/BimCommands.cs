using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    // The commands of the ribbon's "BIM & Documentation" panel. Each one acts only on what the last Sportify import created in this project (SportifyElementScan: the import
    // ledger), never on the user's own elements, runs in one transaction that is rolled back if anything fails, tells the user what it did (or why it did nothing) in a
    // TaskDialog, and writes the same to the add-in's log. None of them blocks Revit on an error: every failure is caught, reported and logged.
    //
    // GenerateRevitSchedulesCommand is not called GenerateSchedulesCommand: that name already belongs to the CSV export (Schedules (CSV) in the export panel), and stays.

    /// <summary>Native Revit schedules of the equipment, the planting and the roof build-ups Sportify placed.</summary>
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    public class GenerateRevitSchedulesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Generate Schedules";
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                BimScheduleResult result;
                using (var t = new Transaction(doc, "Sportify: generate schedules"))
                {
                    t.Start();
                    result = BimScheduleBuilder.CreateSportifySchedules(doc, null);
                    if (result.Any) t.Commit(); else t.RollBack();
                }

                if (!result.Any)
                {
                    TaskDialog.Show(title, "No schedule was generated.\n\n" + string.Join("\n", result.Notes));
                    return Result.Succeeded;
                }

                var described = result.Created.Select(n => BimScheduleBuilder.Describe(doc, n)).ToList();
                SportifyLog.Info("bim", "schedules made: " + string.Join("; ", described));
                var text = "Sportify: Equipment and Roof Takeoff schedules generated successfully.\n\n" + string.Join("\n", described.Select(n => "• " + n));
                if (result.Notes.Count > 0) text += "\n\nNote:\n" + string.Join("\n", result.Notes);
                TaskDialog.Show(title, text);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "the schedules could not be generated", ex, ref message);
            }
        }
    }

    /// <summary>Colour filters (zone types, kinds of piece) on duplicates of the active view: the view itself stays as it is.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ApplyViewFiltersCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Apply View Filters";
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                ViewFilterResult result;
                using (var t = new Transaction(doc, "Sportify: apply view filters"))
                {
                    t.Start();
                    result = ViewFilterManager.ApplySportifyViewFilters(doc, doc.ActiveView);
                    if (result.OnView > 0 || result.Created > 0 || result.Updated > 0) t.Commit(); else t.RollBack();
                }

                var head = result.OnView > 0
                    ? $"Sportify view filters: {result.OnView} filter(s) put on {result.Views.Count} duplicate view(s) of \"{result.ViewName}\" ({result.Created} new filters, {result.Updated} updated):\n"
                      + string.Join("\n", result.Views.Select(v => "• " + v))
                      + $"\n\nThey are in the Project Browser next to the view; \"{result.ViewName}\" itself was not changed."
                    : $"No Sportify view filter was applied (from \"{result.ViewName}\").";
                TaskDialog.Show(title, head + (result.Notes.Count > 0 ? "\n\n" + string.Join("\n", result.Notes) : ""));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "the view filters could not be applied", ex, ref message);
            }
        }
    }

    /// <summary>Puts every Sportify element of the project into the phase the user picks (Phase Created).</summary>
    [Transaction(TransactionMode.Manual)]
    public class AssignPhasingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Batch Assign Phasing";
            try
            {
                var uidoc = commandData.Application.ActiveUIDocument;
                var doc = uidoc?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                var found = SportifyElementScan.Find(doc);
                if (found.IsEmpty) { TaskDialog.Show(title, "This project holds nothing from Sportify yet: import a layout first."); return Result.Succeeded; }

                var phases = new List<Phase>();
                foreach (Phase ph in doc.Phases) phases.Add(ph);
                if (phases.Count == 0) { TaskDialog.Show(title, "This project has no phases."); return Result.Succeeded; }

                var target = Choose(title, found, phases, doc.ActiveView);
                if (target == null) return Result.Cancelled;

                int set = 0, already = 0, without = 0, readOnly = 0;
                using (var t = new Transaction(doc, "Sportify: assign phase"))
                {
                    t.Start();
                    foreach (var el in found.Elements)
                    {
                        var p = el.get_Parameter(BuiltInParameter.PHASE_CREATED);
                        if (p == null) { without++; continue; }          // sketch planes, text notes ...: not phased
                        if (p.IsReadOnly) { readOnly++; continue; }
                        if (p.AsElementId() == target.Id) { already++; continue; }
                        if (p.Set(target.Id)) set++; else readOnly++;
                    }
                    t.Commit();
                }

                var text = $"{set} Sportify element(s) put into phase \"{target.Name}\" (Phase Created).";
                if (already > 0) text += $"\n{already} were already in it.";
                if (without > 0) text += $"\n{without} have no phase of their own (sketch planes, notes) and were left.";
                if (readOnly > 0) text += $"\n{readOnly} could not be changed (read-only or owned by someone else).";
                SportifyLog.Info("bim", $"phasing: {set} to \"{target.Name}\", {already} already, {without} unphased, {readOnly} refused");
                TaskDialog.Show(title, text);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "the phase could not be assigned", ex, ref message);
            }
        }

        /// <summary>A dialog with the project's phases as buttons (Revit's dialog takes four; the phase of the active view comes first when it is among them).</summary>
        private static Phase? Choose(string title, SportifyElementScan.Found found, List<Phase> phases, View activeView)
        {
            var viewPhase = activeView?.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId();
            var offered = phases.OrderByDescending(p => p.Id == viewPhase).Take(4).ToList();
            var dialog = new TaskDialog(title)
            {
                MainInstruction = $"Put {found.Elements.Count} Sportify element(s) into which phase?",
                MainContent = "Sets Phase Created on what " + found.Source + " made in this project." + (phases.Count > 4 ? $"\n(The project has {phases.Count} phases; the first four are offered.)" : ""),
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            for (int i = 0; i < offered.Count; i++)
                dialog.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + i), offered[i].Name);
            dialog.DefaultButton = TaskDialogResult.CommandLink1;       // after the links exist: Revit throws otherwise

            var answer = dialog.Show();
            int index = answer - TaskDialogResult.CommandLink1;
            return index >= 0 && index < offered.Count ? offered[index] : null;
        }
    }

    /// <summary>Puts every Sportify element on its own workset (Sports, Gardens, Combine), as the import does; needs a workshared project.</summary>
    [Transaction(TransactionMode.Manual)]
    public class AssignWorksetsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Organize Multi-Worksets";
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                var found = SportifyElementScan.Find(doc);
                if (found.IsEmpty) { TaskDialog.Show(title, "This project holds nothing from Sportify yet: import a layout first."); return Result.Succeeded; }

                // Worksets need worksharing, which cannot be turned off again: so it is asked, every time, and only this command's own answer counts (the import's is another question).
                if (!doc.IsWorkshared && !doc.CanEnableWorksharing())
                {
                    SportifyLog.Info("worksharing", "Organize Multi-Worksets: Revit does not allow worksharing in \"" + doc.Title + "\" (" + (string.IsNullOrEmpty(doc.PathName) ? "not saved" : doc.PathName) + ", read-only: " + doc.IsReadOnly + ")");
                    TaskDialog.Show(title, "Revit does not allow worksharing to be turned on in this document, so there are no worksets to organize.\n\n" +
                                           "This happens when the document is a template file (.rte) opened as itself, or is read-only or not a saved project. Save the project as an .rvt (File > Save As > Project), open that, and run this again.");
                    return Result.Cancelled;
                }
                if (!doc.IsWorkshared)
                {
                    var ask = new TaskDialog(title)
                    {
                        MainInstruction = "Turn worksharing on to put Sportify on worksets?",
                        MainContent = $"This project is not workshared, so it has no worksets. Worksharing makes it a central-model project (it has to be saved as one) and cannot be turned off again.\n\n" +
                                      $"The {found.Elements.Count} Sportify element(s) would go on the worksets Sports (courts, activities, furniture), Gardens (planting and ground) and Combine (roof outline, paths, entries).",
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                    };
                    ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Turn worksharing on and organize Sportify on worksets", "Cannot be undone.");
                    ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Leave the project as it is", "Nothing changes.");
                    ask.DefaultButton = TaskDialogResult.CommandLink2;       // after the links exist: Revit throws otherwise
                    if (ask.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;

                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");     // outside any transaction: Revit refuses it inside one
                    SportifyLog.Info("worksharing", "worksharing enabled by Organize Multi-Worksets, with the person's consent");
                }

                var moved = new Dictionary<string, int>();
                int already = 0, without = 0, others = 0, refused = 0;
                using (var t = new Transaction(doc, "Sportify: organize worksets"))
                {
                    t.Start();
                    var worksets = EnsureWorksets(doc);
                    foreach (var el in found.Elements)
                    {
                        var p = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
                        if (p == null) { without++; continue; }
                        var name = BimRules.WorksetFor(SportifyElementScan.KindOf(el), SportifyElementScan.CategoryOf(el), SportifyElementScan.IsPlanting(el));
                        var id = worksets[name];
                        if (p.AsInteger() == id.IntegerValue) { already++; continue; }
                        if (p.IsReadOnly) { refused++; continue; }
                        if (WorksharingUtils.GetCheckoutStatus(doc, el.Id) == CheckoutStatus.OwnedByOtherUser) { others++; continue; }
                        if (p.Set(id.IntegerValue)) moved[name] = moved.GetValueOrDefault(name) + 1; else refused++;
                    }
                    t.Commit();
                }

                int total = moved.Values.Sum();
                var text = total > 0
                    ? $"{total} Sportify element(s) moved: " + string.Join(", ", moved.OrderBy(k => k.Key).Select(k => $"{k.Value} to {k.Key}")) + "."
                    : "No Sportify element needed to move.";
                if (already > 0) text += $"\n{already} were already on the right workset.";
                if (without > 0) text += $"\n{without} have no workset of their own and were left.";
                if (others > 0) text += $"\n{others} are checked out by someone else and were left.";
                if (refused > 0) text += $"\n{refused} could not be changed.";
                SportifyLog.Info("bim", $"worksets: {total} moved ({string.Join(", ", moved.Select(k => k.Key + " " + k.Value))}), {already} already, {without} without, {others} owned by others, {refused} refused");
                TaskDialog.Show(title, text);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "the worksets could not be organized", ex, ref message);
            }
        }

        /// <summary>The three worksets by name, made when missing (inside the caller's transaction).</summary>
        private static Dictionary<string, WorksetId> EnsureWorksets(Document doc)
        {
            var existing = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Name, w => w.Id);
            var result = new Dictionary<string, WorksetId>();
            foreach (var name in BimRules.WorksetNames)
                result[name] = existing.TryGetValue(name, out var id) ? id : Workset.Create(doc, name).Id;
            return result;
        }
    }

    internal static class BimCommandErrors
    {
        /// <summary>One way to fail: the log gets the whole exception, the user a dialog with the reason, Revit the message (shown in its own error dialog for Result.Failed).</summary>
        public static Result Failed(string title, string what, Exception ex, ref string message)
        {
            SportifyLog.Error("bim", title + ": " + what, ex);
            message = what + ": " + ex.Message;
            TaskDialog.Show(title, "Sportify: " + what + ".\n\n" + ex.Message + "\n\nNothing was changed. The details are in the add-in's log (%APPDATA%\\Sportify\\logs).");
            return Result.Cancelled;      // Cancelled, not Failed: Revit would add a second, generic error dialog on top of ours
        }
    }
}
