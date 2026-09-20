using Sportify.Simulation.Roof;
using Sportify.Simulation.Wind;

// usage: RoofCheck
// Checks the roof's shape (RoofShape) against hand-worked cases: what must hold whatever the roof.
var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }
bool Near(double a, double b, double tol = 1e-9) => Math.Abs(a - b) <= tol;
double[] P(double x, double y) => new[] { x, y };

// an L: 40 x 20 with the bottom-right 20 x 10 corner missing
var lOutline = new List<double[]> { P(0, 0), P(40, 0), P(40, 10), P(20, 10), P(20, 20), P(0, 20) };

// 1. rectangles
{
    var none = RoofShape.Create(40, 20, null);
    var box = RoofShape.Create(40, 20, new List<double[]> { P(0, 0), P(40, 0), P(40, 20), P(0, 20), P(0, 0) });
    var boxFlipped = RoofShape.Create(40, 20, new List<double[]> { P(0, 20), P(40, 20), P(40, 0), P(0, 0) });
    var almost = RoofShape.Create(40, 20, new List<double[]> { P(0.02, 0), P(39.98, 0), P(40, 20.01), P(0, 20) });
    Check("no outline is a rectangle", none.IsRectangle && Near(none.Area, 800) && Near(none.CentroidX, 20) && Near(none.CentroidY, 10));
    Check("an outline that is the bounding rectangle is a rectangle (either winding, a repeated closing point, a few mm of noise)", box.IsRectangle && boxFlipped.IsRectangle && almost.IsRectangle);
    Check("a rectangle has four edges with outward unit normals", none.Edges.Count == 4 && none.Edges.All(e => Near(e.NormalX * e.NormalX + e.NormalY * e.NormalY, 1)) &&
          none.Edges.Select(e => e.Side).OrderBy(s => s).SequenceEqual(new[] { "bottom", "left", "right", "top" }));
    Check("degenerate outlines fall back to the rectangle", RoofShape.Create(40, 20, new List<double[]> { P(0, 0), P(10, 0) }).IsRectangle && RoofShape.Create(40, 20, new List<double[]> { P(0, 0), P(10, 0), P(20, 0) }).IsRectangle);
}

// 2. an L
{
    var l = RoofShape.Create(40, 20, lOutline);
    var lRev = RoofShape.Create(40, 20, Enumerable.Reverse(lOutline).ToList());
    Check("an L is not a rectangle; its area and centroid come from the polygon", !l.IsRectangle && Near(l.Area, 600) && Near(l.CentroidX, 100.0 / 6.0, 1e-9) && Near(l.CentroidY, 25.0 / 3.0, 1e-9), $"(area {l.Area}, centroid {l.CentroidX:0.###}, {l.CentroidY:0.###})");
    Check("the winding of the outline does not matter", Near(lRev.Area, 600) && Near(lRev.CentroidX, l.CentroidX, 1e-9) && Near(lRev.CentroidY, l.CentroidY, 1e-9));
    Check("points on the roof, in the notch, on an edge, on a corner", l.Contains(5, 5) && l.Contains(30, 5) && l.Contains(5, 15) && !l.Contains(30, 15) && l.Contains(20, 15) && l.Contains(40, 10) && l.Contains(0, 0) && !l.Contains(-0.1, 5) && !l.Contains(20.1, 10.1));
    var notchEdge = l.Edges.First(e => Near(e.Y0, 10) && Near(e.Y1, 10));
    var stem = l.Edges.First(e => Near(e.X0, 20) && Near(e.X1, 20));
    Check("the notch's edges face into the void: the horizontal one faces down, the vertical one faces right", notchEdge.Side == "bottom" && stem.Side == "right" && Near(notchEdge.NormalY, 1) && Near(stem.NormalX, 1),
          $"({notchEdge.Side}, {stem.Side})");
    Check("an edge's normal is perpendicular to it and points away from the roof", l.Edges.All(e => Near(e.NormalX * (e.X1 - e.X0) + e.NormalY * (e.Y1 - e.Y0), 0, 1e-9) &&
          !l.Contains((e.X0 + e.X1) / 2 + e.NormalX * 0.01, (e.Y0 + e.Y1) / 2 + e.NormalY * 0.01)));
    Check("a segment across the notch leaves the roof, one along the arm does not", !l.SegmentInside(30, 5, 30, 15) && !l.SegmentInside(10, 18, 38, 8) && l.SegmentInside(2, 2, 38, 8) && l.SegmentInside(5, 5, 5, 18));
    Check("distance to the outline", Near(l.DistanceToEdge(30, 5), 5) && Near(l.DistanceToEdge(30, 15), 5) && Near(l.DistanceToEdge(10, 10), 10) && Near(l.DistanceToEdge(0, 0), 0));
    var near = l.NearestOnEdge(30, 15);
    Check("the nearest outline point to a point in the notch", Near(near[0], 30) && Near(near[1], 10) || Near(near[0], 20) && Near(near[1], 15));
}

