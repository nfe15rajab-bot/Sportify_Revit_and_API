using Autodesk.Revit.DB;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Sun;
using Sportify.Simulation.Wind;

namespace SportfyRevit
{
    /// <summary>
    /// One Revit view per analysis that has real per-element spatial results — structural loads, wind and erosion,
    /// sun and shade — each showing the same coloured plan PhysicalAnalysisPdf's chart draws for that analysis, as
    /// real Revit FilledRegion hatches instead of an SVG image in a PDF: a reviewer working in Revit sees exactly
    /// where a bay is over capacity, a zone could lift, or a zone is too sunny, in the model itself. Dynamic
    /// analysis and rain/soil percolation are NOT included: their reports have no per-bay/per-zone plan breakdown
    /// (PhysicalAnalysisPdf draws only bar charts and curves for them, no SvgChart.Plan call) — a hatch view with
    /// nothing genuinely spatial to show would be a view for its own sake, not a real deliverable.
    ///
    /// Reuses the exact shape lists PhysicalAnalysisPdf.Build already computes for its PDF charts
    /// (StructuralShapes/WindShapes/SunShapes, extracted there for this reuse) — one source of the per-bay/per-zone
    /// geometry and status colours, whether it ends up as an SVG rectangle or a Revit FilledRegion. Each PlanShape's
    /// roof-local (metres, y-down) coordinates are turned into real Revit world points with the same RoofFrame the
    /// import itself places pieces with (SportifyLayoutBuilder), read fresh from the layout's own RoofContext rather
    /// than any live import state, so this works independently of whether an import ran in this Revit session.
    ///
    /// Called from GenerateAnalysisReportCommand, inside its own transaction. Reruns replace what an earlier run of
    /// this same analysis made (a clean slate, the same convention StyleCirculationDiagram uses for its own labels)
    /// rather than piling up duplicates.
    /// </summary>
    internal static class AnalysisHatchViews
    {
        private static readonly (string Key, string Title)[] ViewTitles =
        {
            ("structural_loads", "Sportify - Structural Loads"),
            ("wind_erosion", "Sportify - Wind and Erosion"),
            ("sun_and_shading", "Sportify - Sun and Shade"),
        };

        /// <summary>Best-effort, call inside an open transaction: builds whichever of the three hatch views this layout has real shapes for, skipping the rest quietly — the same "nothing to draw yet" reasoning PhysicalAnalysisPdf.Build already uses per analysis.</summary>
        public static void Build(Document doc, SportifyLayout layout)
        {
            var frame = new RoofFrame(layout.RoofContext?.WorldOriginXM ?? 0, layout.RoofContext?.WorldOriginYM ?? 0,
                (layout.RoofContext?.RotationDeg ?? 0) * Math.PI / 180.0, layout.RoofContext?.LengthM ?? 0, layout.RoofContext?.WidthM ?? 0);
            var originZFt = SportifyLayoutBuilder.FeetFromMeters(layout.RoofContext?.WorldOriginZM ?? 0);
            var cache = new Dictionary<string, ElementId>();
            var analysisWorksetId = SportifyWorksetSet.Ensure(doc, new[] { SportifyWorksetSet.Analysis })[SportifyWorksetSet.Analysis];
            var roofId = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();

            TryBuild(doc, frame, originZFt, cache, analysisWorksetId, roofId, "structural_loads", () =>
            {
                var inputs = StructureLayoutAdapter.ToInputs(layout);
                return inputs.Items.Count == 0 ? null : PhysicalAnalysisPdf.StructuralShapes(StructureModel.Analyse(inputs));
            });
            TryBuild(doc, frame, originZFt, cache, analysisWorksetId, roofId, "wind_erosion", () =>
            {
                var inputs = WindLayoutAdapter.ToInputs(layout);
                return inputs.Zones.Count == 0 && inputs.Plants.Count == 0 ? null : PhysicalAnalysisPdf.WindShapes(inputs, WindModel.Analyse(inputs));
            });
            TryBuild(doc, frame, originZFt, cache, analysisWorksetId, roofId, "sun_and_shading", () =>
            {
                var inputs = SunLayoutAdapter.ToInputs(layout);
                return inputs.Structure.Items.Count == 0 ? null : PhysicalAnalysisPdf.SunShapes(inputs, SunModel.Analyse(inputs));
            });
        }

