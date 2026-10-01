using System.Text.Json;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// The circulation, fire safety, accessibility and zoning diagrams as Revit views, in the visual style of the web app's Algorithmic placement plan, a little less
    /// saturated (user, 2026-09-29): built inside Revit, under a Sportify view template for analysis diagrams (made when the project has none), and
    /// generated again every time an analysis command runs, so they always show the layout as it is now. What they show is DiagramPlan (Revit-free); this
    /// puts it in the views with the same filled regions the analysis hatch views use (AnalysisHatchViews).
    ///
    ///   "Sportify - Circulation Diagram - Roof N"    (the Generate Functional Diagrams view, now drawn in this style)
    ///   "Sportify - Fire Safety Diagram - Roof N"
    ///   "Sportify - Accessibility Diagram - Roof N"
    ///   "Sportify - Zoning Diagram - Roof N"         (user, 2026-09-29: "add also a zoning diagram", in the same template as the circulation one)
    /// and with them "Sportify - Tags - Roof N" (SportifyTagView), then, in a project on the Sportify template, the plan set's sheets (SportifySheets).
    /// </summary>
    internal static class SportifyDiagramViews
    {
        internal const string TemplateName = "Diagrams";                                // user, 2026-09-29: "assign diagram views to a template, name it Diagrams"
        const string OldTemplateName = "Sportify - Analysis Diagrams";           // what it was first made as: renamed, not made twice
        internal const string FireTitle = "Sportify - Fire Safety Diagram";
        internal const string AccessibilityTitle = "Sportify - Accessibility Diagram";
        internal const string ZoningTitle = "Sportify - Zoning Diagram";

        /// <summary>
        /// Draws the four diagrams for the layout (the one the command read, else the newest the web app sent). Best-effort: never throws, and a project or a
        /// layout with nothing to draw is simply left alone. Opens its own transaction, so it must be called outside one (the analysis commands call it).
        /// Not drawn again when nothing they are drawn from has changed since the last drawing in this Revit session (see Fingerprint): every analysis
        /// calls this before its first window, so an unchanged redraw was a wait on every click (2026-09-30: Structural (bay by bay)). `force`: drawn
        /// whatever (Generate Functional Diagrams, where drawing them is what was asked).
        /// </summary>
        internal static void Refresh(Document? doc, SportifyLayout? layout = null, bool force = false)
        {
            try
            {
                if (doc == null || doc.IsFamilyDocument || doc.IsReadOnly) return;
                if (layout == null)
                {
                    if (!ProjectLayout.TryGet(doc, out var json, out _) || json == null) return;
                    layout = JsonSerializer.Deserialize<SportifyLayout>(json);
                }
                if (layout?.Placements == null || layout.Placements.Count == 0) return;

                var (distances, unreachable) = CirculationEngine.ComputeTravelDistances(layout);
                var limit = AnalysisReferenceData.GetParam("Fire Safety", "max_travel_distance_m");
                var minWidth = AnalysisReferenceData.GetParam("Accessibility", "min_circulation_width_m");

                var key = KeyOf(doc);
                var print = Fingerprint(doc, layout, limit, minWidth);
                WatchClosing(doc);
                if (!force && Drawn.TryGetValue(key, out var last) && last.Print == print && last.Views.All(id => doc.GetElement(id) is View))
                {
                    SportifyLog.Info("diagrams", "the layout, the imports and the pieces are as they were at the last drawing: the diagrams and the tag view are left as they are");
                    return;
                }
                Drawn.Remove(key);

                using var t = new Transaction(doc, "Sportify: analysis diagrams");
                t.Start();
                var views = new List<View?>();
                var fireView = Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedName(FireTitle), DiagramPlan.FireSafety(layout, distances, unreachable, limit), null);
                var template = fireView != null ? EnsureTemplate(doc, fireView) : null;
                if (fireView != null && template != null) Apply(fireView, template);
                views.Add(fireView);
                views.Add(Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedName(AccessibilityTitle), DiagramPlan.Accessibility(layout, minWidth), template));
                views.Add(Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedCirculationName(), DiagramPlan.Circulation(layout), template));
                views.Add(Draw(doc, layout, GenerateFunctionalDiagramsCommand.RoofScopedName(ZoningTitle), DiagramPlan.Zoning(layout), template));
                var whole = Isolated(doc, "the tag view", () => views.Add(SportifyTagView.Draw(doc, template)));
                whole &= Isolated(doc, "the plan set", () => ArrangeSheets(doc));
                // a drawing with a part that failed is not remembered: the next analysis tries again
                if (t.Commit() == TransactionStatus.Committed && whole)
                    Drawn[key] = (print, views.OfType<View>().Select(v => v.Id).ToArray());
                SportifyLog.Info("diagrams", "circulation, fire safety, accessibility and zoning diagrams drawn for roof " + (RoofBoundaryServer.ActiveRoofId ?? 0));
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("diagrams", "the analysis diagrams could not be drawn: " + ex.Message);
            }
        }

        /// <summary>A project on the Sportify template gets each diagram on its sheet of the plan set as soon as it is drawn (SportifySheets). Others are left alone.</summary>
        internal static void ArrangeSheets(Document doc)
        {
            var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name).ToList();
            if (SportifyTemplateSpec.Detect(names) is not TemplateLanguage language) return;
            var made = new List<string>(); var notes = new List<string>();
            SportifySheets.Arrange(doc, SportifyTemplateSpec.For(language), null, null, made, notes);
            foreach (var n in notes) SportifyLog.Warn("sheets", n);
        }

        /// <summary>
        /// What the diagrams and the tag view are drawn from, in one string: the layout, the active roof, the two reference values, every recorded import
        /// (roof, time, how many pieces), the iterations' copies and how many Generic Models the project holds (a piece added, deleted or re-imported in Revit
        /// changes it, so the tag view catches up). Hashed, so the remembered value stays small.
        /// </summary>
        static string Fingerprint(Document doc, SportifyLayout layout, double limit, double minWidth)
        {
            var sb = new System.Text.StringBuilder(JsonSerializer.Serialize(layout));
            sb.Append("|roof ").Append(RoofBoundaryServer.ActiveRoofId ?? 0).Append("|limit ").Append(limit).Append("|width ").Append(minWidth);
            foreach (var e in ImportLedger.ReadEntries(doc).OrderBy(e => e.RoofKey, StringComparer.Ordinal).ThenBy(e => e.ImportedAtUtc))
                sb.Append("|import ").Append(e.RoofKey).Append('@').Append(e.ImportedAtUtc.Ticks).Append('#').Append(e.Elements.Count);
            sb.Append("|iterations ").Append(IterationLedger.ReadUniqueIds(doc).Count);
            sb.Append("|generic models ").Append(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType().GetElementCount());
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
        }

        /// <summary>The last drawing in this Revit session, per project: its fingerprint and the views it drew (a view deleted, or the drawing undone, draws again).</summary>
        static readonly Dictionary<string, (string Print, ElementId[] Views)> Drawn = new();
        static bool _watchingClosing;

        static string KeyOf(Document doc) => string.IsNullOrEmpty(doc.PathName) ? "unsaved:" + doc.Title : doc.PathName;

        /// <summary>A project closed (perhaps without saving the last drawing) is drawn again the next time it is opened and analysed.</summary>
        static void WatchClosing(Document doc)
        {
            if (_watchingClosing) return;
            doc.Application.DocumentClosing += (_, e) => { try { Drawn.Remove(KeyOf(e.Document)); } catch (Exception) { /* nothing remembered */ } };
            _watchingClosing = true;
        }

        /// <summary>A part that must never cost the diagrams: in a sub-transaction, rolled back and logged when it fails. False when it failed.</summary>
        static bool Isolated(Document doc, string what, Action action)
        {
            using var sub = new SubTransaction(doc);
            try { sub.Start(); action(); sub.Commit(); return true; }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                SportifyLog.Warn("diagrams", what + " could not be drawn: " + ex.Message);
                return false;
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
        /// "Diagrams": the project's own, or made from the first diagram view - the model greyed (halftone) so the diagram's colours
        /// read, the diagram's own regions (Detail Items) left in full colour, coarse detail. A person can change it in Revit like any template; it is only
        /// made when it is not there.
        /// </summary>
        static View? EnsureTemplate(Document doc, View from)
        {
            var templates = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).ToList();
            var existing = templates.FirstOrDefault(v => v.Name == TemplateName);
            if (existing != null) return KeepOwnViewRange(existing);
            var old = templates.FirstOrDefault(v => v.Name == OldTemplateName);
            if (old != null)
            {
                try { old.Name = TemplateName; SportifyLog.Info("diagrams", $"renamed the view template \"{OldTemplateName}\" to \"{TemplateName}\""); }
                catch (Exception ex) { SportifyLog.Warn("diagrams", "the old view template could not be renamed: " + ex.Message); }
                return KeepOwnViewRange(old);
            }
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
                return KeepOwnViewRange(template);
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("diagrams", "the view template could not be made: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// A template made from a plan view controls its View Range too, and would then force the first diagram's range onto every view it is on:
        /// each diagram keeps its own, fitted to its roof (GenerateFunctionalDiagramsCommand.FixViewRangeForRoof).
        /// </summary>
        static View KeepOwnViewRange(View template)
        {
            try
            {
                var free = template.GetNonControlledTemplateParameterIds();
                var range = new ElementId(BuiltInParameter.PLAN_VIEW_RANGE);
                if (!free.Contains(range) && template.GetTemplateParameterIds().Contains(range))
                {
                    free.Add(range);
                    template.SetNonControlledTemplateParameterIds(free);
                }
            }
            catch (Exception ex) { SportifyLog.Warn("diagrams", "the template's view range could not be left to each view: " + ex.Message); }
            return template;
        }
    }
}
