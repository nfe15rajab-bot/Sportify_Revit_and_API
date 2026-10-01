using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Sportify's working lines (the circulation paths, their nodes, the entrance circles, the setback line) are for the diagrams only: hidden in every other
    /// plan, 3D view, section and elevation, and in their view templates (user, 2026-10-01: "remove these useless lines from the 2d and 3d"). Kept in the
    /// model, because the diagrams draw on them; shown where the "Diagrams" template is, and in Sportify's own diagram and tag views.
    /// </summary>
    internal static class SportifyLines
    {
        static readonly string[] Styles = { BimRules.CirculationLineStyle, BimRules.EntryLineStyle, BimRules.CirculationNodeLineStyle, BimRules.SetbackLineStyle };

        /// <summary>Inside the caller's transaction. Returns how many view/category settings it changed.</summary>
        internal static int HideOutsideDiagrams(Document doc)
        {
            var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var ids = linesCategory.SubCategories.Cast<Category>().Where(c => Styles.Contains(c.Name)).Select(c => c.Id).ToList();
            if (ids.Count == 0) return 0;
            var diagrams = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.IsTemplate && v.Name == SportifyDiagramViews.TemplateName);
            var changed = 0;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (v is ViewSheet || v is ViewSchedule) continue;
                if (v.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.AreaPlan or ViewType.EngineeringPlan or ViewType.ThreeD or ViewType.Section or ViewType.Elevation)) continue;
                if (diagrams != null && (v.Id == diagrams.Id || v.ViewTemplateId == diagrams.Id)) continue;
                if (v.Name.StartsWith("Sportify - ", StringComparison.Ordinal) && (v.Name.Contains("Diagram") || v.Name.Contains("Tags"))) continue;
                foreach (var id in ids)
                {
                    try
                    {
                        if (!v.CanCategoryBeHidden(id) || v.GetCategoryHidden(id)) continue;
                        v.SetCategoryHidden(id, true);
                        changed++;
                    }
                    catch (Exception) { /* a view its template controls: the template is hidden in its own turn */ }
                }
            }
            return changed;
        }
    }
}
