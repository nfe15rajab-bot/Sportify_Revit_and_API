using System.Globalization;

namespace SportfyRevit
{
    /// <summary>
    /// What Revit's three diagrams draw (Revit-free, so Tools/AddinCheck checks it; SportifyDiagramViews puts it in views) - in the visual style of the
    /// web app's Algorithmic placement plan (user, 2026-09-29): the pieces in the colours that plan gives them (exported with each piece as
    /// diagram_color), the pathways as light grey bands, the garden in green, the entrances in red - all a little less saturated than on screen.
    ///
    ///   Circulation     the plan with its circulation: pieces softened, the paths clear.
    ///   Fire safety     every piece coloured by its walking distance to the nearest entrance against the limit (green to red), with the distance on it;
    ///                   a piece with no route red and saying so; its route in its colour.
    ///   Accessibility   the paths, and each path's clear width in a circle on it - green where it meets the reference width, amber where it does not.
    /// Shapes are in plan metres (x right, y down), drawn in order: ground, paths, pieces, entrances, circles.
    /// </summary>
    internal static class DiagramPlan
    {
        /// <summary>How much the plan's colours are softened toward their own grey and lightened: "the vis style of the web algo but less saturation".</summary>
        internal const double Softening = 0.35;

        // the Algorithmic placement plan's own colours (algoPlacementCore.js COLOR_PATH, COLOR_GARDEN, COLOR_VC) and the board's per-kind ones for pieces it has none for
        internal const string PathGrey = "#bebebe", GardenGreen = "#96c878", EntranceRed = "#c81e1e";
        static readonly Dictionary<string, string> KindColors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["field"] = "#3d6fff", ["activity"] = "#9c4fe0", ["garden"] = "#0ea355", ["gardenBlock"] = "#8b6b4a", ["vegetation"] = "#2f7a43", ["furniture"] = "#8a94a6", ["kinetics"] = "#d08a2e",
        };

        internal sealed record Diagram(List<PlanShape> Shapes, string Caption);

        /// <summary>A stretch of walkway shorter than this (metres) is a crossing or a corner, not a path of its own: no width circle for it.</summary>
        internal const double JunctionM = 2.0;

        /// <summary>A strip narrower than this (metres) is paving or a leftover sliver of the plan, not a walkway: no width circle, not counted as narrow.</summary>
        internal const double MinWalkableM = 0.9;

        /// <summary>Whether a stretch is a path of its own whose width means something: long enough (not a crossing) and wide enough (not a sliver).</summary>
        internal static bool IsPath(PathBand b) => b.LengthM >= JunctionM && b.WidthM >= MinWalkableM;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>A colour softened: mixed toward its own grey (desaturated) by amount, then a little toward white, so the plan reads calmer than on screen.</summary>
        internal static string Soften(string hex, double amount = Softening)
        {
            var (r, g, b) = Rgb(hex);
            double grey = 0.299 * r + 0.587 * g + 0.114 * b;
            double Mix(double c) { var d = c + (grey - c) * amount; return d + (255 - d) * amount * 0.35; }
            return Hex(Mix(r), Mix(g), Mix(b));
        }

        /// <summary>The fire-safety gradient: green (plenty of margin) through yellow and orange to red (at or past the limit), by distance / limit.</summary>
        internal static string Gradient(double ratio)
        {
            var bands = new[] { "#3f9d63", "#6fae53", "#9dbf3f", "#d0b93a", "#e0a030", "#e07a30", "#d9534f", "#b83030" };
            var t = Math.Max(0.0, Math.Min(1.0, ratio / 1.15));
            return bands[Math.Clamp((int)Math.Round(t * (bands.Length - 1)), 0, bands.Length - 1)];
        }

        internal static string PieceColor(PlacementDto p)
        {
            var c = p.DiagramColor;
            if (!string.IsNullOrWhiteSpace(c) && c!.StartsWith("#") && c.Length == 7) return c;
            return KindColors.TryGetValue(p.Category ?? "", out var k) ? k : "#8a94a6";
        }

        // ---------------------------------------------------------------------------------------------------------- the three diagrams

