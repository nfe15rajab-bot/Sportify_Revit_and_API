#nullable disable
using System;
using System.Collections.Generic;

// ---------------------------------------------------------------------------------------------------------------
//  The roof's real shape, for every analysis that used to work on its bounding rectangle.
//
//  The roof arrives as a length, a width and (from a Revit push) an outline: the polygon of the top face in the roof's own plan
//  coordinates (x right, y DOWN from the top edge, metres, the same as everything else in a layout). A rectangle needs no outline; an
//  L-shaped, stepped or chamfered roof does. RoofShape holds it, cleaned (repeated and collinear points removed, one winding), and
//  answers the questions the analyses ask: is this point on the roof, how much of this cell is roof, where are the edges and which
//  way do they face, what does a bay (the roof cut by grid lines) look like.
//
//  Plain C# with no Unity types: compiled into the Unity project, the Revit add-in and the check tools, like the analyses' cores.
//
//  A roof whose outline is its bounding rectangle (or that has none) is IsRectangle: every analysis then takes exactly the path it
//  took before this class existed, so their numbers do not change for such roofs.
// ---------------------------------------------------------------------------------------------------------------
namespace Sportify.Simulation.Roof
{
    /// <summary>One straight stretch of the roof's outline.</summary>
    public sealed class RoofEdge
    {
        public double X0, Y0, X1, Y1;
        public double Length;
        public double NormalX, NormalY;          // unit, pointing OUT of the roof
        public int Index;                        // position in the outline (after cleaning)

        /// <summary>The plan side this edge faces: "top", "bottom", "left" or "right" (the outward normal's dominant axis; y grows downward).</summary>
        public string Side
        {
            get
            {
                if (Math.Abs(NormalX) >= Math.Abs(NormalY)) return NormalX > 0 ? "right" : "left";
                return NormalY > 0 ? "bottom" : "top";
            }
        }
    }

    public sealed class RoofShape
    {
        public const double RectangleToleranceM = 0.05;

        public double Length, Width;
        /// <summary>The cleaned outline (no repeated first point, counter-clockwise by the shoelace sign), or null when the roof is a rectangle.</summary>
        public List<double[]> Outline;
        public bool IsRectangle { get { return Outline == null; } }
        public double Area;
        public double CentroidX, CentroidY;
        public List<RoofEdge> Edges = new List<RoofEdge>();

        RoofShape() { }

        public static RoofShape Rectangle(double length, double width)
        {
            var s = new RoofShape { Length = length, Width = width, Area = length * width, CentroidX = length / 2.0, CentroidY = width / 2.0 };
            s.BuildEdges(RectPoints(0, 0, length, width));
            return s;
        }

        /// <summary>The roof of the given size with the given outline (null, empty or degenerate: a rectangle).</summary>
        public static RoofShape Create(double length, double width, IList<double[]> outline)
        {
            var poly = Clean(outline);
            if (poly == null || poly.Count < 3) return Rectangle(length, width);
            var area = SignedArea(poly);
            if (Math.Abs(area) < 1e-6) return Rectangle(length, width);
            if (area < 0) poly.Reverse();                                    // one winding: positive shoelace sign

            if (IsAxisRectangle(poly, length, width)) return Rectangle(length, width);

            var s = new RoofShape { Length = length, Width = width, Outline = poly, Area = Math.Abs(area) };
            double cx = 0, cy = 0;
            for (var i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                var cr = a[0] * b[1] - b[0] * a[1];
                cx += (a[0] + b[0]) * cr;
                cy += (a[1] + b[1]) * cr;
            }
            var a6 = 6.0 * Math.Abs(area);
            s.CentroidX = cx / (6.0 * SignedArea(poly));
            s.CentroidY = cy / (6.0 * SignedArea(poly));
            if (double.IsNaN(s.CentroidX) || a6 < 1e-12) { s.CentroidX = length / 2.0; s.CentroidY = width / 2.0; }
            s.BuildEdges(poly);
            return s;
        }

        // ---------------------------------------------------------------- polygons

        public static List<double[]> RectPoints(double x0, double y0, double x1, double y1)
        {
            return new List<double[]> { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } };
        }

