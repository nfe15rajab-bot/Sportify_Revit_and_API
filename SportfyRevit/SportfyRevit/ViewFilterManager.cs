using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <param name="Views">The names of the duplicate views that carry the filters (new or reused), in the order they were made.</param>
    internal sealed record ViewFilterResult(string ViewName, int Created, int Updated, int OnView, List<string> Notes, List<string> Views);

    /// <summary>
    /// Colour filters for what Sportify put in the project, on duplicates of a view so that the view itself is never touched:
    ///   "&lt;view&gt; - Sportify Zone Types"   the duplicate carrying "Sportify Zone - &lt;build-up&gt;", one filter per Sportify floor type (a zone's or the roof finish's build-up):
    ///                                         the floors of that type get a solid colour;
    ///   "&lt;view&gt; - Sportify Piece Kinds"  the duplicate carrying "Sportify Type - &lt;category&gt;", one filter per Sportify_Category in use (field, activity, garden, vegetation,
    ///                                         furniture): the pieces of that kind get its colour.
    /// The duplicates appear in the Project Browser next to the view they were made from. Running the command again updates the same filters and the same duplicates (found by name)
    /// instead of adding more. The filters are project elements (visible in Visibility/Graphics of every view, reusable in view templates).
    /// Structural Stress colouring is NOT part of this: it needs a utilisation value on the elements, and the structural analysis computes it per bay, not per element.
    /// Call inside a transaction.
    /// </summary>
    internal static class ViewFilterManager
    {
        public const string ZoneGroup = "Zone Types";
        public const string PieceGroup = "Piece Kinds";

        private sealed record FilterDef(string Group, string Name, IList<ElementId> Categories, FilterRule Rule, Autodesk.Revit.DB.Color Color);

        /// <param name="duplicate">true (the default): the filters go onto duplicates of <paramref name="view"/>; false: onto the view itself.</param>
        public static ViewFilterResult ApplySportifyViewFilters(Document doc, View view, bool duplicate = true)
        {
            var notes = new List<string>();
            var views = new List<string>();
            if (view == null || !view.AreGraphicsOverridesAllowed())
            {
                notes.Add("This view cannot carry graphic overrides (a schedule, a legend or a sheet, say). Open a plan, section or 3D view and run the command again.");
                return new ViewFilterResult(view?.Name ?? "", 0, 0, 0, notes, views);
            }

            var found = SportifyElementScan.Find(doc);
            if (found.IsEmpty)
            {
                notes.Add("This project holds nothing from Sportify yet: import a layout first (Import Configuration, or Auto Import).");
                return new ViewFilterResult(view.Name, 0, 0, 0, notes, views);
            }

            var solid = SolidFillPatternId(doc);
            if (solid == ElementId.InvalidElementId) notes.Add("The project has no solid fill pattern: the filters colour the lines only.");

            var defs = Definitions(doc, found, notes);

            int created = 0, updated = 0, onView = 0;
            foreach (var group in defs.Select(d => d.Group).Distinct())
            {
                var target = duplicate ? DuplicateFor(doc, view, group, notes) : view;
                if (target == null) continue;
                if (!views.Contains(target.Name)) views.Add(target.Name);
                foreach (var def in defs.Where(d => d.Group == group))
                {
                    var element = EnsureFilter(doc, def, ref created, ref updated);
                    if (PutOnView(target, element, def.Color, solid, notes)) onView++;
                }
            }

            notes.Add("Structural Stress colouring is not included: it needs a utilisation value on the elements, and the structural analysis gives one per bay.");
            SportifyLog.Info("bim", $"view filters from \"{view.Name}\": {created} created, {updated} updated, {onView} on {views.Count} view(s): {string.Join("; ", views)}");
            return new ViewFilterResult(view.Name, created, updated, onView, notes, views);
        }

        // ---------------------------------------------------------------- what to filter

        private static List<FilterDef> Definitions(Document doc, SportifyElementScan.Found found, List<string> notes)
        {
            var defs = new List<FilterDef>();

            // zone types (the build-ups)
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
                    defs.Add(new FilterDef(ZoneGroup, BimRules.SafeName("Sportify Zone - " + zoneTypes[i].Substring(BimRules.SportifyTypePrefix.Length)), floorCats,
                        ParameterFilterRuleFactory.CreateContainsRule(typeNameParam!, zoneTypes[i]), new Autodesk.Revit.DB.Color(r, g, b)));
                }

            // the kinds of piece (Sportify_Category)
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
                        defs.Add(new FilterDef(PieceGroup, BimRules.SafeName("Sportify Type - " + category), pieceCats,
                            ParameterFilterRuleFactory.CreateEqualsRule(shared!.Id, category), new Autodesk.Revit.DB.Color(r, g, b)));
                    }
            }
            return defs;
        }

        // ---------------------------------------------------------------- the duplicate views

        /// <summary>The duplicate of a view for one group of filters: the one that already exists under its name, or a new one (made with its detailing when Revit allows).</summary>
        private static View? DuplicateFor(Document doc, View source, string group, List<string> notes)
        {
            var name = BimRules.SportifyViewName(source.Name, group);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null) return existing;

            var option = source.CanViewBeDuplicated(ViewDuplicateOption.WithDetailing) ? ViewDuplicateOption.WithDetailing
                       : source.CanViewBeDuplicated(ViewDuplicateOption.Duplicate) ? ViewDuplicateOption.Duplicate
                       : (ViewDuplicateOption?)null;
            if (option == null)
            {
                notes.Add($"\"{source.Name}\" cannot be duplicated, so no \"{group}\" view was made.");
                return null;
            }

            var copy = (View)doc.GetElement(source.Duplicate(option.Value));
            copy.Name = name;
            return copy;
        }

        // ---------------------------------------------------------------- one filter

        private static ParameterFilterElement EnsureFilter(Document doc, FilterDef def, ref int created, ref int updated)
        {
            var filter = new ElementParameterFilter(def.Rule);
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().FirstOrDefault(f => f.Name == def.Name);
            if (existing != null)
            {
                existing.SetCategories(def.Categories);
                existing.SetElementFilter(filter);
                updated++;
                return existing;
            }
            created++;
            return ParameterFilterElement.Create(doc, def.Name, def.Categories, filter);
        }

        private static bool PutOnView(View view, ParameterFilterElement element, Autodesk.Revit.DB.Color color, ElementId solid, List<string> notes)
        {
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
                return true;
            }
            catch (Exception ex)
            {
                // typically a view whose template controls the filters
                notes.Add($"\"{element.Name}\" was made but could not be put on \"{view.Name}\": {ex.Message.Split('\n')[0]}");
                return false;
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
