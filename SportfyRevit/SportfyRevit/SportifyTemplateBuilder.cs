using Autodesk.Revit.DB;

namespace SportfyRevit
{
    internal sealed record TemplateResult(List<string> Made, List<string> Notes);

    /// <summary>
    /// Makes the Sportify templates in a project from SportifyTemplateSpec: the view templates (one roof plan per HOAI Leistungsphase at that phase's scale, plus the Sportify ones: pieces by kind,
    /// zones by build-up, circulation, axonometric, sections), their filters, the seven German-named schedules, and the sheets on the German template's Plankopf (A1 for plans, A3 for lists).
    /// With Sportify elements in the project (an import's ledger) it also makes the five views of the model, puts each view template on its view, colours the zones per build-up, writes the
    /// DIN 277 / DIN 276 classes onto the elements and puts the views and schedules on the sheets. Without any (a new template) the views and the plan sheets wait for an import, and the
    /// list sheets and schedules are there already. Running it again updates what it made and never doubles it. Call inside a transaction.
    /// </summary>
    internal static class SportifyTemplateBuilder
    {
        public static TemplateResult Apply(Document doc, TemplateLanguage language, bool withContent = true)
        {
            var set = SportifyTemplateSpec.For(language);
            var made = new List<string>();
            var notes = new List<string>();

            TemplateUnits.EnsureMetric(doc, made, notes);              // an imperial project is converted to the German template's units first
            SportifySharedParameters.EnsureBound(doc);
            SportifySharedParameters.EnsureCategories(doc);           // an import from before the norm classes bound the parameters to fewer categories
            EnsureTitleBlocks(doc, set, notes);
            var templates = EnsureViewTemplates(doc, set, made, notes);
            EnsureStaticFilters(doc, set, templates, made, notes);
            var schedules = SportifyScheduleFactory.EnsureAll(doc, set, templates, made, notes);

            var found = SportifyElementScan.Find(doc);
            var views = new Dictionary<string, View>();
            if (withContent && !found.IsEmpty)
            {
                AddZoneFilters(doc, set, templates, found, made, notes);
                views = EnsureViews(doc, set, templates, found, made, notes);
                var norms = NormParameters.Assign(doc, found.Elements, language);
                SportifyLog.Info("templates", $"norm classes: {norms.Changed} element(s) classed of {norms.Floors} floor(s) and {norms.Pieces} piece(s); {norms.WithoutParameter} without the parameters (Sportify_KG is bound to: {SportifySharedParameters.BoundCategories(doc, NormParameters.Kg) ?? "nothing"})");
                if (norms.Changed > 0) made.Add($"DIN 277 / DIN 276 classes on {norms.Changed} element(s) ({norms.Floors} floor(s), {norms.Pieces} piece(s))");
                if (norms.WithoutParameter > 0) notes.Add($"{norms.WithoutParameter} element(s) could not be classed: Sportify_KG / Sportify_DIN277 are not available on them (an older import bound the parameters to fewer categories).");
            }
            else if (withContent) notes.Add("The project holds nothing from Sportify yet, so the views and the plan sheets wait for an import (run Apply Sportify Template again after it).");

            if (!withContent) EnsurePlaceholderView(doc, set, templates, made);
            else if (views.ContainsKey("plan")) RemovePlaceholderView(doc, set);
            EnsureSheets(doc, set, views, schedules, made, notes);
            SportifyLog.Info("templates", "Sportify template (" + language + ") applied: " + made.Count + " item(s) made or updated, " + notes.Count + " note(s)");
            return new TemplateResult(made, notes);
        }

        // ---------------------------------------------------------------- title blocks

        private static Dictionary<string, FamilySymbol> TitleBlockSymbols(Document doc) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>()
                .GroupBy(s => s.FamilyName + " : " + s.Name).ToDictionary(g => g.Key, g => g.First());

        /// <summary>The German template's Plankopf types the sheets use; copied out of the German template when the project does not have them.</summary>
        private static void EnsureTitleBlocks(Document doc, SportifyTemplateSet set, List<string> notes)
        {
            var needed = set.Sheets.Select(set.TitleBlockType).Distinct().ToList();
            var missing = needed.Where(n => !TitleBlockSymbols(doc).ContainsKey(n)).ToList();
            if (missing.Count == 0) return;
            var copied = TemplateSource.CopyFromGerman(doc, source =>
                (ICollection<ElementId>)new FilteredElementCollector(source).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>()
                    .Where(s => missing.Contains(s.FamilyName + " : " + s.Name)).Select(s => s.Id).ToList(), notes);
            var stillMissing = needed.Where(n => !TitleBlockSymbols(doc).ContainsKey(n)).ToList();
            if (stillMissing.Count > 0) notes.Add("Title block(s) not available, sheets are made without: " + string.Join(", ", stillMissing));
        }

