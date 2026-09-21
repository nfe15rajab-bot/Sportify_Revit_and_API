using Autodesk.Revit.DB;

namespace SportfyRevit
{
    internal sealed record ViewFilterResult(string ViewName, int Created, int Updated, int OnView, List<string> Notes);

    /// <summary>
    /// Colour filters for what Sportify put in the project, applied to a view:
    ///   "Sportify Zone - &lt;build-up&gt;"   one per Sportify floor type (a zone's or the roof finish's build-up): the floors of that type get a solid colour;
    ///   "Sportify Type - &lt;category&gt;"   one per Sportify_Category in use (field, activity, garden, vegetation, furniture): the pieces of that kind get its colour.
    /// The filters are project elements (visible in Visibility/Graphics of every view, reusable in view templates); running the command again updates them instead of adding
    /// second ones. Structural Stress colouring is NOT part of this: it needs a utilisation value on the elements, and the structural analysis computes it per bay, not per
    /// element. Call inside a transaction.
    /// </summary>
    internal static class ViewFilterManager
    {
        public static ViewFilterResult ApplySportifyViewFilters(Document doc, View view)
        {
            var notes = new List<string>();
            if (view == null || !view.AreGraphicsOverridesAllowed())
            {
                notes.Add("This view cannot carry graphic overrides (a schedule, a legend or a sheet, say). Open a plan, section or 3D view and run the command again.");
                return new ViewFilterResult(view?.Name ?? "", 0, 0, 0, notes);
            }

            var found = SportifyElementScan.Find(doc);
            if (found.IsEmpty)
            {
                notes.Add("This project holds nothing from Sportify yet: import a layout first (Import Configuration, or Auto Import).");
                return new ViewFilterResult(view.Name, 0, 0, 0, notes);
            }

            var solid = SolidFillPatternId(doc);
            if (solid == ElementId.InvalidElementId) notes.Add("The project has no solid fill pattern: the filters colour the lines only.");

            int created = 0, updated = 0, onView = 0;

            // ---- zone types (the build-ups)
            var zoneTypes = found.Elements.OfType<Floor>()
                .Select(f => doc.GetElement(f.GetTypeId()) as ElementType)
                .Where(t => t != null && BimRules.IsSportifyTypeName(t.Name))
                .Select(t => t!.Name).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
            var floorCats = new List<ElementId> { new ElementId(BuiltInCategory.OST_Floors) };
            var typeNameParam = FirstFilterable(doc, floorCats, BuiltInParameter.ALL_MODEL_TYPE_NAME, BuiltInParameter.SYMBOL_NAME_PARAM, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
            if (zoneTypes.Count > 0 && typeNameParam == null)
                notes.Add("Revit does not let floors be filtered by type name here: no zone-type filters.");
            else
                for (int i = 0; i < zoneTypes.Count; i++)
                {
                    var (r, g, b) = BimRules.ColorForZoneType(i);
                    var rule = ParameterFilterRuleFactory.CreateContainsRule(typeNameParam!, zoneTypes[i]);
                    Apply(doc, view, BimRules.SafeName("Sportify Zone - " + zoneTypes[i].Substring(BimRules.SportifyTypePrefix.Length)), floorCats, rule, new Autodesk.Revit.DB.Color(r, g, b), solid, ref created, ref updated, ref onView, notes);
                }

            // ---- the kinds of piece (Sportify_Category)
            var categories = found.Elements.OfType<FamilyInstance>()
                .Select(SportifyElementScan.CategoryOf).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            if (categories.Count > 0)
            {
                var pieceCats = new List<ElementId> { new ElementId(BuiltInCategory.OST_GenericModel), new ElementId(BuiltInCategory.OST_Planting) };
                var shared = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().FirstOrDefault(p => p.Name == "Sportify_Category");
                var usable = shared != null && ParameterFilterUtilities.GetFilterableParametersInCommon(doc, pieceCats).Contains(shared.Id);
                if (!usable) notes.Add("Sportify_Category cannot be filtered on in this project (the parameter is missing): no filters for the kinds of piece.");
                else
                    foreach (var category in categories)
                    {
                        var (r, g, b) = BimRules.ColorForCategory(category);
                        var rule = ParameterFilterRuleFactory.CreateEqualsRule(shared!.Id, category);
                        Apply(doc, view, BimRules.SafeName("Sportify Type - " + category), pieceCats, rule, new Autodesk.Revit.DB.Color(r, g, b), solid, ref created, ref updated, ref onView, notes);
                    }
            }

            notes.Add("Structural Stress colouring is not included: it needs a utilisation value on the elements, and the structural analysis gives one per bay.");
            SportifyLog.Info("bim", $"view filters on \"{view.Name}\": {created} created, {updated} updated, {onView} on the view");
            return new ViewFilterResult(view.Name, created, updated, onView, notes);
        }

        // ---------------------------------------------------------------- one filter

        private static void Apply(Document doc, View view, string name, IList<ElementId> categories, FilterRule rule, Autodesk.Revit.DB.Color color, ElementId solid,
            ref int created, ref int updated, ref int onView, List<string> notes)
        {
            var filter = new ElementParameterFilter(rule);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == name);
            ParameterFilterElement element;
            if (existing != null)
            {
                existing.SetCategories(categories);
                existing.SetElementFilter(filter);
                element = existing;
                updated++;
            }
            else
            {
                element = ParameterFilterElement.Create(doc, name, categories, filter);
                created++;
            }

            var graphics = new OverrideGraphicSettings();
            graphics.SetProjectionLineColor(color);
            graphics.SetCutLineColor(color);
            if (solid != ElementId.InvalidElementId)
            {
                graphics.SetSurfaceForegroundPatternId(solid);
                graphics.SetSurfaceForegroundPatternColor(color);
                graphics.SetCutForegroundPatternId(solid);
                graphics.SetCutForegroundPatternColor(color);
            }

            try
            {
                if (!view.GetFilters().Contains(element.Id)) view.AddFilter(element.Id);
                view.SetFilterVisibility(element.Id, true);
                view.SetFilterOverrides(element.Id, graphics);
                onView++;
            }
            catch (Exception ex)
            {
                // typically a view whose template controls the filters
                notes.Add($"\"{name}\" was made but could not be put on this view: {ex.Message.Split('\n')[0]}");
            }
        }

        /// <summary>The first of the built-in parameters that Revit lets the given categories be filtered by, or null.</summary>
        private static ElementId? FirstFilterable(Document doc, ICollection<ElementId> categories, params BuiltInParameter[] candidates)
        {
            var allowed = ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categories);
            foreach (var c in candidates)
            {
                var id = new ElementId(c);
                if (allowed.Contains(id)) return id;
            }
            return null;
        }

        private static ElementId SolidFillPatternId(Document doc)
        {
            foreach (var pattern in new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>())
            {
                var p = pattern.GetFillPattern();
                if (p.IsSolidFill && p.Target == FillPatternTarget.Drafting) return pattern.Id;
            }
            return ElementId.InvalidElementId;
        }
    }
}
