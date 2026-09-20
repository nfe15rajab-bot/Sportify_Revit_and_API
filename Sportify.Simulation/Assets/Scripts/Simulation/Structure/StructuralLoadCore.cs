#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Sportify.Simulation.Roof;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

namespace Sportify.Simulation.Structure
{
    // ---------------------------------------------------------------------------------------
    //  Static loads on the roof structure: where the weight and the people are, which bays of the
    //  structural grid carry most, and whether the load sits to one side.
    //
    //  Like the wind and rain models this uses nothing from UnityEngine, so the numbers come out in
    //  the Revit add-in with no Unity installed; Unity only films them.
    //
    //  It is a SCREENING model, not a structural verification. It works on the loads that sit ON the
    //  structure (the structure's own weight is outside it):
    //
    //    permanent  G   green-roof build-ups at their water-SATURATED weight (a green roof's permanent
    //                   load is its saturated weight), trees, sports and play surfaces, roof finishes
    //    imposed    Q   the characteristic value of the use category of each area (sports areas are
    //                   category C4, 5.0 kN/m2 in DIN EN 1991-1-1/NA Table 6.1DE), the maximum where
    //                   areas overlap
    //    people         the EXPECTED number of people (players, spectators, visitors, arrivals at the
    //                   entries): where activity concentrates. It is not what Q is made of; Q is the
    //                   code's design value, people are what will actually be there.
    //
    //  The roof (its bounding rectangle) is cut into 0.5 m cells. Every piece of the layout puts its
    //  load on the cells it overlaps in proportion to the overlap, so totals are conserved exactly.
    //  A roof that is not a rectangle (an L, a step, a chamfer: its outline is given) has cells that are
    //  only partly roof or not roof at all: a cell carries only what is on its roof part, so nothing is
    //  loaded on air.
    //  The cells are summed into the bays of the structural grid (a cell across a grid line is split
    //  by area), each bay's characteristic G + Q is compared with the deck capacity, and the centre of
    //  the total load is compared with the centre of the structure (the columns' centroid, else the
    //  plan centre, else the roof's centroid).
    //  A grid line is a line, at any angle to the roof's edges: a bay is the part of the roof between
    //  its four lines, a quadrilateral when the grid is skewed, and it is cut by the outline. Where it is off to one side, the single move or lightening that brings it back
    //  is found by re-running the model, so the advice is verified, not estimated.
    //
    //  Every constant that is a judgement call is named below and repeated in the report's
    //  assumptions. The deck's capacity is NOT known from the layout: it is an input, with a default
    //  that is only a placeholder for the structural engineer's figure.
    //
    //  Units: metres, kN, kN/m2. Plan coordinates as everywhere: x right, y DOWN from the top edge.
    // ---------------------------------------------------------------------------------------

    public enum LoadKind { Court, Activity, Zone, Tree }

    public class GridLineInput
    {
        public string Name;
        public double Position;   // x for a vertical line, y for a horizontal one (of a slanted line: where it crosses the middle of the roof)

        // A line at an angle to the roof's axes keeps its own two points (plan coordinates); a line square to them needs only its Position.
        public bool HasGeometry;
        public double X0, Y0, X1, Y1;

        /// <summary>The line as two points, oriented so that a vertical line runs downward and a horizontal one to the right.</summary>
        internal void Points(bool vertical, out double ax, out double ay, out double bx, out double by)
        {
            if (!HasGeometry)
            {
                if (vertical) { ax = Position; ay = 0; bx = Position; by = 1; }
                else { ax = 0; ay = Position; bx = 1; by = Position; }
                return;
            }
            ax = X0; ay = Y0; bx = X1; by = Y1;
            if (vertical ? by < ay : bx < ax) { ax = X1; ay = Y1; bx = X0; by = Y0; }
        }

        /// <summary>Where the line crosses another (a vertical one with a horizontal one), or null when they are parallel.</summary>
        internal static double[] Cross(GridLineInput v, GridLineInput h)
        {
            return Meet(v, true, h, false);
        }

        /// <summary>Where two lines (each with its own family, which decides how its points are ordered) meet when extended, or null when they are parallel.</summary>
        internal static double[] Meet(GridLineInput a, bool aVertical, GridLineInput b, bool bVertical)
        {
            double a0, a1, a2, a3, b0, b1, b2, b3;
            a.Points(aVertical, out a0, out a1, out a2, out a3);
            b.Points(bVertical, out b0, out b1, out b2, out b3);
            double d1x = a2 - a0, d1y = a3 - a1, d2x = b2 - b0, d2y = b3 - b1;
            var den = d1x * d2y - d1y * d2x;
            if (Math.Abs(den) < 1e-12) return null;
            var t = ((b0 - a0) * d2y - (b1 - a1) * d2x) / den;
            return new[] { a0 + t * d1x, a1 + t * d1y };
        }

        /// <summary>How far the line is turned from its own axis (degrees): 0 for a line square to the roof.</summary>
        internal double Deviation(bool vertical)
        {
            if (!HasGeometry) return 0;
            double dx = X1 - X0, dy = Y1 - Y0;
            return Math.Atan2(vertical ? Math.Abs(dx) : Math.Abs(dy), vertical ? Math.Abs(dy) : Math.Abs(dx)) * 180.0 / Math.PI;
        }
    }

    public class LoadItem
    {
        public string Id, Label;
        public string Name = "";             // what it is, for the videos: "Badminton (standard)", "Yoga", "Green roof: ZinCo Roof Garden"
        public LoadKind Kind;
        public double X, Y, Width, Height;   // footprint (a tree: its crown's box)
        public double DeadKnM2;              // permanent load on the footprint
        public double LiveKnM2;              // characteristic imposed load of its use
        public double PointKn;               // a concentrated permanent load (a tree's weight)
        public double Persons;               // expected people on it
        public double Players, Seats;        // a court's players and seated spectators (Persons is their sum)
        public string Basis = "";            // where the numbers come from, for the report

        public LoadItem Copy() { return (LoadItem)MemberwiseClone(); }
    }

    public class PathInput
    {
        public List<double[]> Points = new List<double[]>();
        public double WidthM;
    }

    public class StructureInputs
    {
        public double RoofLength, RoofWidth;
        public List<double[]> Outline;                                            // the roof's outline in plan coordinates, when it is not the rectangle (null = the rectangle)
        RoofShape _shape;
        /// <summary>The roof's shape: its outline, or the rectangle.</summary>
        public RoofShape Shape { get { return _shape ?? (_shape = RoofShape.Create(RoofLength, RoofWidth, Outline)); } }
        public List<GridLineInput> VerticalLines = new List<GridLineInput>();     // lines of constant x
        public List<GridLineInput> HorizontalLines = new List<GridLineInput>();   // lines of constant y
        public List<double[]> Columns = new List<double[]>();                     // {x, y}
        public string GridSource = "";                                            // "revit" | "manual" | ""
        public double? CapacityKnM2;
        public List<string> AcceptedAssumptions = new List<string>();             // keys (AnalysisAssumptions) whose built-in value the designer accepted
        public List<LoadItem> Items = new List<LoadItem>();
        public List<PathInput> Paths = new List<PathInput>();
        public List<double[]> Entries = new List<double[]>();
        public List<string> Notes = new List<string>();                           // what the reader of the layout had to skip or assume, for the report

        /// <summary>The same inputs with every item copied, so pieces can be moved or lightened without touching the original.</summary>
        public StructureInputs CopyWithItems()
        {
            var copy = (StructureInputs)MemberwiseClone();
            copy.Items = new List<LoadItem>();
            foreach (var i in Items) copy.Items.Add(i.Copy());
            return copy;
        }

        /// <summary>The same inputs with one item replaced (the lists are shared, apart from Items).</summary>
        public StructureInputs WithItem(int index, LoadItem replacement)
        {
            var copy = (StructureInputs)MemberwiseClone();
            copy.Items = new List<LoadItem>(Items);
            copy.Items[index] = replacement;
            return copy;
        }
    }

    // ------------------------------------------------------------------------ results (serialised by Unity's JsonUtility)

    [Serializable]
    public class BayResult
    {
        public string label;
        public string gridNames;          // "B-C / 2-3" when the grid lines are named
        public float x0, x1, y0, y1;      // the bay's bounding box
        public float[] polygon;           // x, y, x, y ... of the bay when it is not a rectangle (a skewed grid, an outline), else null
        public float areaM2;
        public float deadKn, liveKn, totalKn;
        public float totalKnM2;
        public float utilisation;         // characteristic G + Q against the deck capacity
        public string status;             // ok | marginal | over
        public float persons;
        public float personsPerM2;
        public float crowdKn;             // expected people x their weight
        public float shareOfLoadPercent;
        public string topContributor;
    }

    [Serializable]
    public class ColumnResult
    {
        public string label;
        public float x, y;
        public float tributaryM2;
        public float loadKn;
        public float ratioToMean;
        public string status;             // ok | high
    }

    [Serializable]
    public class ItemLoadResult
    {
        public string id, label, kind, basis;
        public float areaM2;
        public float deadKnM2, liveKnM2;
        public float deadKn, liveKn, persons;
    }

    [Serializable]
    public class BalanceResult
    {
        public float centreX, centreY;            // the structure's centre (columns' centroid, else the plan centre)
        public string centreBasis;
        public float deadCentroidX, deadCentroidY;
        public float deadEccentricityX, deadEccentricityY;     // permanent load only, as a fraction of the roof's length / width; + = toward +x / +y (right / bottom)
        public float totalCentroidX, totalCentroidY;
        public float totalEccentricityX, totalEccentricityY;   // permanent + imposed: what the status is judged on
        public float leftSharePercent, rightSharePercent, topSharePercent, bottomSharePercent;   // of the total (G + Q) load
        public string status;                     // balanced | marginal | unbalanced
        public string heavySide;                  // "right", "left", "top", "bottom" or ""
    }

