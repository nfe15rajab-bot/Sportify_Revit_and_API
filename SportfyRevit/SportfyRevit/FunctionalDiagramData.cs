using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Reads what a Sportify import actually built (SportifyElementScan) into the plain shapes the bubble and spine diagrams draw — the Revit-touching half of the functional
    /// diagrams; FunctionalDiagramSvg (the bubble) and SvgChart.Plan (the spine, reused) do the Revit-free drawing. Scoped to the PLACEMENTS (courts, activities, furniture,
    /// planting) as the diagram's "rooms": a zone or the roof finish is ground cover, not a discrete functional space the way a court or a bench is, so it is left out — the
    /// spine diagram's own outline is drawn from the placements' extent instead of the roof boundary, which nothing here reads.
    /// </summary>
    internal static class FunctionalDiagramData
    {
        /// <summary>One piece as the diagrams see it: real position/size/area (metres, roof-local, y down) and its human label.</summary>
        internal sealed record Piece(string Id, string Label, string? Category, double XM, double YM, double WM, double HM, double AreaM2);

        internal sealed record Entry(double XM, double YM);

        internal sealed class Data
        {
            public List<Piece> Pieces = new();
            public List<Entry> Entries = new();
            public double LengthM, WidthM;         // the drawn extent (placements' own bounding box + a margin), not necessarily the real roof size
        }

        static double M(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);

        /// <summary>Everything the diagrams need, in one pass over what the last import built. Best-effort per element: one piece that cannot be read is skipped, not fatal.</summary>
        public static Data Collect(Document doc)
        {
            var elements = SportifyElementScan.Find(doc).Elements;

            // labels: TextNote.Create put each piece's own label at its exact X, Y (FamilyPlacementBuilder.PlaceComponent — only Z differs, by the label's own thickness offset),
            // so "the text note at this X, Y" is a precise, reliable match — no fuzzy nearest-neighbour search needed.
            var labelsByXY = new Dictionary<(long X10, long Y10), string>();
            foreach (var tn in elements.OfType<TextNote>())
            {
                var p = tn.Coord;
                labelsByXY[(Round10(p.X), Round10(p.Y))] = tn.Text;
            }
            string? LabelNear(XYZ p) => labelsByXY.TryGetValue((Round10(p.X), Round10(p.Y)), out var t) ? t : null;
            long Round10(double ft) => (long)Math.Round(ft * 10);   // a tenth of a foot: closer than any two distinct labels could legitimately land

            var pieces = new List<Piece>();
            foreach (var el in elements)
            {
                if (el is TextNote || el is ModelCurve) continue;
                var category = SportifyElementScan.CategoryOf(el);
                if (category == null) continue;                     // not a piece: a zone/roof-finish floor (Sportify_Category is only stamped on placements) or something else
                var bb = el.get_BoundingBox(null);
                if (bb == null) continue;

                var centerFt = (bb.Min + bb.Max) / 2;
                // the real, unrotated footprint from the stamped dimensions (SportifySharedParameters) where present, since a rotated piece's own Revit bounding box is
                // axis-aligned to the MODEL and so larger than its true footprint for anything not turned a multiple of 90 degrees
                var lengthM = ParamM(el, "Sportify_LengthM"); var widthM = ParamM(el, "Sportify_WidthM");
                double wM = lengthM ?? M(bb.Max.X - bb.Min.X), hM = widthM ?? M(bb.Max.Y - bb.Min.Y);
                if (wM <= 0 || hM <= 0) continue;

                var label = LabelNear(centerFt) ?? el.LookupParameter("Sportify_QualityKey")?.AsString() ?? Cap(category);
                pieces.Add(new Piece(el.UniqueId, label, category, M(centerFt.X) - wM / 2, M(centerFt.Y) - hM / 2, wM, hM, wM * hM));
            }

            // entries: the one shape among the import's own model curves that is a single closed arc (SportifyLayoutBuilder.CreateEntryMarkers) — the roof boundary, the
            // setback and the circulation paths are all straight ModelCurve segments, so "a closed Arc" alone tells an entry marker apart from them.
            var entries = new List<Entry>();
            foreach (var mc in elements.OfType<ModelCurve>())
            {
                if (mc.GeometryCurve is not Arc arc) continue;
                if (!arc.GetEndPoint(0).IsAlmostEqualTo(arc.GetEndPoint(1))) continue;
                var c = (arc.GetEndPoint(0) + arc.Evaluate(0.5, true)) / 2;    // the arc's own centre: on a full circle, any two points average toward it — cheap and exact enough
                entries.Add(new Entry(M(c.X), M(c.Y)));
            }

            var data = new Data { Pieces = pieces, Entries = entries };
            if (pieces.Count == 0 && entries.Count == 0) return data;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in pieces) { minX = Math.Min(minX, p.XM); minY = Math.Min(minY, p.YM); maxX = Math.Max(maxX, p.XM + p.WM); maxY = Math.Max(maxY, p.YM + p.HM); }
            foreach (var e in entries) { minX = Math.Min(minX, e.XM); minY = Math.Min(minY, e.YM); maxX = Math.Max(maxX, e.XM); maxY = Math.Max(maxY, e.YM); }
            const double margin = 1.5;
            data.Pieces = pieces.Select(p => p with { XM = p.XM - minX + margin, YM = p.YM - minY + margin }).ToList();
            data.Entries = entries.Select(e => e with { XM = e.XM - minX + margin, YM = e.YM - minY + margin }).ToList();
            data.LengthM = maxX - minX + margin * 2;
            data.WidthM = maxY - minY + margin * 2;
            return data;
        }

        static double? ParamM(Element el, string name)
        {
            var p = el.LookupParameter(name);
            if (p == null || string.IsNullOrWhiteSpace(p.AsString())) return null;
            return double.TryParse(p.AsString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
        }

        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ------------------------------------------------------------------ turning Data into the two diagrams

        /// <summary>The spine (to-scale) diagram: each piece a rounded box at its real position, coloured by BimRules' own category palette, the real walkable route from
        /// every piece to its entry drawn as a thick red spine underneath, a red dot per entry — CirculationEngine.ComputeTravelPaths run on a layout built from Data alone
        /// (no roof_context, no design rules survive an import — a plain 1.2 m circulation width is assumed, the same default CirculationEngine itself falls back to).</summary>
        public static string SpineSvg(Data data)
        {
            if (data.Pieces.Count == 0) return FunctionalDiagramSvg.Bubble("Sportify — Circulation Spine (nothing to diagram)", Array.Empty<BubbleNode>(), Array.Empty<BubbleEdge>());

            var layout = new SportifyLayout
            {
                RoofContext = new RoofContextDto { LengthM = data.LengthM, WidthM = data.WidthM },
                DesignRules = new DesignRulesDto { CirculationWidthM = 1.2 },
                Placements = data.Pieces.Select(p => new PlacementDto { Id = p.Id, BoundingBox = new BoundingBoxDto { TopLeftXM = p.XM, TopLeftYM = p.YM, WidthM = p.WM, HeightM = p.HM } }).ToList(),
                EntryPoints = data.Entries.Select(e => new EntryPointDto { XM = e.XM, YM = e.YM, Edge = NearestEdge(e, data.LengthM, data.WidthM) }).ToList(),
            };
            var paths = data.Entries.Count > 0 ? CirculationEngine.ComputeTravelPaths(layout) : new Dictionary<string, List<(double X, double Y)>>();

            var shapes = data.Pieces.Select(p =>
            {
                var (r, g, b) = BimRules.ColorForCategory(p.Category);
                return new PlanShape
                {
                    Kind = "rect", X = p.XM, Y = p.YM, W = p.WM, H = p.HM,
                    Fill = $"#{r:X2}{g:X2}{b:X2}", FillOpacity = 0.9, Stroke = "#1a1a1a", StrokeWidthM = 0.05, CornerRadiusM = 0.3,
                    Label = p.Label, LabelColor = "#ffffff",
                };
            }).ToList();
            shapes.AddRange(data.Entries.Select(e => new PlanShape { Kind = "circle", X = e.XM, Y = e.YM, W = 0.9, Fill = "#c0142c", Stroke = "#ffffff", StrokeWidthM = 0.04 }));

            var spine = paths.Values.Where(pts => pts.Count >= 2).Select(pts => new PlanPolyline
            {
                Points = pts.Select(pt => new[] { pt.X, pt.Y }).ToList(),
                Color = "#c0142c", WidthM = 0.45,
            }).ToList();

            return SvgChart.Plan("Sportify — Circulation Spine", data.LengthM, data.WidthM, null, shapes, CategoryLegend(data.Pieces), paths: spine);
        }

        /// <summary>The bubble (relationship) diagram: one node per piece plus one "Entry" hub node, a strong dotted line from every piece to the entry when a walkable route
        /// exists (CirculationEngine again — the same graph the spine draws, just not to scale), and a weak line to each piece's single nearest OTHER piece so the diagram
        /// reads as a web of relationships rather than a bare star, the way a hand-drawn bubble diagram always has a few room-to-room lines beside the hall's own.</summary>
        public static string BubbleSvg(Data data)
        {
            var nodes = data.Pieces.Select(p => new BubbleNode(p.Id, p.Label, p.AreaM2)).ToList();
            if (nodes.Count == 0) return FunctionalDiagramSvg.Bubble("Sportify — Bubble Diagram (nothing to diagram)", nodes, Array.Empty<BubbleEdge>());

            const string entryId = "__entry__";
            var edges = new List<BubbleEdge>();
            var reachable = new HashSet<string>();
            if (data.Entries.Count > 0)
            {
                nodes.Add(new BubbleNode(entryId, data.Entries.Count == 1 ? "Entry" : "Entries", 4));
                var layout = new SportifyLayout
                {
                    RoofContext = new RoofContextDto { LengthM = data.LengthM, WidthM = data.WidthM },
                    DesignRules = new DesignRulesDto { CirculationWidthM = 1.2 },
                    Placements = data.Pieces.Select(p => new PlacementDto { Id = p.Id, BoundingBox = new BoundingBoxDto { TopLeftXM = p.XM, TopLeftYM = p.YM, WidthM = p.WM, HeightM = p.HM } }).ToList(),
                    EntryPoints = data.Entries.Select(e => new EntryPointDto { XM = e.XM, YM = e.YM, Edge = NearestEdge(e, data.LengthM, data.WidthM) }).ToList(),
                };
                var paths = CirculationEngine.ComputeTravelPaths(layout);
                foreach (var id in paths.Keys) { edges.Add(new BubbleEdge(entryId, id, true)); reachable.Add(id); }
            }

            // every piece the entry graph missed (no walkable route, or no entry at all) still gets a line to its nearest other piece, and every piece gets ONE such line
            // besides its entry line, so the diagram is never just a flower of spokes off the hub
            foreach (var p in data.Pieces)
            {
                var nearest = data.Pieces.Where(q => q.Id != p.Id)
                    .OrderBy(q => Math.Pow(q.XM + q.WM / 2 - (p.XM + p.WM / 2), 2) + Math.Pow(q.YM + q.HM / 2 - (p.YM + p.HM / 2), 2))
                    .FirstOrDefault();
                if (nearest == null) continue;
                var already = edges.Any(e => (e.A == p.Id && e.B == nearest.Id) || (e.A == nearest.Id && e.B == p.Id));
                if (!already) edges.Add(new BubbleEdge(p.Id, nearest.Id, false));
            }

            return FunctionalDiagramSvg.Bubble("Sportify — Bubble Diagram", nodes, edges);
        }

        static string NearestEdge(Entry e, double lengthM, double widthM)
        {
            var dLeft = e.XM; var dRight = lengthM - e.XM; var dTop = e.YM; var dBottom = widthM - e.YM;
            var min = Math.Min(Math.Min(dLeft, dRight), Math.Min(dTop, dBottom));
            return min == dLeft ? "left" : min == dRight ? "right" : min == dTop ? "top" : "bottom";
        }

        static IEnumerable<(string text, string color)> CategoryLegend(List<Piece> pieces)
        {
            foreach (var cat in pieces.Select(p => p.Category ?? "").Distinct().OrderBy(c => c))
            {
                if (cat.Length == 0) continue;
                var (r, g, b) = BimRules.ColorForCategory(cat);
                yield return (Cap(cat), $"#{r:X2}{g:X2}{b:X2}");
            }
        }
    }
}
