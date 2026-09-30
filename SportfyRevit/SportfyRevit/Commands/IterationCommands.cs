using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "Design Options" for the web app's saved iterations, without the Revit API's own DesignOption/DesignOptionSet: the API has no way to create
    /// either programmatically (DesignOption exposes only GetActiveDesignOptionId, a getter — confirmed against the 2025.3 API docs), and even a
    /// Design Option Set built by hand ahead of time does not help, because DESIGN_OPTION_ID (which option an element belongs to) is read-only —
    /// there is no API call that puts a newly created element into a chosen option. Worksets stand in instead: every reference this file's own
    /// comments make to "an option" means a workset (or, in "detailed" mode, the normal Sports/Gardens/Combine three) per saved iteration, so
    /// showing exactly one iteration's worksets at a time in a view is the same on/off switch a Design Option gives, through the API surface
    /// Revit actually offers (Workset.Create, View.SetWorksetVisibility, both already used elsewhere in this add-in).
    /// </summary>
    internal static class IterationWorksets
    {
        public const string NamePrefix = "Sportify Iteration ";

        /// <summary>One iteration's worksets: just one in "simple" mode, the normal Sports/Gardens/Combine three in "detailed" mode.</summary>
        public sealed record IterationGroup(int Index, List<Workset> Worksets);

        /// <summary>
        /// What SportifyLayoutBuilder.BuildGeometry's worksetNameOverride should be for iteration `oneBasedIndex`: in detailed mode every
        /// category keeps its own workset ("Sportify Iteration 2 - Sports"); in simple mode every category maps to the very same name
        /// ("Sportify Iteration 2"), which EnsureWorksets collapses onto one shared workset for the whole iteration.
        /// </summary>
        public static Func<string, string> NameOverride(int oneBasedIndex, bool detailed) =>
            detailed ? (category => $"{NamePrefix}{oneBasedIndex} - {category}") : (_ => $"{NamePrefix}{oneBasedIndex}");

        /// <summary>
        /// Every iteration's group of worksets already in the project, in iteration order — grouped by the leading number after "Sportify
        /// Iteration " regardless of which mode created them, so "Show Iteration" works the same whichever mode "Import Iterations" last used.
        /// </summary>
        public static List<IterationGroup> Find(Document doc)
        {
            if (!doc.IsWorkshared) return new List<IterationGroup>();
            var byIndex = new Dictionary<int, List<Workset>>();
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
            {
                if (!w.Name.StartsWith(NamePrefix, StringComparison.Ordinal)) continue;
                var rest = w.Name.Substring(NamePrefix.Length);           // "2 - Sports" (detailed) or "2" (simple)
                var sep = rest.IndexOf(' ');
                var numPart = sep < 0 ? rest : rest.Substring(0, sep);
                if (!int.TryParse(numPart, out var idx)) continue;
                if (!byIndex.TryGetValue(idx, out var list)) byIndex[idx] = list = new List<Workset>();
                list.Add(w);
            }
            return byIndex.OrderBy(kv => kv.Key).Select(kv => new IterationGroup(kv.Key, kv.Value)).ToList();
        }

        /// <summary>Shows exactly one iteration's worksets in `view` and hides the rest; `keep = null` shows all of them. Call inside a transaction.</summary>
        public static void ShowOnly(View view, List<IterationGroup> groups, IterationGroup? keep)
        {
            foreach (var g in groups)
                foreach (var w in g.Worksets)
                    view.SetWorksetVisibility(w.Id, keep == null || g.Index == keep.Index ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
        }
    }

    /// <summary>
    /// The web app's own id for each iteration index (SavedIterationDto.Id), from the last "Import Iterations as Design Options" run
    /// THIS Revit session — never persisted, so a session reopened without a fresh import has none. Lets "Accept as Primary"
    /// (SwitchIterationCommand) tell the web app exactly which saved layout was accepted by id, instead of only a position that
    /// could have shifted if the user saved or evicted more since sending; the web app falls back to position when there is no id.
    /// </summary>
    internal static class IterationSourceIds
    {
        private static readonly Dictionary<int, string?> ById = new();
        public static void Set(int oneBasedIndex, string? sourceId) => ById[oneBasedIndex] = sourceId;
        public static string? Get(int oneBasedIndex) => ById.TryGetValue(oneBasedIndex, out var v) ? v : null;
    }

    /// <summary>
    /// Reads the iterations the web app last sent (POST /iterations, compareController.js's savedCompareConfigs — up to 3 saved layouts) and builds
    /// each one's complete geometry onto its own workset(s) (IterationWorksets), so they can be switched like Design Options (SwitchIterationCommand).
    /// Re-running this replaces what an earlier run of this command built (IterationLedger), independent of the normal single-layout import/ledger.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportIterationsAsOptionsCommand : IExternalCommand
    {
        const string Title = "Sportify — Import Iterations as Design Options";
        const int MaxIterations = 3;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }

            if (!RoofBoundaryServer.TryGetLatestIterations(out var json) || string.IsNullOrWhiteSpace(json))
            {
                TaskDialog.Show(Title, "No iterations sent from the web app yet.\n\n" +
                    "In Combine, use \"Save for Compare\" to save up to 3 layouts, then send them to Revit from the Results tab's Iterations panel.");
                return Result.Succeeded;
            }

            List<SavedIterationDto>? saved;
            try { saved = JsonSerializer.Deserialize<List<SavedIterationDto>>(json!); }
            catch (Exception ex) { message = "The saved iterations could not be read: " + ex.Message; return Result.Failed; }

            var iterations = (saved ?? new List<SavedIterationDto>()).Where(s => s.Payload != null).Take(MaxIterations).ToList();
            if (iterations.Count == 0)
            {
                TaskDialog.Show(Title, "The web app sent no usable iterations (each needs a saved layout). Save at least one from Combine's \"Save for Compare\" first.");
                return Result.Succeeded;
            }

            // A design option being edited: that option gets ONE iteration, as real Revit Design Options (below). The API cannot make an option or
            // move an element into one, but what it creates while an option is being edited lands in that option — so the person makes the option
            // set in Revit (Manage > Design Options), edits each option in turn and runs this once per option.
            var activeOption = DesignOption.GetActiveDesignOptionId(doc);
            if (activeOption != ElementId.InvalidElementId) return ImportIntoActiveOption(doc, iterations, activeOption, ref message);

            try
            {
                var choice = WorksharingConsent.Decide(doc, interactive: true);
                if (choice == WorksharingChoice.Cancel) return Result.Cancelled;
                if (choice is WorksharingChoice.NoWorksets)
                {
                    TaskDialog.Show(Title, "Design Options need worksets to switch between: without worksharing there is nowhere to put a second iteration that would not " +
                                           "collide with the first. Nothing was imported.");
                    return Result.Cancelled;
                }
                if (choice == WorksharingChoice.Enable)
                {
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");     // outside any transaction: Revit refuses it inside one
                    SportifyLog.Info("iterations", "worksharing enabled with the person's consent (Import Iterations as Design Options)");
                }

                // one workset per iteration, "Sportify Iteration 1 / 2 / 3", as the first version did (user, 2026-09-30: "iterations should be just like
                // the old version ... iteration 1, iteration 2, iteration 3"): no question any more. The detailed Sports/Gardens/Combine split per
                // iteration made nine worksets, and hiding or showing an iteration meant three of them.
                bool? detailed = false;

                // FamilyPreparation loads/builds each iteration's own families before the transaction: it needs its own small transactions and,
                // on a manual run, may show a template picker — same ordering LayoutImporter uses for the normal single-layout import.
                var prepared = iterations.Select(it => FamilyPreparation.Prepare(doc, it.Payload!, allowTemplateDialog: true)).ToList();

                var createdIds = new List<ElementId>();
                var built = new List<(int Index, string Name, int Pieces)>();

                using var transaction = new Transaction(doc, "Sportify: import iterations as Design Options");
                transaction.Start();
                try
                {
                    IterationLedger.RemovePrevious(doc);
                    for (var i = 0; i < iterations.Count; i++)
                    {
                        var oneBased = i + 1;
                        var summary = SportifyLayoutBuilder.BuildGeometry(doc, iterations[i].Payload!, prepared[i], useWorksets: true,
                            worksetNameOverride: IterationWorksets.NameOverride(oneBased, detailed.Value));
                        createdIds.AddRange(summary.CreatedIds);
                        built.Add((oneBased, string.IsNullOrWhiteSpace(iterations[i].Name) ? $"Iteration {oneBased}" : iterations[i].Name!, summary.PieceCount));
                        IterationSourceIds.Set(oneBased, iterations[i].Id);
                    }
                    IterationLedger.Write(doc, createdIds);

                    var groups = IterationWorksets.Find(doc);
                    var first = groups.FirstOrDefault(g => g.Index == 1);
                    if (first != null) IterationWorksets.ShowOnly(doc.ActiveView, groups, first);
                }
                catch (Exception ex)
                {
                    try { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); } catch (Exception) { /* already rolled back */ }
                    return BimCommandErrors.Failed(Title, "the iterations could not be imported", ex, ref message);
                }

                var status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    message = "Revit rolled the import back when it was committed (" + status + ").";
                    TaskDialog.Show(Title, "Sportify: " + message);
                    return Result.Cancelled;
                }

                var body = new StringBuilder();
                foreach (var b in built)
                {
                    var worksetNote = detailed.Value
                        ? $"its own Sports/Gardens/Combine worksets (\"{IterationWorksets.NamePrefix}{b.Index} - ...\")"
                        : $"its own workset (\"{IterationWorksets.NamePrefix}{b.Index}\")";
                    body.AppendLine($"• Iteration {b.Index}: \"{b.Name}\" — {b.Pieces} piece(s) on {worksetNote}.");
                }
                body.AppendLine();
                body.AppendLine("Showing Iteration 1 in the active view. Use \"Show Iteration\" (BIM & Documentation) to switch, or show them all at once from there.");
                SportifyLog.Info("iterations", $"imported {built.Count} iteration(s), {(detailed.Value ? "detailed" : "simple")} worksets: " + string.Join(", ", built.Select(b => $"#{b.Index} ({b.Pieces} pieces)")));
                TaskDialog.Show(Title, body.ToString());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the iterations could not be imported", ex, ref message);
            }
        }

        /// <summary>
        /// One saved iteration into the design option being edited: chosen by the person, the one whose name matches the option's first
        /// ("Option 1 planter" → "iteration 1 planted"). Running it again for the same option replaces what it put there before (ImportLedger,
        /// keyed to the option). Before committing it checks that the new elements really are in the option; if Revit put them in the main
        /// model instead, it rolls back and says so, so the main model never collects an iteration by accident.
        /// </summary>
        /// <summary>The ledger's Source for an iteration put into a design option: "design-option:" + the iteration's name, so the next run can tell what the option holds.</summary>
        internal const string OptionSourcePrefix = "design-option:";

        static Result ImportIntoActiveOption(Document doc, List<SavedIterationDto> iterations, ElementId optionId, ref string message)
        {
            var optionName = doc.GetElement(optionId)?.Name ?? "the design option being edited";
            var names = iterations.Select((it, i) => string.IsNullOrWhiteSpace(it.Name) ? $"Iteration {i + 1}" : it.Name!.Trim()).ToList();
            var match = RoofMatch.IterationForOption(optionName, names);

            var ask = new TaskDialog(Title)
            {
                MainInstruction = $"Which iteration goes into \"{optionName}\" (the option being edited)?",
                MainContent = "It is built inside this design option only; the main model and the other options stay as they are. " +
                              "Running this again in the same option replaces what it put there. To fill another option, make it the one being edited first " +
                              "(the Design Options bar at the bottom of the window).",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            for (var i = 0; i < names.Count; i++)
                ask.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + i), names[i],
                    $"{iterations[i].Payload!.Placements?.Count ?? 0} piece(s), {iterations[i].Payload!.Zones?.Count ?? 0} zone(s)" + (i == match ? " — matches the option's name" : ""));
            ask.DefaultButton = (TaskDialogResult)((int)TaskDialogResult.CommandLink1 + Math.Max(0, match));
            var index = ask.Show() - TaskDialogResult.CommandLink1;
            if (index < 0 || index >= iterations.Count) return Result.Cancelled;

            // Live 2026-09-29: three runs meant for three options all went into Option 1, each replacing the one before, because the option being
            // edited never changed. An option that already holds a DIFFERENT iteration is now asked about first.
            var key = RoofMatch.OptionKey(optionId.Value);
            var holds = ImportLedger.ReadEntries(doc).Where(e => e.RoofKey == key).Select(e => e.Source).FirstOrDefault(s => s.StartsWith(OptionSourcePrefix, StringComparison.Ordinal));
            var heldName = holds?.Substring(OptionSourcePrefix.Length);
            if (!string.IsNullOrEmpty(heldName) && heldName != names[index])
            {
                var replace = new TaskDialog(Title)
                {
                    MainInstruction = $"\"{optionName}\" already holds \"{heldName}\". Replace it with \"{names[index]}\"?",
                    MainContent = "To fill ANOTHER option instead, first make that option the one being edited: the Design Options bar at the bottom of the Revit window " +
                                  "(or Manage > Design Options > Edit Selected). Choosing an option in a view's Visibility/Graphics only changes what that view shows.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (replace.Show() != TaskDialogResult.Yes) return Result.Cancelled;
            }

            var choice = WorksharingConsent.Decide(doc, interactive: true);
            if (choice == WorksharingChoice.Cancel) return Result.Cancelled;
            if (choice == WorksharingChoice.Enable) doc.EnableWorksharing("Shared Levels and Grids", "Workset1");

            var payload = iterations[index].Payload!;
            var prepared = FamilyPreparation.Prepare(doc, payload, allowTemplateDialog: true);

            using var t = new Transaction(doc, $"Sportify: {names[index]} into {optionName}");
            t.Start();
            int replaced, built, inOption;
            try
            {
                replaced = ImportLedger.RemovePrevious(doc, key);
                var summary = SportifyLayoutBuilder.BuildGeometry(doc, payload, prepared, useWorksets: choice != WorksharingChoice.NoWorksets);
                var model = summary.CreatedIds.Distinct().Select(id => doc.GetElement(id)).Where(e => e != null && !e.ViewSpecific && e.Category != null).ToList();
                built = model.Count;
                inOption = model.Count(e => e.DesignOption?.Id == optionId);
                if (built > 0 && inOption * 2 < built)
                {
                    t.RollBack();
                    SportifyLog.Warn("iterations", $"design option \"{optionName}\": only {inOption} of {built} new element(s) landed in it; rolled back");
                    TaskDialog.Show(Title, $"Revit put the new elements in the main model, not in \"{optionName}\" ({inOption} of {built} in the option), so nothing was imported.\n\n" +
                                           "Make sure the option is being edited (Design Options toolbar at the bottom of the window, or Manage > Design Options > Edit Selected), then run this again.");
                    return Result.Cancelled;
                }
                ImportLedger.Write(doc, summary.CreatedIds, OptionSourcePrefix + names[index], key);
            }
            catch (Exception ex)
            {
                try { if (t.GetStatus() == TransactionStatus.Started) t.RollBack(); } catch (Exception) { }
                return BimCommandErrors.Failed(Title, "the iteration could not be imported into the design option", ex, ref message);
            }
            if (t.Commit() != TransactionStatus.Committed)
            {
                message = "Revit rolled the import back when it was committed.";
                return Result.Failed;
            }

            IterationSourceIds.Set(index + 1, iterations[index].Id);
            SportifyLog.Info("iterations", $"\"{names[index]}\" imported into design option \"{optionName}\": {built} element(s), {inOption} in the option" +
                                           (replaced > 0 ? $", {replaced} of its earlier import replaced" : ""));
            TaskDialog.Show(Title, $"\"{names[index]}\" is in \"{optionName}\": {inOption} element(s)" + (replaced > 0 ? $" (replacing the {replaced} it had before)" : "") + ".\n\n" +
                                   "Next: edit the next option in the Design Options toolbar and run this again. Finish with the toolbar back on the Main Model; " +
                                   "a view shows one option through Visibility/Graphics > Design Options.");
            return Result.Succeeded;
        }

    }

    /// <summary>Switches which iteration's worksets are visible in the active view — the "Design Option switcher": instant, no rebuild, works as often as wanted.</summary>
    [Transaction(TransactionMode.Manual)]
    public class SwitchIterationCommand : IExternalCommand
    {
        const string Title = "Sportify — Show Iteration";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }

            var groups = IterationWorksets.Find(doc);
            if (groups.Count == 0)
            {
                TaskDialog.Show(Title, "No iteration worksets in this project yet — run \"Import Iterations as Design Options\" first.");
                return Result.Succeeded;
            }

            var ask = new TaskDialog(Title)
            {
                MainInstruction = $"Show which iteration in \"{doc.ActiveView.Name}\"?",
                MainContent = $"{groups.Count} iteration(s) are imported. Showing one hides the others in this view only — other views keep their own choice.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            for (var i = 0; i < groups.Count && i < 4; i++)
                ask.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + i), $"Show Iteration {groups[i].Index}");
            if (groups.Count <= 3)
                ask.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + groups.Count), "Show all iterations");
            ask.DefaultButton = TaskDialogResult.CommandLink1;      // after the links exist: Revit throws otherwise

            var answer = ask.Show();
            var index = answer - TaskDialogResult.CommandLink1;
            if (index < 0) return Result.Cancelled;

            try
            {
                using var t = new Transaction(doc, "Sportify: show iteration");
                t.Start();
                var keep = index < groups.Count ? groups[index] : null;
                IterationWorksets.ShowOnly(doc.ActiveView, groups, keep);
                t.Commit();

                if (keep == null)
                {
                    TaskDialog.Show(Title, $"Showing all iterations in \"{doc.ActiveView.Name}\".");
                    return Result.Succeeded;
                }

                // A second, separate ask rather than a fifth command-link on the dialog above (TaskDialog allows at
                // most four): accepting is a bigger decision than just looking, and it reaches into the web app's own
                // state (archiving the others there), so it gets its own explicit Yes/No rather than riding along.
                var acceptAsk = new TaskDialog(Title)
                {
                    MainInstruction = $"Showing Iteration {keep.Index} in \"{doc.ActiveView.Name}\". Accept it as the primary layout?",
                    MainContent = "The web app will archive the other saved iterations in the Compare tab so only this one stays active there. Nothing else in this Revit project changes.",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (acceptAsk.Show() == TaskDialogResult.Yes)
                {
                    RoofBoundaryServer.SetAcceptedPrimary(keep.Index, IterationSourceIds.Get(keep.Index));
                    SportifyLog.Info("iterations", $"Iteration {keep.Index} accepted as primary — the web app will archive the others next time it checks in.");
                    TaskDialog.Show(Title, $"Iteration {keep.Index} accepted as primary. The web app archives the others the next time it checks in (within a few seconds if it's open).");
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the view could not be switched", ex, ref message);
            }
        }
    }
}