    [Serializable]
    public class StructureRecommendation
    {
        public string kind;                       // move | lighten | over-capacity | vulnerable | concentration | grid | fine
        public string target;
        public string text;
        public string itemId;                     // move / lighten: the piece
        public string axis;                       // move: "x" or "y"
        public float moveM;                       // move: signed distance along the axis (+ = toward +x / +y)
        public float newDeadKnM2;                 // lighten: the permanent load the piece should come down to
        public float eccentricityAfter;           // move / lighten: what the balance becomes (fraction of the dimension)
        public float peakUtilisationAfter;        // move / lighten: the busiest bay afterwards
    }

    [Serializable]
    public class StructureSummary
    {
        public float roofAreaM2;
        public float deadKn, liveKn, totalKn;
        public float meanKnM2, peakBayKnM2;
        public float capacityKnM2;
        public bool capacityAssumed;
        public bool capacityAccepted;             // the designer accepted the built-in capacity
        public bool preliminary;                  // some input is still a built-in value nobody confirmed
        public string preliminaryNote = "";       // which ones, for the top of a video and a dialog
        public string acceptedNote = "";          // which built-in values the designer accepted
        public string gridSource;
        public bool gridAssumed;
        public int baysChecked, baysOver, baysMarginal;
        public int columnsChecked, columnsHigh;
        public float expectedPersons;
        public float busiestBaysSharePercent;     // of the people, in the busiest fifth of the bays
        public string worstBay;
        public float peakUtilisation;
    }

    [Serializable]
    public class StructureReport
    {
        public bool ran;
        public List<string> assumptions = new List<string>();
        public List<AssumptionUse> assumptionUses = new List<AssumptionUse>();   // the inputs behind the numbers and whether the designer confirmed them
        public StructureSummary summary = new StructureSummary();
        public BalanceResult balance = new BalanceResult();
        public List<BayResult> bays = new List<BayResult>();
        public List<ColumnResult> columns = new List<ColumnResult>();
        public List<ItemLoadResult> items = new List<ItemLoadResult>();
        public List<StructureRecommendation> recommendations = new List<StructureRecommendation>();
        public float[] verticalLinesM = new float[0];       // the bay boundaries actually used, x
        public float[] horizontalLinesM = new float[0];     // and y
    }

    /// <summary>Per-cell loads, in kN and people per cell, top row first: what the report is summed from and what the video colours.</summary>
    public sealed class LoadField
    {
        public int Nx, Ny;
        public double CellW, CellH;
        public double[] Dead, Live, Persons;
        public double[] Coverage;                    // the share (0 to 1) of each cell that is roof: 1 everywhere on a rectangular roof. Dead, Live and Persons already carry it.
        public RoofShape Roof;
        internal Dictionary<BaySystem, BaySystem.Weights> BayWeights = new Dictionary<BaySystem, BaySystem.Weights>();

        public int Index(int ix, int iy) { return iy * Nx + ix; }
    }

    // ------------------------------------------------------------------------ bays

    /// <summary>One bay of the structural grid: the part of the roof between four grid lines.</summary>
    public sealed class BayPart
    {
        public int Col, Row;                         // its place in the grid of lines (bays outside the roof leave gaps)
        public List<double[]> Polygon;               // the roof outline cut by the bay's lines
        public double Area;
        public double MinX, MinY, MaxX, MaxY;
        public double CentreX, CentreY;
        public int Reported = -1;                    // index in the report; a sliver bay is counted with its neighbour
    }

    /// <summary>
    /// The bays of the structural grid on the roof. On a rectangular roof with a grid square to it (the usual case) the bays are the rectangles
    /// between the lines and every sum takes the plain path. Otherwise the bays are polygons: the roof's outline cut by four half-planes, so a
    /// skewed grid gives quadrilaterals and an L-shaped roof an L-shaped corner bay, and a cell across a line or an edge is split by exact area.
    /// </summary>
    public sealed class BaySystem
    {
        public const double MinBayAreaM2 = 0.5;      // a smaller piece of bay (a wedge at a corner) is counted with its neighbour

        public RoofShape Roof;
        public bool Plain;
        public List<GridLineInput> VLines = new List<GridLineInput>();   // the lines that bound bays (inside the roof, one per cell of distance)
        public List<GridLineInput> HLines = new List<GridLineInput>();
        public double[] Xs, Ys;                      // where they cross the roof's middle, with the roof's edges: the bay boundaries as numbers
        public int NCols, NRows;
        public List<BayPart> Parts = new List<BayPart>();
        public List<BayPart> Bays = new List<BayPart>();                 // the reported bays, row by row
        public List<string> DroppedLines = new List<string>();           // grid lines that cross others of their direction on the roof, left out

        public int Count { get { return Bays.Count; } }

        internal sealed class Weights
        {
            public int[] Cell, Bay;
            public double[] Share;
        }