        // ---------------------------------------------------------------- view templates

        private static Dictionary<string, View> EnsureViewTemplates(Document doc, SportifyTemplateSet set, List<string> made, List<string> notes)
        {
            var result = new Dictionary<string, View>();
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First());
            foreach (var spec in set.ViewTemplates)
            {
                try
                {
                    if (!existing.TryGetValue(spec.Name, out var template))
                    {
                        var source = TemporaryView(doc, spec.ViewType);
                        if (source == null) { notes.Add($"No {spec.ViewType} view could be made to create \"{spec.Name}\" from."); continue; }
                        template = source.CreateViewTemplate();
                        template.Name = spec.Name;
                        if (spec.ViewType == "Schedule") SportifyScheduleFactory.ControlNothing(template);
                        doc.Delete(source.Id);
                        made.Add("View template: " + spec.Name);
                    }
                    Configure(template, spec);
                    result[spec.Name] = template;
                }
                catch (Exception ex)
                {
                    notes.Add($"View template \"{spec.Name}\" could not be made: {ex.Message.Split('\n')[0]}");
                    SportifyLog.Warn("templates", "view template " + spec.Name + ": " + ex);
                }
            }
            return result;
        }

        private static void Configure(View template, ViewTemplateSpec spec)
        {
            if (spec.ViewType == "Schedule") return;        // a schedule has no scale, detail level or display style
            template.Scale = spec.Scale;
            template.DetailLevel = spec.Detail switch { "Coarse" => ViewDetailLevel.Coarse, "Fine" => ViewDetailLevel.Fine, _ => ViewDetailLevel.Medium };
            template.DisplayStyle = spec.Display == "ShadingWithEdges" ? DisplayStyle.ShadingWithEdges : DisplayStyle.HLR;
        }

