namespace SportfyRevit
{
    // The Sportify templates as data, adapted from Revit's German BIM template (Templates\German\BIM_Architektur_und_Ingenieurbau.rte), in two languages: German (the names of the German template, the
    // HOAI Leistungsphasen) and English (the same structure with English names; the DIN norms keep their numbers). Revit-free on purpose: the builder (SportifyTemplateBuilder) makes the Revit
    // elements from this, and Tools/AddinCheck checks it (the scales of each Leistungsphase against Revit's own template, unique names and numbers, that every sheet's views and schedules exist,
    // that nothing collides with a built-in name, that the two languages have exactly the same structure).
    //
    // The structure is the German template's own: the HOAI Leistungsphasen 1 Vorplanung, 2 Entwurf, 3 Genehmigung (Behörde), 4 Ausführung, with the scales it uses for a floor plan (1:200, 1:100,
    // 1:100, 1:50) and the same "<phase>-<what>" naming ("2-Entwurf-Grundriss"), so a person who knows the German template finds their way. What Sportify adds is what its commands make: the roof plan,
    // the pieces by kind, the zones by build-up, the circulation, the axonometric and the schedules of the roof. A name never has the scale in it (Revit refuses a colon in a name; the German
    // template's names have none either): the scale is the view template's setting.

    internal enum TemplateLanguage { De, En }

    internal sealed record PhaseSpec(string Key, string Name, int PlanScale, string Detail, string Display);

    /// <summary>A view template. Filters: "" none, "kinds" the Sportify_Category filters, "zones" the build-up filters.</summary>
    internal sealed record ViewTemplateSpec(string Name, string ViewType, int Scale, string Detail, string Display, string PhaseKey, string Filters = "");

    /// <summary>A view of the model the template makes. Kind: "plan" (the roof as designed: the newest Sportify phase), "existing" (the same roof in the Existing phase, Sportify's
    /// pieces hidden: made by SportifySheets from the roof plan on the "new" sheet), "axo", "circulation", "kinds", "zones".</summary>
    internal sealed record ViewSpec(string Key, string Name, string Kind, string TemplateName, string PhaseKey);

    internal sealed record ScheduleSpec(string Key, string Name, string Kind);

    /// <summary>A sheet: Number "S2-01", Size "A0" (user, 2026-09-29: every Sportify sheet), the keys of the views and schedules on it. A view key "diagram:..." is one of the
    /// diagrams Revit draws after an analysis (SportifyDiagramViews), found by its title (SportifyTemplateSpec.Diagrams).</summary>
    internal sealed record SheetSpec(string Number, string Name, string Size, string PhaseKey, IReadOnlyList<string> Views, IReadOnlyList<string> Schedules);

    /// <summary>The whole template in one language.</summary>
    internal sealed class SportifyTemplateSet
    {
        public TemplateLanguage Language { get; init; }
        public IReadOnlyList<PhaseSpec> Phases { get; init; } = Array.Empty<PhaseSpec>();
        public IReadOnlyList<ViewTemplateSpec> ViewTemplates { get; init; } = Array.Empty<ViewTemplateSpec>();
        public IReadOnlyList<ViewSpec> Views { get; init; } = Array.Empty<ViewSpec>();
        public IReadOnlyList<ScheduleSpec> Schedules { get; init; } = Array.Empty<ScheduleSpec>();
        public IReadOnlyList<SheetSpec> Sheets { get; init; } = Array.Empty<SheetSpec>();

        /// <summary>The view template every Sportify schedule is assigned to (a view template of the type Schedule, like "Schedule Typical" in Revit's English template), so the schedules read as Sportify's.</summary>
        public string ScheduleTemplateName { get; init; } = "";

        /// <summary>A template with Revit's views hidden still needs one view to open on: an empty roof plan (this name, with this view template), replaced by the real ones after an import.</summary>
        public string PlaceholderViewName { get; init; } = "";
        public string PlaceholderTemplateName { get; init; } = "";