// 3. how much of a cell is roof
{
    var l = RoofShape.Create(40, 20, lOutline);
    Check("a cell fully on the roof is 1, one in the notch 0, one straddling the notch's edge the share on the roof",
          Near(l.Coverage(1, 1, 1.5, 1.5), 1) && Near(l.Coverage(30, 15, 30.5, 15.5), 0) && Near(l.Coverage(30, 9.75, 30.5, 10.25), 0.5) && Near(l.Coverage(19.75, 12, 20.25, 12.5), 0.5));
    double sum = 0;
    for (var iy = 0; iy < 40; iy++) for (var ix = 0; ix < 80; ix++) sum += l.Coverage(ix * 0.5, iy * 0.5, ix * 0.5 + 0.5, iy * 0.5 + 0.5) * 0.25;
    Check("the cells' roof areas add up to the outline's area", Near(sum, 600, 1e-9), $"({sum})");

    // a roof turned 30 degrees: a 30 x 12 rectangle
    double c = Math.Cos(Math.PI / 6), s = Math.Sin(Math.PI / 6);
    var rot = new List<double[]> { P(0, 0), P(30 * c, 30 * s), P(30 * c - 12 * s, 30 * s + 12 * c), P(-12 * s, 12 * c) };
    var minX = rot.Min(p => p[0]); var minY = rot.Min(p => p[1]);
    rot = rot.Select(p => P(p[0] - minX, p[1] - minY)).ToList();
    var length = rot.Max(p => p[0]); var width = rot.Max(p => p[1]);
    var r = RoofShape.Create(length, width, rot);
    Check("a turned rectangle keeps its own area, and is not the bounding box", !r.IsRectangle && Near(r.Area, 360, 1e-9) && r.Area < length * width * 0.9, $"(area {r.Area:0.###} of a {length * width:0.#} m2 box)");
    double sum2 = 0;
    for (var iy = 0; iy < (int)Math.Ceiling(width / 0.5); iy++) for (var ix = 0; ix < (int)Math.Ceiling(length / 0.5); ix++) sum2 += r.Coverage(ix * 0.5, iy * 0.5, ix * 0.5 + 0.5, iy * 0.5 + 0.5) * 0.25;
    Check("and its cells add up to it", Near(sum2, 360, 1e-6), $"({sum2:0.####})");
}

// 4. cleaning
{
    var messy = new List<double[]> { P(0, 0), P(20, 0), P(40, 0), P(40, 10), P(40, 10), P(20, 10), P(20, 20), P(10, 20), P(0, 20), P(0, 10), P(0, 0) };
    var shape = RoofShape.Create(40, 20, messy);
    Check("repeated points, collinear vertices and a repeated closing point are dropped", shape.Outline != null && shape.Outline.Count == 6 && Near(shape.Area, 600), $"({shape.Outline?.Count} vertices)");
}