        /// <summary>A view of the type the template is for, only to make the template from (deleted straight after).</summary>
        private static View? TemporaryView(Document doc, string viewType)
        {
            ViewFamilyType? Type(ViewFamily family) => new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().FirstOrDefault(t => t.ViewFamily == family);
            switch (viewType)
            {
                case "FloorPlan":
                {
                    var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
                    var type = Type(ViewFamily.FloorPlan);
                    return level != null && type != null ? ViewPlan.Create(doc, type.Id, level.Id) : null;
                }
                case "ThreeD":
                {
                    var type = Type(ViewFamily.ThreeDimensional);
                    return type != null ? View3D.CreateIsometric(doc, type.Id) : null;
                }
                case "Section":
                {
                    var type = Type(ViewFamily.Section);
                    if (type == null) return null;
                    var box = new BoundingBoxXYZ { Transform = Transform.Identity, Min = new XYZ(-5, -5, 0), Max = new XYZ(5, 5, 5) };
                    return ViewSection.CreateSection(doc, type.Id, box);
                }
                case "Schedule":
                    return ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_GenericModel));
                default: return null;
            }
        }

        // ---------------------------------------------------------------- filters

        /// <summary>The filters a template can have without any Sportify element in the project: the five kinds of piece (by Sportify_Category) on the pieces template, and "all Sportify build-ups" on the zones template.</summary>
        private static void EnsureStaticFilters(Document doc, SportifyTemplateSet set, Dictionary<string, View> templates, List<string> made, List<string> notes)
        {
            int created = 0, updated = 0;
            var solid = ViewFilterManager.SolidFillPatternId(doc);

            var kindsTemplates = set.ViewTemplates.Where(t => t.Filters == "kinds" && templates.ContainsKey(t.Name)).Select(t => templates[t.Name]).ToList();
            var shared = new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<SharedParameterElement>().FirstOrDefault(p => p.Name == "Sportify_Category");
            var pieceCats = new List<ElementId> { new ElementId(BuiltInCategory.OST_GenericModel), new ElementId(BuiltInCategory.OST_Planting) };
            if (kindsTemplates.Count > 0)
            {
                if (shared == null || !ParameterFilterUtilities.GetFilterableParametersInCommon(doc, pieceCats).Contains(shared.Id))
                    notes.Add("Sportify_Category is not available for filters in this project: the pieces-by-kind template has no filters.");
                else
                    foreach (var kind in new[] { "field", "activity", "garden", "vegetation", "furniture" })
                    {
                        var (r, g, b) = BimRules.ColorForCategory(kind);
                        var color = new Autodesk.Revit.DB.Color(r, g, b);
                        var def = new ViewFilterManager.FilterDef(ViewFilterManager.PieceGroup, BimRules.SafeName(set.KindFilterName(kind)), pieceCats, ParameterFilterRuleFactory.CreateEqualsRule(shared.Id, kind), color);
                        var element = ViewFilterManager.EnsureFilter(doc, def, ref created, ref updated);
                        foreach (var t in kindsTemplates) ViewFilterManager.PutOnView(t, element, color, solid, notes);
                    }
            }

            var zonesTemplates = set.ViewTemplates.Where(t => t.Filters == "zones" && templates.ContainsKey(t.Name)).Select(t => templates[t.Name]).ToList();
            if (zonesTemplates.Count > 0)
            {
                var floorCats = new List<ElementId> { new ElementId(BuiltInCategory.OST_Floors) };
                var typeName = ViewFilterManager.FirstFilterable(doc, floorCats, BuiltInParameter.ALL_MODEL_TYPE_NAME, BuiltInParameter.SYMBOL_NAME_PARAM, BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM);
                if (typeName == null) notes.Add("Floors cannot be filtered by type name here: the zones template has no filter.");
                else
                {
                    var (r, g, b) = BimRules.ColorForZoneType(0);
                    var color = new Autodesk.Revit.DB.Color(r, g, b);
                    var def = new ViewFilterManager.FilterDef(ViewFilterManager.ZoneGroup, set.ZoneFilterName, floorCats, ParameterFilterRuleFactory.CreateContainsRule(typeName, BimRules.SportifyTypePrefix), color);
                    var element = ViewFilterManager.EnsureFilter(doc, def, ref created, ref updated);
                    foreach (var t in zonesTemplates) ViewFilterManager.PutOnView(t, element, color, solid, notes);
                }
            }
            if (created > 0) made.Add($"Filters: {created} made, {updated} updated");
        }

        /// <summary>With Sportify floors in the project: one colour per build-up on the zones template, so every view made from it shows the zones by build-up.</summary>
        private static void AddZoneFilters(Document doc, SportifyTemplateSet set, Dictionary<string, View> templates, SportifyElementScan.Found found, List<string> made, List<string> notes)
        {
            foreach (var spec in set.ViewTemplates.Where(t => t.Filters == "zones"))
                if (templates.TryGetValue(spec.Name, out var template))
                {
                    var result = ViewFilterManager.ApplySportifyViewFilters(doc, template, duplicate: false, onlyGroup: ViewFilterManager.ZoneGroup);
                    if (result.OnView > 0) made.Add($"Zone colours on \"{spec.Name}\": {result.OnView} build-up filter(s)");
                }
        }

        // ---------------------------------------------------------------- views

        private static ElementId? RoofLevel(SportifyElementScan.Found found) => found.Elements.OfType<Floor>().Select(f => f.LevelId).FirstOrDefault(id => id != ElementId.InvalidElementId);

        private static Dictionary<string, View> EnsureViews(Document doc, SportifyTemplateSet set, Dictionary<string, View> templates, SportifyElementScan.Found found, List<string> made, List<string> notes)
        {
            var result = new Dictionary<string, View>();
            var level = RoofLevel(found) ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First().Id;
            foreach (var spec in set.Views)
            {
                try
                {
                    var isNew = !new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(v => !v.IsTemplate && v.Name == spec.Name);
                    View view = spec.Kind switch
                    {
                        "circulation" => GenerateFunctionalDiagramsCommand.CreateOrReuseCirculationView(doc, spec.Name, configure: false, levelId: level),
                        "axo" => GenerateFunctionalDiagramsCommand.CreateOrReuseAxonometricView(doc, spec.Name),
                        _ => PlanView(doc, spec.Name, level),
                    };
                    if (templates.TryGetValue(spec.TemplateName, out var template) && view.ViewTemplateId != template.Id)
                    {
                        try { view.ViewTemplateId = template.Id; }
                        catch (Exception ex) { notes.Add($"\"{template.Name}\" could not be put on \"{view.Name}\": {ex.Message.Split('\n')[0]}"); }
                    }
                    if (spec.Kind == "circulation") GenerateFunctionalDiagramsCommand.ConfigureCirculationView(doc, (ViewPlan)view);      // after the template: it may set what is visible
                    SetPhase(set, view, spec.PhaseKey);
                    result[spec.Key] = view;
                    if (isNew) made.Add("View: " + spec.Name);
                }
                catch (Exception ex)
                {
                    notes.Add($"View \"{spec.Name}\" could not be made: {ex.Message.Split('\n')[0]}");
                    SportifyLog.Warn("templates", "view " + spec.Name + ": " + ex);
                }
            }
            return result;
        }

        private static ViewPlan PlanView(Document doc, string name, ElementId levelId)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().FirstOrDefault(v => !v.IsTemplate && v.Name == name);
            if (existing != null) return existing;
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().First(t => t.ViewFamily == ViewFamily.FloorPlan);
            var view = ViewPlan.Create(doc, type.Id, levelId);
            view.Name = name;
            return view;
        }

        /// <summary>The German template's browser is organised by a project parameter "Projektbrowser Leistungsphase": filled, its views and sheets group by phase there.</summary>
        private static void SetPhase(SportifyTemplateSet set, Element element, string phaseKey)
        {
            var phase = set.Phases.FirstOrDefault(p => p.Key == phaseKey);
            if (phase == null) return;
            try
            {
                var p = element.LookupParameter("Projektbrowser Leistungsphase");
                if (p != null && !p.IsReadOnly && p.AsString() != phase.Name) p.Set(phase.Name);
            }
            catch (Exception) { /* a project without that parameter (another template) */ }
        }

        /// <summary>A template with Revit's views hidden still needs one view to open on: an empty roof plan on the lowest level, replaced by the real ones after an import.</summary>
        private static void EnsurePlaceholderView(Document doc, SportifyTemplateSet set, Dictionary<string, View> templates, List<string> made)
        {
            if (new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Any(v => !v.IsTemplate && v.Name == set.PlaceholderViewName)) return;
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            if (level == null) return;
            var view = PlanView(doc, set.PlaceholderViewName, level.Id);
            if (templates.TryGetValue(set.PlaceholderTemplateName, out var template)) { try { view.ViewTemplateId = template.Id; } catch (Exception) { /* left without */ } }
            made.Add("View: " + set.PlaceholderViewName);
        }

        private static void RemovePlaceholderView(Document doc, SportifyTemplateSet set)
        {
            var placeholder = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => !v.IsTemplate && v.Name == set.PlaceholderViewName);
            try { if (placeholder != null) doc.Delete(placeholder.Id); } catch (Exception) { /* it is the active view: it stays */ }
        }

        // ---------------------------------------------------------------- sheets

        private static (double W, double H) SheetSize(string size) => size == "A1" ? (841.0, 594.0) : size == "A0" ? (1189.0, 841.0) : (420.0, 297.0);

        private static void EnsureSheets(Document doc, SportifyTemplateSet set, Dictionary<string, View> views, Dictionary<string, ViewSchedule> schedules, List<string> made, List<string> notes)
        {
            const double mmToFeet = 1.0 / 304.8;
            var symbols = TitleBlockSymbols(doc);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().GroupBy(s => s.SheetNumber).ToDictionary(g => g.Key, g => g.First());
            foreach (var spec in set.Sheets)
            {
                try
                {
                    if (spec.Views.Any(k => !views.ContainsKey(k))) continue;                 // a plan sheet waits for its view (an import)
                    if (spec.Schedules.Any(k => !schedules.ContainsKey(k))) continue;
                    var isNew = !sheets.TryGetValue(spec.Number, out var sheet);
                    if (isNew)
                    {
                        symbols.TryGetValue(set.TitleBlockType(spec), out var symbol);
                        if (symbol != null && !symbol.IsActive) symbol.Activate();
                        sheet = ViewSheet.Create(doc, symbol?.Id ?? ElementId.InvalidElementId);
                        sheet.SheetNumber = spec.Number;
                        sheet.Name = spec.Name;
                        sheets[spec.Number] = sheet;
                        made.Add($"Sheet {spec.Number} {spec.Name} ({spec.Size})");
                    }
                    SetPhase(set, sheet!, spec.PhaseKey);

                    var (w, h) = SheetSize(spec.Size);
                    foreach (var key in spec.Views)
                    {
                        var view = views[key];
                        if (Viewport.CanAddViewToSheet(doc, sheet!.Id, view.Id)) Viewport.Create(doc, sheet.Id, view.Id, new XYZ(w * 0.5 * mmToFeet, h * 0.5 * mmToFeet, 0));
                    }
                    int i = 0;
                    foreach (var key in spec.Schedules)
                    {
                        var schedule = schedules[key];
                        var already = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Any(x => x.OwnerViewId == sheet!.Id && x.ScheduleId == schedule.Id);
                        if (!already) ScheduleSheetInstance.Create(doc, sheet!.Id, schedule.Id, new XYZ(20 * mmToFeet, (h - 30 - i * 110) * mmToFeet, 0));
                        i++;
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"Sheet {spec.Number} could not be made: {ex.Message.Split('\n')[0]}");
                    SportifyLog.Warn("templates", "sheet " + spec.Number + ": " + ex);
                }
            }
        }
    }
}