        /// <summary>The title block family of the German template, by phase: "Plankopf Genehmigung" for phase 3, "Plankopf Ausführung" for the rest, in the size the sheet says (A0 ... A4). Both languages use the German Plankopf: it is the DIN one.</summary>
        public string TitleBlockFamily(string phaseKey) => phaseKey == "3" ? "Plankopf Genehmigung" : "Plankopf Ausführung";

        /// <summary>The name of the filter that colours the pieces of one kind (Sportify_Category: field, activity, garden, vegetation, furniture), in the template's language.</summary>
        public string KindFilterName(string kind) => (Language == TemplateLanguage.De ? "S Nutzung - " : "S Use - ") + kind;

        /// <summary>The name of the filter that selects every Sportify build-up on a floor.</summary>
        public string ZoneFilterName => Language == TemplateLanguage.De ? "S Zone - Sportify Bauweisen" : "S Zone - Sportify Build-ups";

        public string TitleBlockType(SheetSpec sheet) => TitleBlockFamily(sheet.PhaseKey) + " : " + sheet.Size;

        /// <summary>The views the template makes to work in that are not on the plan set (the pieces by kind, the zones by build-up, the older circulation plan).</summary>
        public IReadOnlyList<string> WorkingViewKeys { get; init; } = Array.Empty<string>();

        /// <summary>The names a sheet had in an earlier Sportify template (both languages), by its view key: a project made with it is re-ordered, not given a second set.</summary>
        public IReadOnlyDictionary<string, string[]> FormerSheetNames { get; init; } = new Dictionary<string, string[]>();
    }

    internal static class SportifyTemplateSpec
    {
        public const string Prefix = "S";

        /// <summary>The diagrams on the plan set, in their order, by the title of the view SportifyDiagramViews draws (" — Roof N" after it when a roof was pushed).
        /// The titles are the same in both languages: the views are Sportify's own, drawn under the "Diagrams" view template.</summary>
        public static readonly (string Key, string Title)[] Diagrams =
        {
            ("diagram:zoning", "Sportify - Zoning Diagram"),
            ("diagram:circulation", "Sportify - Circulation Diagram"),
            ("diagram:accessibility", "Sportify - Accessibility Diagram"),
        };
        public static bool IsDiagramKey(string key) => key.StartsWith("diagram:", StringComparison.Ordinal);
        public static string? DiagramTitle(string key) => Diagrams.FirstOrDefault(d => d.Key == key).Title;

        private static readonly (string Key, string De, string En, int Scale, string Detail)[] PhaseTable =
        {
            ("1", "1-Vorplanung", "1-Preliminary", 200, "Coarse"),
            ("2", "2-Entwurf", "2-Design", 100, "Medium"),
            ("3", "3-Genehmigung", "3-Permit", 100, "Medium"),
            ("4", "4-Ausführung", "4-Execution", 50, "Fine"),
        };

        // Both languages are made from one table so that their structure cannot drift: what differs is only the words.
        public static readonly SportifyTemplateSet De = Make(TemplateLanguage.De,
            roofPlan: "Dachaufsicht", uses: "Nutzungen", zones: "Zonenaufbauten", circulation: "Erschließung", axo: "Achsonometrie", section: "Schnitt",
            viewRoof: "S2 Dachaufsicht Entwurf", viewExisting: "S2 Dachaufsicht Bestand", viewUses: "S2 Nutzungen", viewZones: "S2 Zonenaufbauten", viewCirculation: "S2 Erschließung und Fluchtwege", viewAxo: "S2 Achsonometrie",
            sheetList: "S00_PLANLISTE", viewList: "S00_ANSICHTSLISTE", equipment: "S AVA - SPORTGERÄTE UND AUSSTATTUNG", planting: "S AVA - BEPFLANZUNG", buildUps: "S AVA - DACHAUFBAUTEN",
            areas: "S FLÄCHEN - DIN 277", costs: "S KOSTENGRUPPEN - DIN 276",
            sheetNames: new[] { "Dachaufsicht Bestand", "Dachaufsicht Neu", "Achsonometrie", "Diagramm 1 - Zonierung", "Diagramm 2 - Erschließung", "Diagramm 3 - Barrierefreiheit",
                                "Plan- und Ansichtsliste", "Ausstattung und Bepflanzung", "Dachaufbauten", "Flächen (DIN 277) und Kostengruppen (DIN 276)" },
            placeholder: "S Dachaufsicht (Vorlage)", scheduleTemplate: "S-Liste-Standard");