        internal static Diagram Circulation(SportifyLayout layout)
        {
            var shapes = new List<PlanShape>();
            shapes.AddRange(Garden(layout));
            var paths = PathBands(layout);
            shapes.AddRange(paths.Select(b => Band(b, Soften(PathGrey, 0.1))));
            shapes.AddRange(Pieces(layout, p => Soften(PieceColor(p)), p => Short(p.Label)));
            shapes.AddRange(Entrances(layout));
            var total = paths.Sum(b => b.LengthM * b.WidthM);
            return new Diagram(shapes, string.Format(Inv, "Circulation - {0} path(s), about {1:0} m2 of walkway, {2} entrance(s)", paths.Count, total, layout.EntryPoints?.Count ?? 0));
        }

        internal static Diagram FireSafety(SportifyLayout layout, IReadOnlyDictionary<string, double> distancesM, ICollection<string> unreachable, double limitM)
        {
            var shapes = new List<PlanShape>();
            shapes.AddRange(Garden(layout));
            double Ratio(PlacementDto p) => p.Id != null && distancesM.TryGetValue(p.Id, out var d) && limitM > 0 ? d / limitM : 0;
            bool NoRoute(PlacementDto p) => p.Id != null && unreachable.Contains(p.Id);
            // each piece's route to its entrance, in the piece's colour
            var byItem = (layout.Placements ?? new List<PlacementDto>()).Where(p => p.Id != null).ToDictionary(p => p.Id!, p => p);
            foreach (var path in layout.CirculationPaths ?? new List<CirculationPathDto>())
            {
                var colour = path.ItemId != null && byItem.TryGetValue(path.ItemId, out var owner) ? (NoRoute(owner) ? SvgChart.Bad : Gradient(Ratio(owner))) : PathGrey;
                shapes.AddRange(PolylineBands(path.PointsM, Math.Max(0.6, CirculationWidth(layout) * 0.6)).Select(b => Band(b, Soften(colour, 0.2))));
            }
            shapes.AddRange(Pieces(layout,
                p => Soften(NoRoute(p) ? SvgChart.Bad : Gradient(Ratio(p)), 0.2),
                p => NoRoute(p) ? Short(p.Label) + " - no route" : string.Format(Inv, "{0} - {1:0.0} m", Short(p.Label), p.Id != null && distancesM.TryGetValue(p.Id, out var d) ? d : 0),
                p => NoRoute(p) ? 1.5 : Ratio(p)));
            shapes.AddRange(Entrances(layout));
            var reachable = distancesM.Values.Where(v => v > 0).ToList();
            var longest = reachable.Count > 0 ? reachable.Max() : 0;
            var caption = unreachable.Count > 0
                ? string.Format(Inv, "Fire safety - {0} piece(s) with no route to an entrance; longest route {1:0.0} m (limit {2:0} m)", unreachable.Count, longest, limitM)
                : string.Format(Inv, "Fire safety - longest route {0:0.0} m of the {1:0} m limit: {2}", longest, limitM, longest <= limitM ? "within it" : "OVER IT");
            return new Diagram(shapes, caption);
        }

        internal static Diagram Accessibility(SportifyLayout layout, double minWidthM)
        {
            var shapes = new List<PlanShape>();
            shapes.AddRange(Garden(layout));
            var bands = PathBands(layout);
            // a stretch shorter than JunctionM is a crossing or a corner, not a path: grey, no circle, not counted
            shapes.AddRange(bands.Select(b => Band(b, Soften(!IsPath(b) || b.WidthM + 1e-9 >= minWidthM ? PathGrey : SvgChart.Warn, 0.15))));
            shapes.AddRange(Pieces(layout, p => Soften(PieceColor(p), 0.7), p => Short(p.Label)));
            shapes.AddRange(Entrances(layout));
            // the width of each path in a circle on it (one per path, and never two circles on top of each other)
            var placed = new List<(double X, double Y)>();
            foreach (var b in bands.OrderByDescending(b => b.LengthM))
            {
                if (!IsPath(b)) continue;
                if (placed.Any(q => Math.Sqrt((q.X - b.Cx) * (q.X - b.Cx) + (q.Y - b.Cy) * (q.Y - b.Cy)) < 4.0)) continue;
                placed.Add((b.Cx, b.Cy));
                bool ok = b.WidthM + 1e-9 >= minWidthM;
                shapes.Add(new PlanShape
                {
                    Kind = "circle", X = b.Cx, Y = b.Cy, W = 1.9, Fill = Soften(ok ? SvgChart.Ok : SvgChart.Warn, 0.15),
                    Label = string.Format(Inv, "{0:0.0#} m", b.WidthM), LabelColor = "#ffffff",
                });
            }
            var narrow = bands.Count(b => IsPath(b) && b.WidthM + 1e-9 < minWidthM);
            var caption = string.Format(Inv, "Accessibility - clear path widths in the circles; reference {0:0.0#} m: {1}", minWidthM,
                narrow == 0 ? "every path meets it" : narrow + " path(s) narrower");
            return new Diagram(shapes, caption);
        }

