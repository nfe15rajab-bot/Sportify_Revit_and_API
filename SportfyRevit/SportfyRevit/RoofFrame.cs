using System;
using System.Collections.Generic;
using System.Linq;

namespace SportfyRevit
{
    /// <summary>
    /// The roof's own plan frame: where the plan's origin sits in the Revit model, and how far the plan is turned from the model's axes.
    ///
    /// A building is rarely drawn square to the model's X and Y. A roof turned 30 degrees has an axis-aligned bounding box far bigger than
    /// itself, and everything measured against that box (the pieces the designer places, the structure's grid, the bays, the wind zones)
    /// is wrong. So the plan is turned to follow the roof: x runs along the roof's own long direction (found from its outline), y runs DOWN
    /// from its top edge, and the origin is the minimum corner of the outline's bounding box in THOSE axes.
    ///
    ///     model point P  ->  a = (P - origin) . u,   b = (P - origin) . v          u = (cos t, sin t),  v = (-sin t, cos t)
    ///     plan x = a,   plan y = Width - b          (y DOWN, the convention of every placement, entry, path and analysis)
    ///     ("local-up" coordinates (a, b) are the same with y up: what the roof's boundary polygon keeps, see PushRoofBoundaryCommand)
    ///
    /// For a roof square to the model (t = 0) this is exactly the old convention: origin = the bounding box's minimum corner, plan y =
    /// width - (Y - minimum Y). Free of Revit types on purpose, so the one place where a coordinate convention could go wrong is testable
    /// without Revit (Sportify.Simulation/Tools/AddinCheck). The import (SportifyLayoutBuilder) builds the inverse from the same numbers.
    /// </summary>
    internal sealed class RoofFrame
    {
        /// <summary>A turn smaller than this (degrees) is not a turn: the roof is square to the model.</summary>
        public const double MinTurnDeg = 0.25;

        /// <summary>The turn must save at least this share of the bounding box's area to be worth it (a round or organic roof has no axis).</summary>
        public const double MinAreaGain = 0.01;

        /// <summary>An edge within this many degrees of a direction (modulo 90) runs that way.</summary>
        public const double AlignedDeg = 3.0;

        /// <summary>The share of the outline's length that must run along a direction for it to be the roof's axis.</summary>
        public const double AxisSupport = 0.5;

        public double OriginX { get; }                 // model coordinates, metres
        public double OriginY { get; }
        public double AngleRad { get; }                // of the plan's x axis from the model's X axis, counter-clockwise
        public double Length { get; }
        public double Width { get; }
        readonly double _cos, _sin;

        public RoofFrame(double originX, double originY, double angleRad, double length, double width)
        {
            OriginX = originX; OriginY = originY; AngleRad = angleRad; Length = length; Width = width;
            _cos = angleRad == 0 ? 1.0 : Math.Cos(angleRad);
            _sin = angleRad == 0 ? 0.0 : Math.Sin(angleRad);
        }

        public double AngleDeg => AngleRad * 180.0 / Math.PI;
        public bool IsTurned => AngleRad != 0;

        /// <summary>The frame of a roof square to the model: origin at the box's minimum corner.</summary>
        public static RoofFrame Axis(double minX, double minY, double maxX, double maxY) => new RoofFrame(minX, minY, 0, maxX - minX, maxY - minY);

        public static RoofFrame FromRect(StructureGeometry.RoofRect r) => Axis(r.MinX, r.MinY, r.MaxX, r.MaxY);

        /// <summary>
        /// The frame for a roof with no real Revit model behind it (drawn or typed in the web app, never pushed): the geometry's own middle (G,
        /// the centre of its Length x Width box) sits at the project's origin, instead of its corner — which put a wide roof's far side a long
        /// way from wherever a designer happened to be looking. At angle 0 (always, for a roof with no push to have turned it) this is simply
        /// origin = (-Length/2, -Width/2); the general form below also holds for a turned box, local centre (Length/2, Width/2) -> model (0, 0).
        /// </summary>
        public static RoofFrame Centered(double lengthM, double widthM, double angleRad)
        {
            double halfL = lengthM / 2.0, halfW = widthM / 2.0;
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            return new RoofFrame(-(halfL * c - halfW * s), -(halfL * s + halfW * c), angleRad, lengthM, widthM);
        }