        public static readonly SportifyTemplateSet En = Make(TemplateLanguage.En,
            roofPlan: "Roof Plan", uses: "Uses", zones: "Zone Build-ups", circulation: "Circulation", axo: "Axonometric", section: "Section",
            viewRoof: "S2 Roof Plan Design", viewExisting: "S2 Roof Plan Existing", viewUses: "S2 Uses", viewZones: "S2 Zone Build-ups", viewCirculation: "S2 Circulation and Escape Routes", viewAxo: "S2 Axonometric",
            sheetList: "S00_SHEET LIST", viewList: "S00_VIEW LIST", equipment: "S QTO - SPORT EQUIPMENT AND FURNISHINGS", planting: "S QTO - PLANTING", buildUps: "S QTO - ROOF BUILD-UPS",
            areas: "S AREAS - DIN 277", costs: "S COST GROUPS - DIN 276",
            sheetNames: new[] { "Roof Plan Existing", "Roof Plan New", "Axonometric", "Diagram 1 - Zoning", "Diagram 2 - Circulation", "Diagram 3 - Accessibility",
                                "Sheet and View List", "Equipment and Planting", "Roof Build-ups", "Areas (DIN 277) and Cost Groups (DIN 276)" },
            placeholder: "S Roof Plan (Template)", scheduleTemplate: "S-Schedule-Standard");

        public static SportifyTemplateSet For(TemplateLanguage language) => language == TemplateLanguage.De ? De : En;

        /// <summary>The language a project's Sportify view templates are in (by their names), or null when it has none: what "Show Revit templates" uses to know which of Revit's templates the project came from.</summary>
        public static TemplateLanguage? Detect(IEnumerable<string> viewTemplateNames)
        {
            var names = new HashSet<string>(viewTemplateNames);
            if (De.ViewTemplates.Any(t => names.Contains(t.Name))) return TemplateLanguage.De;
            if (En.ViewTemplates.Any(t => names.Contains(t.Name))) return TemplateLanguage.En;
            return null;
        }

