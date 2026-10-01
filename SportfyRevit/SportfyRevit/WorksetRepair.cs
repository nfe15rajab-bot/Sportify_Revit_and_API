using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Every workset visible again (user, 2026-09-30: "make sure all the worksets are visible"; then, the same evening, a session that ended half way left the
    /// team's model with the worksets turned off and the axonometry of Iteration 1 showing the bare building). What it puts back, in one transaction:
    ///   - every workset visible by default, except the "Sportify Iteration …" ones (Import Iterations keeps only Iteration 1 visible by default, so three
    ///     never stand on top of each other);
    ///   - in every view and view template, no workset left Hidden, again except the iterations';
    ///   - a view named for an iteration ("… iteration 2", the team's own "… interation 1") shows that iteration and only that one.
    /// A workset that is CLOSED (Manage > Worksets, Opened: No) cannot be opened from the Revit API in a project that is open: those are named, with where to open them.
    /// Used by the ribbon's Show All Worksets and by the submission tidy-up's "worksets" step (SubmissionFix).
    /// </summary>
    internal static class WorksetRepair
    {
        static readonly Regex IterationInName = new(@"\bi?n?teration\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The iteration a view is named for ("S2 Achsonometrie interation 1" = 1), or null.</summary>
        internal static int? IterationOfViewName(string name)
        {
            var m = IterationInName.Match(name);
            return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : null;
        }

        /// <summary>
        /// What each iteration view really shows: the Sportify elements Revit draws in it (the view's own element collector: it knows the worksets, the phase filter and what is
        /// hidden), by workset, and how each Sportify workset stands in the view. For comparing two versions of a model ("make sure it matches the previous version in terms of
        /// iterations") and for the report of the step "iterviews". Read only.
        /// </summary>
        internal static void DiagnoseIterationViews(Document doc, List<string> lines, string label)
        {
            if (!doc.IsWorkshared) return;
            var names = new Dictionary<int, string>();
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)) names[w.Id.IntegerValue] = w.Name;
            var defaults = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc);
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Where(w => w.Name.StartsWith("Sportify", StringComparison.Ordinal)))
                lines.Add($"{label} workset \"{w.Name}\": visible by default {defaults.IsWorksetVisible(w.Id)}");
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule).OrderBy(v => v.Name))
            {
                if (IterationOfViewName(v.Name) is null) continue;
                var shown = new Dictionary<string, int>();
                try
                {
                    foreach (var e in new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType())
                    {
                        if (!doc.IsWorkshared || !names.TryGetValue(e.WorksetId.IntegerValue, out var wn) || !wn.StartsWith("Sportify", StringComparison.Ordinal)) continue;
                        if (e.Category == null || e.Category.CategoryType != CategoryType.Model) continue;
                        shown[wn] = shown.GetValueOrDefault(wn) + 1;
                    }
                }
                catch (Exception ex) { lines.Add($"{label} view \"{v.Name}\": could not be read ({ex.Message})"); continue; }
                var states = string.Join(", ", names.Where(kv => kv.Value is "Sportify Gardens" or "Sportify Annotations and Tags" || kv.Value.StartsWith(IterationWorksets.NamePrefix, StringComparison.Ordinal))
                    .OrderBy(kv => kv.Value).Select(kv => kv.Value.Replace("Sportify ", "") + "=" + v.GetWorksetVisibility(new WorksetId(kv.Key))));
                lines.Add($"{label} {v.ViewType} \"{v.Name}\" shows: {string.Join("; ", shown.OrderBy(kv => kv.Key).Select(kv => kv.Value + " on " + kv.Key.Replace("Sportify ", "")))}   [{states}]");
            }
        }

        /// <summary>
        /// The submission step "iterviews": every view named for an iteration shows that iteration and not the main model's own copy (Sportify Gardens, Sportify Annotations and Tags),
        /// and the Annotations workset is hidden by default again, as it was before "Show All Worksets" made it visible for every view. Opens its own transaction.
        /// </summary>
        internal static void IterationViews(Document doc, List<string> lines)
        {
            if (!doc.IsWorkshared) { lines.Add("iteration views: the project is not workshared"); return; }
            using var t = new Transaction(doc, "Sportify: iteration views show their iteration");
            t.Start();
            var defaults = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc);
            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Where(w => w.Name == "Sportify Annotations and Tags"))
                if (defaults.IsWorksetVisible(w.Id)) { defaults.SetWorksetVisibility(w.Id, false); lines.Add("iteration views: \"" + w.Name + "\" hidden by default again"); }
            int fixedViews = 0, refused = 0;
            var groups = IterationWorksets.Find(doc);
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule))
            {
                if (IterationOfViewName(v.Name) is not int n) continue;
                try
                {
                    // each view shows ITS iteration (the others hidden) and not the main model's own copy: the views of Iteration 1, 2 and 3 are three different pictures
                    if (groups.FirstOrDefault(g => g.Index == n) is IterationWorksets.IterationGroup mine) IterationWorksets.ShowOnly(v, groups, mine);
                    else IterationWorksets.HideMainCopy(v);
                    fixedViews++;
                }
                catch (Exception ex) { refused++; lines.Add($"iteration views: \"{v.Name}\" could not be set: {ex.Message}"); }
            }
            t.Commit();
            lines.Add($"iteration views: {fixedViews} view(s) set to show their own iteration and not the main model's copy" + (refused > 0 ? $"; {refused} refused" : ""));
        }

        /// <summary>Opens its own transaction. `lines`: what it did, one line each, for the report or the dialog.</summary>
        internal static void ShowAll(Document doc, List<string> lines)
        {
            if (!doc.IsWorkshared) { lines.Add("worksets: the project is not workshared"); return; }
            using var t = new Transaction(doc, "Sportify: every workset visible");
            t.Start();
            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();
            bool IsIteration(Workset w) => w.Name.StartsWith(IterationWorksets.NamePrefix, StringComparison.Ordinal);
            var defaults = WorksetDefaultVisibilitySettings.GetWorksetDefaultVisibilitySettings(doc);
            var madeDefault = new List<string>();
            foreach (var w in worksets.Where(w => !IsIteration(w) && !defaults.IsWorksetVisible(w.Id)))
            {
                defaults.SetWorksetVisibility(w.Id, true);
                madeDefault.Add(w.Name);
            }

            var groups = IterationWorksets.Find(doc);
            int views = 0, settings = 0, refused = 0, iterationViews = 0;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v is ViewSheet || v is ViewSchedule || v.ViewType == ViewType.Legend || v.ViewType == ViewType.DrawingSheet) continue;
                bool changed = false;
                foreach (var w in worksets.Where(w => !IsIteration(w)))
                {
                    try
                    {
                        if (v.GetWorksetVisibility(w.Id) != WorksetVisibility.Hidden) continue;
                        v.SetWorksetVisibility(w.Id, WorksetVisibility.UseGlobalSetting);
                        settings++; changed = true;
                    }
                    catch (Exception) { refused++; }
                }
                if (changed) { views++; lines.Add($"worksets: \"{v.Name}\"{(v.IsTemplate ? " (view template)" : "")} showed hidden worksets again"); }

                // a view named for an iteration shows that one. Not through a view template: one template is often on the views of all three
                // iterations, and would be set to each in turn; a view whose template controls its worksets is named instead.
                if (v.IsTemplate || IterationOfViewName(v.Name) is not int n || groups.FirstOrDefault(g => g.Index == n) is not IterationWorksets.IterationGroup mine) continue;
                // GetTemplateParameterIds is everything a template CAN control; what it leaves to the view is GetNonControlledTemplateParameterIds (found
                // 2026-09-30 on the team's model: every iteration view was reported as template-controlled while each showed its own iteration)
                var worksetsParam = new ElementId(BuiltInParameter.VIS_GRAPHICS_WORKSETS);
                if (v.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(v.ViewTemplateId) is View template
                    && template.GetTemplateParameterIds().Contains(worksetsParam) && !template.GetNonControlledTemplateParameterIds().Contains(worksetsParam))
                {
                    lines.Add($"worksets: \"{v.Name}\" is named for Iteration {n}, but its template \"{template.Name}\" decides its worksets: left as the template has them");
                    try { if (IterationWorksets.HideMainCopy(v)) lines.Add($"worksets: \"{v.Name}\": the main model's own copy hidden"); } catch (Exception) { refused++; }
                    continue;
                }
                try
                {
                    bool Right(IterationWorksets.IterationGroup g) => g.Worksets.All(w => v.GetWorksetVisibility(w.Id) == (g.Index == n ? WorksetVisibility.Visible : WorksetVisibility.Hidden));
                    if (groups.All(Right)) continue;
                    IterationWorksets.ShowOnly(v, groups, mine);
                    iterationViews++;
                    lines.Add($"worksets: \"{v.Name}\" shows Iteration {n} again");
                }
                catch (Exception ex) { refused++; lines.Add($"worksets: \"{v.Name}\" could not be set to Iteration {n}: {ex.Message}"); }
            }
            t.Commit();

            lines.Add($"worksets: {madeDefault.Count} made visible by default{(madeDefault.Count > 0 ? " (" + string.Join(", ", madeDefault) + ")" : "")}; {settings} hidden setting(s) cleared in {views} view(s)/template(s)"
                      + (iterationViews > 0 ? $"; {iterationViews} iteration view(s) set to their iteration" : "")
                      + (refused > 0 ? $"; {refused} could not be changed (a view template controls them there)" : "") + "; the Sportify Iteration worksets kept as each view has them");
            var closed = worksets.Where(w => !w.IsOpen).Select(w => w.Name).ToList();
            if (closed.Count > 0)
                lines.Add($"worksets CLOSED ({closed.Count}): {string.Join(", ", closed)}. Their elements are not loaded, so no view can show them: open them in Manage > Worksets (select them, Open), " +
                          "or close the project and open it again with Worksets: All.");
        }
    }

    /// <summary>The ribbon's Show All Worksets (Phasing &amp; Worksets): WorksetRepair on the open project, and what it did.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ShowAllWorksetsCommand : IExternalCommand
    {
        const string Title = "Sportify — Show All Worksets";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }
            if (!doc.IsWorkshared) { TaskDialog.Show(Title, "This project is not workshared: it has no worksets to show."); return Result.Succeeded; }
            try
            {
                var lines = new List<string>();
                WorksetRepair.ShowAll(doc, lines);
                foreach (var l in lines) SportifyLog.Info("worksets", l);
                var closed = lines.Any(l => l.StartsWith("worksets CLOSED", StringComparison.Ordinal));
                TaskDialog.Show(Title, (closed ? "Some worksets are closed: see the last line.\n\n" : "Every workset is visible again.\n\n") + string.Join("\n", lines.Select(l => "• " + l.Replace("worksets: ", ""))));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the worksets could not be shown", ex, ref message);
            }
        }
    }
}
