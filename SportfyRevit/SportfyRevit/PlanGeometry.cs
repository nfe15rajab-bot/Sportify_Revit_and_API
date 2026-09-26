namespace SportfyRevit
{
    /// <summary>
    /// Plan-view polygon tests for the roof finish's holes, without Revit: is a hole a real polygon, is it inside the roof, does it overlap another hole. Coordinates are plain
    /// numbers in any one consistent unit (the builder passes feet); the tolerance is in the same unit. Honest about what it is: simple polygons, no clipping, the answers Revit's
    /// Floor.Create needs (it refuses a sketch whose loops cross or lie outside the boundary and names six possible causes without saying which).
    /// Touching is allowed by these tests (a court on the roof edge, two zones that share a side, are ordinary layouts); Revit is not so lenient, so the builder insets every hole (Inset).
    /// </summary>
    internal static class PlanGeometry
    {
        public readonly record struct P(double X, double Y);

        public static double SignedArea(IReadOnlyList<P> poly)
        {
            double twice = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                twice += a.X * b.Y - b.X * a.Y;
            }
            return twice / 2;
        }

        public static double Area(IReadOnlyList<P> poly) => Math.Abs(SignedArea(poly));

        /// <summary>The polygon without repeated points (a pushed outline can carry duplicates) and without a closing point equal to the first.</summary>
        public static List<P> Clean(IEnumerable<P> points, double tol)
        {
            var result = new List<P>();
            foreach (var p in points)
                if (result.Count == 0 || Dist(result[^1], p) > tol) result.Add(p);
            while (result.Count > 1 && Dist(result[0], result[^1]) <= tol) result.RemoveAt(result.Count - 1);
            return result;
        }

        static double Dist(P a, P b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        static double Cross(P o, P a, P b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        /// <summary>Distance from p to the segment a-b.</summary>
        static double DistToSegment(P p, P a, P b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            if (len2 <= 0) return Dist(p, a);
            double t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            return Dist(p, new P(a.X + t * dx, a.Y + t * dy));
        }

        public static bool OnBoundary(IReadOnlyList<P> poly, P p, double tol)
        {
            for (int i = 0; i < poly.Count; i++) if (DistToSegment(p, poly[i], poly[(i + 1) % poly.Count]) <= tol) return true;
            return false;
        }

        /// <summary>Strictly inside (ray casting); a point on the boundary is not.</summary>
        public static bool StrictlyInside(IReadOnlyList<P> poly, P p, double tol)
        {
            if (OnBoundary(poly, p, tol)) return false;
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        public static bool InsideOrOn(IReadOnlyList<P> poly, P p, double tol) => OnBoundary(poly, p, tol) || StrictlyInside(poly, p, tol);

        /// <summary>The two segments cross at a point interior to both (not touching at an end, not lying along each other).</summary>
        static bool ProperCrossing(P a, P b, P c, P d, double tol)
        {
            double d1 = Cross(a, b, c), d2 = Cross(a, b, d), d3 = Cross(c, d, a), d4 = Cross(c, d, b);
            double la = Dist(a, b), lc = Dist(c, d);
            if (la <= tol || lc <= tol) return false;
            // signed distances of the ends from the other segment's line, in length units
            double e1 = d1 / la, e2 = d2 / la, e3 = d3 / lc, e4 = d4 / lc;
            return ((e1 > tol && e2 < -tol) || (e1 < -tol && e2 > tol)) && ((e3 > tol && e4 < -tol) || (e3 < -tol && e4 > tol));
        }

        /// <summary>No two edges of the polygon cross (edges that meet at a shared corner do not count). A bow-tie is not simple.</summary>
        public static bool IsSimple(IReadOnlyList<P> poly, double tol)
        {
            int n = poly.Count;
            if (n < 3) return false;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                    if (ProperCrossing(poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n], tol)) return false;
                }
            return true;
        }

        /// <summary>The hole lies in the outline: every corner is inside or on it and no edge of the hole crosses an edge of the outline. A hole that pokes through the edge is a notch, not a hole.</summary>
        public static bool HoleInside(IReadOnlyList<P> outline, IReadOnlyList<P> hole, double tol)
        {
            foreach (var p in hole) if (!InsideOrOn(outline, p, tol)) return false;
            int n = outline.Count, m = hole.Count;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < m; j++)
                    if (ProperCrossing(outline[i], outline[(i + 1) % n], hole[j], hole[(j + 1) % m], tol)) return false;
            // a concave outline can still have a hole whose corners are all inside while an edge runs out and back in through a notch: the midpoints of the hole's edges too
            for (int j = 0; j < m; j++)
            {
                var a = hole[j]; var b = hole[(j + 1) % m];
                if (!InsideOrOn(outline, new P((a.X + b.X) / 2, (a.Y + b.Y) / 2), tol)) return false;
            }
            return true;
        }

        /// <summary>Two polygons share area (touching along a side or at a corner is not sharing).</summary>
        public static bool Overlap(IReadOnlyList<P> a, IReadOnlyList<P> b, double tol)
        {
            if (a.Max(p => p.X) <= b.Min(p => p.X) + tol || b.Max(p => p.X) <= a.Min(p => p.X) + tol || a.Max(p => p.Y) <= b.Min(p => p.Y) + tol || b.Max(p => p.Y) <= a.Min(p => p.Y) + tol) return false;
            int n = a.Count, m = b.Count;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < m; j++)
                    if (ProperCrossing(a[i], a[(i + 1) % n], b[j], b[(j + 1) % m], tol)) return true;
            if (a.Any(p => StrictlyInside(b, p, tol)) || b.Any(p => StrictlyInside(a, p, tol))) return true;
            // identical or all-corners-on-the-boundary cases (the same rectangle twice): the middle of an edge, or a point known to be inside
            for (int i = 0; i < n; i++) if (StrictlyInside(b, new P((a[i].X + a[(i + 1) % n].X) / 2, (a[i].Y + a[(i + 1) % n].Y) / 2), tol)) return true;
            for (int j = 0; j < m; j++) if (StrictlyInside(a, new P((b[j].X + b[(j + 1) % m].X) / 2, (b[j].Y + b[(j + 1) % m].Y) / 2), tol)) return true;
            var ca = Centroid(a);
            return StrictlyInside(a, ca, tol) && StrictlyInside(b, ca, tol);
        }

        /// <summary>
        /// The polygon moved inward by <paramref name="d"/> on every side (each edge offset, corners at the meeting of the offset edges), or null when it is too thin to survive that.
        /// Revit's Floor.Create takes two holes that share an edge, or a hole that lies on the outline, as loops that intersect and refuses the whole sketch (found by the live import:
        /// the zones and courts of a packed roof all touch). A hole a few millimetres smaller leaves a strip of finish that thin between neighbours: not visible, not measurable, valid.
        /// </summary>
        public static List<P>? Inset(IReadOnlyList<P> poly, double d)
        {
            int n = poly.Count;
            if (n < 3) return null;
            double sign = SignedArea(poly) >= 0 ? 1 : -1;          // counter-clockwise: the inside is to the left of each edge
            var lines = new List<(P A, P B)>();
            for (int i = 0; i < n; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % n];
                double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
                if (len <= 0) return null;
                double nx = -dy / len * sign, ny = dx / len * sign;   // the inward normal
                lines.Add((new P(a.X + nx * d, a.Y + ny * d), new P(b.X + nx * d, b.Y + ny * d)));
            }
            var result = new List<P>();
            for (int i = 0; i < n; i++)
            {
                var (a1, a2) = lines[(i + n - 1) % n];
                var (b1, b2) = lines[i];
                double r = (a2.X - a1.X) * (b2.Y - b1.Y) - (a2.Y - a1.Y) * (b2.X - b1.X);
                if (Math.Abs(r) < 1e-12) { result.Add(b1); continue; }               // parallel: the offset edge's own end
                double t = ((b1.X - a1.X) * (b2.Y - b1.Y) - (b1.Y - a1.Y) * (b2.X - b1.X)) / r;
                result.Add(new P(a1.X + t * (a2.X - a1.X), a1.Y + t * (a2.Y - a1.Y)));
            }
            // it must still be a polygon of the same turning and a smaller, positive area
            if (Math.Sign(SignedArea(result)) != Math.Sign(SignedArea(poly)) || Area(result) >= Area(poly) || Area(result) <= 0) return null;
            return IsSimple(result, d / 10) ? result : null;
        }

        public static P Centroid(IReadOnlyList<P> poly) => new(poly.Average(p => p.X), poly.Average(p => p.Y));
    }
}