        // ---------------------------------------------------------------------------------------------------------- zoning (user, 2026-09-29: "add also a zoning diagram")

        /// <summary>The zones of the Algorithmic placement (algoPlacementCore.js zoneOf): indoor (walls round it), outdoor sport, garden. Their colours, softened when drawn.</summary>
        internal const string IndoorColor = "#6f5bd0", SportZoneColor = "#3d6fff", GardenZoneColor = "#2f9e5b", WallColor = "#3a3f4b";

        static readonly string[] IndoorWords = { "locker", "bathroom", "shower", "rest / hydration", "rest area", "hydration" };

        /// <summary>A piece's zone: the one the web app exported with it, else by its name (the service modules are indoor) and its kind (gardens, plants,
        /// furniture and kinetic shading are garden; courts and activities outdoor sport).</summary>
        internal static string ZoneOf(PlacementDto p)
        {
            var z = (p.Zone ?? "").Trim().ToLowerInvariant();
            if (z is "indoor" or "outdoor" or "garden") return z;
            var label = (p.Label ?? "").ToLowerInvariant();
            if (IndoorWords.Any(w => label.Contains(w))) return "indoor";
            return (p.Category ?? "").ToLowerInvariant() switch { "garden" or "gardenblock" or "vegetation" or "furniture" or "kinetics" => "garden", _ => "outdoor" };
        }

        static (string Name, string Color) ZoneStyle(string zone) => zone switch
        {
            "indoor" => ("Indoor", IndoorColor),
            "garden" => ("Garden", GardenZoneColor),
            _ => ("Sport", SportZoneColor),
        };

        /// <summary>A colour mixed toward white: the pale ground of a zone under its pieces.</summary>
        internal static string Tint(string hex, double toWhite)
        {
            var (r, g, b) = Rgb(hex);
            return Hex(r + (255 - r) * toWhite, g + (255 - g) * toWhite, b + (255 - b) * toWhite);
        }

        /// <summary>How close two pieces of one zone must be (their gap, metres) to be one cluster of it: the widest primary path, 2.5 m, and a little.</summary>
        internal const double ZoneClusterGapM = 2.6;

