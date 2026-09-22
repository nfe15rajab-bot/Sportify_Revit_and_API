using System.Text;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// "Design Options" for the web app's saved iterations, without the Revit API's own DesignOption/DesignOptionSet: the API has no way to create
    /// either programmatically (DesignOption exposes only GetActiveDesignOptionId, a getter — confirmed against the 2025.3 API docs; the only
    /// supported route is Revit's own Manage tab, driven by hand). Worksets stand in for it instead: every reference this file's own comments make
    /// to "an option" means one workset, one per saved iteration, each holding that iteration's complete geometry (its own courts, garden, roof
    /// outline, circulation and entries) — so showing exactly one at a time in a view is the same on/off switch a Design Option gives, through the
    /// API surface Revit actually offers (Workset.Create, View.SetWorksetVisibility, both already used elsewhere in this add-in).
    /// </summary>
    internal static class IterationWorksets
    {
        public const string NamePrefix = "Sportify Iteration ";

        public static string NameFor(int oneBasedIndex) => NamePrefix + oneBasedIndex;

        /// <summary>Every "Sportify Iteration N" workset already in the project, in iteration order.</summary>
        public static List<Workset> Find(Document doc)
        {
            if (!doc.IsWorkshared) return new List<Workset>();
            return new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .Where(w => w.Name.StartsWith(NamePrefix, StringComparison.Ordinal))
                .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Shows exactly one iteration's workset in `view` and hides the rest; `keep = null` shows all of them. Call inside a transaction.</summary>
        public static void ShowOnly(View view, List<Workset> iterationWorksets, Workset? keep)
        {
            foreach (var w in iterationWorksets)
                view.SetWorksetVisibility(w.Id, keep == null || w.Id == keep.Id ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
        }
    }

    /// <summary>
    /// Reads the iterations the web app last sent (POST /iterations, compareController.js's savedCompareConfigs — up to 3 saved layouts) and builds
    /// each one's complete geometry onto its own workset (IterationWorksets), so they can be switched like Design Options (SwitchIterationCommand).
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
                    "In Combine, use \"Save for Compare\" to save up to 3 layouts, then send them to Revit from the Analysis tab's Iterations panel.");
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

                // FamilyPreparation loads/builds each iteration's own families before the transaction: it needs its own small transactions and,
                // on a manual run, may show a template picker — same ordering LayoutImporter uses for the normal single-layout import.
                var prepared = iterations.Select(it => FamilyPreparation.Prepare(doc, it.Payload!, allowTemplateDialog: true)).ToList();

                var createdIds = new List<ElementId>();
                var built = new List<(int Index, string Name, string Workset, int Pieces)>();

                using var transaction = new Transaction(doc, "Sportify: import iterations as Design Options");
                transaction.Start();
                try
                {
                    IterationLedger.RemovePrevious(doc);
                    for (var i = 0; i < iterations.Count; i++)
                    {
                        var worksetName = IterationWorksets.NameFor(i + 1);
                        var summary = SportifyLayoutBuilder.BuildGeometry(doc, iterations[i].Payload!, prepared[i], useWorksets: true, singleWorksetName: worksetName);
                        createdIds.AddRange(summary.CreatedIds);
                        built.Add((i + 1, string.IsNullOrWhiteSpace(iterations[i].Name) ? $"Iteration {i + 1}" : iterations[i].Name!, worksetName, summary.PieceCount));
                    }
                    IterationLedger.Write(doc, createdIds);

                    var worksets = IterationWorksets.Find(doc);
                    var first = worksets.FirstOrDefault(w => w.Name == IterationWorksets.NameFor(1));
                    if (first != null) IterationWorksets.ShowOnly(doc.ActiveView, worksets, first);
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
                foreach (var b in built) body.AppendLine($"• Iteration {b.Index}: \"{b.Name}\" — {b.Pieces} piece(s) on workset \"{b.Workset}\".");
                body.AppendLine();
                body.AppendLine($"Showing Iteration 1 in the active view. Use \"Show Iteration\" (BIM & Documentation) to switch, or show them all at once from there.");
                SportifyLog.Info("iterations", $"imported {built.Count} iteration(s): " + string.Join(", ", built.Select(b => $"{b.Workset} ({b.Pieces} pieces)")));
                TaskDialog.Show(Title, body.ToString());
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the iterations could not be imported", ex, ref message);
            }
        }
    }

    /// <summary>Switches which iteration's workset is visible in the active view — the "Design Option switcher": instant, no rebuild, works as often as wanted.</summary>
    [Transaction(TransactionMode.Manual)]
    public class SwitchIterationCommand : IExternalCommand
    {
        const string Title = "Sportify — Show Iteration";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }

            var worksets = IterationWorksets.Find(doc);
            if (worksets.Count == 0)
            {
                TaskDialog.Show(Title, "No iteration worksets in this project yet — run \"Import Iterations as Design Options\" first.");
                return Result.Succeeded;
            }

            var ask = new TaskDialog(Title)
            {
                MainInstruction = $"Show which iteration in \"{doc.ActiveView.Name}\"?",
                MainContent = $"{worksets.Count} iteration(s) are imported. Showing one hides the others in this view only — other views keep their own choice.",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            for (var i = 0; i < worksets.Count && i < 4; i++)
                ask.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + i), worksets[i].Name);
            if (worksets.Count <= 3)
                ask.AddCommandLink((TaskDialogCommandLinkId)((int)TaskDialogCommandLinkId.CommandLink1 + worksets.Count), "Show all iterations");
            ask.DefaultButton = TaskDialogResult.CommandLink1;      // after the links exist: Revit throws otherwise

            var answer = ask.Show();
            var index = answer - TaskDialogResult.CommandLink1;
            if (index < 0) return Result.Cancelled;

            try
            {
                using var t = new Transaction(doc, "Sportify: show iteration");
                t.Start();
                var keep = index < worksets.Count ? worksets[index] : null;
                IterationWorksets.ShowOnly(doc.ActiveView, worksets, keep);
                t.Commit();

                TaskDialog.Show(Title, keep != null ? $"Showing \"{keep.Name}\" in \"{doc.ActiveView.Name}\"." : $"Showing all iterations in \"{doc.ActiveView.Name}\".");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the view could not be switched", ex, ref message);
            }
        }
    }
}