        private static void TryBuild(Document doc, RoofFrame frame, double originZFt, Dictionary<string, ElementId> cache, WorksetId analysisWorksetId, string roofId, string key, Func<List<PlanShape>?> compute)
        {
            try
            {
                var shapes = compute();
                if (shapes == null || shapes.Count == 0) return;

                // Roof-scoped like the circulation/axonometric views (GenerateFunctionalDiagramsCommand.RoofScopedName):
                // a fixed title here was the same shared-view bug — building "Sportify - Structural Loads" for a second
                // roof reused and overwrote the first roof's hatches instead of the two coexisting.
                var title = GenerateFunctionalDiagramsCommand.RoofScopedName(ViewTitles.First(v => v.Key == key).Title);
                var view = GenerateFunctionalDiagramsCommand.CreateOrReuseCirculationView(doc, title, configure: false);
                var found = SportifyElementScan.Find(doc, roofId);
                if (!found.IsEmpty) GenerateFunctionalDiagramsCommand.FixViewRangeForRoof(view, found);

                var stale = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).ToElementIds();
                if (stale.Count > 0) doc.Delete(stale);
                var staleLabels = new FilteredElementCollector(doc, view.Id).OfClass(typeof(TextNote)).ToElementIds();
                if (staleLabels.Count > 0) doc.Delete(staleLabels);

                var baseTypeId = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).FirstElementId();
                var solidPatternId = ViewFilterManager.SolidFillPatternId(doc);
                var textTypeId = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstOrDefault()?.Id;
                if (baseTypeId == ElementId.InvalidElementId) return;      // no filled region type in this project template at all — nothing this can build on