        /// <summary>
        /// Zoning: every piece in its zone's colour, each cluster of a zone on a pale ground of that colour (its pieces' box, 0.6 m round) labelled with the zone,
        /// how many pieces and their area; the indoor zone's walls dark; the paths, the garden band and the entrances as on the other diagrams.
        /// </summary>
        internal static Diagram Zoning(SportifyLayout layout)
        {
            var shapes = new List<PlanShape>();
            shapes.AddRange(Garden(layout));
            shapes.AddRange(PathBands(layout).Select(b => Band(b, Soften(PathGrey, 0.1))));
            double L = layout.RoofContext?.LengthM ?? 0, W = layout.RoofContext?.WidthM ?? 0;
            var pieces = (layout.Placements ?? new List<PlacementDto>()).Where(p => p.BoundingBox is { WidthM: > 0, HeightM: > 0 }).ToList();
            var areaBy = new Dictionary<string, double>();
            foreach (var zone in new[] { "outdoor", "garden", "indoor" })
            {
                var members = pieces.Where(p => ZoneOf(p) == zone).ToList();
                var (name, color) = ZoneStyle(zone);
                areaBy[zone] = members.Sum(p => p.BoundingBox!.WidthM * p.BoundingBox.HeightM);
                foreach (var group in Clusters(members, ZoneClusterGapM))
                {
                    double x0 = group.Min(p => p.BoundingBox!.TopLeftXM) - 0.6, y0 = group.Min(p => p.BoundingBox!.TopLeftYM) - 0.6;
                    double x1 = group.Max(p => p.BoundingBox!.TopLeftXM + p.BoundingBox.WidthM) + 0.6, y1 = group.Max(p => p.BoundingBox!.TopLeftYM + p.BoundingBox.HeightM) + 0.6;
                    if (L > 0 && W > 0) { x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(L, x1); y1 = Math.Min(W, y1); }
                    var area = group.Sum(p => p.BoundingBox!.WidthM * p.BoundingBox.HeightM);
                    shapes.Add(new PlanShape
                    {
                        Kind = "rect", X = x0, Y = y0, W = x1 - x0, H = y1 - y0, Fill = Tint(Soften(color, 0.2), 0.72),
                        Label = string.Format(Inv, "{0} - {1} piece(s), {2:0} m2", name, group.Count, area), LabelColor = "#222633",
                    });
                }
            }
            foreach (var w in layout.IndoorWalls ?? new List<IndoorWallDto>())
                foreach (var r in w.RectsM ?? new List<List<double>>())
                    if (r is { Count: >= 4 } && r[2] - r[0] > 1e-6 && r[3] - r[1] > 1e-6)
                        shapes.Add(new PlanShape { Kind = "rect", X = r[0], Y = r[1], W = r[2] - r[0], H = r[3] - r[1], Fill = WallColor });
            shapes.AddRange(Pieces(layout, p => Soften(ZoneStyle(ZoneOf(p)).Color, 0.3), _ => ""));
            shapes.AddRange(Entrances(layout));
            var paths = PathBands(layout).Sum(b => b.LengthM * b.WidthM);
            return new Diagram(shapes, string.Format(Inv, "Zoning - indoor {0:0} m2, sport {1:0} m2, garden {2:0} m2 of pieces; about {3:0} m2 of paths",
                areaBy.GetValueOrDefault("indoor"), areaBy.GetValueOrDefault("outdoor"), areaBy.GetValueOrDefault("garden"), paths));
        }

        /// <summary>The pieces of one zone in clusters: two pieces are in one when the gap between their boxes is at most `gapM` (and a cluster is everything so linked).</summary>
        internal static List<List<PlacementDto>> Clusters(List<PlacementDto> pieces, double gapM)
        {
            var parent = Enumerable.Range(0, pieces.Count).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            for (int i = 0; i < pieces.Count; i++)
                for (int j = i + 1; j < pieces.Count; j++)
                {
                    var a = pieces[i].BoundingBox!; var b = pieces[j].BoundingBox!;
                    double gx = Math.Max(0, Math.Max(a.TopLeftXM, b.TopLeftXM) - Math.Min(a.TopLeftXM + a.WidthM, b.TopLeftXM + b.WidthM));
                    double gy = Math.Max(0, Math.Max(a.TopLeftYM, b.TopLeftYM) - Math.Min(a.TopLeftYM + a.HeightM, b.TopLeftYM + b.HeightM));
                    if (Math.Sqrt(gx * gx + gy * gy) <= gapM) parent[Find(i)] = Find(j);
                }
            return Enumerable.Range(0, pieces.Count).GroupBy(Find).Select(g => g.Select(i => pieces[i]).ToList()).ToList();
        }

        // ---------------------------------------------------------------------------------------------------------- the parts

        /// <summary>A stretch of walkway: a rectangle along a path, its width and length (plan metres), its middle.</summary>
        internal sealed record PathBand(double X0, double Y0, double X1, double Y1, double WidthM, double LengthM, double Cx, double Cy, List<double[]>? Poly);