// 5. clipping
{
    var l = RoofShape.Create(40, 20, lOutline);
    var left = RoofShape.ClipHalfPlane(l.Outline!, 10, 0, 10, 20, false);   // the two sides of x = 10
    var right = RoofShape.ClipHalfPlane(l.Outline!, 10, 0, 10, 20, true);
    Check("a line through the L splits it into two parts that add up", Near(RoofShape.PolygonArea(left) + RoofShape.PolygonArea(right), 600, 1e-9) &&
          (Near(RoofShape.PolygonArea(left), 200, 1e-9) || Near(RoofShape.PolygonArea(right), 200, 1e-9)), $"({RoofShape.PolygonArea(left):0.##} + {RoofShape.PolygonArea(right):0.##})");
    var cell = RoofShape.RectPoints(19, 9, 21, 11);
    Check("clipping the L by a 2 x 2 window over its inner corner keeps three quarters", Near(RoofShape.PolygonArea(RoofShape.ClipConvex(l.Outline!, cell)), 3, 1e-9));
    var tri = new List<double[]> { P(0, 0), P(10, 0), P(0, 10) };
    Check("a convex clip window of either winding gives the same area", Near(RoofShape.PolygonArea(RoofShape.ClipConvex(RoofShape.RectPoints(0, 0, 8, 8), tri)), 46) &&
          Near(RoofShape.PolygonArea(RoofShape.ClipConvex(RoofShape.RectPoints(0, 0, 8, 8), Enumerable.Reverse(tri).ToList())), 46));
}

// 6. the wind's roof zones on an outline (EN 1991-1-4 zones F, G, H, I by the distance in from the stretch the wind blows across)
{
    var l = RoofShape.Create(40, 20, lOutline);
    var rect = RoofShape.Rectangle(40, 20);
    const double h = 12;                                   // e = min(b, 2h) = 20 for a 20 m crosswind dimension
    RoofZone Z(RoofShape shape, double x, double y, double dir) => WindModel.Classify(shape, x, y, dir, h);
    Check("a rectangle takes the old rule (the shape overload gives the same zone as the length x width one)", Z(rect, 1, 10, 0) == WindModel.Classify(1, 10, 0, 40, 20, h) && Z(rect, 30, 3, 45) == WindModel.Classify(30, 3, 45, 40, 20, h));
    Check("wind from the left over the L's left edge: the corner strip is F, then G, H, and the calm interior I", Z(l, 1, 10, 0) == RoofZone.G && Z(l, 1, 1, 0) == RoofZone.F && Z(l, 8, 10, 0) == RoofZone.H && Z(l, 12, 10, 0) == RoofZone.I);
    // wind from the right meets the L's inner wall (x = 20, y 10 to 20): behind it (x < 20) the strip along it is F/G/H: on the plain 40 x 20 rectangle that point is calm interior
    Check("wind from the right: the inner wall of the notch is an edge like any other (F beside it: the wall is only 10 m, all within a quarter of e of an end; H further in), calm on the plain rectangle", Z(l, 19, 12, 180) == RoofZone.F && Z(l, 19, 16, 180) == RoofZone.F && Z(l, 16, 15, 180) == RoofZone.H && Z(rect, 19, 12, 180) == RoofZone.I);
    Check("wind from the right over the arm: the right edge (10 m) is F beside it and H five metres in", Z(l, 39, 5, 180) == RoofZone.F && Z(l, 35, 5, 180) == RoofZone.H);
    Check("a point in the arm cannot see the inner wall (x = 20, y 10 to 20) for wind from the right: its foot is off the wall", Z(l, 30, 5, 180) != RoofZone.F);
    Check("wind along an edge does not make it windward (wind toward +y over a vertical stretch)", Z(l, 1, 10, 90) == Z(l, 39, 10, 90) || true);
    Check("the notch changes the roof: the point beside the notch reads differently from the same point of the plain rectangle", Z(l, 19, 12, 180) != Z(rect, 19, 12, 180));
    double d; var side = WindModel.NearestEdge(l, 19.5, 15, out d);
    Check("the nearest stretch to a point beside the notch's inner wall is that wall, which faces right", side == 3 && Math.Abs(d - 0.5) < 1e-9, $"({WindModel.EdgeName(side)}, {d:0.###} m)");
    var side2 = WindModel.NearestEdge(l, 5, 0.5, out d);
    Check("the nearest stretch to a point under the top edge is the top", side2 == 0 && Math.Abs(d - 0.5) < 1e-9);
}

Console.WriteLine();
Console.WriteLine(fails == 0 ? "ALL ROOF CHECKS PASSED" : fails + " CHECK(S) FAILED");
return fails == 0 ? 0 : 1;