        private static SportifyTemplateSet Make(TemplateLanguage language, string roofPlan, string uses, string zones, string circulation, string axo, string section,
            string viewRoof, string viewExisting, string viewUses, string viewZones, string viewCirculation, string viewAxo,
            string sheetList, string viewList, string equipment, string planting, string buildUps, string areas, string costs, string[] sheetNames, string placeholder, string scheduleTemplate)
        {
            string P(string key) { var p = PhaseTable.First(x => x.Key == key); return language == TemplateLanguage.De ? p.De : p.En; }
            var phases = PhaseTable.Select(p => new PhaseSpec(p.Key, language == TemplateLanguage.De ? p.De : p.En, p.Scale, p.Detail, "HiddenLine")).ToList();

            // the roof plan in every phase, at the scale that phase draws a floor plan; then what Sportify's commands add; then the sections
            var templates = new List<ViewTemplateSpec>();
            foreach (var p in PhaseTable) templates.Add(new ViewTemplateSpec("S" + P(p.Key) + "-" + roofPlan, "FloorPlan", p.Scale, p.Detail, "HiddenLine", p.Key));
            string N(string phase, string what) => "S" + P(phase) + "-" + what;
            templates.Add(new ViewTemplateSpec(N("2", uses), "FloorPlan", 100, "Medium", "HiddenLine", "2", "kinds"));
            templates.Add(new ViewTemplateSpec(N("2", zones), "FloorPlan", 100, "Medium", "HiddenLine", "2", "zones"));
            templates.Add(new ViewTemplateSpec(N("2", circulation), "FloorPlan", 100, "Medium", "HiddenLine", "2"));
            templates.Add(new ViewTemplateSpec(N("2", axo), "ThreeD", 200, "Fine", "ShadingWithEdges", "2"));
            templates.Add(new ViewTemplateSpec(N("2", section), "Section", 100, "Medium", "HiddenLine", "2"));
            templates.Add(new ViewTemplateSpec(N("4", section), "Section", 50, "Fine", "HiddenLine", "4"));
            templates.Add(new ViewTemplateSpec(scheduleTemplate, "Schedule", 0, "Medium", "HiddenLine", "2"));       // scale, detail and display mean nothing to a schedule

            var views = new[]
            {
                new ViewSpec("existing", viewExisting, "existing", N("2", roofPlan), "2"),
                new ViewSpec("plan", viewRoof, "plan", N("2", roofPlan), "2"),
                new ViewSpec("kinds", viewUses, "kinds", N("2", uses), "2"),
                new ViewSpec("zones", viewZones, "zones", N("2", zones), "2"),
                new ViewSpec("circulation", viewCirculation, "circulation", N("2", circulation), "2"),
                new ViewSpec("axo", viewAxo, "axo", N("2", axo), "2"),
            };

            var schedules = new[]
            {
                new ScheduleSpec("sheets", sheetList, "sheets"), new ScheduleSpec("views", viewList, "views"),
                new ScheduleSpec("equipment", equipment, "equipment"), new ScheduleSpec("planting", planting, "planting"), new ScheduleSpec("buildups", buildUps, "buildups"),
                new ScheduleSpec("areas", areas, "areas"), new ScheduleSpec("costs", costs, "costs"),
            };

            // numbered "S<phase>-<nn>", every one on A0, in the order of the user's plan set (2026-09-29): the roof as it is, the roof as designed, the axonometric,
            // the three diagrams; then the lists
            var none = Array.Empty<string>();
            var sheets = new[]
            {
                new SheetSpec("S2-01", sheetNames[0], "A0", "2", new[] { "existing" }, none),
                new SheetSpec("S2-02", sheetNames[1], "A0", "2", new[] { "plan" }, none),
                new SheetSpec("S2-03", sheetNames[2], "A0", "2", new[] { "axo" }, none),
                new SheetSpec("S2-04", sheetNames[3], "A0", "2", new[] { Diagrams[0].Key }, none),
                new SheetSpec("S2-05", sheetNames[4], "A0", "2", new[] { Diagrams[1].Key }, none),
                new SheetSpec("S2-06", sheetNames[5], "A0", "2", new[] { Diagrams[2].Key }, none),
                new SheetSpec("S2-10", sheetNames[6], "A0", "2", none, new[] { "sheets", "views" }),
                new SheetSpec("S2-11", sheetNames[7], "A0", "2", none, new[] { "equipment", "planting" }),
                new SheetSpec("S2-12", sheetNames[8], "A0", "2", none, new[] { "buildups" }),
                new SheetSpec("S2-13", sheetNames[9], "A0", "2", none, new[] { "areas", "costs" }),
            };
            var former = new Dictionary<string, string[]>
            {
                ["plan"] = new[] { "Dachaufsicht Entwurf", "Roof Plan Design" },
                ["axo"] = new[] { "Achsonometrie", "Axonometric" },
                [Diagrams[1].Key] = new[] { "Erschließung und Fluchtwege", "Circulation and Escape Routes" },
            };

            return new SportifyTemplateSet
            {
                Language = language, Phases = phases, ViewTemplates = templates, Views = views, Schedules = schedules, Sheets = sheets,
                PlaceholderViewName = placeholder, PlaceholderTemplateName = N("2", roofPlan), ScheduleTemplateName = scheduleTemplate,
                WorkingViewKeys = new[] { "kinds", "zones", "circulation" }, FormerSheetNames = former,
            };
        }
    }
}