                foreach (var shape in shapes)
                {
                    try
                    {
                        var loop = LoopFor(shape, frame, originZFt);
                        if (loop == null) continue;
                        var hex = shape.Ratio.HasValue ? GradientColorFor(shape.Ratio.Value) : shape.Fill;
                        var typeId = GetOrCreateFilledRegionType(doc, cache, hex, baseTypeId, solidPatternId);
                        var region = FilledRegion.Create(doc, typeId, view.Id, new List<CurveLoop> { loop });
                        SportifyLayoutBuilder.SetWorkset(region, analysisWorksetId);
                        if (!string.IsNullOrWhiteSpace(shape.Label)) CreateDiagramLabel(doc, view, shape.Label, Centroid(shape, frame, originZFt), textTypeId, analysisWorksetId);
                    }
                    catch (Exception ex) { SportifyLog.Warn("hatches", $"one {key} shape could not be drawn: " + ex.Message); }
                }
            }
            catch (Exception ex) { SportifyLog.Warn("hatches", $"the {key} hatch view could not be built: " + ex.Message); }
        }

        /// <summary>
        /// An 8-band green→yellow→orange→red gradient across a utilisation ratio (0 = plenty of margin, 1 = right at the limit, further
        /// past 1 for over) — finer than PhysicalAnalysisPdf's own 3-band ok/marginal/over colouring (SvgChart.ForStatus), which stays
        /// exactly as it is for the PDF chart; this is for Revit's own hatch fills only; see PlanShape.Ratio. The bands start and end
        /// close to SvgChart's own Ok/Warn/Bad so a reader used to those still recognises "green means fine, red means over" at a glance.
        /// Ratios beyond about 1.15 (well over) all land on the same deepest red rather than growing without bound.
        /// </summary>
        internal static string GradientColorFor(double ratio)
        {
            var bands = new[] { "#3f9d63", "#6fae53", "#9dbf3f", "#d0b93a", "#e0a030", "#e07a30", "#d9534f", "#b83030" };
            var t = Math.Max(0.0, Math.Min(1.0, ratio / 1.15));
            var idx = (int)Math.Round(t * (bands.Length - 1));
            return bands[Math.Clamp(idx, 0, bands.Length - 1)];
        }

        /// <summary>The shape's own plan points (poly/circle/rect — PlanShape's three kinds), turned into a closed Revit CurveLoop in real world feet at the roof's own top elevation. A circle becomes a 16-sided polygon: close enough at this scale, and CurveLoop needs no Arc-continuity bookkeeping this way.</summary>
        private static CurveLoop? LoopFor(PlanShape shape, RoofFrame frame, double originZFt)
        {
            var planPts = PlanPoints(shape);
            if (planPts.Count < 3) return null;

            var worldPts = planPts.Select(p =>
            {
                var (mx, my) = frame.ToModel(p.X, p.Y);
                return new XYZ(SportifyLayoutBuilder.FeetFromMeters(mx), SportifyLayoutBuilder.FeetFromMeters(my), originZFt);
            }).ToList();

            var loop = new CurveLoop();
            var added = 0;
            for (var i = 0; i < worldPts.Count; i++)
            {
                var a = worldPts[i];
                var b = worldPts[(i + 1) % worldPts.Count];
                if (a.DistanceTo(b) < 0.01) continue;      // a degenerate edge (near-duplicate points) — Line.CreateBound throws on one
                loop.Append(Line.CreateBound(a, b));
                added++;
            }
            return added >= 3 ? loop : null;
        }

        private static List<(double X, double Y)> PlanPoints(PlanShape shape)
        {
            if (shape.Kind == "poly" && shape.Points.Count >= 3)
                return shape.Points.Select(p => (p[0], p[1])).ToList();
            if (shape.Kind == "circle")
            {
                var pts = new List<(double, double)>();
                var r = Math.Max(0.05, shape.W / 2.0);
                for (var i = 0; i < 16; i++) { var t = 2 * Math.PI * i / 16; pts.Add((shape.X + r * Math.Cos(t), shape.Y + r * Math.Sin(t))); }
                return pts;
            }
            // "rect" (PlanShape's default): X,Y is the top-left corner, W,H the size.
            return new List<(double, double)> { (shape.X, shape.Y), (shape.X + shape.W, shape.Y), (shape.X + shape.W, shape.Y + shape.H), (shape.X, shape.Y + shape.H) };
        }

        private static XYZ Centroid(PlanShape shape, RoofFrame frame, double originZFt)
        {
            double cx, cy;
            if (shape.Kind == "circle") { cx = shape.X; cy = shape.Y; }
            else if (shape.Kind == "poly" && shape.Points.Count > 0) { cx = shape.Points.Average(p => p[0]); cy = shape.Points.Average(p => p[1]); }
            else { cx = shape.X + shape.W / 2; cy = shape.Y + shape.H / 2; }
            var (mx, my) = frame.ToModel(cx, cy);
            return new XYZ(SportifyLayoutBuilder.FeetFromMeters(mx), SportifyLayoutBuilder.FeetFromMeters(my), originZFt + 0.05);
        }

        private static void CreateDiagramLabel(Document doc, View view, string text, XYZ origin, ElementId? textTypeId, WorksetId worksetId)
        {
            GenerateFunctionalDiagramsCommand.CreateDiagramLabel(doc, view, text, origin, textTypeId, worksetId);
        }

        /// <summary>A named, coloured, solid-fill FilledRegionType for `hex` — reused across shapes and across runs (looked up by name first, so a rerun does not pile up duplicate types).</summary>
        private static ElementId GetOrCreateFilledRegionType(Document doc, Dictionary<string, ElementId> cache, string hex, ElementId baseTypeId, ElementId solidPatternId)
        {
            if (cache.TryGetValue(hex, out var cached)) return cached;

            var name = "Sportify Hatch " + hex.TrimStart('#').ToUpperInvariant();
            var existing = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().FirstOrDefault(t => t.Name == name);
            FilledRegionType type;
            if (existing != null) type = existing;
            else
            {
                var baseType = (FilledRegionType)doc.GetElement(baseTypeId);
                type = (FilledRegionType)baseType.Duplicate(name);
                var (r, g, b) = ParseHex(hex);
                if (solidPatternId != ElementId.InvalidElementId) type.ForegroundPatternId = solidPatternId;
                type.ForegroundPatternColor = new Autodesk.Revit.DB.Color(r, g, b);
                type.IsMasking = false;
            }
            cache[hex] = type.Id;
            return type.Id;
        }

        private static (byte R, byte G, byte B) ParseHex(string hex)
        {
            hex = hex.TrimStart('#');
            if (hex.Length != 6) return (150, 150, 150);
            try { return (Convert.ToByte(hex.Substring(0, 2), 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16)); }
            catch (Exception) { return (150, 150, 150); }
        }
    }
}