        /// <summary>
        /// Lines of one direction cannot cross on the roof and still cut it into strips: a brace running at 19 degrees among horizontal grid lines
        /// is not a grid line. While two of the lines meet inside the roof, the one that meets the most (the more slanted one when equal) is left out.
        /// </summary>
        static List<GridLineInput> WithoutCrossings(List<GridLineInput> lines, bool vertical, double roofL, double roofW, List<string> dropped)
        {
            var list = new List<GridLineInput>(lines);
            while (true)
            {
                var counts = new int[list.Count];
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        if (!list[i].HasGeometry && !list[j].HasGeometry) continue;
                        var at = GridLineInput.Meet(list[i], vertical, list[j], vertical);
                        if (at == null || at[0] <= 0.05 || at[0] >= roofL - 0.05 || at[1] <= 0.05 || at[1] >= roofW - 0.05) continue;
                        counts[i]++; counts[j]++;
                    }
                var worst = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    if (counts[i] == 0) continue;
                    if (worst < 0 || counts[i] > counts[worst] ||
                        (counts[i] == counts[worst] && (list[i].Deviation(vertical) > list[worst].Deviation(vertical) + 1e-9 ||
                                                       (Math.Abs(list[i].Deviation(vertical) - list[worst].Deviation(vertical)) <= 1e-9 && list[i].Position > list[worst].Position)))) worst = i;
                }
                if (worst < 0) return list;
                dropped.Add(string.IsNullOrEmpty(list[worst].Name) ? "(unnamed)" : list[worst].Name);
                list.RemoveAt(worst);
            }
        }

        static List<GridLineInput> Inside(List<GridLineInput> lines, double length, bool vertical, double roofL, double roofW, List<string> dropped)
        {
            var candidates = lines.Where(l => l.Position > StructureModel.CellM && l.Position < length - StructureModel.CellM).ToList();
            var sorted = WithoutCrossings(candidates, vertical, roofL, roofW, dropped).OrderBy(l => l.Position).ToList();
            var kept = new List<GridLineInput>();
            double last = 0;
            foreach (var l in sorted)
            {
                if (l.Position - last <= StructureModel.CellM) continue;
                kept.Add(l);
                last = l.Position;
            }
            return kept;
        }

        public static BaySystem Build(StructureInputs inputs)
        {
            var sys = new BaySystem { Roof = inputs.Shape };
            var assumed = inputs.VerticalLines.Count == 0 && inputs.HorizontalLines.Count == 0;
            var vAll = assumed ? StructureModel.RegularLines(inputs.RoofLength) : inputs.VerticalLines;
            var hAll = assumed ? StructureModel.RegularLines(inputs.RoofWidth) : inputs.HorizontalLines;
            sys.VLines = Inside(vAll, inputs.RoofLength, true, inputs.RoofLength, inputs.RoofWidth, sys.DroppedLines);
            sys.HLines = Inside(hAll, inputs.RoofWidth, false, inputs.RoofLength, inputs.RoofWidth, sys.DroppedLines);
            sys.Xs = new[] { 0.0 }.Concat(sys.VLines.Select(l => l.Position)).Concat(new[] { inputs.RoofLength }).ToArray();
            sys.Ys = new[] { 0.0 }.Concat(sys.HLines.Select(l => l.Position)).Concat(new[] { inputs.RoofWidth }).ToArray();
            sys.NCols = sys.Xs.Length - 1;
            sys.NRows = sys.Ys.Length - 1;
            sys.Plain = sys.Roof.IsRectangle && !sys.VLines.Any(l => l.HasGeometry) && !sys.HLines.Any(l => l.HasGeometry);

            for (var r = 0; r < sys.NRows; r++)
                for (var c = 0; c < sys.NCols; c++)
                {
                    var part = new BayPart { Col = c, Row = r };
                    if (sys.Plain)
                    {
                        part.MinX = sys.Xs[c]; part.MaxX = sys.Xs[c + 1]; part.MinY = sys.Ys[r]; part.MaxY = sys.Ys[r + 1];
                        part.Area = (part.MaxX - part.MinX) * (part.MaxY - part.MinY);
                        part.CentreX = (part.MinX + part.MaxX) / 2; part.CentreY = (part.MinY + part.MaxY) / 2;
                        part.Reported = sys.Bays.Count;
                        sys.Bays.Add(part);
                    }
                    else
                    {
                        var poly = sys.Roof.Polygon;
                        if (c > 0) poly = Clip(poly, sys.VLines[c - 1], true, true);
                        if (c < sys.NCols - 1) poly = Clip(poly, sys.VLines[c], true, false);
                        if (r > 0) poly = Clip(poly, sys.HLines[r - 1], false, true);
                        if (r < sys.NRows - 1) poly = Clip(poly, sys.HLines[r], false, false);
                        var area = RoofShape.PolygonArea(poly);
                        if (area < 1e-9) continue;                                   // a bay wholly outside the roof (the notch of an L)
                        part.Polygon = poly; part.Area = area;
                        part.MinX = poly.Min(q => q[0]); part.MaxX = poly.Max(q => q[0]);
                        part.MinY = poly.Min(q => q[1]); part.MaxY = poly.Max(q => q[1]);
                        Centroid(poly, out part.CentreX, out part.CentreY);
                        sys.Parts.Add(part);
                    }
                }

            if (!sys.Plain)
            {
                // wedges too small to be a bay are counted with the nearest real one
                var real = sys.Parts.Where(q => q.Area >= MinBayAreaM2).ToList();
                if (real.Count == 0) real = sys.Parts.OrderByDescending(q => q.Area).Take(1).ToList();
                foreach (var part in real) { part.Reported = sys.Bays.Count; sys.Bays.Add(part); }
                foreach (var part in sys.Parts.Where(q => q.Reported < 0))
                {
                    var target = real.OrderBy(q => (q.CentreX - part.CentreX) * (q.CentreX - part.CentreX) + (q.CentreY - part.CentreY) * (q.CentreY - part.CentreY)).First();
                    part.Reported = target.Reported;
                }
            }
            return sys;
        }

        /// <summary>The part of a polygon on one side of a grid line: the larger-coordinate side (right of a vertical line, below a horizontal one) or the smaller.</summary>
        static List<double[]> Clip(List<double[]> poly, GridLineInput line, bool vertical, bool keepGreater)
        {
            double ax, ay, bx, by;
            line.Points(vertical, out ax, out ay, out bx, out by);
            // a vertical line runs downward: its LEFT (numerically positive cross product) is the smaller x; a horizontal one runs right: its left is the larger y
            var leftIsInside = vertical ? !keepGreater : keepGreater;
            return RoofShape.ClipHalfPlane(poly, ax, ay, bx, by, leftIsInside);
        }

        static void Centroid(List<double[]> poly, out double cx, out double cy)
        {
            double a = 0, sx = 0, sy = 0;
            for (var i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                var cr = p[0] * q[1] - q[0] * p[1];
                a += cr; sx += (p[0] + q[0]) * cr; sy += (p[1] + q[1]) * cr;
            }
            if (Math.Abs(a) < 1e-12) { cx = poly.Average(q => q[0]); cy = poly.Average(q => q[1]); return; }
            cx = sx / (3.0 * a); cy = sy / (3.0 * a);
        }

        /// <summary>Area of the bay (a reported bay also carries the wedges counted with it).</summary>
        public double AreaOf(int bay)
        {
            if (Plain) return Bays[bay].Area;
            return Parts.Where(q => q.Reported == bay).Sum(q => q.Area);
        }

        /// <summary>The bay's spans: its area over its height and over its width (a rectangle's own sides; for a skewed or cut bay the mean sides).</summary>
        public void Spans(int bay, out double alongX, out double alongY)
        {
            var b = Bays[bay];
            var area = AreaOf(bay);
            alongX = Plain ? b.MaxX - b.MinX : area / Math.Max(1e-9, b.MaxY - b.MinY);
            alongY = Plain ? b.MaxY - b.MinY : area / Math.Max(1e-9, b.MaxX - b.MinX);
        }

        /// <summary>The area of the bay under a rectangle (a piece's footprint).</summary>
        public double Overlap(int bay, double x0, double y0, double x1, double y1)
        {
            var b = Bays[bay];
            if (Plain)
            {
                var w = Math.Max(0, Math.Min(x1, b.MaxX) - Math.Max(x0, b.MinX));
                var h = Math.Max(0, Math.Min(y1, b.MaxY) - Math.Max(y0, b.MinY));
                return w * h;
            }
            double sum = 0;
            var rect = RoofShape.RectPoints(x0, y0, x1, y1);
            foreach (var part in Parts)
            {
                if (part.Reported != bay || part.MaxX <= x0 || part.MinX >= x1 || part.MaxY <= y0 || part.MinY >= y1) continue;
                sum += RoofShape.PolygonArea(RoofShape.ClipConvex(part.Polygon, rect));
            }
            return sum;
        }

        /// <summary>The bay a point is in; a point outside every bay (off the roof) belongs to the nearest.</summary>
        public int IndexAt(double x, double y)
        {
            if (Plain)
            {
                var c = 0;
                while (c < NCols - 1 && x >= Xs[c + 1]) c++;
                var r = 0;
                while (r < NRows - 1 && y >= Ys[r + 1]) r++;
                return r * NCols + c;
            }
            foreach (var part in Parts)
                if (x >= part.MinX - 1e-9 && x <= part.MaxX + 1e-9 && y >= part.MinY - 1e-9 && y <= part.MaxY + 1e-9 && InsidePolygon(part.Polygon, x, y)) return part.Reported;
            var best = Parts[0];
            var bd = double.MaxValue;
            foreach (var part in Parts)
            {
                var d = (part.CentreX - x) * (part.CentreX - x) + (part.CentreY - y) * (part.CentreY - y);
                if (d < bd) { bd = d; best = part; }
            }
            return best.Reported;
        }

        static bool InsidePolygon(List<double[]> p, double x, double y)
        {
            var inside = false;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            {
                double xi = p[i][0], yi = p[i][1], xj = p[j][0], yj = p[j][1];
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        /// <summary>Per-cell values (kN) summed into the bays, a cell across a grid line or the roof's edge split by exact area; bay order as in the report.</summary>
        public double[] Sums(double[] cellValues, LoadField f)
        {
            var sums = new double[Count];
            if (Plain)
            {
                var ovX = StructureModel.Overlaps(Xs, f.Nx, f.CellW);
                var ovY = StructureModel.Overlaps(Ys, f.Ny, f.CellH);
                for (var iy = 0; iy < f.Ny; iy++)
                    for (var ix = 0; ix < f.Nx; ix++)
                    {
                        var v = cellValues[f.Index(ix, iy)];
                        foreach (var py in ovY[iy])
                            foreach (var px in ovX[ix])
                                sums[py.Key * NCols + px.Key] += v * px.Value * py.Value;
                    }
                return sums;
            }
            Weights w;
            if (!f.BayWeights.TryGetValue(this, out w)) { w = BuildWeights(f); f.BayWeights[this] = w; }
            for (var i = 0; i < w.Cell.Length; i++) sums[w.Bay[i]] += cellValues[w.Cell[i]] * w.Share[i];
            return sums;
        }

        Weights BuildWeights(LoadField f)
        {
            var cells = new List<int>();
            var bays = new List<int>();
            var shares = new List<double>();
            var perBay = new Dictionary<int, double>();
            for (var iy = 0; iy < f.Ny; iy++)
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var k = f.Index(ix, iy);
                    if (f.Coverage[k] <= 1e-12) continue;
                    double x0 = ix * f.CellW, y0 = iy * f.CellH, x1 = x0 + f.CellW, y1 = y0 + f.CellH;
                    var rect = RoofShape.RectPoints(x0, y0, x1, y1);
                    perBay.Clear();
                    var total = 0.0;
                    foreach (var part in Parts)
                    {
                        if (part.MaxX <= x0 || part.MinX >= x1 || part.MaxY <= y0 || part.MinY >= y1) continue;
                        var a = RoofShape.PolygonArea(RoofShape.ClipConvex(part.Polygon, rect));
                        if (a <= 1e-14) continue;
                        double have;
                        perBay[part.Reported] = (perBay.TryGetValue(part.Reported, out have) ? have : 0) + a;
                        total += a;
                    }
                    if (total <= 1e-14) continue;
                    foreach (var kv in perBay) { cells.Add(k); bays.Add(kv.Key); shares.Add(kv.Value / total); }
                }
            return new Weights { Cell = cells.ToArray(), Bay = bays.ToArray(), Share = shares.ToArray() };
        }
    }

    // ------------------------------------------------------------------------ the model

    public static class StructureModel
    {
        // --- constants that are judgement calls (all repeated in the report) ---
        public const double Gravity = 9.81;
        public const double CellM = 0.5;
        public const double PersonMassKg = 90.0;                 // the Sportify reference value (Live Loads)
        public const double RoofFinishesKnM2 = 0.5;              // waterproofing, insulation, finishes on top of the deck
        public const double SportsSurfaceKnM2 = 0.5;             // floor build-up of a court or play area
        public const double CourtLiveKnM2 = 5.0;                 // DIN EN 1991-1-1/NA Table 6.1DE, category C4
        public const double ActivityLiveKnM2 = 4.0;              // play and assembly areas (C3 range 3-5), assumed
        public const double AccessibleLiveKnM2 = 3.0;            // accessible roof garden, walkways, circulation, assumed
        public const double RoofLiveKnM2 = 0.75;                 // roof not accessible but for maintenance (category H), assumed: check the National Annex
        public const double DefaultCapacityKnM2 = 8.0;           // placeholder for the engineer's figure: 5.0 sports use + 3.0 permanent
        public const double TreeMassAtSixMetresKg = 100.0;       // above-ground mass, scaled with (height / 6 m)^2, as in the wind model
        public const double AssumedBayM = 8.4;                   // a regular grid when the model has none
        public const double EntryRadiusM = 3.0;
        public const double EntryPersons = 10.0;                 // arrivals waiting at each entry
        public const double AccessiblePersonsPerM2 = 0.1;
        public const double ActivityPersonsPerM2 = 0.3;
        public const double MarginalFrom = 0.8;
        public const double ColumnHighFactor = 1.5;              // a column carrying this much more than the mean
        public const double EccentricityMarginal = 0.05;         // centre of the load this far off the structure's centre, as a share of the dimension
        public const double EccentricityHigh = 0.10;
        public const double TargetEccentricity = 0.03;           // what a recommended move or lightening brings it to
        public const double MinItemShare = 0.05;                 // an item must carry this share of the load to be worth moving
        public const double MinStepGain = 0.01;                  // a step that doesn't reach the target must still take this much (of the dimension) off the eccentricity
        public const double LightenFloor = 0.4;                  // a build-up is not asked to be lighter than this share of what it is

        static readonly string[] SportPlayers = { "badminton:4", "tennis:4", "basketball:10", "handball:14", "volleyball:12", "football:10", "futsal:10" };

        public static double PlayersOn(string sport)
        {
            var s = (sport ?? "").Trim().ToLowerInvariant();
            foreach (var entry in SportPlayers)
            {
                var parts = entry.Split(':');
                if (s.Contains(parts[0])) return double.Parse(parts[1], CultureInfo.InvariantCulture);
            }
            return 8;
        }

        // ---------------------------------------------------------------- items from layout pieces

        /// <summary>A court: its floor, the code's sports load, its players and any seated spectators.</summary>
        public static LoadItem CourtItem(string id, string label, string sport, double x, double y, double w, double h, int seats)
        {
            var players = PlayersOn(sport);
            return new LoadItem
            {
                Id = id, Label = label, Name = label, Kind = LoadKind.Court, X = x, Y = y, Width = w, Height = h,
                DeadKnM2 = SportsSurfaceKnM2, LiveKnM2 = CourtLiveKnM2,
                Persons = players + seats, Players = players, Seats = seats,
                Basis = "sports area, category C4 (" + F1(CourtLiveKnM2) + " kN/m2); " + F0(players) + " players" + (seats > 0 ? " and " + seats + " seats" : ""),
            };
        }

        public static LoadItem ActivityItem(string id, string label, double x, double y, double w, double h)
        {
            return new LoadItem
            {
                Id = id, Label = label, Name = label, Kind = LoadKind.Activity, X = x, Y = y, Width = w, Height = h,
                DeadKnM2 = SportsSurfaceKnM2, LiveKnM2 = ActivityLiveKnM2,
                Persons = w * h * ActivityPersonsPerM2,
                Basis = "play / assembly area (" + F1(ActivityLiveKnM2) + " kN/m2, assumed); " + F1(ActivityPersonsPerM2) + " people per m2",
            };
        }

        /// <summary>A green-roof zone at its water-saturated weight: published where the provider prints it, else the dry layers plus the water at saturation.</summary>
        public static LoadItem ZoneItem(ZoneInput zone)
        {
            var a = zone.Assembly;
            string source;
            double kgM2;
            if (a != null && a.SaturatedKgM2.HasValue && a.SaturatedKgM2.Value > 0)
            {
                kgM2 = a.SaturatedKgM2.Value;
                source = "published saturated weight";
            }
            else
            {
                var spec = PercolationModel.SpecFor(a);
                string ignored;
                kgM2 = WindModel.DryWeightKgM2(a, out ignored) + PercolationModel.StorageAt(spec, spec.ThetaS);
                source = "dry layers plus water at saturation";
            }

            var paved = WindModel.Cover(a) == CoverKind.Paved;
            var intensive = a != null && string.Equals(a.Category, "intensive", StringComparison.OrdinalIgnoreCase);
            var accessible = paved || intensive;

            return new LoadItem
            {
                Id = zone.Id, Label = zone.Label, Kind = LoadKind.Zone,
                Name = "Green roof: " + (a != null && !string.IsNullOrEmpty(a.SystemName) ? a.SystemName : (a != null && !string.IsNullOrEmpty(a.System) ? a.System : "no build-up")),
                X = zone.X, Y = zone.Y, Width = zone.Width, Height = zone.Height,
                DeadKnM2 = kgM2 * Gravity / 1000.0,
                LiveKnM2 = accessible ? AccessibleLiveKnM2 : RoofLiveKnM2,
                Persons = accessible ? zone.Width * zone.Height * AccessiblePersonsPerM2 : 0,
                Basis = (a != null && !string.IsNullOrEmpty(a.System) ? a.System : "no build-up") + ": " + F0(kgM2) + " kg/m2 saturated (" + source + "); " +
                        (accessible ? "accessible, " + F1(AccessibleLiveKnM2) : "not accessible, " + F2(RoofLiveKnM2)) + " kN/m2 imposed",
            };
        }

        /// <summary>A tree's own weight as a concentrated permanent load. (Its root ball is the bed's substrate, already in the zone's weight.)</summary>
        public static LoadItem TreeItem(PlantInput plant)
        {
            var kn = TreeMassAtSixMetresKg * Math.Pow(plant.HeightM / 6.0, 2) * Gravity / 1000.0;
            return new LoadItem
            {
                Id = plant.Id, Label = plant.Species, Name = plant.Species, Kind = LoadKind.Tree,
                X = plant.X - plant.CrownM / 2, Y = plant.Y - plant.CrownM / 2, Width = plant.CrownM, Height = plant.CrownM,
                PointKn = kn,
                LiveKnM2 = RoofLiveKnM2,
                Basis = plant.Species + ", " + F1(plant.HeightM) + " m: " + F1(kn) + " kN above ground",
            };
        }

        // ---------------------------------------------------------------- the load field

        sealed class Claim
        {
            public double Q;                                       // imposed load intensity, kN/m2
            public List<KeyValuePair<int, double>> Cells = new List<KeyValuePair<int, double>>();   // cell index, share of the cell covered
        }

        static int CellIndexOf(double v, double size, int n)
        {
            return Math.Min(n - 1, Math.Max(0, (int)Math.Floor(v / size)));
        }

        public static LoadField BuildField(StructureInputs inputs)
        {
            var f = new LoadField
            {
                Nx = Math.Max(1, (int)Math.Floor(inputs.RoofLength / CellM + 0.5)),
                Ny = Math.Max(1, (int)Math.Floor(inputs.RoofWidth / CellM + 0.5)),
            };
            f.CellW = inputs.RoofLength / f.Nx;
            f.CellH = inputs.RoofWidth / f.Ny;
            var cw = f.CellW;
            var ch = f.CellH;
            var cellArea = cw * ch;
            var n = f.Nx * f.Ny;
            f.Roof = inputs.Shape;
            f.Coverage = new double[n];
            for (var iy = 0; iy < f.Ny; iy++)
                for (var ix = 0; ix < f.Nx; ix++)
                    f.Coverage[f.Index(ix, iy)] = f.Roof.IsRectangle ? 1.0 : f.Roof.Coverage(ix * cw, iy * ch, (ix + 1) * cw, (iy + 1) * ch);
            f.Dead = new double[n];
            f.Live = new double[n];
            f.Persons = new double[n];
            for (var i = 0; i < n; i++) f.Dead[i] = RoofFinishesKnM2 * cellArea;

            var claims = new List<Claim>();

            foreach (var item in inputs.Items)
            {
                var area = item.Width * item.Height;
                var claim = new Claim { Q = item.LiveKnM2 };
                if (area > 1e-12)
                {
                    var ix0 = CellIndexOf(item.X, cw, f.Nx);
                    var ix1 = CellIndexOf(item.X + item.Width, cw, f.Nx);
                    var iy0 = CellIndexOf(item.Y, ch, f.Ny);
                    var iy1 = CellIndexOf(item.Y + item.Height, ch, f.Ny);
                    for (var iy = iy0; iy <= iy1; iy++)
                    {
                        var oy = Math.Min(item.Y + item.Height, (iy + 1) * ch) - Math.Max(item.Y, iy * ch);
                        if (oy <= 0) continue;
                        for (var ix = ix0; ix <= ix1; ix++)
                        {
                            var ox = Math.Min(item.X + item.Width, (ix + 1) * cw) - Math.Max(item.X, ix * cw);
                            if (ox <= 0) continue;
                            var a = ox * oy;
                            var k = f.Index(ix, iy);
                            f.Dead[k] += item.DeadKnM2 * a;
                            f.Persons[k] += item.Persons * a / area;
                            claim.Cells.Add(new KeyValuePair<int, double>(k, a / cellArea));
                        }
                    }
                }
                claims.Add(claim);

                if (item.PointKn > 0) SpreadPoint(f, item.X + item.Width / 2, item.Y + item.Height / 2, item.PointKn);
            }

            // Circulation is accessible ground: at least the accessible imposed load on the cells its centre line crosses.
            foreach (var path in inputs.Paths)
            {
                var claim = new Claim { Q = AccessibleLiveKnM2 };
                for (var iy = 0; iy < f.Ny; iy++)
                    for (var ix = 0; ix < f.Nx; ix++)
                        if (DistanceToPath(path, (ix + 0.5) * cw, (iy + 0.5) * ch) <= path.WidthM / 2.0)
                            claim.Cells.Add(new KeyValuePair<int, double>(f.Index(ix, iy), 1.0));
                claims.Add(claim);
            }

            // The imposed load of each cell is the highest intensity of what covers it, weighted by how much of the cell each covers
            // (highest first; what is left over carries the roof's own). Exact for pieces that don't overlap or that nest.
            var claimed = new double[n];
            foreach (var claim in claims.OrderByDescending(c => c.Q))
            {
                foreach (var cell in claim.Cells)
                {
                    var take = Math.Min(cell.Value, 1.0 - claimed[cell.Key]);
                    if (take <= 0) continue;
                    f.Live[cell.Key] += claim.Q * take * cellArea;
                    claimed[cell.Key] += take;
                }
            }
            for (var i = 0; i < n; i++) f.Live[i] += RoofLiveKnM2 * (1.0 - claimed[i]) * cellArea;

            // People waiting at an entry.
            foreach (var e in inputs.Entries)
            {
                var near = new List<int>();
                var nearest = 0;
                var nearestD = double.MaxValue;
                for (var iy = 0; iy < f.Ny; iy++)
                {
                    for (var ix = 0; ix < f.Nx; ix++)
                    {
                        if (f.Coverage[f.Index(ix, iy)] < 0.5) continue;        // people wait on the roof, not in the notch beside an entry
                        var dx = (ix + 0.5) * cw - e[0];
                        var dy = (iy + 0.5) * ch - e[1];
                        var d = Math.Sqrt(dx * dx + dy * dy);
                        if (d <= EntryRadiusM) near.Add(f.Index(ix, iy));
                        if (d < nearestD) { nearestD = d; nearest = f.Index(ix, iy); }
                    }
                }
                if (near.Count == 0) near.Add(nearest);
                // the arrivals are shared between the cells by how much of each is roof (all whole cells on a rectangular roof: the same share each)
                var coverSum = 0.0;
                foreach (var k in near) coverSum += f.Coverage[k];
                if (coverSum <= 1e-9) continue;                                   // an entry with no roof anywhere near it
                foreach (var k in near) f.Persons[k] += EntryPersons / coverSum;
            }

            // A cell carries only what is on its roof part.
            if (!f.Roof.IsRectangle)
                for (var k = 0; k < n; k++)
                {
                    var c = f.Coverage[k];
                    if (c >= 1.0) continue;
                    f.Dead[k] *= c; f.Live[k] *= c; f.Persons[k] *= c;
                }

            return f;
        }

        /// <summary>A concentrated load shared between the four cell centres around it, so it neither jumps nor tips at a cell edge.</summary>
        static void SpreadPoint(LoadField f, double x, double y, double kn)
        {
            var gx = x / f.CellW - 0.5;
            var gy = y / f.CellH - 0.5;
            var ix = (int)Math.Floor(gx);
            var iy = (int)Math.Floor(gy);
            var tx = gx - ix;
            var ty = gy - iy;
            for (var dy = 0; dy <= 1; dy++)
            {
                for (var dx = 0; dx <= 1; dx++)
                {
                    var w = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
                    if (w <= 0) continue;
                    var cx = Math.Min(f.Nx - 1, Math.Max(0, ix + dx));
                    var cy = Math.Min(f.Ny - 1, Math.Max(0, iy + dy));
                    f.Dead[f.Index(cx, cy)] += kn * w;
                }
            }
        }

        static double DistanceToPath(PathInput p, double x, double y)
        {
            var best = double.MaxValue;
            for (var i = 0; i + 1 < p.Points.Count; i++)
            {
                var a = p.Points[i];
                var b = p.Points[i + 1];
                double dx = b[0] - a[0], dy = b[1] - a[1];
                var len2 = dx * dx + dy * dy;
                var t = len2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((x - a[0]) * dx + (y - a[1]) * dy) / len2));
                var px = a[0] + t * dx - x;
                var py = a[1] + t * dy - y;
                best = Math.Min(best, Math.Sqrt(px * px + py * py));
            }
            return best;
        }

        // ---------------------------------------------------------------- bays

        /// <summary>The boundaries of the bays along one axis: the roof's edges and every grid line more than a cell inside it.</summary>
        public static double[] Boundaries(List<GridLineInput> lines, double length)
        {
            var list = new List<double> { 0, length };
            foreach (var l in lines)
                if (l.Position > CellM && l.Position < length - CellM) list.Add(l.Position);
            list.Sort();
            var merged = new List<double>();
            foreach (var v in list)
                if (merged.Count == 0 || v - merged[merged.Count - 1] > CellM) merged.Add(v);
            merged[merged.Count - 1] = length;
            return merged.ToArray();
        }

        static string NameBetween(List<GridLineInput> lines, double a, double b)
        {
            string Nearest(double pos)
            {
                var hit = lines.OrderBy(l => Math.Abs(l.Position - pos)).FirstOrDefault();
                return hit != null && Math.Abs(hit.Position - pos) < 0.5 && !string.IsNullOrEmpty(hit.Name) ? hit.Name : null;
            }
            var na = Nearest(a);
            var nb = Nearest(b);
            return na != null && nb != null ? na + "-" + nb : "";
        }

        /// <summary>For each cell along an axis, the bays it overlaps and the share of the cell in each.</summary>
        internal static List<KeyValuePair<int, double>>[] Overlaps(double[] bounds, int cells, double cell)
        {
            var result = new List<KeyValuePair<int, double>>[cells];
            for (var i = 0; i < cells; i++)
            {
                result[i] = new List<KeyValuePair<int, double>>();
                double lo = i * cell, hi = (i + 1) * cell;
                for (var b = 0; b + 1 < bounds.Length; b++)
                {
                    var o = Math.Min(hi, bounds[b + 1]) - Math.Max(lo, bounds[b]);
                    if (o > 1e-12) result[i].Add(new KeyValuePair<int, double>(b, o / cell));
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- the analysis

        /// <summary>One line naming what was analysed, shared by the add-in's dialogs and the video's title card.</summary>
        public static string CaseStudy(StructureInputs inputs, StructureReport report)
        {
            var n = inputs.Items.Count;
            var bays = report.bays.Count;
            return n + " load piece" + (n == 1 ? "" : "s") + " on a " + F1(inputs.RoofLength) + " x " + F1(inputs.RoofWidth) + " m roof" + (inputs.Shape.IsRectangle ? "" : " (outline " + F0(inputs.Shape.Area) + " m2)") + ", " + bays + " bay" + (bays == 1 ? "" : "s") +
                   " (" + (report.summary.gridAssumed ? "assumed grid" : "grid from " + (string.IsNullOrEmpty(inputs.GridSource) ? "the layout" : inputs.GridSource)) + ")";
        }

        public static StructureReport Analyse(StructureInputs inputs)
        {
            var report = Compute(inputs);
            Recommend(report, inputs);
            return report;
        }

        /// <summary>The bays the analysis uses: the model's own grid or the assumed regular one, cut by the roof's outline.</summary>
        public static BaySystem Bays(StructureInputs inputs)
        {
            return BaySystem.Build(inputs);
        }

        /// <summary>Everything except the advice.</summary>
        public static StructureReport Compute(StructureInputs inputs)
        {
            var report = new StructureReport { ran = true };

            // A grid the model doesn't give is assumed regular, and says so.
            var gridAssumed = inputs.VerticalLines.Count == 0 && inputs.HorizontalLines.Count == 0;
            var vLines = inputs.VerticalLines;
            var hLines = inputs.HorizontalLines;
            if (gridAssumed)
            {
                vLines = RegularLines(inputs.RoofLength);
                hLines = RegularLines(inputs.RoofWidth);
            }
            var baySystem = BaySystem.Build(inputs);
            var xs = baySystem.Xs;
            var ys = baySystem.Ys;
            report.verticalLinesM = xs.Select(v => (float)v).ToArray();
            report.horizontalLinesM = ys.Select(v => (float)v).ToArray();
            var shape = inputs.Shape;

            var field = BuildField(inputs);
            var cw = field.CellW;
            var ch = field.CellH;
            var capacityAssumed = !inputs.CapacityKnM2.HasValue;
            var capacity = inputs.CapacityKnM2 ?? DefaultCapacityKnM2;
            report.assumptions.AddRange(Assumptions(capacity, capacityAssumed, gridAssumed, inputs.GridSource));
            if (!shape.IsRectangle) report.assumptions.AddRange(OutlineNotes(inputs, shape));
            if (baySystem.DroppedLines.Count > 0)
                report.assumptions.Add("Grid line" + (baySystem.DroppedLines.Count == 1 ? " " : "s ") + string.Join(", ", baySystem.DroppedLines.ToArray()) + " cross" + (baySystem.DroppedLines.Count == 1 ? "es" : "") +
                                       " other grid lines of " + (baySystem.DroppedLines.Count == 1 ? "its" : "their") + " direction on the roof, so " + (baySystem.DroppedLines.Count == 1 ? "it does" : "they do") + " not cut it into bays: left out.");
            if (!baySystem.Plain && baySystem.Parts.Any(q => q.Area < BaySystem.MinBayAreaM2))
                report.assumptions.Add("Some wedge of a bay under " + F1(BaySystem.MinBayAreaM2) + " m2 (where a slanted grid line meets the roof's edge) is counted with its neighbouring bay.");
            report.assumptionUses.Add(AnalysisAssumptions.Use(AnalysisAssumptions.DeckCapacity, !capacityAssumed, F1(capacity) + " kN/m2", inputs.AcceptedAssumptions));
            report.assumptions.AddRange(AnalysisAssumptions.Lines(report.assumptionUses));
            report.assumptions.AddRange(inputs.Notes);

            // ---- bays: each cell splits over the bays it overlaps, by area
            var nb = baySystem.Count;
            var dead = baySystem.Sums(field.Dead, field);
            var live = baySystem.Sums(field.Live, field);
            var persons = baySystem.Sums(field.Persons, field);

            double totalDead = 0, totalLive = 0, totalPersons = 0;
            for (var k = 0; k < field.Dead.Length; k++)
            {
                totalDead += field.Dead[k];
                totalLive += field.Live[k];
                totalPersons += field.Persons[k];
            }
            var roofArea = shape.Area;

            {
                for (var b = 0; b < nb; b++)
                {
                    var part = baySystem.Bays[b];
                    var c = part.Col;
                    var r = part.Row;
                    var bay = new BayResult
                    {
                        label = "Bay " + (c + 1) + "." + (r + 1),
                        gridNames = JoinNames(NameBetween(vLines, xs[c], xs[c + 1]), NameBetween(hLines, ys[r], ys[r + 1])),
                        x0 = (float)part.MinX, x1 = (float)part.MaxX, y0 = (float)part.MinY, y1 = (float)part.MaxY,
                    };
                    if (!baySystem.Plain) bay.polygon = part.Polygon.SelectMany(q => new[] { (float)q[0], (float)q[1] }).ToArray();
                    var area = baySystem.AreaOf(b);
                    var total = dead[b] + live[b];
                    bay.areaM2 = (float)area;
                    bay.deadKn = (float)dead[b];
                    bay.liveKn = (float)live[b];
                    bay.totalKn = (float)total;
                    bay.totalKnM2 = (float)(total / area);
                    bay.utilisation = (float)(total / area / capacity);
                    bay.status = bay.utilisation > 1.0f ? "over" : (bay.utilisation > MarginalFrom ? "marginal" : "ok");
                    bay.persons = (float)persons[b];
                    bay.personsPerM2 = (float)(persons[b] / area);
                    bay.crowdKn = (float)(persons[b] * PersonMassKg * Gravity / 1000.0);
                    bay.shareOfLoadPercent = (float)(100.0 * total / Math.Max(1e-9, totalDead + totalLive));
                    bay.topContributor = TopContributor(inputs, bay);
                    report.bays.Add(bay);
                }
            }

            // ---- columns and their tributary loads
            var columns = inputs.Columns.Count > 0 ? inputs.Columns : Intersections(vLines, hLines, inputs.RoofLength, inputs.RoofWidth, shape);
            if (columns.Count > 0)
            {
                var load = new double[columns.Count];
                var area = new double[columns.Count];
                for (var iy = 0; iy < field.Ny; iy++)
                {
                    for (var ix = 0; ix < field.Nx; ix++)
                    {
                        var cover = field.Coverage[field.Index(ix, iy)];
                        if (cover <= 0) continue;
                        var x = (ix + 0.5) * cw;
                        var y = (iy + 0.5) * ch;
                        var best = 0;
                        var bestD = double.MaxValue;
                        for (var c = 0; c < columns.Count; c++)
                        {
                            var d = (columns[c][0] - x) * (columns[c][0] - x) + (columns[c][1] - y) * (columns[c][1] - y);
                            if (d < bestD) { bestD = d; best = c; }
                        }
                        var k = field.Index(ix, iy);
                        load[best] += field.Dead[k] + field.Live[k];
                        area[best] += cw * ch * cover;
                    }
                }
                var mean = load.Average();
                for (var c = 0; c < columns.Count; c++)
                {
                    report.columns.Add(new ColumnResult
                    {
                        label = "Column " + (c + 1),
                        x = (float)columns[c][0], y = (float)columns[c][1],
                        tributaryM2 = (float)area[c], loadKn = (float)load[c],
                        ratioToMean = (float)(mean > 0 ? load[c] / mean : 1),
                        status = mean > 0 && load[c] > ColumnHighFactor * mean ? "high" : "ok",
                    });
                }
            }

            // ---- items
            foreach (var item in inputs.Items)
            {
                var area = item.Width * item.Height;
                report.items.Add(new ItemLoadResult
                {
                    id = item.Id, label = item.Label, kind = item.Kind.ToString(), basis = item.Basis,
                    areaM2 = (float)area, deadKnM2 = (float)item.DeadKnM2, liveKnM2 = (float)item.LiveKnM2,
                    deadKn = (float)(item.DeadKnM2 * area + item.PointKn),
                    liveKn = (float)(item.LiveKnM2 * area),
                    persons = (float)item.Persons,
                });
            }

            report.balance = Balance(inputs, field, columns);

            var s = new StructureSummary
            {
                roofAreaM2 = (float)roofArea,
                deadKn = (float)totalDead, liveKn = (float)totalLive, totalKn = (float)(totalDead + totalLive),
                meanKnM2 = (float)((totalDead + totalLive) / roofArea),
                peakBayKnM2 = report.bays.Max(b => b.totalKnM2),
                capacityKnM2 = (float)capacity, capacityAssumed = capacityAssumed,
                capacityAccepted = report.assumptionUses[0].state == AnalysisAssumptions.Accepted,
                preliminary = AnalysisAssumptions.IsPreliminary(report.assumptionUses),
                preliminaryNote = AnalysisAssumptions.PreliminaryNote(report.assumptionUses),
                acceptedNote = AnalysisAssumptions.AcceptedNote(report.assumptionUses),
                gridSource = gridAssumed ? "assumed regular " + F1(AssumedBayM) + " m grid" : (string.IsNullOrEmpty(inputs.GridSource) ? "given with the layout" : inputs.GridSource),
                gridAssumed = gridAssumed,
                baysChecked = report.bays.Count,
                baysOver = report.bays.Count(b => b.status == "over"),
                baysMarginal = report.bays.Count(b => b.status == "marginal"),
                columnsChecked = report.columns.Count,
                columnsHigh = report.columns.Count(c => c.status == "high"),
                expectedPersons = (float)totalPersons,
                peakUtilisation = report.bays.Max(b => b.utilisation),
                worstBay = report.bays.OrderByDescending(b => b.utilisation).First().label,
            };
            var busiest = report.bays.OrderByDescending(b => b.persons).Take(Math.Max(1, (int)Math.Ceiling(report.bays.Count * 0.2))).Sum(b => b.persons);
            s.busiestBaysSharePercent = totalPersons > 0 ? (float)(100.0 * busiest / totalPersons) : 0f;
            report.summary = s;
            return report;
        }

        static string JoinNames(string across, string down)
        {
            return string.IsNullOrEmpty(across) || string.IsNullOrEmpty(down) ? "" : across + " / " + down;
        }

        internal static List<GridLineInput> RegularLines(double length)
        {
            var n = Math.Max(1, (int)Math.Floor(length / AssumedBayM + 0.5));   // half up, not to even, so every reader of the layout agrees
            var lines = new List<GridLineInput>();
            for (var i = 1; i < n; i++) lines.Add(new GridLineInput { Name = "", Position = length * i / n });
            return lines;
        }

        static List<double[]> Intersections(List<GridLineInput> v, List<GridLineInput> h, double length, double width, RoofShape shape)
        {
            var list = new List<double[]>();
            foreach (var x in v.Where(l => l.Position >= -0.3 && l.Position <= length + 0.3))
                foreach (var y in h.Where(l => l.Position >= -0.3 && l.Position <= width + 0.3))
                {
                    var at = x.HasGeometry || y.HasGeometry ? GridLineInput.Cross(x, y) : new[] { x.Position, y.Position };
                    if (at == null || at[0] < -0.3 || at[0] > length + 0.3 || at[1] < -0.3 || at[1] > width + 0.3) continue;
                    if (!shape.IsRectangle && !shape.Contains(at[0], at[1]) && shape.DistanceToEdge(at[0], at[1]) > 0.3) continue;   // a crossing in the notch holds up nothing
                    list.Add(at);
                }
            return list;
        }

        /// <summary>What a roof that is not a rectangle changes, and which pieces are not (wholly) on it.</summary>
        static List<string> OutlineNotes(StructureInputs inputs, RoofShape shape)
        {
            var notes = new List<string>
            {
                "The roof's outline (" + shape.Outline.Count + " corners, " + F0(shape.Area) + " m2 of the " + F0(inputs.RoofLength * inputs.RoofWidth) + " m2 of its bounding rectangle) decides where there is roof: a cell carries only its roof part, and a bay is the roof between its grid lines.",
            };
            var outside = inputs.Items.Where(i => i.Width > 0 && i.Height > 0 && shape.Coverage(i.X, i.Y, i.X + i.Width, i.Y + i.Height) < 0.98).Select(i => i.Label).ToList();
            if (outside.Count > 0)
                notes.Add(outside.Count + " piece" + (outside.Count == 1 ? " stands" : "s stand") + " partly or wholly off the roof (" + string.Join(", ", outside.Take(4).ToArray()) + (outside.Count > 4 ? ", ..." : "") + "): the part off the roof carries nothing.");
            return notes;
        }

        static string TopContributor(StructureInputs inputs, BayResult bay)
        {
            double overlap;
            var item = TopItem(inputs, bay, out overlap);
            return item == null ? "" : item.Label;
        }

        /// <summary>The piece that puts most load into the bay, and how much of the bay it covers (m2).</summary>
        static LoadItem TopItem(StructureInputs inputs, BayResult bay, out double overlapM2)
        {
            LoadItem best = null;
            overlapM2 = 0;
            var bestKn = 0.0;
            foreach (var item in inputs.Items)
            {
                var wh = BayOverlapM2(bay, item.X, item.Y, item.X + item.Width, item.Y + item.Height);
                var kn = (item.DeadKnM2 + item.LiveKnM2) * wh + (item.PointKn > 0 && wh > 0 ? item.PointKn * wh / Math.Max(1e-9, item.Width * item.Height) : 0);
                if (kn > bestKn) { bestKn = kn; best = item; overlapM2 = wh; }
            }
            return best;
        }

        /// <summary>The area of a reported bay under a rectangle: the rectangles' overlap, or, for a skewed or cut bay, the overlap with its polygon.</summary>
        public static double BayOverlapM2(BayResult bay, double x0, double y0, double x1, double y1)
        {
            if (bay.polygon == null || bay.polygon.Length < 6)
            {
                var w = Math.Max(0, Math.Min(x1, bay.x1) - Math.Max(x0, bay.x0));
                var h = Math.Max(0, Math.Min(y1, bay.y1) - Math.Max(y0, bay.y0));
                return w * h;
            }
            var poly = new List<double[]>();
            for (var i = 0; i + 1 < bay.polygon.Length; i += 2) poly.Add(new[] { (double)bay.polygon[i], (double)bay.polygon[i + 1] });
            return RoofShape.PolygonArea(RoofShape.ClipConvex(poly, RoofShape.RectPoints(x0, y0, x1, y1)));
        }

        /// <summary>Where to put a bay's label or block: its centroid (its box's middle for a rectangle).</summary>
        public static void BayCentre(BayResult bay, out double cx, out double cy)
        {
            cx = (bay.x0 + bay.x1) * 0.5;
            cy = (bay.y0 + bay.y1) * 0.5;
            if (bay.polygon == null || bay.polygon.Length < 6) return;
            double a = 0, sx = 0, sy = 0;
            var n = bay.polygon.Length / 2;
            for (var i = 0; i < n; i++)
            {
                double x0 = bay.polygon[2 * i], y0 = bay.polygon[2 * i + 1];
                double x1 = bay.polygon[2 * ((i + 1) % n)], y1 = bay.polygon[2 * ((i + 1) % n) + 1];
                var cr = x0 * y1 - x1 * y0;
                a += cr; sx += (x0 + x1) * cr; sy += (y0 + y1) * cr;
            }
            if (Math.Abs(a) < 1e-9) return;
            cx = sx / (3.0 * a); cy = sy / (3.0 * a);
        }

        /// <summary>A bay's spans (metres) along x and along y: a rectangle's sides; for a skewed or cut bay its area over its height and over its width.</summary>
        public static void BaySpans(BayResult bay, out double alongX, out double alongY)
        {
            var bx = bay.x1 - bay.x0;
            var by = bay.y1 - bay.y0;
            if (bay.polygon == null || bay.polygon.Length < 6) { alongX = bx; alongY = by; return; }
            alongX = bay.areaM2 / Math.Max(1e-9, by);
            alongY = bay.areaM2 / Math.Max(1e-9, bx);
        }

        // ---------------------------------------------------------------- balance

        static BalanceResult Balance(StructureInputs inputs, LoadField f, List<double[]> columns)
        {
            var b = new BalanceResult();
            if (columns.Count > 0)
            {
                b.centreX = (float)columns.Average(c => c[0]);
                b.centreY = (float)columns.Average(c => c[1]);
                b.centreBasis = "the columns' centroid";
            }
            else if (inputs.Shape.IsRectangle)
            {
                b.centreX = (float)(inputs.RoofLength / 2);
                b.centreY = (float)(inputs.RoofWidth / 2);
                b.centreBasis = "the plan centre";
            }
            else
            {
                b.centreX = (float)inputs.Shape.CentroidX;
                b.centreY = (float)inputs.Shape.CentroidY;
                b.centreBasis = "the roof's centroid";
            }

            var cw = f.CellW;
            var ch = f.CellH;
            double dSum = 0, dx = 0, dy = 0, tSum = 0, tx = 0, ty = 0;
            double left = 0, right = 0, top = 0, bottom = 0;
            for (var iy = 0; iy < f.Ny; iy++)
            {
                for (var ix = 0; ix < f.Nx; ix++)
                {
                    var k = f.Index(ix, iy);
                    var x = (ix + 0.5) * cw;
                    var y = (iy + 0.5) * ch;
                    var d = f.Dead[k];
                    var t = f.Dead[k] + f.Live[k];
                    dSum += d; dx += d * x; dy += d * y;
                    tSum += t; tx += t * x; ty += t * y;
                    var fl = Math.Max(0, Math.Min(1, (b.centreX - ix * cw) / cw));   // share of the cell left of the centre
                    var ft = Math.Max(0, Math.Min(1, (b.centreY - iy * ch) / ch));   // share above it
                    left += t * fl; right += t * (1 - fl);
                    top += t * ft; bottom += t * (1 - ft);
                }
            }

            b.deadCentroidX = (float)(dx / dSum);
            b.deadCentroidY = (float)(dy / dSum);
            b.totalCentroidX = (float)(tx / tSum);
            b.totalCentroidY = (float)(ty / tSum);
            b.deadEccentricityX = (float)((dx / dSum - b.centreX) / inputs.RoofLength);
            b.deadEccentricityY = (float)((dy / dSum - b.centreY) / inputs.RoofWidth);
            b.totalEccentricityX = (float)((tx / tSum - b.centreX) / inputs.RoofLength);
            b.totalEccentricityY = (float)((ty / tSum - b.centreY) / inputs.RoofWidth);
            b.leftSharePercent = (float)(100.0 * left / tSum);
            b.rightSharePercent = (float)(100.0 * right / tSum);
            b.topSharePercent = (float)(100.0 * top / tSum);
            b.bottomSharePercent = (float)(100.0 * bottom / tSum);

            var ex = Math.Abs(b.totalEccentricityX);
            var ey = Math.Abs(b.totalEccentricityY);
            var worst = Math.Max(ex, ey);
            b.status = worst > EccentricityHigh ? "unbalanced" : (worst > EccentricityMarginal ? "marginal" : "balanced");
            b.heavySide = worst <= EccentricityMarginal ? "" : (ex >= ey ? (b.totalEccentricityX > 0 ? "right" : "left") : (b.totalEccentricityY > 0 ? "bottom" : "top"));
            return b;
        }

        // ---------------------------------------------------------------- advice

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
        static string F2(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
        static string Pct(double fraction) { return F1(Math.Abs(fraction) * 100.0) + "%"; }

        static string SideWord(string axis, bool positive)
        {
            return axis == "x" ? (positive ? "right" : "left") : (positive ? "bottom" : "top");
        }

        static double Ecc(StructureReport r, string axis)
        {
            return axis == "x" ? r.balance.totalEccentricityX : r.balance.totalEccentricityY;
        }

        static LoadItem Moved(LoadItem item, string axis, double by)
        {
            var c = item.Copy();
            if (axis == "x") c.X += by; else c.Y += by;
            return c;
        }

        static void Recommend(StructureReport r, StructureInputs inputs)
        {
            var s = r.summary;
            var b = r.balance;
            var capacity = s.capacityKnM2;

            foreach (var bay in r.bays.Where(x => x.status == "over").OrderByDescending(x => x.utilisation).Take(3))
            {
                double overlap;
                var top = TopItem(inputs, bay, out overlap);
                var fit = "";
                if (top != null && top.DeadKnM2 > 0 && overlap > 0)
                {
                    // the piece's permanent load that would bring this bay exactly to its capacity, if only that piece changed
                    var fitKnM2 = top.DeadKnM2 - (bay.totalKnM2 - capacity) / (overlap / bay.areaM2);
                    fit = fitKnM2 >= top.DeadKnM2 * LightenFloor
                        ? top.Label + " would have to come down from " + F2(top.DeadKnM2) + " to " + F2(fitKnM2) + " kN/m2 (" + F0(fitKnM2 * 1000 / Gravity) + " kg/m2) here to fit. Otherwise, "
                        : "Lightening " + top.Label + " alone will not bring it within the capacity: ";
                }
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "over-capacity",
                    target = bay.label,
                    text = bay.label + (string.IsNullOrEmpty(bay.gridNames) ? "" : " (" + bay.gridNames + ")") + " carries " + F1(bay.totalKnM2) + " kN/m2 against a capacity of " +
                           F1(capacity) + " (" + F0(bay.utilisation * 100) + "%)" + (string.IsNullOrEmpty(bay.topContributor) ? "" : ", most of it from " + bay.topContributor) +
                           ". " + (fit == "" ? "Lighten what stands there, " : fit) + "move it to a lighter bay or have the deck checked" + (s.capacityAssumed ? (s.capacityAccepted ? " (the capacity is the built-in assumption you accepted, not the engineer's figure)" : " (the capacity is a placeholder: enter the engineer's figure)") : "") + ".",
                });
            }

            var vulnerable = r.bays.OrderByDescending(x => x.utilisation).Take(3).ToList();
            r.recommendations.Add(new StructureRecommendation
            {
                kind = "vulnerable",
                target = string.Join(", ", vulnerable.Select(x => x.label).ToArray()),
                text = "Most heavily loaded: " + string.Join("; ", vulnerable.Select(x => x.label + (string.IsNullOrEmpty(x.gridNames) ? "" : " (" + x.gridNames + ")") + " " + F0(x.utilisation * 100) + "%" +
                       (string.IsNullOrEmpty(x.topContributor) ? "" : " under " + x.topContributor)).ToArray()) + ".",
            });

            foreach (var axis in new[] { "x", "y" })
            {
                var e = Ecc(r, axis);
                if (Math.Abs(e) > EccentricityMarginal) AdviseBalance(r, inputs, axis);
            }

            if (s.expectedPersons > 0)
            {
                var busiest = r.bays.OrderByDescending(x => x.persons).Take(Math.Max(1, (int)Math.Ceiling(r.bays.Count * 0.2))).ToList();
                if (s.busiestBaysSharePercent >= 50f && r.bays.Count >= 5)
                {
                    var names = string.Join(", ", busiest.Select(x => x.label).ToArray());
                    r.recommendations.Add(new StructureRecommendation
                    {
                        kind = "concentration",
                        target = names,
                        text = "About " + F0(s.busiestBaysSharePercent) + "% of the " + F0(s.expectedPersons) + " expected people are in " + busiest.Count + " of " + r.bays.Count + " bays (" + names +
                               "). Keep heavy permanent loads out of them and check the deck there first.",
                    });
                }
            }

            foreach (var col in r.columns.Where(x => x.status == "high").OrderByDescending(x => x.ratioToMean).Take(2))
            {
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "over-capacity",
                    target = col.label,
                    text = col.label + " at (" + F1(col.x) + ", " + F1(col.y) + ") takes " + F0(col.loadKn) + " kN, " + F1(col.ratioToMean) + "x the average column. Move load off its tributary area or have it checked.",
                });
            }

            if (s.gridAssumed)
            {
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "grid",
                    target = "Structural grid",
                    text = "The layout carries no structural grid, so a regular " + F1(AssumedBayM) + " m grid was assumed. Push the roof from Revit (its grids and columns come with it) for real bays.",
                });
            }

            if (s.baysOver == 0 && b.status == "balanced")
            {
                r.recommendations.Insert(0, new StructureRecommendation
                {
                    kind = "fine",
                    target = "All bays",
                    text = "No bay is over its capacity, and the centre of the load is within " + F0(EccentricityMarginal * 100) + "% of the structure's centre.",
                });
            }
        }

        sealed class Step
        {
            public bool IsMove;
            public int Index;
            public double Amount;          // metres moved, or the new permanent load in kN/m2
            public bool Hits;              // reaches the target
            public StructureInputs State;
            public StructureReport After;
        }

        /// <summary>
        /// The balance on one axis, put right by up to three steps, each one piece moved toward the light side or made lighter, each found by
        /// re-running the model: the smallest move that reaches the target if one does (before any lightening), else the step that gets
        /// closest. A step is refused if it would push the busiest bay further over its capacity than it already is.
        /// </summary>
        static void AdviseBalance(StructureReport r, StructureInputs inputs, string axis)
        {
            var b = r.balance;
            var e0 = Ecc(r, axis);
            var sign = e0 > 0 ? 1.0 : -1.0;
            var heavy = SideWord(axis, e0 > 0);
            var light = SideWord(axis, e0 <= 0);
            var share = axis == "x" ? (e0 > 0 ? b.rightSharePercent : b.leftSharePercent) : (e0 > 0 ? b.bottomSharePercent : b.topSharePercent);
            var dimName = axis == "x" ? "length" : "width";
            var head = F0(share) + "% of the load sits on the " + heavy + " half (its centre is " + Pct(e0) + " of the " + dimName + " off the structure's centre). ";

            var state = inputs;
            var cur = r;
            var steps = 0;
            for (; steps < 3; steps++)
            {
                if (Math.Abs(Ecc(cur, axis)) <= TargetEccentricity + 0.005) break;
                var step = BestStep(cur, state, axis, sign, inputs);
                if (step == null) break;

                var item = state.Items[step.Index];
                var beforePeak = cur.summary.peakUtilisation;
                var afterE = Ecc(step.After, axis);
                var peakText = step.After.summary.peakUtilisation > beforePeak + 0.005
                    ? ", and the busiest bay goes from " + F0(beforePeak * 100) + "% to " + F0(step.After.summary.peakUtilisation * 100) + "%"
                    : (step.After.summary.peakUtilisation < beforePeak - 0.005 ? ", and the busiest bay comes down from " + F0(beforePeak * 100) + "% to " + F0(step.After.summary.peakUtilisation * 100) + "%" : "");
                var rec = new StructureRecommendation
                {
                    kind = step.IsMove ? "move" : "lighten", target = item.Label, itemId = item.Id, axis = axis,
                    eccentricityAfter = (float)afterE, peakUtilisationAfter = step.After.summary.peakUtilisation,
                };
                if (step.IsMove)
                {
                    rec.moveM = (float)(-sign * step.Amount);
                    rec.text = (steps == 0 ? head : "Then ") + "Move " + item.Label + " " + F1(step.Amount) + " m toward the " + light + ": the centre of the load goes to " + Pct(afterE) + " off" + peakText + ".";
                }
                else
                {
                    rec.newDeadKnM2 = (float)step.Amount;
                    rec.text = (steps == 0 ? head : "Then ") + "Bring " + item.Label + " down from " + F2(item.DeadKnM2) + " to about " + F2(step.Amount) + " kN/m2 (" + F0(step.Amount * 1000 / Gravity) +
                               " kg/m2: a lighter build-up or a thinner substrate): the centre of the load goes to " + Pct(afterE) + " off" + peakText + ".";
                }
                r.recommendations.Add(rec);
                state = step.State;
                cur = step.After;
            }

            if (steps == 0)
            {
                r.recommendations.Add(new StructureRecommendation
                {
                    kind = "move", target = "Load on the " + heavy + " half", axis = axis,
                    text = head + "No single move or lightening of one piece puts it right without overloading a bay: shift several pieces toward the " + light + " or lighten what stays on the " + heavy + " half.",
                });
            }
            else if (Math.Abs(Ecc(cur, axis)) > TargetEccentricity + 0.005)
            {
                var last = r.recommendations[r.recommendations.Count - 1];
                last.text += " That is as far as these steps go: " + Pct(Ecc(cur, axis)) + " is still off, so shift more load toward the " + light + ".";
            }
        }

        /// <summary>How far a piece can slide along an axis (direction -1 / +1) before it leaves the roof or runs into another piece.</summary>
        static double FreeTravel(StructureInputs state, int index, string axis, double direction)
        {
            var item = state.Items[index];
            var x = axis == "x";
            double lo = x ? item.X : item.Y, size = x ? item.Width : item.Height;
            double crossLo = x ? item.Y : item.X, crossSize = x ? item.Height : item.Width;
            var travel = direction < 0 ? lo : (x ? state.RoofLength : state.RoofWidth) - (lo + size);
            if (!state.Shape.IsRectangle)
            {
                // on a roof with an outline the piece must stay wholly on it: slide it in small steps and stop at the first that leaves
                var free = 0.0;
                for (var d = 0.05; d <= travel + 1e-9; d += 0.05)
                {
                    var dx = x ? direction * d : 0;
                    var dy = x ? 0 : direction * d;
                    if (state.Shape.Coverage(item.X + dx, item.Y + dy, item.X + dx + item.Width, item.Y + dy + item.Height) < 0.999) break;
                    free = d;
                }
                travel = Math.Min(travel, free);
            }
            for (var k = 0; k < state.Items.Count; k++)
            {
                var o = state.Items[k];
                if (k == index || o.Kind == LoadKind.Tree) continue;
                double olo = x ? o.X : o.Y, osize = x ? o.Width : o.Height;
                double ocrossLo = x ? o.Y : o.X, ocrossSize = x ? o.Height : o.Width;
                if (Math.Min(crossLo + crossSize, ocrossLo + ocrossSize) - Math.Max(crossLo, ocrossLo) <= 0.05) continue;   // beside it, not in its way
                if (direction < 0 && olo + osize <= lo + 1e-9) travel = Math.Min(travel, lo - (olo + osize));
                if (direction > 0 && olo >= lo + size - 1e-9) travel = Math.Min(travel, olo - (lo + size));
            }
            return travel;
        }

        static Step BestStep(StructureReport cur, StructureInputs state, string axis, double sign, StructureInputs original)
        {
            var centre = axis == "x" ? cur.balance.centreX : cur.balance.centreY;
            var e0 = Math.Abs(Ecc(cur, axis));
            var peakLimit = Math.Max(cur.summary.peakUtilisation, 1.0) + 0.005;

            // Pieces that carry a fair share of the load and sit on the heavy side, heaviest first.
            var candidates = new List<int>();
            for (var i = 0; i < state.Items.Count; i++)
            {
                var item = state.Items[i];
                if (item.Kind == LoadKind.Tree) continue;
                var kn = (item.DeadKnM2 + item.LiveKnM2) * item.Width * item.Height;
                var pos = axis == "x" ? item.X + item.Width / 2 : item.Y + item.Height / 2;
                if (kn < MinItemShare * cur.summary.totalKn || (pos - centre) * sign <= 0) continue;
                candidates.Add(i);
            }
            candidates = candidates.OrderByDescending(i => (state.Items[i].DeadKnM2 + state.Items[i].LiveKnM2) * state.Items[i].Width * state.Items[i].Height).Take(4).ToList();

            Step best = null;
            Action<Step> consider = s =>
            {
                var e = Math.Abs(Ecc(s.After, axis));
                if (s.After.summary.peakUtilisation > peakLimit || e >= e0 - (s.Hits ? 1e-4 : MinStepGain)) return;
                if (best == null) { best = s; return; }
                var better = s.Hits != best.Hits ? s.Hits
                           : s.Hits ? (s.IsMove != best.IsMove ? s.IsMove : (s.IsMove ? s.Amount < best.Amount : s.Amount > best.Amount))
                           : e < Math.Abs(Ecc(best.After, axis));
                if (better) best = s;
            };

            foreach (var i in candidates)
            {
                var index = i;
                var item = state.Items[i];

                // a move toward the light side
                var room = Math.Max(0, FreeTravel(state, i, axis, -sign));
                if (room >= 0.05)
                {
                    Func<double, StructureInputs> at = by => state.WithItem(index, Moved(item, axis, -sign * by));
                    Func<double, double> excess = by => sign * Ecc(Compute(at(by)), axis) - TargetEccentricity;
                    var amount = room;
                    var hits = excess(room) <= 0;
                    if (hits)
                    {
                        double lower = 0, upper = room;
                        for (var it = 0; it < 22; it++)
                        {
                            var mid = (lower + upper) / 2;
                            if (excess(mid) > 0) lower = mid; else upper = mid;
                        }
                        amount = Math.Min(room, Math.Ceiling(upper * 10 - 1e-9) / 10.0);
                    }
                    var st = at(amount);
                    consider(new Step { IsMove = true, Index = index, Amount = amount, Hits = hits, State = st, After = Compute(st) });
                }

                // a lighter build-up (once per piece: a second step would compound the floor)
                if (item.DeadKnM2 > 0 && Math.Abs(item.DeadKnM2 - original.Items[index].DeadKnM2) < 1e-12)
                {
                    Func<double, StructureInputs> at = v => { var c = item.Copy(); c.DeadKnM2 = v; return state.WithItem(index, c); };
                    Func<double, double> excess = v => sign * Ecc(Compute(at(v)), axis) - TargetEccentricity;
                    var floor = item.DeadKnM2 * LightenFloor;
                    var amount = floor;
                    var hits = excess(floor) <= 0;
                    if (hits)
                    {
                        double lower = floor, upper = item.DeadKnM2;   // excess(lower) <= 0 < excess(upper)
                        for (var it = 0; it < 22; it++)
                        {
                            var mid = (lower + upper) / 2;
                            if (excess(mid) > 0) upper = mid; else lower = mid;
                        }
                        amount = Math.Floor(lower * 100 + 1e-9) / 100.0;
                    }
                    else amount = Math.Ceiling(floor * 100 - 1e-9) / 100.0;
                    var st = at(amount);
                    consider(new Step { IsMove = false, Index = index, Amount = amount, Hits = hits, State = st, After = Compute(st) });
                }
            }
            return best;
        }

        static List<string> Assumptions(double capacity, bool capacityAssumed, bool gridAssumed, string gridSource)
        {
            return new List<string>
            {
                "Screening model, not a structural verification: loads that sit ON the structure (its own weight is outside), on the roof's bounding rectangle, in 0.5 m cells split over the bays of the structural grid by area.",
                "Permanent: green-roof build-ups at their water-saturated weight (published where the provider prints it, else dry layers plus water at saturation), a tree's weight as a point load, " +
                    F1(SportsSurfaceKnM2) + " kN/m2 under courts and play areas, " + F1(RoofFinishesKnM2) + " kN/m2 of roof finishes everywhere.",
                "Imposed (characteristic, the highest intensity where areas overlap): courts " + F1(CourtLiveKnM2) + " kN/m2 (category C4, DIN EN 1991-1-1/NA Table 6.1DE); play and assembly areas " + F1(ActivityLiveKnM2) +
                    "; accessible roof gardens, walkways and circulation " + F1(AccessibleLiveKnM2) + "; a roof not accessible but for maintenance " + F2(RoofLiveKnM2) + " (category H: check the National Annex). Only the court value is from the standard; the others are the author's.",
                "People (where activity concentrates, not what the imposed load is made of): players by sport, seated spectators, " + F1(AccessiblePersonsPerM2) + " per m2 on accessible gardens, " + F1(ActivityPersonsPerM2) +
                    " per m2 on play areas, " + F0(EntryPersons) + " waiting at each entry; " + F0(PersonMassKg) + " kg each.",
                "Capacity: " + F1(capacity) + " kN/m2 characteristic G + Q, the same in every bay" + (capacityAssumed ? ". This is a PLACEHOLDER (5.0 sports use + 3.0 permanent): the layout carries no deck capacity, so enter the structural engineer's figure." : "."),
                "Balance: the centre of the total (G + Q) load against the columns' centroid (else the plan centre); off by more than " + F0(EccentricityMarginal * 100) + "% of the dimension is marginal, more than " +
                    F0(EccentricityHigh * 100) + "% unbalanced. Advice moves one piece until it is " + F0(TargetEccentricity * 100) + "%. Column loads are tributary areas (each cell to its nearest column).",
                gridAssumed ? "No structural grid in the layout: a regular " + F1(AssumedBayM) + " m grid is assumed." : "Structural grid from " + (string.IsNullOrEmpty(gridSource) ? "the layout" : gridSource) + ".",
            };
        }
    }
}