        /// <summary>The walkways: the Algorithmic placement's own path rectangles when the layout has them (with their real widths), else the circulation lines at the design rule's width.</summary>
        internal static List<PathBand> PathBands(SportifyLayout layout)
        {
            if (layout.PathRects is { Count: > 0 } rects)
                return rects.Where(r => r.X1 - r.X0 > 1e-6 && r.Y1 - r.Y0 > 1e-6).Select(r =>
                {
                    double w = r.X1 - r.X0, h = r.Y1 - r.Y0;
                    return new PathBand(r.X0, r.Y0, r.X1, r.Y1, Math.Round(Math.Min(w, h), 2), Math.Max(w, h), (r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2, null);
                }).ToList();
            var width = CirculationWidth(layout);
            return (layout.CirculationPaths ?? new List<CirculationPathDto>()).SelectMany(p => PolylineBands(p.PointsM, width)).ToList();
        }

        static double CirculationWidth(SportifyLayout layout) => layout.DesignRules?.CirculationWidthM is double w && w > 0 ? w : 1.2;

        /// <summary>A polyline as bands of the given width, one per segment (an axis-parallel segment a rectangle, any other a quadrilateral).</summary>
        internal static List<PathBand> PolylineBands(List<PointDto>? pts, double width)
        {
            var bands = new List<PathBand>();
            if (pts == null || pts.Count < 2) return bands;
            for (int i = 0; i + 1 < pts.Count; i++)
            {
                double ax = pts[i].XM, ay = pts[i].YM, bx = pts[i + 1].XM, by = pts[i + 1].YM;
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                if (len < 0.05) continue;
                double h = width / 2, nx = -(by - ay) / len * h, ny = (bx - ax) / len * h;
                var poly = new List<double[]> { new[] { ax + nx, ay + ny }, new[] { bx + nx, by + ny }, new[] { bx - nx, by - ny }, new[] { ax - nx, ay - ny } };
                bands.Add(new PathBand(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by), width, len, (ax + bx) / 2, (ay + by) / 2, poly));
            }
            return bands;
        }

        static PlanShape Band(PathBand b, string fill) => b.Poly != null
            ? new PlanShape { Kind = "poly", Points = b.Poly, Fill = fill }
            : new PlanShape { Kind = "rect", X = b.X0, Y = b.Y0, W = b.X1 - b.X0, H = b.Y1 - b.Y0, Fill = fill };

        static IEnumerable<PlanShape> Garden(SportifyLayout layout)
        {
            foreach (var z in layout.Zones ?? new List<ZoneDto>())
            {
                if (z.Kind != null && !z.Kind.Equals("green_roof", StringComparison.OrdinalIgnoreCase)) continue;
                if (z.Points is { Count: >= 3 } pts)
                    yield return new PlanShape { Kind = "poly", Points = pts.Select(q => new[] { q.XM, q.YM }).ToList(), Fill = Soften(GardenGreen) };
                else if (z.BoundingBox is { } bb)
                    yield return new PlanShape { Kind = "rect", X = bb.TopLeftXM, Y = bb.TopLeftYM, W = bb.WidthM, H = bb.HeightM, Fill = Soften(GardenGreen) };
            }
        }

        static IEnumerable<PlanShape> Pieces(SportifyLayout layout, Func<PlacementDto, string> fill, Func<PlacementDto, string> label, Func<PlacementDto, double>? ratio = null)
        {
            foreach (var p in layout.Placements ?? new List<PlacementDto>())
            {
                if (p.BoundingBox is not { } bb || bb.WidthM <= 0 || bb.HeightM <= 0) continue;
                yield return new PlanShape
                {
                    Kind = "rect", X = bb.TopLeftXM, Y = bb.TopLeftYM, W = bb.WidthM, H = bb.HeightM,
                    Fill = fill(p), Label = label(p), LabelColor = "#222633", Ratio = null,
                };
            }
        }

        static IEnumerable<PlanShape> Entrances(SportifyLayout layout) =>
            (layout.EntryPoints ?? new List<EntryPointDto>()).Select(e => new PlanShape { Kind = "circle", X = e.XM, Y = e.YM, W = 1.2, Fill = Soften(EntranceRed, 0.2), Label = "" });

        static string Short(string? label)
        {
            var s = (label ?? "").Trim();
            return s.Length <= 26 ? s : s.Substring(0, 25).TrimEnd() + "...";
        }

        static (double R, double G, double B) Rgb(string hex)
        {
            hex = (hex ?? "").TrimStart('#');
            if (hex.Length != 6) return (138, 148, 166);
            try { return (Convert.ToInt32(hex.Substring(0, 2), 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16)); }
            catch (Exception) { return (138, 148, 166); }
        }

        static string Hex(double r, double g, double b)
        {
            int C(double v) => Math.Max(0, Math.Min(255, (int)Math.Round(v)));
            return $"#{C(r):x2}{C(g):x2}{C(b):x2}";
        }
    }
}
