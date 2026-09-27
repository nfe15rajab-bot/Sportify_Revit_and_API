using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>What "Hide Revit templates" would remove from (or "Show Revit templates" would add to) a project, by kind.</summary>
    internal sealed record BuiltInPlan(List<ElementId> Sheets, List<ElementId> Views, List<ElementId> Schedules, List<ElementId> ViewTemplates, List<ElementId> Filters)
    {
        public int Total => Sheets.Count + Views.Count + Schedules.Count + ViewTemplates.Count + Filters.Count;
        public string Describe() => $"{ViewTemplates.Count} view templates, {Views.Count} views, {Sheets.Count} sheets, {Schedules.Count} schedules and {Filters.Count} filters";
        public static BuiltInPlan Empty() => new(new(), new(), new(), new(), new());
        public BuiltInPlan Plus(BuiltInPlan o) => new(Sheets.Concat(o.Sheets).ToList(), Views.Concat(o.Views).ToList(), Schedules.Concat(o.Schedules).ToList(), ViewTemplates.Concat(o.ViewTemplates).ToList(), Filters.Concat(o.Filters).ToList());
    }

    /// <summary>
    /// Revit has no way to hide a view template, a view, a sheet or a schedule from the browser and the dialogs, and its German BIM template brings 59 view templates, 118 views, 10 sheets, 31 schedules
    /// and 22 filters (the English multi-discipline one 40 / 30 / 22 / 14 / 29): too much to look at when a project is about a roof. So a Sportify project carries none of them until asked:
    /// Hide removes what Revit's template brought (by name, from the BuiltInSet, which is generated from the template itself) and that nothing of the user's uses, Show copies them back out of the
    /// template file. Nothing that is not on a list is ever touched, and a built-in item that the user's own work uses (a view made from a built-in view template, a sheet that carries one of the
    /// user's views) stays.
    /// </summary>
    internal static class BuiltInTemplates
    {
        private static string ViewKey(View v) => v.ViewType + "|" + v.Name;

        private static T? Try<T>(Func<T> f) { try { return f(); } catch (Exception) { return default; } }

        /// <summary>What Hide would remove now (of one set), computed without changing anything.</summary>
        public static BuiltInPlan PlanHide(Document doc, BuiltInSet set)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var sheets = all.OfType<ViewSheet>().Where(s => set.SheetNumbers.Contains(s.SheetNumber)).ToList();

            // a sheet goes only if every view on it is a built-in one
            var removableSheets = sheets.Where(s => s.GetAllPlacedViews().All(id => doc.GetElement(id) is View v && set.Views.Contains(ViewKey(v)))).ToList();
            var removableSheetIds = new HashSet<ElementId>(removableSheets.Select(s => s.Id));

            // a built-in view goes unless it is on a sheet that stays, or is the active view
            var onStayingSheets = new HashSet<ElementId>(all.OfType<ViewSheet>().Where(s => !removableSheetIds.Contains(s.Id)).SelectMany(s => s.GetAllPlacedViews()));
            var activeId = Try(() => doc.ActiveView?.Id) ?? ElementId.InvalidElementId;
            var views = all.Where(v => !v.IsTemplate && v is not ViewSheet && v is not ViewSchedule && set.Views.Contains(ViewKey(v)) && !onStayingSheets.Contains(v.Id) && v.Id != activeId).ToList();
            var removedViewIds = new HashSet<ElementId>(views.Select(v => v.Id));

            var schedules = all.OfType<ViewSchedule>().Where(s => !s.IsTemplate && set.Schedules.Contains(s.Name)).ToList();

            // a built-in view template goes unless a view that stays is made from it
            var staying = all.Where(v => !v.IsTemplate && !removedViewIds.Contains(v.Id) && !schedules.Any(s => s.Id == v.Id)).ToList();
            var usedTemplates = new HashSet<ElementId>(staying.Select(v => v.ViewTemplateId).Where(id => id != ElementId.InvalidElementId));
            var templates = all.Where(v => v.IsTemplate && set.ViewTemplates.Contains(v.Name) && !usedTemplates.Contains(v.Id)).ToList();
            var removedTemplateIds = new HashSet<ElementId>(templates.Select(t => t.Id));

            // a built-in filter goes unless a view or view template that stays uses it
            var usedFilters = new HashSet<ElementId>();
            foreach (var v in all.Where(v => !removedViewIds.Contains(v.Id) && !removedTemplateIds.Contains(v.Id)))
                foreach (var f in Try(() => v.GetFilters()) ?? new List<ElementId>()) usedFilters.Add(f);
            var filters = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().Where(f => set.Filters.Contains(f.Name) && !usedFilters.Contains(f.Id)).ToList();

            return new BuiltInPlan(removableSheets.Select(s => s.Id).ToList(), views.Select(v => v.Id).ToList(), schedules.Select(s => s.Id).ToList(), templates.Select(t => t.Id).ToList(), filters.Select(f => f.Id).ToList());
        }

        /// <summary>What Hide would remove now, of every set (a project can come from either template).</summary>
        public static BuiltInPlan PlanHide(Document doc) => BuiltInSet.All.Aggregate(BuiltInPlan.Empty(), (plan, set) => plan.Plus(PlanHide(doc, set)));

        /// <summary>Removes what PlanHide found, of every set. Call inside a transaction. Returns what was actually removed (an element Revit refuses to delete, or that went with another, is not counted).</summary>
        public static BuiltInPlan Hide(Document doc, List<string> notes)
        {
            var done = BuiltInPlan.Empty();
            foreach (var set in BuiltInSet.All)
            {
                var plan = PlanHide(doc, set);
                // sheets and views first, then what they used
                Delete(doc, plan.Sheets, done.Sheets, notes);
                Delete(doc, plan.Views, done.Views, notes);
                Delete(doc, plan.Schedules, done.Schedules, notes);
                Delete(doc, plan.ViewTemplates, done.ViewTemplates, notes);
                Delete(doc, plan.Filters, done.Filters, notes);
            }
            SportifyLog.Info("templates", "Revit templates hidden: " + done.Describe());
            return done;
        }

        private static void Delete(Document doc, List<ElementId> ids, List<ElementId> done, List<string> notes)
        {
            foreach (var id in ids)
            {
                try
                {
                    if (doc.GetElement(id) == null) continue;              // already gone with something else
                    doc.Delete(id);
                    done.Add(id);
                }
                catch (Exception ex)
                {
                    notes.Add($"Revit kept element {id.Value}: {ex.Message.Split('\n')[0]}");
                }
            }
        }

        /// <summary>What Show would copy back from one template: its built-in view templates, filters and schedules that the project does not have (by name). The views and sheets are not brought back: they are examples, not tools.</summary>
        public static (int ViewTemplates, int Filters, int Schedules) PlanShow(Document doc, BuiltInSet set)
        {
            var haveTemplates = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name));
            var haveFilters = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<Element>().Select(f => f.Name));
            var haveSchedules = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<View>().Select(s => s.Name));
            return (set.ViewTemplates.Count(n => !haveTemplates.Contains(n)), set.Filters.Count(n => !haveFilters.Contains(n)), set.Schedules.Count(n => !haveSchedules.Contains(n)));
        }

        /// <summary>Copies the missing built-in view templates, filters and schedules of one set out of its template. Call inside a transaction. Returns how many elements were copied.</summary>
        public static int Show(Document doc, List<string> notes, BuiltInSet set)
        {
            var haveTemplates = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name));
            var haveFilters = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<Element>().Select(f => f.Name));
            var haveSchedules = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<View>().Select(s => s.Name));
            var copied = TemplateSource.CopyFrom(doc, set, source =>
            {
                var ids = new List<ElementId>();
                foreach (var v in new FilteredElementCollector(source).OfClass(typeof(View)).Cast<View>())
                {
                    if (v.IsTemplate && set.ViewTemplates.Contains(v.Name) && !haveTemplates.Contains(v.Name)) ids.Add(v.Id);
                    else if (v is ViewSchedule s && !v.IsTemplate && set.Schedules.Contains(v.Name) && !haveSchedules.Contains(v.Name)) ids.Add(s.Id);
                }
                foreach (var f in new FilteredElementCollector(source).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>())
                    if (set.Filters.Contains(f.Name) && !haveFilters.Contains(f.Name)) ids.Add(f.Id);
                return ids;
            }, notes);
            SportifyLog.Info("templates", $"Revit templates shown: {copied} element(s) copied from the {set.Name} template");
            return copied;
        }
    }
}