        /// <summary>
        /// The frame that follows the roof: the direction of one of the outline's edges (folded into +-45 degrees, since a rectangle repeats every
        /// 90) that gives the smallest bounding rectangle. When that turn is negligible, or barely shrinks the box, the roof is taken as square to
        /// the model and the given bounding box is used as it always was.
        /// </summary>
        public static RoofFrame Fit(IReadOnlyList<(double X, double Y)>? outline, double minX, double minY, double maxX, double maxY)
        {
            var axis = Axis(minX, minY, maxX, maxY);
            if (outline == null || outline.Count < 3) return axis;

            var pts = outline.ToList();
            double AreaAt(double t, out double a0, out double b0, out double a1, out double b1)
            {
                double c = Math.Cos(t), s = Math.Sin(t);
                a0 = b0 = double.MaxValue; a1 = b1 = double.MinValue;
                foreach (var p in pts)
                {
                    var a = p.X * c + p.Y * s;
                    var b = -p.X * s + p.Y * c;
                    if (a < a0) a0 = a; if (a > a1) a1 = a;
                    if (b < b0) b0 = b; if (b > b1) b1 = b;
                }
                return (a1 - a0) * (b1 - b0);
            }

            // the direction of every edge folded into [-45, 45] degrees, and how much of the outline runs that way (a rectangle repeats every 90)
            var edges = new List<(double T, double Len)>();
            double perimeter = 0;
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i]; var q = pts[(i + 1) % pts.Count];
                double dx = q.X - p.X, dy = q.Y - p.Y;
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 0.05) continue;
                var t = Math.Atan2(dy, dx);
                t -= Math.Round(t / (Math.PI / 2.0)) * (Math.PI / 2.0);
                edges.Add((t, len));
                perimeter += len;
            }

            var straight = AreaAt(0, out _, out _, out _, out _);
            var bestT = 0.0;
            var bestArea = straight;
            foreach (var (t, _) in edges)
            {
                // an axis needs support: at least half the outline runs within a few degrees of it (a round or organic roof has none)
                var support = edges.Where(e => { var d = Math.Abs(e.T - t); return Math.Min(d, Math.PI / 2.0 - d) < AlignedDeg * Math.PI / 180.0; }).Sum(e => e.Len);
                if (support < AxisSupport * perimeter) continue;
                var area = AreaAt(t, out _, out _, out _, out _);
                // strictly smaller, or equally small with a smaller turn
                if (area < bestArea - 1e-9 * Math.Max(1, bestArea) || (Math.Abs(area - bestArea) <= 1e-9 * Math.Max(1, bestArea) && Math.Abs(t) < Math.Abs(bestT))) { bestArea = area; bestT = t; }
            }

            if (Math.Abs(bestT) * 180.0 / Math.PI < MinTurnDeg || bestArea > straight * (1.0 - MinAreaGain)) return axis;

            AreaAt(bestT, out var A0, out var B0, out var A1, out var B1);
            double cc = Math.Cos(bestT), ss = Math.Sin(bestT);
            var ox = A0 * cc - B0 * ss;                      // the point with a = A0, b = B0
            var oy = A0 * ss + B0 * cc;
            return new RoofFrame(ox, oy, bestT, A1 - A0, B1 - B0);
        }

        /// <summary>The plan coordinates (y down) of a model point.</summary>
        public (double X, double Y) ToPlan(double mx, double my)
        {
            var (a, b) = ToLocalUp(mx, my);
            return (a, Width - b);
        }

        /// <summary>The same with y up (what the roof's boundary polygon keeps).</summary>
        public (double A, double B) ToLocalUp(double mx, double my)
        {
            double dx = mx - OriginX, dy = my - OriginY;
            return (dx * _cos + dy * _sin, -dx * _sin + dy * _cos);
        }

        /// <summary>The model point of plan coordinates (y down).</summary>
        public (double X, double Y) ToModel(double planX, double planY) => FromLocalUp(planX, Width - planY);

        public (double X, double Y) FromLocalUp(double a, double b) => (OriginX + a * _cos - b * _sin, OriginY + a * _sin + b * _cos);

        /// <summary>
        /// A point that was worked out as if the roof were square to the model (the origin plus the plan's x, the origin plus the height above the roof's bottom edge:
        /// what the plain convention gives) turned about the origin into the model, as the frame turns it: the same point as FromLocalUp of its offset from the origin.
        /// For code that computes in the roof's own axes and in the origin's own unit (the floor builder works in feet) and turns only what it has finished, so that
        /// what it computed, tested and compared is exactly what it was before roofs could be turned. Free of the instance so that it takes the origin in that unit.
        /// </summary>
        public static (double X, double Y) TurnAbout(double originX, double originY, double angleRad, double x, double y)
        {
            if (angleRad == 0) return (x, y);
            double dx = x - originX, dy = y - originY, c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            return (originX + dx * c - dy * s, originY + dx * s + dy * c);
        }

        /// <summary>Whether plan coordinates lie within the roof's bounding rectangle, grown by the tolerance.</summary>
        public bool InsidePlan(double x, double y, double tol) => x >= -tol && x <= Length + tol && y >= -tol && y <= Width + tol;
    }
}
