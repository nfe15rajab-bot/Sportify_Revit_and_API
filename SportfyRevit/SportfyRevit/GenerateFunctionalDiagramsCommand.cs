using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Four deliverables, all from geometry SportifyLayoutBuilder already creates, without any new input: two real Revit views (a circulation floor plan — only what the
    /// import put on the "Combine" workset: the roof outline, setback, circulation-path lines and entry markers — and a 3D axonometric of everything Sportify built), and
    /// two SVG diagrams in the architectural style (FunctionalDiagramData/FunctionalDiagramSvg): a "spine" diagram — the placements as rounded rooms, coloured by category,
    /// with the real walkable route to each drawn as a thick red circulation spine and a red dot per entry — and a bubble (relationship) diagram, circles sized by real
    /// footprint area and connected by dotted lines, not to scale on purpose. Re-running this command updates the same named views and files rather than creating
    /// duplicates each time.
    ///
    /// It works with or without worksets. In a workshared project the circulation
    /// view hides every workset but Sportify's own (SportifyWorksetSet.All), as it
    /// always did. In a project without worksharing (the import asks first, and
    /// "import without worksets" is a legitimate answer) the same is done element by
    /// element: what the import created and would have put on any other workset is
    /// hidden in that view. It used to refuse such a project with "No Sportify layout
    /// has been imported", which was wrong: the layout was there, it was the worksets
    /// that were not.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class GenerateFunctionalDiagramsCommand : IExternalCommand
    {
        private const string CirculationViewName = "Sportify - Circulation Diagram";
        private const string AxonometricViewName = "Sportify - Massing Axonometric";

        /// <summary>A fixed view name is one shared view: with two roofs pushed, generating the diagrams for the second reused and restyled the first's, which read as
        /// "the other roof's info got overwritten." Every caller of CreateOrReuseCirculationView/CreateOrReuseAxonometricView that is not the German/English template's own
        /// fixed S2 sheets (a separate, larger scope this does not touch) now scopes its view name by the active roof.</summary>
        internal static string RoofScopedName(string baseName) => baseName + " — Roof " + (RoofBoundaryServer.ActiveRoofId ?? 0);
        internal static string RoofScopedCirculationName() => RoofScopedName(CirculationViewName);
        internal static string RoofScopedAxoName() => RoofScopedName(AxonometricViewName);

        /// <summary>Hides, in `view`, every Sportify element that belongs to a DIFFERENT pushed roof than `roofId` — the other half of the fix: a shared workset (Sports,
        /// Gardens, ...) holds every roof's pieces together, so scoping the view's NAME is not enough by itself to keep one roof's diagram from also showing the other
        /// roof's pieces layered in. Best-effort: an element Revit refuses to hide in this particular view is simply left, never a reason to fail the whole view.</summary>
        internal static void HideOtherRoofs(Document doc, View view, string roofId)
        {
            try
            {
                var mine = new HashSet<ElementId>(SportifyElementScan.Find(doc, roofId).Elements.Select(e => e.Id));
                var others = SportifyElementScan.Find(doc).Elements.Where(e => !mine.Contains(e.Id));
                var hideable = others.Where(e => { try { return e.CanBeHidden(view); } catch (Exception) { return false; } }).Select(e => e.Id).ToList();
                if (hideable.Count > 0) view.HideElements(hideable);
            }
            catch (Exception ex) { SportifyLog.Warn("diagrams", "could not hide another roof's elements in \"" + view.Name + "\": " + ex.Message); }
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Generate Functional Diagrams";
            var doc = commandData.Application.ActiveUIDocument.Document;

            var roofId = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();

            // What decides whether there is anything to draw is what the import created (its ledger), not whether the project has worksets.
            if (SportifyElementScan.Find(doc, roofId).IsEmpty)
            {
                TaskDialog.Show(title,
                    "This project holds nothing from Sportify yet — run \"Import Configuration\" or turn on \"Auto Import\" and push a layout first.");
                return Result.Succeeded;
            }

            string circulationViewName, axoViewName;
            ElementId circulationViewId, axoViewId;
            string how;

            using (var t = new Transaction(doc, "Generate Sportify functional diagrams"))
            {
                t.Start();

                var circulationView = CreateOrReuseCirculationView(doc, RoofScopedCirculationName());
                var axoView = CreateOrReuseAxonometricView(doc, RoofScopedAxoName());
                var groups = IterationWorksets.Find(doc);
                how = groups.Count > 0
                    ? $"Iteration {groups[^1].Index} only — {groups.Count} imported; circulation bold red, pieces greyed out and labelled"
                    : doc.IsWorkshared
                        ? "circulation bold red, pieces greyed out and labelled"
                        : "the circulation paths, roof outline and entries only (the project has no worksets)";
                circulationViewName = circulationView.Name;
                axoViewName = axoView.Name;
                circulationViewId = circulationView.Id;
                axoViewId = axoView.Id;

                t.Commit();
            }

            // the circulation view in the Algorithmic placement's style, with the fire safety and accessibility diagrams beside it (SportifyDiagramViews)
            SportifyDiagramViews.Refresh(doc);

            // the bubble and spine diagrams: read-only over what the import built, no transaction needed
            var data = FunctionalDiagramData.Collect(doc, roofId);
            var spineSvg = FunctionalDiagramData.SpineSvg(data);
            var bubbleSvg = FunctionalDiagramData.BubbleSvg(data);

            // the two views as images, and the two SVG diagrams, in the workspace's Diagrams folder: the deliverable outside Revit
            string saved;
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "Sportify", "DiagramImages");
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                var (circulationImage, axoImage) = GenerateAnalysisReportCommand.KeepDiagrams(
                    GenerateAnalysisReportCommand.ExportViewImage(doc, circulationViewId, Path.Combine(temp, "circulation_roof" + roofId)),
                    GenerateAnalysisReportCommand.ExportViewImage(doc, axoViewId, Path.Combine(temp, "axonometric_roof" + roofId)));

                // roof-suffixed: a project with two roofs pushed keeps both roofs' diagrams on disk, the second never overwriting the first's.
                var dir = SportifyWorkspace.PathFor("diagrams", DeliverableNaming.FolderFor("diagrams"));
                File.WriteAllText(Path.Combine(dir, "functional_spine_roof" + roofId + ".svg"), spineSvg);
                File.WriteAllText(Path.Combine(dir, "functional_bubble_roof" + roofId + ".svg"), bubbleSvg);
                saved = circulationImage != null || axoImage != null ? $"\n\nAll four saved to {dir}." : $"\n\nThe two diagrams saved to {dir}.";
            }
            catch (Exception ex) { saved = "\n\nThe diagrams could not be saved: " + ex.Message; }

            SportifyLog.Info("diagrams", $"views \"{circulationViewName}\" ({how}) and \"{axoViewName}\"; {data.Pieces.Count} piece(s), {data.Entries.Count} entr{(data.Entries.Count == 1 ? "y" : "ies")} in the spine/bubble diagrams");
            TaskDialog.Show(title,
                $"Created/updated:\n" +
                $"- \"{circulationViewName}\" — floor plan, {how}.\n" +
                $"- \"{axoViewName}\" — 3D isometric, everything Sportify built.\n" +
                $"- Circulation Spine — {data.Pieces.Count} piece(s) with the real route to {(data.Entries.Count == 0 ? "an entry (none found — every route is missing)" : data.Entries.Count == 1 ? "the entry" : data.Entries.Count + " entries")}.\n" +
                $"- Bubble Diagram — the same pieces by relationship, not to scale." + saved);

            return Result.Succeeded;
        }

        /// <summary>internal, not private: GenerateAnalysisReportCommand reuses this so the PDF report's diagram images always have a source view, creating one if needed rather than requiring this command to be run first. Call inside a transaction.</summary>
        internal static ViewPlan CreateOrReuseCirculationView(Document doc, string? name = null, bool configure = true, ElementId? levelId = null)
        {
            name ??= CirculationViewName;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);

            ViewPlan view = existing ?? CreateFloorPlanView(doc, levelId);
            if (existing == null) view.Name = name;
            if (configure) ConfigureCirculationView(doc, view);
            return view;
        }

        /// <summary>What makes the circulation view the circulation view: only Sportify's own content visible (by workset, or element by element without worksets). Separate so that a view template can be applied first and this after.</summary>
        internal static void ConfigureCirculationView(Document doc, ViewPlan view)
        {
            ApplySportifyVisibility(doc, view);
            StyleCirculationDiagram(doc, view);
            HideOtherRoofs(doc, view, (RoofBoundaryServer.ActiveRoofId ?? 0).ToString());
        }

        /// <summary>
        /// Shows exactly one coherent Sportify layout in `view` — the latest iteration's own worksets if "Import Iterations as
        /// Design Options" has run, otherwise the plain import's own 6 canonical worksets (SportifyWorksetSet.All) — and hides
        /// every other workset (the rest of the building this add-in did not create). Sports and Gardens are shown, not hidden:
        /// StyleCirculationDiagram grays those pieces out and labels them rather than hiding them, so a workset-hidden piece
        /// (which no per-element override can undo) would leave nothing for that styling to gray.
        /// </summary>
        private static void ApplySportifyVisibility(Document doc, View view)
        {
            if (!doc.IsWorkshared) return;   // no worksets to hide the rest of the building by; StyleCirculationDiagram still styles every Sportify element it finds

            var groups = IterationWorksets.Find(doc);
            if (groups.Count > 0)
            {
                IterationWorksets.ShowOnly(view, groups, groups[^1]);
                // A plain (non-iteration) import's own worksets, if any, would otherwise sit alongside the iteration's —
                // hide those too so the view shows exactly one coherent layout.
                foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    if (SportifyWorksetSet.All.Contains(w.Name)) view.SetWorksetVisibility(w.Id, WorksetVisibility.Hidden);
                return;
            }

            foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                view.SetWorksetVisibility(w.Id, SportifyWorksetSet.All.Contains(w.Name) ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
        }

        /// <summary>
        /// Bold red for circulation paths and entry markers (tagged with their own line style at creation time — see
        /// SportifyLayoutBuilder.GetOrCreateLineStyle), grayed-out fill/outline plus a name label for every placed piece
        /// (court, furniture, garden item), and a label for every entry marker. Scoped to this one view via per-element/
        /// per-category overrides, so normal working views stay undecorated. A project imported before this styling
        /// existed has no tagged lines yet — the category lookups below come back null and are simply skipped; the next
        /// re-import (or re-push from the web app) tags them.
        /// </summary>
        /// <summary>The setback boundary (BimRules.SetbackLineStyle) is a design-rule reference, not something a reader
        /// needs to see — Revit's own default line colour for it otherwise reads as a stray, unexplained diagonal in any
        /// view that shows Sportify's own worksets. Hidden here rather than recoloured, in every Sportify-generated view
        /// (circulation diagram and the massing axonometric alike); a project imported before this style existed has no
        /// tagged setback line yet, so the category lookup comes back null and this is simply skipped.</summary>
        private static void HideSetbackLine(Document doc, View view)
        {
            var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var setbackCategory = linesCategory.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == BimRules.SetbackLineStyle);
            if (setbackCategory == null || !view.CanCategoryBeHidden(setbackCategory.Id)) return;
            try { view.SetCategoryHidden(setbackCategory.Id, true); } catch (Exception) { /* a template-locked view — not fatal */ }
        }

        private static void StyleCirculationDiagram(Document doc, View view)
        {
            var found = SportifyElementScan.Find(doc);
            if (found.IsEmpty) return;

            if (view is ViewPlan plan) FixViewRangeForRoof(plan, found);

            var red = new Autodesk.Revit.DB.Color(196, 30, 58);
            var gray = new Autodesk.Revit.DB.Color(140, 140, 140);
            var solid = ViewFilterManager.SolidFillPatternId(doc);

            var linesCategory = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
            var circCategory = linesCategory.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == BimRules.CirculationLineStyle);
            var entryCategory = linesCategory.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == BimRules.EntryLineStyle);
            var nodeCategory = linesCategory.SubCategories.Cast<Category>().FirstOrDefault(c => c.Name == BimRules.CirculationNodeLineStyle);
            HideSetbackLine(doc, view);

            // A solid bold line (not dashed) with a round dot at each path's ends, the way a hand-drawn circulation diagram marks a route.
            if (circCategory != null)
            {
                var ov = new OverrideGraphicSettings();
                ov.SetProjectionLineColor(red);
                ov.SetProjectionLineWeight(8);
                view.SetCategoryOverrides(circCategory.Id, ov);
            }
            if (entryCategory != null)
            {
                var ov = new OverrideGraphicSettings();
                ov.SetProjectionLineColor(red);
                ov.SetProjectionLineWeight(14);
                view.SetCategoryOverrides(entryCategory.Id, ov);
            }
            if (nodeCategory != null)
            {
                var ov = new OverrideGraphicSettings();
                ov.SetProjectionLineColor(red);
                ov.SetProjectionLineWeight(10);
                view.SetCategoryOverrides(nodeCategory.Id, ov);
            }

            // This view is Sportify's own — every text note in it is one this method put there, so a clean slate each
            // regeneration is simpler and safer than trying to match old labels back up to elements that may have moved.
            var staleLabels = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).ToElementIds();
            if (staleLabels.Count > 0) doc.Delete(staleLabels);

            var textTypeId = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault()?.Id;

            int entryIndex = 0;
            foreach (var el in found.Elements)
            {
                if (el is TextNote or SketchPlane) continue;

                if (el is ModelCurve mc)
                {
                    if (mc.LineStyle?.Name == BimRules.EntryLineStyle && mc.GeometryCurve is Arc arc)
                    {
                        entryIndex++;
                        CreateDiagramLabel(doc, view, $"Entry {entryIndex}", arc.Center + new XYZ(arc.Radius * 1.6, 0, 0), textTypeId, ElementWorksetId(el));
                    }
                    continue;   // circulation paths: the category override above already colors them; roof outline/setback: left as-is, a neutral reference frame
                }

                if (el is not (Floor or FamilyInstance)) continue;

                var pieceOv = new OverrideGraphicSettings();
                pieceOv.SetProjectionLineColor(gray);
                pieceOv.SetProjectionLineWeight(2);
                if (el is Floor && solid != ElementId.InvalidElementId)
                {
                    pieceOv.SetSurfaceForegroundPatternId(solid);
                    pieceOv.SetSurfaceForegroundPatternColor(gray);
                }
                try { view.SetElementOverrides(el.Id, pieceOv); } catch (Exception) { /* a category this view type can't override — skip it, not fatal */ }

                // A defensive check, not the usual path: nothing here hides a piece by view anymore (see ApplySportifyVisibility),
                // but skip the label too if something else did, or it would float free of its now-invisible piece.
                if (el is FamilyInstance && !el.IsHidden(view))
                {
                    var bb = el.get_BoundingBox(view);
                    if (bb != null)
                    {
                        var center = (bb.Min + bb.Max) / 2;
                        // el.Name is the family/type's own Revit name — for a generated piece that is an internal
                        // identifier (e.g. "ACTIVITY_PICKLEBALL_COURT"), not prose. Sportify_Label (SportifySharedParameters,
                        // stamped from the web app's own placement label) is the human-readable one; fall back to el.Name
                        // only for an older import made before that parameter existed, or a non-Sportify family.
                        var niceLabel = el.LookupParameter("Sportify_Label")?.AsString();
                        var displayLabel = !string.IsNullOrWhiteSpace(niceLabel) ? niceLabel : el.Name;
                        CreateDiagramLabel(doc, view, displayLabel, new XYZ(center.X, center.Y, bb.Max.Z), textTypeId, ElementWorksetId(el));
                    }
                }
            }
        }

        private static WorksetId ElementWorksetId(Element el)
        {
            var p = el.get_Parameter(BuiltInParameter.ELEM_PARTITION_PARAM);
            return p != null && p.HasValue ? new WorksetId(p.AsInteger()) : WorksetId.InvalidWorksetId;
        }

        /// <summary>internal, not private: AnalysisHatchViews reuses this for its own per-shape labels (the same "a label must never break diagram generation" reasoning applies there too).</summary>
        internal static void CreateDiagramLabel(Document doc, View view, string text, XYZ origin, ElementId? textTypeId, WorksetId worksetId)
        {
            if (textTypeId == null) return;
            try
            {
                var note = TextNote.Create(doc, view.Id, origin, text, textTypeId);
                SportifyLayoutBuilder.SetWorkset(note, worksetId);
            }
            catch (Exception) { /* a label must never break diagram generation */ }
        }

        /// <summary>internal, not private: see CreateOrReuseCirculationView's note.</summary>
        internal static View3D CreateOrReuseAxonometricView(Document doc, string? name = null)
        {
            name ??= AxonometricViewName;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);

            View3D view;
            if (existing != null)
            {
                view = existing;
            }
            else
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(doc, vft.Id);
                view.Name = name;
                ViewDefaults.DisableCrop(view);
            }

            // A view template (SportifyTemplateBuilder's German/English ones) can take control of Detail Level; setting
            // it on a view whose template already controls it throws "The Detail Level of this view cannot be
            // modified," which used to roll back this whole transaction — the circulation view along with it.
            var detailLevel = view.get_Parameter(BuiltInParameter.VIEW_DETAIL_LEVEL);
            if (detailLevel != null && !detailLevel.IsReadOnly) view.DetailLevel = ViewDetailLevel.Fine;
            view.DisplayStyle = DisplayStyle.ShadingWithEdges;
            HideSetbackLine(doc, view);
            HideOtherRoofs(doc, view, (RoofBoundaryServer.ActiveRoofId ?? 0).ToString());

            if (doc.IsWorkshared)
            {
                var groups = IterationWorksets.Find(doc);
                if (groups.Count > 0)
                {
                    IterationWorksets.ShowOnly(view, groups, groups[^1]);
                }
                else
                {
                    foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                        view.SetWorksetVisibility(w.Id, WorksetVisibility.Visible);
                }
            }

            return view;
        }

        private static ViewPlan CreateFloorPlanView(Document doc, ElementId? levelId = null)
        {
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.FloorPlan);
            var level = levelId ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).First().Id;
            var view = ViewPlan.Create(doc, vft.Id, level);
            ViewDefaults.DisableCrop(view);
            return view;
        }

        /// <summary>
        /// A freshly created floor plan is tied to the project's lowest level with a default (~2 m) view range around it — fine for a
        /// roof a storey or two up, but this add-in's own roof can sit anywhere (a rooftop deck 12+ m up, in the building this was built
        /// against). Without this, the view range simply does not reach the roof: nothing on it renders (courts, circulation, the
        /// boundary), only annotation survives (text notes, the north arrow), which looked exactly like "the styling isn't applying" but
        /// was really "there is nothing in view to style." Read the roof's own elements for their real Z instead of guessing from the
        /// level, and set the range in offsets from whatever level the view is stuck with (ViewPlan.GenLevel cannot be reassigned after
        /// creation) — offsets can be arbitrarily large, so this works whether that level is close to the roof or far below it.
        /// internal, not private: AnalysisHatchViews reuses this for its own per-analysis views, which are plain floor plans with the same problem.
        /// </summary>
        internal static void FixViewRangeForRoof(ViewPlan view, SportifyElementScan.Found found)
        {
            double? minZ = null, maxZ = null;
            foreach (var el in found.Elements)
            {
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;
                minZ = minZ == null ? bb.Min.Z : Math.Min(minZ.Value, bb.Min.Z);
                maxZ = maxZ == null ? bb.Max.Z : Math.Max(maxZ.Value, bb.Max.Z);
            }
            if (minZ == null || maxZ == null) return;

            var levelElevationFt = view.GenLevel.Elevation;
            var marginFt = UnitUtils.ConvertToInternalUnits(1.5, UnitTypeId.Meters);

            var range = view.GetViewRange();
            var levelId = view.GenLevel.Id;
            foreach (var plane in new[] { PlanViewPlane.TopClipPlane, PlanViewPlane.CutPlane, PlanViewPlane.BottomClipPlane, PlanViewPlane.ViewDepthPlane })
                range.SetLevelId(plane, levelId);
            range.SetOffset(PlanViewPlane.TopClipPlane, maxZ.Value - levelElevationFt + marginFt);
            range.SetOffset(PlanViewPlane.CutPlane, maxZ.Value - levelElevationFt + marginFt * 0.5);
            range.SetOffset(PlanViewPlane.BottomClipPlane, minZ.Value - levelElevationFt - marginFt);
            range.SetOffset(PlanViewPlane.ViewDepthPlane, minZ.Value - levelElevationFt - marginFt);
            view.SetViewRange(range);
        }
    }
}
