using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SportfyRevit
{
    // ---------------------------------------------------------------------------------------
    //  What else the Revit model knows about the roof, besides its outline and its structure: the openings in it, the stairs, cores
    //  and doors that reach it, its edge (parapet, railing or open), its drains, the build-up of the slab and the levels. It travels
    //  with the roof push (PushRoofBoundaryCommand -> the web app's Combine tab) as "roof.features", and on into every export as
    //  "roof_context.features", so the rules that decide where things may stand (combining rules, live sync) have the real roof
    //  to work with instead of a blank rectangle.
    //
    //  This file is free of Revit types on purpose: the Revit calls that COLLECT these things live in RoofFeatureCollector.cs and
    //  hand plain records to Build(), so the one place where a coordinate convention could go wrong is testable without Revit
    //  (Sportify.Simulation/Tools/AddinCheck).
    //
    //  Convention, the same as for placements, entry points, circulation and the structure: the roof's origin is the minimum corner
    //  of its bounding box, x runs right and y runs DOWN from the roof's top (maximum-Y) edge, so canvas y = roof width -
    //  (model Y - minimum Y). A roof turned against the model's axes has its own axes (RoofFrame): the same convention holds in them.
    //  Everything is in metres. See ROOF_FEATURES.md at the repository root for the JSON contract.
    //  (The DTO classes it produces, RoofFeaturesDto and its parts, live in SportifyLayoutDto.cs with the rest of the layout's JSON.)
    // ---------------------------------------------------------------------------------------

    /// <summary>Turns what the collectors read from the model into the features block, in the roof's own plan coordinates.</summary>
    internal static class RoofFeaturesGeometry
    {
        internal record PointM(double X, double Y);                                                                 // model coordinates, metres
        internal record OpeningLoop(IReadOnlyList<PointM> Points);
        internal record EntryRecord(string Kind, string Name, double X, double Y, double WidthM, long SourceId);
        /// <summary>A wall or railing that stands on the roof; HeightM is above the roof's top face.</summary>
        internal record EdgeElement(string Kind, string Name, double X0, double Y0, double X1, double Y1, double HeightM, double ThicknessM);
        internal record DrainRecord(string Kind, string Name, double X, double Y, long SourceId);
        /// <summary>
        /// A piece of plant on the roof: the middle of its footprint (model coordinates, metres), its own width and depth, the angle its width runs at
        /// (radians from the model's X axis), its height, and its weight in kN when the family says.
        /// </summary>
        internal record EquipmentRecord(string Kind, string Name, double CentreX, double CentreY, double WidthM, double DepthM, double AngleRad, double HeightM, double? WeightKn, long SourceId);
        internal record LayerRecord(string Function, string Material, double ThicknessM);
        internal record SlabRecord(string TypeName, IReadOnlyList<LayerRecord> Layers);
        internal record LevelRecord(string Name, double ElevationM);

        /// <summary>How far beyond the roof's bounding box a door or stair still counts as reaching the roof (a stair house at its edge).</summary>
        public const double EntryReachM = 3.0;

        /// <summary>A wall or railing this close to an outline edge, and parallel to it, stands on that edge.</summary>
        public const double EdgeToleranceM = 0.6;

        /// <summary>Within this angle (degrees) a wall is parallel to an edge.</summary>
        public const double ParallelDeg = 10.0;

        /// <summary>An opening smaller than this is a modelling artefact, not a hole.</summary>
        public const double MinOpeningM2 = 0.05;

        /// <summary>Two entries of one kind closer than this are one (a stair has several runs, a door several leaves).</summary>
        public const double DuplicateM = 0.75;

        /// <param name="roof">The roof's bounding box in model coordinates (a roof square to the model's axes).</param>
        /// <param name="outline">The outer boundary of the top face in model coordinates (vertices in order), or empty: the bounding box is used.</param>
        /// <param name="groundM">The elevation the roof height was measured from, or null when no ground was found.</param>
        /// <param name="roofTopM">Elevation of the roof's top face.</param>
        /// <param name="roofLevelName">The name of the level the roof is placed on, when the model says so; otherwise the highest level at or below the top face.</param>
        public static RoofFeaturesDto Build(
            StructureGeometry.RoofRect roof,
            IReadOnlyList<PointM> outline,
            IEnumerable<OpeningLoop> openings,
            IEnumerable<EntryRecord> entries,
            IEnumerable<EdgeElement> edgeElements,
            IEnumerable<DrainRecord> drains,
            SlabRecord? slab,
            IEnumerable<LevelRecord> levels,
            double roofTopM,
            double? groundM,
            string? roofLevelName,
            IEnumerable<string>? notes = null,
            IEnumerable<EdgeElement>? obstacleElements = null,
            IEnumerable<EquipmentRecord>? equipment = null)
            => Build(RoofFrame.FromRect(roof), outline, openings, entries, edgeElements, drains, slab, levels, roofTopM, groundM, roofLevelName, notes, obstacleElements, equipment);

        /// <param name="frame">The roof's own plan frame: where its origin is in the model and how far it is turned.</param>
        public static RoofFeaturesDto Build(
            RoofFrame frame,
            IReadOnlyList<PointM> outline,
            IEnumerable<OpeningLoop> openings,
            IEnumerable<EntryRecord> entries,
            IEnumerable<EdgeElement> edgeElements,
            IEnumerable<DrainRecord> drains,
            SlabRecord? slab,
            IEnumerable<LevelRecord> levels,
            double roofTopM,
            double? groundM,
            string? roofLevelName,
            IEnumerable<string>? notes = null,
            IEnumerable<EdgeElement>? obstacleElements = null,
            IEnumerable<EquipmentRecord>? equipment = null)
        {
            var width = frame.Width;
            var length = frame.Length;
            (double x, double y) ToCanvas(double mx, double my) => frame.ToPlan(mx, my);

            var dto = new RoofFeaturesDto();
            if (notes != null) dto.Notes.AddRange(notes);

            // ---- openings
            var index = 0;
            foreach (var loop in openings)
            {
                var pts = loop.Points.Select(p => ToCanvas(p.X, p.Y)).ToList();
                if (pts.Count > 1 && Same(pts[0], pts[^1])) pts.RemoveAt(pts.Count - 1);
                if (pts.Count < 3) continue;
                var area = Math.Abs(Area(pts));
                if (area < MinOpeningM2) continue;
                double x0 = pts.Min(p => p.x), x1 = pts.Max(p => p.x), y0 = pts.Min(p => p.y), y1 = pts.Max(p => p.y);
                if (x1 < -StructureGeometry.ToleranceM || x0 > length + StructureGeometry.ToleranceM || y1 < -StructureGeometry.ToleranceM || y0 > width + StructureGeometry.ToleranceM) continue;
                dto.Openings.Add(new RoofOpeningDto
                {
                    Id = $"opening_{++index}",
                    PolygonM = pts.Select(p => new PointDto { XM = R(p.x), YM = R(p.y) }).ToList(),
                    AreaM2 = Math.Round(area, 2),
                    XM = R(x0), YM = R(y0), WidthM = R(x1 - x0), HeightM = R(y1 - y0),
                });
            }

            // ---- entries: the ones on the roof, or just beyond its edge (a stair house), one per kind and place
            var kept = new List<RoofEntryDto>();
            foreach (var e in entries)
            {
                var (cx, cy) = ToCanvas(e.X, e.Y);
                if (!frame.InsidePlan(cx, cy, EntryReachM)) continue;
                if (kept.Any(o => o.Kind == e.Kind && Math.Abs(o.XM - cx) < DuplicateM && Math.Abs(o.YM - cy) < DuplicateM)) continue;
                var onRoof = frame.InsidePlan(cx, cy, StructureGeometry.ToleranceM);
                kept.Add(new RoofEntryDto { Kind = e.Kind, Name = e.Name, XM = R(cx), YM = R(cy), WidthM = R(e.WidthM), OnRoof = onRoof, SourceElementId = e.SourceId });
            }
            var counters = new Dictionary<string, int>();
            foreach (var e in kept.OrderBy(k => k.Kind).ThenBy(k => k.XM).ThenBy(k => k.YM))
            {
                counters[e.Kind] = counters.GetValueOrDefault(e.Kind) + 1;
                e.Id = $"{e.Kind}_{counters[e.Kind]}";
                dto.Entries.Add(e);
            }

            // ---- edges: each straight stretch of the outline against the parapets and railings that stand along it
            var outlineCanvas = (outline.Count >= 3
                ? outline.Select(p => ToCanvas(p.X, p.Y)).ToList()
                : new List<(double x, double y)> { (0, 0), (length, 0), (length, width), (0, width) });
            var elements = edgeElements.Select(w =>
            {
                var a = ToCanvas(w.X0, w.Y0); var b = ToCanvas(w.X1, w.Y1);
                return (w.Kind, a, b, w.HeightM, w.ThicknessM);
            }).ToList();
            for (var i = 0; i < outlineCanvas.Count; i++)
            {
                var a = outlineCanvas[i]; var b = outlineCanvas[(i + 1) % outlineCanvas.Count];
                var len = Dist(a, b);
                if (len < 0.05) continue;
                double parapet = 0, railing = 0, hParapet = 0, hRailing = 0, tParapet = 0;
                foreach (var w in elements)
                {
                    var overlap = OverlapAlong(a, b, w.a, w.b, Math.Max(EdgeToleranceM, w.ThicknessM));
                    if (overlap <= 0) continue;
                    if (w.Kind == "railing") { railing += overlap; hRailing += overlap * w.HeightM; }
                    else { parapet += overlap; hParapet += overlap * w.HeightM; tParapet += overlap * w.ThicknessM; }
                }
                var pc = Math.Min(1.0, parapet / len);
                var rc = Math.Min(1.0, railing / len);
                var covered = Math.Min(1.0, pc + rc);
                var kind = covered >= 0.5 ? (pc >= rc ? "parapet" : "railing") : covered >= 0.1 ? "partial" : "open";
                double height = 0, thickness = 0;
                if (kind == "parapet" || (kind == "partial" && pc >= rc)) { height = parapet > 0 ? hParapet / parapet : 0; thickness = parapet > 0 ? tParapet / parapet : 0; }
                else if (kind == "railing" || kind == "partial") { height = railing > 0 ? hRailing / railing : 0; }
                dto.Edges.Add(new RoofEdgeDto
                {
                    Index = i,
                    StartM = new PointDto { XM = R(a.x), YM = R(a.y) },
                    EndM = new PointDto { XM = R(b.x), YM = R(b.y) },
                    LengthM = Math.Round(len, 2),
                    Kind = kind,
                    HeightM = Math.Round(height, 2),
                    ThicknessM = Math.Round(thickness, 2),
                    ParapetCoverage = Math.Round(pc, 2),
                    RailingCoverage = Math.Round(rc, 2),
                });
            }

            // ---- obstacles: the walls standing on the roof, whatever their height (they cast shadows), on the roof or just beyond its edge
            var obstacleIndex = 0;
            foreach (var w in (obstacleElements ?? Enumerable.Empty<EdgeElement>()))
            {
                bool Near(double px, double py) { var (qx, qy) = ToCanvas(px, py); return frame.InsidePlan(qx, qy, EntryReachM); }
                if (!Near(w.X0, w.Y0) && !Near(w.X1, w.Y1)) continue;
                if (Math.Abs(w.X1 - w.X0) + Math.Abs(w.Y1 - w.Y0) < 0.05 || w.HeightM <= 0) continue;
                var a = ToCanvas(w.X0, w.Y0); var b = ToCanvas(w.X1, w.Y1);
                dto.Obstacles.Add(new RoofObstacleDto
                {
                    Id = $"obstacle_{++obstacleIndex}", Name = w.Name,
                    StartM = new PointDto { XM = R(a.x), YM = R(a.y) }, EndM = new PointDto { XM = R(b.x), YM = R(b.y) },
                    HeightM = Math.Round(w.HeightM, 2), ThicknessM = Math.Round(w.ThicknessM, 2),
                });
            }

            // ---- equipment: what stands on the roof itself; its box in the plan is the box of its four corners turned into the roof's own axes
            var equipmentList = new List<RoofEquipmentDto>();
            foreach (var q in equipment ?? Enumerable.Empty<EquipmentRecord>())
            {
                // its footprint turned into the roof's axes: a box of its own width and depth at the angle between them, and the plan's box round it
                var (cx, cy) = ToCanvas(q.CentreX, q.CentreY);
                if (!frame.InsidePlan(cx, cy, StructureGeometry.ToleranceM)) continue;
                var phi = q.AngleRad - frame.AngleRad;
                double c = Math.Abs(Math.Cos(phi)), sn = Math.Abs(Math.Sin(phi));
                double boxW = q.WidthM * c + q.DepthM * sn, boxD = q.WidthM * sn + q.DepthM * c;
                equipmentList.Add(new RoofEquipmentDto
                {
                    Kind = q.Kind, Name = q.Name, XM = R(cx), YM = R(cy), WidthM = R(boxW), DepthM = R(boxD), HeightM = R(q.HeightM),
                    WeightKn = q.WeightKn.HasValue ? Math.Round(q.WeightKn.Value, 2) : null, SourceElementId = q.SourceId,
                });
            }
            var equipmentCounters = new Dictionary<string, int>();
            foreach (var e in equipmentList.OrderBy(k => k.Kind).ThenBy(k => k.XM).ThenBy(k => k.YM))
            {
                equipmentCounters[e.Kind] = equipmentCounters.GetValueOrDefault(e.Kind) + 1;
                e.Id = $"{e.Kind}_{equipmentCounters[e.Kind]}";
                dto.Equipment.Add(e);
            }

            // ---- drains
            var drainList = new List<RoofDrainDto>();
            foreach (var d in drains)
            {
                var (cx, cy) = ToCanvas(d.X, d.Y);
                if (!frame.InsidePlan(cx, cy, StructureGeometry.ToleranceM)) continue;
                if (drainList.Any(o => Math.Abs(o.XM - cx) < 0.05 && Math.Abs(o.YM - cy) < 0.05)) continue;
                drainList.Add(new RoofDrainDto { Kind = d.Kind, Name = d.Name, XM = R(cx), YM = R(cy), SourceElementId = d.SourceId });
            }
            var drainCounters = new Dictionary<string, int>();
            foreach (var d in drainList.OrderBy(k => k.Kind).ThenBy(k => k.XM).ThenBy(k => k.YM))
            {
                drainCounters[d.Kind] = drainCounters.GetValueOrDefault(d.Kind) + 1;
                d.Id = $"{d.Kind}_{drainCounters[d.Kind]}";
                dto.Drains.Add(d);
            }

            // ---- slab
            if (slab != null && slab.Layers.Count > 0)
            {
                var layers = slab.Layers.Where(l => l.ThicknessM > 0).ToList();
                dto.Slab = new RoofSlabDto
                {
                    TypeName = slab.TypeName,
                    ThicknessM = Math.Round(layers.Sum(l => l.ThicknessM), 3),
                    StructuralThicknessM = Math.Round(layers.Where(l => l.Function is "Structure" or "StructuralDeck").Sum(l => l.ThicknessM), 3),
                    Layers = layers.Select(l => new RoofSlabLayerDto { Function = l.Function, Material = l.Material, ThicknessM = Math.Round(l.ThicknessM, 3) }).ToList(),
                };
            }

            // ---- levels
            var ordered = levels.OrderBy(l => l.ElevationM).ToList();
            var roofLevel = !string.IsNullOrEmpty(roofLevelName) && ordered.Any(l => l.Name == roofLevelName)
                ? ordered.First(l => l.Name == roofLevelName)
                : ordered.LastOrDefault(l => l.ElevationM <= roofTopM + 0.05);
            foreach (var l in ordered)
                dto.Levels.Add(new RoofLevelDto
                {
                    Name = l.Name,
                    ElevationM = Math.Round(l.ElevationM, 3),
                    AboveGroundM = groundM.HasValue ? Math.Round(l.ElevationM - groundM.Value, 3) : null,
                    IsRoofLevel = roofLevel != null && l == roofLevel,
                });

            return dto;
        }

        static double R(double v) => Math.Round(v, 2);
        static bool Same((double x, double y) a, (double x, double y) b) => Math.Abs(a.x - b.x) < 1e-6 && Math.Abs(a.y - b.y) < 1e-6;
        static double Dist((double x, double y) a, (double x, double y) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));

        static double Area(IReadOnlyList<(double x, double y)> pts)
        {
            double sum = 0;
            for (var i = 0; i < pts.Count; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % pts.Count];
                sum += a.x * b.y - b.x * a.y;
            }
            return sum / 2.0;
        }

        /// <summary>
        /// The length of the outline edge a-b that the wall w0-w1 stands along: zero unless the wall is parallel to the edge (within
        /// ParallelDeg) and its line is within the tolerance of the edge's; otherwise how much of the wall's shadow falls on the edge.
        /// </summary>
        static double OverlapAlong((double x, double y) a, (double x, double y) b, (double x, double y) w0, (double x, double y) w1, double toleranceM)
        {
            double ex = b.x - a.x, ey = b.y - a.y;
            var elen = Math.Sqrt(ex * ex + ey * ey);
            double wx = w1.x - w0.x, wy = w1.y - w0.y;
            var wlen = Math.Sqrt(wx * wx + wy * wy);
            if (elen < 1e-9 || wlen < 1e-9) return 0;

            var cos = Math.Abs(ex * wx + ey * wy) / (elen * wlen);
            if (cos < Math.Cos(ParallelDeg * Math.PI / 180.0)) return 0;

            // distance of each wall end from the edge's line, and its position along the edge
            double ux = ex / elen, uy = ey / elen;
            double D(double px, double py) => Math.Abs((px - a.x) * uy - (py - a.y) * ux);
            if (D(w0.x, w0.y) > toleranceM || D(w1.x, w1.y) > toleranceM) return 0;
            double T(double px, double py) => (px - a.x) * ux + (py - a.y) * uy;
            var t0 = Math.Min(T(w0.x, w0.y), T(w1.x, w1.y));
            var t1 = Math.Max(T(w0.x, w0.y), T(w1.x, w1.y));
            return Math.Max(0, Math.Min(t1, elen) - Math.Max(t0, 0));
        }
    }
}