        /// <summary>The shoelace sum / 2: positive for the winding whose interior lies to the left of each edge (in the plan's own numbers).</summary>
        public static double SignedArea(IList<double[]> p)
        {
            double sum = 0;
            for (var i = 0; i < p.Count; i++)
            {
                var a = p[i];
                var b = p[(i + 1) % p.Count];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return sum / 2.0;
        }

        public static double PolygonArea(IList<double[]> p)
        {
            return p == null || p.Count < 3 ? 0 : Math.Abs(SignedArea(p));
        }

        /// <summary>Drops repeated points, a repeated closing point and vertices that lie on the line between their neighbours.</summary>
        public static List<double[]> Clean(IList<double[]> outline)
        {
            if (outline == null || outline.Count < 3) return null;
            var pts = new List<double[]>();
            foreach (var p in outline)
            {
                if (p == null || p.Length < 2) continue;
                if (pts.Count > 0 && Math.Abs(p[0] - pts[pts.Count - 1][0]) < 1e-6 && Math.Abs(p[1] - pts[pts.Count - 1][1]) < 1e-6) continue;
                pts.Add(new[] { p[0], p[1] });
            }
            while (pts.Count > 1 && Math.Abs(pts[0][0] - pts[pts.Count - 1][0]) < 1e-6 && Math.Abs(pts[0][1] - pts[pts.Count - 1][1]) < 1e-6) pts.RemoveAt(pts.Count - 1);

            var changed = true;
            while (changed && pts.Count >= 3)
            {
                changed = false;
                for (var i = 0; i < pts.Count; i++)
                {
                    var a = pts[(i + pts.Count - 1) % pts.Count];
                    var b = pts[i];
                    var c = pts[(i + 1) % pts.Count];
                    double abx = b[0] - a[0], aby = b[1] - a[1], bcx = c[0] - b[0], bcy = c[1] - b[1];
                    var cross = abx * bcy - aby * bcx;
                    var lens = Math.Sqrt(abx * abx + aby * aby) * Math.Sqrt(bcx * bcx + bcy * bcy);
                    if (lens < 1e-12 || (Math.Abs(cross) < 1e-9 * Math.Max(1.0, lens) && abx * bcx + aby * bcy > 0))
                    {
                        pts.RemoveAt(i);
                        changed = true;
                        break;
                    }
                }
            }
            return pts.Count >= 3 ? pts : null;
        }

        static bool IsAxisRectangle(List<double[]> poly, double length, double width)
        {
            if (poly.Count != 4) return false;
            var t = RectangleToleranceM;
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in poly)
            {
                minX = Math.Min(minX, p[0]); maxX = Math.Max(maxX, p[0]);
                minY = Math.Min(minY, p[1]); maxY = Math.Max(maxY, p[1]);
            }
            if (Math.Abs(minX) > t || Math.Abs(minY) > t || Math.Abs(maxX - length) > t || Math.Abs(maxY - width) > t) return false;
            for (var i = 0; i < 4; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % 4];
                if (Math.Abs(a[0] - b[0]) > t && Math.Abs(a[1] - b[1]) > t) return false;      // an edge that is neither vertical nor horizontal
            }
            return true;
        }

        void BuildEdges(List<double[]> poly)
        {
            Edges.Clear();
            var n = poly.Count;
            var positive = SignedArea(poly) >= 0;
            for (var i = 0; i < n; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % n];
                double dx = b[0] - a[0], dy = b[1] - a[1];
                var len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                // interior on the left for a positive winding, so the outward normal is the right-hand one
                var nx = positive ? dy / len : -dy / len;
                var ny = positive ? -dx / len : dx / len;
                Edges.Add(new RoofEdge { X0 = a[0], Y0 = a[1], X1 = b[0], Y1 = b[1], Length = len, NormalX = nx, NormalY = ny, Index = i });
            }
        }

        /// <summary>The corners of the roof as a polygon (the outline, or the rectangle).</summary>
        public List<double[]> Polygon
        {
            get { return Outline ?? RectPoints(0, 0, Length, Width); }
        }

        // ---------------------------------------------------------------- questions

        /// <summary>Whether a point is on the roof (a point on the edge counts).</summary>
        public bool Contains(double x, double y)
        {
            const double e = 1e-9;
            if (IsRectangle) return x >= -e && x <= Length + e && y >= -e && y <= Width + e;
            var p = Outline;
            var inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double xi = p[i][0], yi = p[i][1], xj = p[j][0], yj = p[j][1];
                if (OnSegment(xi, yi, xj, yj, x, y)) return true;
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        static bool OnSegment(double ax, double ay, double bx, double by, double x, double y)
        {
            double dx = bx - ax, dy = by - ay;
            var len2 = dx * dx + dy * dy;
            if (len2 < 1e-18) return false;
            var t = ((x - ax) * dx + (y - ay) * dy) / len2;
            if (t < -1e-9 || t > 1 + 1e-9) return false;
            var px = ax + t * dx - x;
            var py = ay + t * dy - y;
            return px * px + py * py < 1e-14;
        }

        /// <summary>The share (0 to 1) of an axis-aligned rectangle that is roof.</summary>
        public double Coverage(double x0, double y0, double x1, double y1)
        {
            var area = (x1 - x0) * (y1 - y0);
            if (area <= 1e-15) return 0;
            if (IsRectangle)
            {
                var ox = Math.Min(x1, Length) - Math.Max(x0, 0);
                var oy = Math.Min(y1, Width) - Math.Max(y0, 0);
                return ox <= 0 || oy <= 0 ? 0 : Math.Min(1.0, ox * oy / area);
            }
            var clipped = ClipConvex(Outline, RectPoints(x0, y0, x1, y1));
            return Math.Min(1.0, PolygonArea(clipped) / area);
        }

        /// <summary>How far the roof reaches along a direction (its extent when projected on the unit vector): the crosswind dimension of the roof for a wind blowing square to it.</summary>
        public double ExtentAlong(double ux, double uy)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var p in Polygon)
            {
                var t = p[0] * ux + p[1] * uy;
                if (t < lo) lo = t;
                if (t > hi) hi = t;
            }
            return hi - lo;
        }

        /// <summary>The distance from a point to the nearest outline edge (0 on it), the same for a point off the roof.</summary>
        public double DistanceToEdge(double x, double y)
        {
            var best = double.MaxValue;
            foreach (var e in Edges) best = Math.Min(best, DistanceToSegment(e.X0, e.Y0, e.X1, e.Y1, x, y));
            return best;
        }

        public static double DistanceToSegment(double ax, double ay, double bx, double by, double x, double y)
        {
            double dx = bx - ax, dy = by - ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
            var px = ax + t * dx - x;
            var py = ay + t * dy - y;
            return Math.Sqrt(px * px + py * py);
        }

        /// <summary>The point on the outline nearest to (x, y).</summary>
        public double[] NearestOnEdge(double x, double y)
        {
            double[] best = null;
            var bd = double.MaxValue;
            foreach (var e in Edges)
            {
                double dx = e.X1 - e.X0, dy = e.Y1 - e.Y0;
                var len2 = dx * dx + dy * dy;
                var t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((x - e.X0) * dx + (y - e.Y0) * dy) / len2));
                var px = e.X0 + t * dx;
                var py = e.Y0 + t * dy;
                var d = (px - x) * (px - x) + (py - y) * (py - y);
                if (d < bd) { bd = d; best = new[] { px, py }; }
            }
            return best ?? new[] { x, y };
        }

        /// <summary>Whether the straight segment a-b stays on the roof (no proper crossing of the outline, both ends on it).</summary>
        public bool SegmentInside(double ax, double ay, double bx, double by)
        {
            if (IsRectangle) return Contains(ax, ay) && Contains(bx, by);
            if (!Contains(ax, ay) || !Contains(bx, by)) return false;
            foreach (var e in Edges)
                if (ProperCrossing(ax, ay, bx, by, e.X0, e.Y0, e.X1, e.Y1)) return false;
            // no crossing and both ends inside: the midpoint decides the rare case of a segment running along a notch
            return Contains((ax + bx) / 2.0, (ay + by) / 2.0);
        }

        static bool ProperCrossing(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
        {
            double d1 = Cross(cx, cy, dx, dy, ax, ay), d2 = Cross(cx, cy, dx, dy, bx, by);
            double d3 = Cross(ax, ay, bx, by, cx, cy), d4 = Cross(ax, ay, bx, by, dx, dy);
            const double eps = 1e-9;
            return ((d1 > eps && d2 < -eps) || (d1 < -eps && d2 > eps)) && ((d3 > eps && d4 < -eps) || (d3 < -eps && d4 > eps));
        }

        static double Cross(double ax, double ay, double bx, double by, double px, double py)
        {
            return (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        }

        // ---------------------------------------------------------------- clipping

        /// <summary>
        /// The part of a polygon (any shape) inside a CONVEX polygon (Sutherland-Hodgman). Where the result would be in several pieces the
        /// pieces come out joined by zero-width bridges: the area is still right, which is all the analyses take from it.
        /// </summary>
        public static List<double[]> ClipConvex(IList<double[]> subject, IList<double[]> convex)
        {
            var output = new List<double[]>(subject);
            if (output.Count == 0 || convex.Count < 3) return new List<double[]>();
            var ccw = SignedArea(convex) >= 0;
            for (var i = 0; i < convex.Count && output.Count > 0; i++)
            {
                var a = convex[i];
                var b = convex[(i + 1) % convex.Count];
                output = ClipHalfPlane(output, a[0], a[1], b[0], b[1], ccw);
            }
            return output;
        }

        /// <summary>The part of a polygon on the LEFT of the directed line a->b (or the right one when leftIsInside is false).</summary>
        public static List<double[]> ClipHalfPlane(IList<double[]> poly, double ax, double ay, double bx, double by, bool leftIsInside = true)
        {
            var result = new List<double[]>();
            var n = poly.Count;
            if (n == 0) return result;
            double Side(double[] p) { var c = (bx - ax) * (p[1] - ay) - (by - ay) * (p[0] - ax); return leftIsInside ? c : -c; }
            var prev = poly[n - 1];
            var prevSide = Side(prev);
            for (var i = 0; i < n; i++)
            {
                var cur = poly[i];
                var curSide = Side(cur);
                if (curSide >= 0)
                {
                    if (prevSide < 0) result.Add(Intersect(prev, cur, prevSide, curSide));
                    result.Add(cur);
                }
                else if (prevSide >= 0)
                {
                    result.Add(Intersect(prev, cur, prevSide, curSide));
                }
                prev = cur;
                prevSide = curSide;
            }
            return result;
        }

        static double[] Intersect(double[] p, double[] q, double sp, double sq)
        {
            var t = sp / (sp - sq);
            return new[] { p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1]) };
        }
    }
}
