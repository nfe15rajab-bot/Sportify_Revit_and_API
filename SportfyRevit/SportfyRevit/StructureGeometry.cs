using System;
using System.Collections.Generic;
using System.Linq;

namespace SportfyRevit
{
    /// <summary>
    /// The structural grid and columns of the model, brought into the roof's own plan coordinates. Free of Revit types on purpose
    /// (the Revit calls that collect the grids and columns live in PushRoofBoundaryCommand), so the one place where a coordinate
    /// convention could go wrong is testable without Revit.
    ///
    /// Convention, the same as for placements, entry points and circulation: the roof's origin is the minimum corner of its bounding
    /// box, x runs right, and y runs DOWN from the roof's top (maximum-Y) edge, so canvas y = roof width - (model Y - minimum Y).
    /// For a roof turned against the model's axes the same holds in the roof's own axes (see RoofFrame): the plan is turned to follow it.
    /// </summary>
    internal static class StructureGeometry
    {
        internal record GridSegment(string Name, double X0, double Y0, double X1, double Y1);   // model coordinates, metres
        internal record ColumnPoint(string Label, double X, double Y);                          // model coordinates, metres
        internal record RoofRect(double MinX, double MinY, double MaxX, double MaxY);           // model coordinates, metres
        internal record BeamRecord(string Name, double X0, double Y0, double X1, double Y1, double WidthM, double DepthM, double TopM);      // model coordinates, metres
        internal record WallRecord(string Name, double X0, double Y0, double X1, double Y1, double ThicknessM, double HeightM, bool Bearing); // model coordinates, metres

        /// <summary>How far outside the roof's bounding box a grid line or column still counts as on the roof.</summary>
        public const double ToleranceM = 0.25;

        public static StructureDto ToRoofLocal(IEnumerable<GridSegment> grids, IEnumerable<ColumnPoint> columns, RoofRect roof)
            => ToRoofLocal(grids, columns, RoofFrame.FromRect(roof));

        public static StructureDto ToRoofLocal(IEnumerable<GridSegment> grids, IEnumerable<ColumnPoint> columns, RoofFrame frame,
                                                IEnumerable<BeamRecord>? beams = null, IEnumerable<WallRecord>? walls = null)
        {
            var lines = new List<GridLineDto>();
            foreach (var g in grids)
            {
                // a grid line is a straight line: it keeps its angle in the roof's own axes, whatever that angle is
                var (ax, ay) = frame.ToLocalUp(g.X0, g.Y0);
                var (bx, by) = frame.ToLocalUp(g.X1, g.Y1);
                if (!Clip(ax, ay, bx, by, frame.Length, frame.Width, ToleranceM, out var x0, out var y0, out var x1, out var y1)) continue;
                lines.Add(new GridLineDto
                {
                    Name = g.Name,
                    StartM = new PointDto { XM = R(x0), YM = R(frame.Width - y0) },
                    EndM = new PointDto { XM = R(x1), YM = R(frame.Width - y1) },
                });
            }

            // A column runs up through every level: keep one per plan position.
            var cols = new List<StructuralColumnDto>();
            foreach (var c in columns)
            {
                var (px, py) = frame.ToPlan(c.X, c.Y);
                if (!frame.InsidePlan(px, py, ToleranceM)) continue;
                var x = R(px);
                var y = R(py);
                if (cols.Any(o => Math.Abs(o.XM - x) < 0.05 && Math.Abs(o.YM - y) < 0.05)) continue;
                cols.Add(new StructuralColumnDto { Label = c.Label, XM = x, YM = y });
            }

            // Beams and walls run like grid lines: the part inside the roof's box, in the roof's own axes.
            List<StructuralBeamDto>? beamDtos = null;
            if (beams != null)
            {
                beamDtos = new List<StructuralBeamDto>();
                foreach (var b in beams)
                {
                    var (ax, ay) = frame.ToLocalUp(b.X0, b.Y0);
                    var (bx, by) = frame.ToLocalUp(b.X1, b.Y1);
                    if (!Clip(ax, ay, bx, by, frame.Length, frame.Width, ToleranceM, out var x0, out var y0, out var x1, out var y1)) continue;
                    beamDtos.Add(new StructuralBeamDto
                    {
                        Name = b.Name,
                        StartM = new PointDto { XM = R(x0), YM = R(frame.Width - y0) }, EndM = new PointDto { XM = R(x1), YM = R(frame.Width - y1) },
                        WidthM = R(b.WidthM), DepthM = R(b.DepthM), TopElevationM = R(b.TopM),
                    });
                }
                beamDtos = beamDtos.OrderBy(b => b.StartM!.XM).ThenBy(b => b.StartM!.YM).ToList();
            }
            List<StructuralWallDto>? wallDtos = null;
            if (walls != null)
            {
                wallDtos = new List<StructuralWallDto>();
                foreach (var w in walls)
                {
                    var (ax, ay) = frame.ToLocalUp(w.X0, w.Y0);
                    var (bx, by) = frame.ToLocalUp(w.X1, w.Y1);
                    if (!Clip(ax, ay, bx, by, frame.Length, frame.Width, ToleranceM, out var x0, out var y0, out var x1, out var y1)) continue;
                    wallDtos.Add(new StructuralWallDto
                    {
                        Name = w.Name,
                        StartM = new PointDto { XM = R(x0), YM = R(frame.Width - y0) }, EndM = new PointDto { XM = R(x1), YM = R(frame.Width - y1) },
                        ThicknessM = R(w.ThicknessM), HeightM = R(w.HeightM), Bearing = w.Bearing,
                    });
                }
                wallDtos = wallDtos.OrderBy(w => w.StartM!.XM).ThenBy(w => w.StartM!.YM).ToList();
            }

            return new StructureDto
            {
                Source = "revit",
                GridLines = lines.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Columns = cols.OrderBy(c => c.XM).ThenBy(c => c.YM).ToList(),
                Beams = beamDtos,
                Walls = wallDtos,
            };
        }

        static double R(double v) => Math.Round(v, 3);

        /// <summary>The part of a segment (in the roof's own axes, y up) inside the roof's bounding rectangle (grown by the tolerance), by Liang-Barsky clipping.</summary>
        static bool Clip(double sx0, double sy0, double sx1, double sy1, double length, double width, double tol, out double x0, out double y0, out double x1, out double y1)
        {
            double xMin = -tol, xMax = length + tol, yMin = -tol, yMax = width + tol;
            double dx = sx1 - sx0, dy = sy1 - sy0;
            double t0 = 0, t1 = 1;
            x0 = sx0; y0 = sy0; x1 = sx1; y1 = sy1;

            bool Edge(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;           // parallel to this edge: inside or outside it entirely
                var r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
                return true;
            }

            if (!Edge(-dx, sx0 - xMin) || !Edge(dx, xMax - sx0) || !Edge(-dy, sy0 - yMin) || !Edge(dy, yMax - sy0)) return false;
            if (t1 - t0 < 1e-9) return false;

            x0 = sx0 + t0 * dx; y0 = sy0 + t0 * dy;
            x1 = sx0 + t1 * dx; y1 = sy0 + t1 * dy;
            return true;
        }
    }
}
