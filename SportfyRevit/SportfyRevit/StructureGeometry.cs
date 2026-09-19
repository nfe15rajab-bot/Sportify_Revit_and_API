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
    /// </summary>
    internal static class StructureGeometry
    {
        internal record GridSegment(string Name, double X0, double Y0, double X1, double Y1);   // model coordinates, metres
        internal record ColumnPoint(string Label, double X, double Y);                          // model coordinates, metres
        internal record RoofRect(double MinX, double MinY, double MaxX, double MaxY);           // model coordinates, metres

        /// <summary>How far outside the roof's bounding box a grid line or column still counts as on the roof.</summary>
        public const double ToleranceM = 0.25;

        public static StructureDto ToRoofLocal(IEnumerable<GridSegment> grids, IEnumerable<ColumnPoint> columns, RoofRect roof)
        {
            var width = roof.MaxY - roof.MinY;
            var lines = new List<GridLineDto>();
            foreach (var g in grids)
            {
                if (!Clip(g, roof, ToleranceM, out var x0, out var y0, out var x1, out var y1)) continue;
                lines.Add(new GridLineDto
                {
                    Name = g.Name,
                    StartM = new PointDto { XM = R(x0 - roof.MinX), YM = R(width - (y0 - roof.MinY)) },
                    EndM = new PointDto { XM = R(x1 - roof.MinX), YM = R(width - (y1 - roof.MinY)) },
                });
            }

            // A column runs up through every level: keep one per plan position.
            var cols = new List<StructuralColumnDto>();
            foreach (var c in columns)
            {
                if (c.X < roof.MinX - ToleranceM || c.X > roof.MaxX + ToleranceM || c.Y < roof.MinY - ToleranceM || c.Y > roof.MaxY + ToleranceM) continue;
                var x = R(c.X - roof.MinX);
                var y = R(width - (c.Y - roof.MinY));
                if (cols.Any(o => Math.Abs(o.XM - x) < 0.05 && Math.Abs(o.YM - y) < 0.05)) continue;
                cols.Add(new StructuralColumnDto { Label = c.Label, XM = x, YM = y });
            }

            return new StructureDto
            {
                Source = "revit",
                GridLines = lines.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList(),
                Columns = cols.OrderBy(c => c.XM).ThenBy(c => c.YM).ToList(),
            };
        }

        static double R(double v) => Math.Round(v, 3);

        /// <summary>The part of a segment inside the roof's bounding box (grown by the tolerance), by Liang-Barsky clipping.</summary>
        static bool Clip(GridSegment g, RoofRect roof, double tol, out double x0, out double y0, out double x1, out double y1)
        {
            double xMin = roof.MinX - tol, xMax = roof.MaxX + tol, yMin = roof.MinY - tol, yMax = roof.MaxY + tol;
            double dx = g.X1 - g.X0, dy = g.Y1 - g.Y0;
            double t0 = 0, t1 = 1;
            x0 = g.X0; y0 = g.Y0; x1 = g.X1; y1 = g.Y1;

            bool Edge(double p, double q)
            {
                if (Math.Abs(p) < 1e-12) return q >= 0;           // parallel to this edge: inside or outside it entirely
                var r = q / p;
                if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
                return true;
            }

            if (!Edge(-dx, g.X0 - xMin) || !Edge(dx, xMax - g.X0) || !Edge(-dy, g.Y0 - yMin) || !Edge(dy, yMax - g.Y0)) return false;
            if (t1 - t0 < 1e-9) return false;

            x0 = g.X0 + t0 * dx; y0 = g.Y0 + t0 * dy;
            x1 = g.X0 + t1 * dx; y1 = g.Y0 + t1 * dy;
            return true;
        }
    }
}
