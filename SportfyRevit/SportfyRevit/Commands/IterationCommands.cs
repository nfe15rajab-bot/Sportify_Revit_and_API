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

                var detailed = AskOrganization();
                if (detailed == null) return Result.Cancelled;

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

        /// <summary>True = detailed (Sports/Gardens/Combine per iteration), false = simple (one workset per iteration), null = cancelled.</summary>
        static bool? AskOrganization()
        {
            var ask = new TaskDialog(Title)
            {
                MainInstruction = "Organize each iteration's worksets how?",
                MainContent = "Every iteration gets its own workset(s) either way, switchable together with \"Show Iteration\".",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Detailed — Sports, Gardens, Combine per iteration",
                "The same auto-assignment a normal import uses (courts and equipment on Sports, planting and ground on Gardens, boundaries/paths/entries on Combine), just one set of the three per iteration.");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Simple — one workset per iteration",
                "Everything for an iteration together on a single workset. Fewer worksets, less detail.");
            ask.DefaultButton = TaskDialogResult.CommandLink1;      // after the links exist: Revit throws otherwise
            return ask.Show() switch
            {
                TaskDialogResult.CommandLink1 => true,
                TaskDialogResult.CommandLink2 => false,
                _ => (bool?)null,
            };
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

                TaskDialog.Show(Title, keep != null ? $"Showing Iteration {keep.Index} in \"{doc.ActiveView.Name}\"." : $"Showing all iterations in \"{doc.ActiveView.Name}\".");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the view could not be switched", ex, ref message);
            }
        }
    }
}
