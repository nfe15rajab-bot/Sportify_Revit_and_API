using System.Text.Json;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The circulation, fire safety and accessibility diagrams as Revit views, in the visual style of the web app's Algorithmic placement plan, a little less
    /// saturated (user, 2026-09-29): built inside Revit, under a Sportify view template for analysis diagrams (made when the project has none), and
    /// generated again every time an analysis command runs, so they always show the layout as it is now. What they show is DiagramPlan (Revit-free); this
    /// puts it in the views with the same filled regions the analysis hatch views use (AnalysisHatchViews).
    ///
    ///   "Sportify - Circulation Diagram - Roof N"    (the Generate Functional Diagrams view, now drawn in this style)
    ///   "Sportify - Fire Safety Diagram - Roof N"
    ///   "Sportify - Accessibility Diagram - Roof N"
    /// </summary>
    internal static class SportifyDiagramViews
    {
        internal const string TemplateName = "Sportify - Analysis Diagrams";
        internal const string FireTitle = "Sportify - Fire Safety Diagram";
        internal const string AccessibilityTitle = "Sportify - Accessibility Diagram";

        /// <summary>
        /// Draws the three diagrams for the layout (the one the command read, else the newest the web app sent). Best-effort: never throws, and a project or a
        /// layout with nothing to draw is simply left alone. Opens its own transaction, so it must be called outside one (the analysis commands call it).
        /// </summary>
        internal static void Refresh(Document? doc, SportifyLayout? layout = null)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly) return;
                if (layout == null)
                {
                    if (!RoofBoundaryServer.TryGetLatestCombinedLayout(out var json, out _) || json == null) return;
                    layout = JsonSerializer.Deserialize<SportifyLayout>(json);
                }
                if (layout?.Placements == null || layout.Placements.Count == 0) return;

                var (distances, unreachable) = CirculationEngine.ComputeTravelDistances(layout);
                var limit = AnalysisReferenceData.GetParam("Fire Safety", "max_travel_distance_m");
                var minWidth = AnalysisReferenceData.GetParam("Accessibility", "min_circulation_width_m");

                using var t = new Transaction(doc, "Sportify: analysis diagrams");
                t.Start();
                var fireView = Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedName(FireTitle), DiagramPlan.FireSafety(layout, distances, unreachable, limit), null);
                var template = fireView != null ? EnsureTemplate(doc, fireView) : null;
                if (fireView != null && template != null) Apply(fireView, template);
                Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedName(AccessibilityTitle), DiagramPlan.Accessibility(layout, minWidth), template);
                Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedCirculationName(), DiagramPlan.Circulation(layout), template);
                t.Commit();
                SportifyLog.Info("diagrams", "circulation, fire safety and accessibility diagrams drawn for roof " + (RoofBoundaryServer.ActiveRoofId ?? 0));
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("diagrams", "the analysis diagrams could not be drawn: " + ex.Message);
            }
        }

        /// <summary>One diagram in its view: the view made (or found) on the roof's level, cleared of the previous run's regions and labels, drawn again.</summary>
        static ViewPlan? Draw(Document doc, SportifyLayout layout, string title, DiagramPlan.Diagram diagram, View? template)
        {
            if (diagram.Shapes.Count == 0) return null;
            var view = GenerateFunctionalDiagramsCommand.CreateOrReuseCirculationView(doc, title, configure: false);
            var roofId = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();
            var found = SportifyElementScan.Find(doc, roofId);
            if (!found.IsEmpty) GenerateFunctionalDiagramsCommand.FixViewRangeForRoof(view, found);
            if (template != null) Apply(view, template);

            var stale = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).ToElementIds()
                .Concat(new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).ToElementIds()).ToList();
            if (stale.Count > 0) doc.Delete(stale);

            var frame = new RoofFrame(layout.RoofContext?.WorldOriginXM ?? 0, layout.RoofContext?.WorldOriginYM ?? 0,
                (layout.RoofContext?.RotationDeg ?? 0) * Math.PI / 180.0, layout.RoofContext?.LengthM ?? 0, layout.RoofContext?.WidthM ?? 0);
            var z = SportifyLayoutBuilder.FeetFromMeters(layout.RoofContext?.WorldOriginZM ?? 0);
            var baseTypeId = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).FirstElementId();
            if (baseTypeId == ElementId.InvalidElementId) return view;
            var solid = ViewFilterManager.SolidFillPatternId(doc);
            var textTypeId = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault()?.Id;
            var workset = SportifyWorksetSet.Ensure(doc, new[] { SportifyWorksetSet.Analysis })[SportifyWorksetSet.Analysis];
            var cache = new Dictionary<string, ElementId>();

            foreach (var shape in diagram.Shapes)
            {
                try
                {
                    var loop = AnalysisHatchViews.LoopFor(shape, frame, z);
                    if (loop == null) continue;
                    var typeId = AnalysisHatchViews.GetOrCreateFilledRegionType(doc, cache, shape.Fill, baseTypeId, solid);
                    var region = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                    SportifyLayoutBuilder.SetWorkset(region, workset);
                    if (!string.IsNullOrWhiteSpace(shape.Label)) Label(doc, view, shape.Label, AnalysisHatchViews.Centroid(shape, frame, z), textTypeId, workset);
                }
                catch (Exception ex) { SportifyLog.Warn("diagrams", $"one shape of \"{title}\" could not be drawn: " + ex.Message); }
            }

            // the caption above the roof's top-left corner: what the diagram says in one line
            var (cx, cy) = frame.ToModel(0, -1.8);
            Label(doc, view, diagram.Caption, new XYZ(SportifyLayoutBuilder.FeetFromMeters(cx), SportifyLayoutBuilder.FeetFromMeters(cy), z), textTypeId, workset, centred: false);
            return view;
        }

        /// <summary>A label in the middle of its shape (the width in its circle), or left-aligned (the caption).</summary>
        static void Label(Document doc, View view, string text, XYZ at, ElementId? textTypeId, WorksetId workset, bool centred = true)
        {
            if (textTypeId == null) return;
            try
            {
                var opts = new TextNoteOptions(textTypeId)
                {
                    HorizontalAlignment = centred ? HorizontalTextAlignment.Center : HorizontalTextAlignment.Left,
                    VerticalAlignment = centred ? VerticalTextAlignment.Middle : VerticalTextAlignment.Top,
                };
                var note = TextNote.Create(doc, view.Id, at, text, opts);
                SportifyLayoutBuilder.SetWorkset(note, workset);
            }
            catch (Exception) { /* a label never breaks a diagram */ }
        }

        static void Apply(View view, View template)
        {
            try { if (view.ViewTemplateId != template.Id) view.ViewTemplateId = template.Id; }
            catch (Exception ex) { SportifyLog.Warn("diagrams", $"the view template could not be applied to \"{view.Name}\": " + ex.Message); }
        }

        /// <summary>
        /// "Sportify - Analysis Diagrams": the project's own, or made from the first diagram view - the model greyed (halftone) so the diagram's colours
        /// read, the diagram's own regions (Detail Items) left in full colour, coarse detail. A person can change it in Revit like any template; it is only
        /// made when it is not there.
        /// </summary>
        static View? EnsureTemplate(Document doc, View from)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.IsTemplate && v.Name == TemplateName);
            if (existing != null) return existing;
            try
            {
                var template = from.CreateViewTemplate();
                template.Name = TemplateName;
                template.DetailLevel = ViewDetailLevel.Coarse;
                var halftone = new OverrideGraphicSettings().SetHalftone(true);
                foreach (Category c in doc.Settings.Categories)
                {
                    if (c.CategoryType != CategoryType.Model || c.Id.Value == (long)BuiltInCategory.OST_DetailComponents) continue;
                    try { template.SetCategoryOverrides(c.Id, halftone); } catch (Exception) { /* a category this view cannot override */ }
                }
                SportifyLog.Info("diagrams", "made the view template \"" + TemplateName + "\"");
                return template;
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("diagrams", "the view template could not be made: " + ex.Message);
                return null;
            }
        }
    }
}
