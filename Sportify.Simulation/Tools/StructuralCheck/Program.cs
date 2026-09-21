using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Roof;
using Sportify.Simulation.Structure;

// usage: StructuralCheck <layout.json> [--json]
// Prints a summary of the structural load report for the layout (or the whole report as JSON), then runs the checks that must hold whatever the constants.
// StructuralCheck --assumptions-json prints the register of analysis assumptions (Tools/StructuralCheck/assumptions-parity.js compares it with the web app copy)
if (args.Length > 0 && args[0] == "--assumptions-json") { Console.WriteLine(AnalysisAssumptions.ToJson()); return 0; }
var layoutPath = args[0];
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(layoutPath))!;
var inputs = StructureLayoutAdapter.ToInputs(layout);
var report = StructureModel.Analyse(inputs);

if (args.Contains("--json"))
{
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    return 0;
}

var s = report.summary;
Console.WriteLine($"roof {inputs.RoofLength:0.#} x {inputs.RoofWidth:0.#} m ({s.roofAreaM2:0} m2), {inputs.Items.Count} load pieces, grid: {s.gridSource}, {report.bays.Count} bays, {report.columns.Count} columns");
Console.WriteLine($"load: permanent {s.deadKn:0} kN + imposed {s.liveKn:0} kN = {s.totalKn:0} kN ({s.meanKnM2:0.00} kN/m2 mean, busiest bay {s.peakBayKnM2:0.00}); capacity {s.capacityKnM2:0.0} kN/m2{(s.capacityAssumed ? " (placeholder)" : "")}");
Console.WriteLine($"people expected: {s.expectedPersons:0}; {s.busiestBaysSharePercent:0}% of them in the busiest fifth of the bays");
var b = report.balance;
Console.WriteLine($"balance ({b.status}, heavy side: {(b.heavySide == "" ? "none" : b.heavySide)}): total load centre off by {b.totalEccentricityX * 100:+0.0;-0.0}% (x) / {b.totalEccentricityY * 100:+0.0;-0.0}% (y) against {b.centreBasis}; permanent only {b.deadEccentricityX * 100:+0.0;-0.0}% / {b.deadEccentricityY * 100:+0.0;-0.0}%");
Console.WriteLine($"  shares of the load: left {b.leftSharePercent:0}% right {b.rightSharePercent:0}%  top {b.topSharePercent:0}% bottom {b.bottomSharePercent:0}%");
Console.WriteLine();
foreach (var i in report.items)
    Console.WriteLine($"  {i.label,-26} {i.kind,-8} {i.areaM2,7:0.0} m2  G {i.deadKnM2,5:0.00} kN/m2  Q {i.liveKnM2,4:0.00}  {i.deadKn,7:0} kN  {i.persons,4:0} people");
Console.WriteLine();
Console.WriteLine("  bay utilisation (rows = down the plan):");
var xs = report.verticalLinesM.Length - 1;
if (report.bays.Any(bay => bay.polygon != null))
    foreach (var bay in report.bays) Console.WriteLine($"    {bay.label,-8} {bay.areaM2,7:0.0} m2  {bay.utilisation * 100,4:0}%  {bay.status}");
else
for (var r = 0; r < report.horizontalLinesM.Length - 1; r++)
    Console.WriteLine("    " + string.Join(" ", Enumerable.Range(0, xs).Select(c => $"{report.bays[r * xs + c].utilisation * 100,4:0}%{(report.bays[r * xs + c].status == "over" ? "!" : report.bays[r * xs + c].status == "marginal" ? "~" : " ")}")));
foreach (var c in report.columns.Where(c => c.status == "high")) Console.WriteLine($"  HIGH {c.label} ({c.x:0.#}, {c.y:0.#}) {c.loadKn:0} kN = {c.ratioToMean:0.00}x mean");
foreach (var rec in report.recommendations) Console.WriteLine($"  REC [{rec.kind}] {rec.text}");

// ---------------------------------------------------------------- checks (true whatever the constants)
var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }
Console.WriteLine();

// 1. conservation: what the layout puts on the roof is what the bays, the columns and the summary add up to
var shape = inputs.Shape;
// the part of a piece's footprint that is on the roof (all of it, up to the bounding rectangle, for a rectangular roof)
double Overlap(LoadItem i)
{
    var w = Math.Max(0, Math.Min(i.X + i.Width, inputs.RoofLength) - Math.Max(i.X, 0));
    var h = Math.Max(0, Math.Min(i.Y + i.Height, inputs.RoofWidth) - Math.Max(i.Y, 0));
    if (shape.IsRectangle) return w * h;
    return RoofShape.PolygonArea(RoofShape.ClipConvex(shape.Outline!, RoofShape.RectPoints(i.X, i.Y, i.X + i.Width, i.Y + i.Height)));
}
// a concentrated load is shared between the four cell centres around it, and each cell carries only its roof part
double PointOnRoof(LoadItem i)
{
    if (i.PointKn <= 0) return 0;
    if (shape.IsRectangle) return i.PointKn;
    var nx = Math.Max(1, (int)Math.Floor(inputs.RoofLength / StructureModel.CellM + 0.5)); var ny = Math.Max(1, (int)Math.Floor(inputs.RoofWidth / StructureModel.CellM + 0.5));
    double cw = inputs.RoofLength / nx, ch = inputs.RoofWidth / ny;
    double gx = (i.X + i.Width / 2) / cw - 0.5, gy = (i.Y + i.Height / 2) / ch - 0.5;
    int ix = (int)Math.Floor(gx), iy = (int)Math.Floor(gy);
    double tx = gx - ix, ty = gy - iy, sum = 0;
    for (var dy = 0; dy <= 1; dy++)
        for (var dx = 0; dx <= 1; dx++)
        {
            var wgt = (dx == 0 ? 1 - tx : tx) * (dy == 0 ? 1 - ty : ty);
            if (wgt <= 0) continue;
            var cx = Math.Min(nx - 1, Math.Max(0, ix + dx)); var cy = Math.Min(ny - 1, Math.Max(0, iy + dy));
            sum += i.PointKn * wgt * shape.Coverage(cx * cw, cy * ch, (cx + 1) * cw, (cy + 1) * ch);
        }
    return sum;
}
var deadExpected = StructureModel.RoofFinishesKnM2 * shape.Area
                   + inputs.Items.Sum(i => i.DeadKnM2 * Overlap(i) + PointOnRoof(i));
Check("permanent load = finishes + every piece over the roof", Math.Abs(s.deadKn - deadExpected) < 1e-3 * Math.Max(1, deadExpected) / 1000, $"({s.deadKn:0.000} vs {deadExpected:0.000} kN)");
Check("bays add up to the roof total (G, Q, people)",
      Math.Abs(report.bays.Sum(x => x.deadKn) - s.deadKn) < 1e-3 && Math.Abs(report.bays.Sum(x => x.liveKn) - s.liveKn) < 1e-3 && Math.Abs(report.bays.Sum(x => x.persons) - s.expectedPersons) < 1e-3,
      $"({report.bays.Sum(x => x.totalKn):0.000} vs {s.totalKn:0.000} kN)");
Check("bay areas add up to the roof", Math.Abs(report.bays.Sum(x => x.areaM2) - s.roofAreaM2) < 1e-2, $"({report.bays.Sum(x => x.areaM2):0.00} m2)");
if (report.columns.Count > 0)
    Check("columns carry the whole load between them", Math.Abs(report.columns.Sum(x => x.loadKn) - s.totalKn) < 1e-2, $"({report.columns.Sum(x => x.loadKn):0.00} vs {s.totalKn:0.00} kN)");
var expectedPeople = inputs.Items.Sum(i => i.Persons * Overlap(i) / Math.Max(1e-9, i.Width * i.Height)) + inputs.Entries.Count * StructureModel.EntryPersons;
Check("people = every piece's people over the roof + arrivals at the entries", Math.Abs(s.expectedPersons - expectedPeople) < 1e-3, $"({s.expectedPersons:0.000} vs {expectedPeople:0.000})");

// 2. mirror the whole layout: the eccentricity changes sign, nothing else changes
StructureInputs Mirror(StructureInputs a, string axis)
{
    var len = axis == "x" ? a.RoofLength : a.RoofWidth;
    var m = new StructureInputs
    {
        RoofLength = a.RoofLength, RoofWidth = a.RoofWidth, GridSource = a.GridSource, CapacityKnM2 = a.CapacityKnM2,
        Notes = a.Notes,
        Outline = a.Outline?.Select(q => axis == "x" ? new[] { len - q[0], q[1] } : new[] { q[0], len - q[1] }).ToList(),
    };
    GridLineInput MirrorLine(GridLineInput l, bool vertical)
    {
        var ml = new GridLineInput { Name = l.Name, HasGeometry = l.HasGeometry };
        // a line's Position is where it crosses the middle of the roof: it flips only when the line is mirrored ACROSS its own kind of axis
        ml.Position = (vertical ? axis == "x" : axis == "y") ? len - l.Position : l.Position;
        if (l.HasGeometry)
        {
            ml.X0 = axis == "x" ? len - l.X0 : l.X0; ml.X1 = axis == "x" ? len - l.X1 : l.X1;
            ml.Y0 = axis == "y" ? len - l.Y0 : l.Y0; ml.Y1 = axis == "y" ? len - l.Y1 : l.Y1;
        }
        return ml;
    }
    foreach (var l in a.VerticalLines) m.VerticalLines.Add(MirrorLine(l, true));
    foreach (var l in a.HorizontalLines) m.HorizontalLines.Add(MirrorLine(l, false));
    foreach (var c in a.Columns) m.Columns.Add(axis == "x" ? new[] { len - c[0], c[1] } : new[] { c[0], len - c[1] });
    foreach (var e in a.Entries) m.Entries.Add(axis == "x" ? new[] { len - e[0], e[1] } : new[] { e[0], len - e[1] });
    foreach (var p in a.Paths)
    {
        var q = new PathInput { WidthM = p.WidthM };
        foreach (var pt in p.Points) q.Points.Add(axis == "x" ? new[] { len - pt[0], pt[1] } : new[] { pt[0], len - pt[1] });
        m.Paths.Add(q);
    }
    foreach (var i in a.Items)
    {
        var c = i.Copy();
        if (axis == "x") c.X = len - (i.X + i.Width); else c.Y = len - (i.Y + i.Height);
        m.Items.Add(c);
    }
    return m;
}
foreach (var axis in new[] { "x", "y" })
{
    var mr = StructureModel.Analyse(Mirror(inputs, axis));
    var ea = axis == "x" ? report.balance.totalEccentricityX : report.balance.totalEccentricityY;
    var eb = axis == "x" ? mr.balance.totalEccentricityX : mr.balance.totalEccentricityY;
    var sameUtil = report.bays.Select(x => Math.Round(x.utilisation, 4)).OrderBy(v => v).SequenceEqual(mr.bays.Select(x => Math.Round(x.utilisation, 4)).OrderBy(v => v));
    var sameCols = report.columns.Select(x => Math.Round(x.loadKn, 3)).OrderBy(v => v).SequenceEqual(mr.columns.Select(x => Math.Round(x.loadKn, 3)).OrderBy(v => v));
    Check($"mirrored across {axis}: eccentricity flips sign, same bay and column loads", Math.Abs(ea + eb) < 1e-5 && sameUtil && sameCols, $"({ea * 100:+0.00;-0.00}% -> {eb * 100:+0.00;-0.00}%)");
}

// 3. the capacity only rescales utilisation
var doubled = StructureModel.Analyse(new StructureInputs
{
    RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, Outline = inputs.Outline, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
    Items = inputs.Items, Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = s.capacityKnM2 * 2,
});
Check("twice the capacity, half the utilisation, same loads", Math.Abs(doubled.summary.peakUtilisation * 2 - s.peakUtilisation) < 1e-4 && Math.Abs(doubled.summary.totalKn - s.totalKn) < 1e-3);

// 4. the bays' boundaries don't change the totals: the same layout on its assumed grid carries the same load
var gridless = StructureModel.Analyse(new StructureInputs
{
    RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, Outline = inputs.Outline, Items = inputs.Items, Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = inputs.CapacityKnM2,
});
Check("without a grid: the same load, on an assumed grid that says so", Math.Abs(gridless.summary.totalKn - s.totalKn) < 1e-3 && gridless.summary.gridAssumed && gridless.recommendations.Any(x => x.kind == "grid"),
      $"({gridless.bays.Count} bays)");

// 5. put a heavy piece in the lightest bay: that bay gains what the piece weighs there, the others don't
{
    var lightest = report.bays.OrderBy(x => x.totalKn).First();
    // a metre clear of the bay's edges: the model's cells are half a metre wide, so load within a cell of a grid line is shared with the next bay
    // (for a bay that is not a rectangle: the first spot in it, on a half-metre grid, where a 3 x 3 m slab and a metre around it lie wholly inside the bay)
    double sx = lightest.x0 + 1.0, sy = lightest.y0 + 1.0, sw = Math.Min(3, lightest.x1 - lightest.x0 - 2.0), sh = Math.Min(3, lightest.y1 - lightest.y0 - 2.0);
    if (lightest.polygon != null)
    {
        sw = 3; sh = 3;
        var found = false;
        for (double yy = lightest.y0; yy <= lightest.y1 - 5 && !found; yy += 0.5)
            for (double xx = lightest.x0; xx <= lightest.x1 - 5 && !found; xx += 0.5)
                if (StructureModel.BayOverlapM2(lightest, xx, yy, xx + 5, yy + 5) > 25 - 1e-6) { sx = xx + 1; sy = yy + 1; found = true; }
        if (!found) { sw = 0; sh = 0; }
    }
    var slab = new LoadItem { Id = "test", Label = "test slab", Kind = LoadKind.Zone, X = sx, Y = sy, Width = sw, Height = sh, DeadKnM2 = 6.0, LiveKnM2 = StructureModel.RoofLiveKnM2 };
    var more = new StructureInputs
    {
        RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, Outline = inputs.Outline, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
        Items = inputs.Items.Concat(new[] { slab }).ToList(), Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = s.capacityKnM2,
    };
    var after = StructureModel.Analyse(more);
    var bayAfter = after.bays.First(x => x.label == lightest.label);
    var added = 6.0 * slab.Width * slab.Height;
    Check("a 6 kN/m2 slab in the lightest bay adds its weight to that bay only", Math.Abs(bayAfter.deadKn - lightest.deadKn - added) < 1e-3 && after.bays.Where(x => x.label != lightest.label).All(x => Math.Abs(x.deadKn - report.bays.First(y => y.label == x.label).deadKn) < 1e-6),
          $"(+{bayAfter.deadKn - lightest.deadKn:0.000} of {added:0.000} kN in {lightest.label})");
}

// 5b. a bed whose corners were moved weighs its own outline, not its bounding box; a piece of furniture adds its catalogue weight, at its centre, once
{
    var lightest = report.bays.OrderBy(x => x.totalKn).First();
    double fx = 0, fy = 0; var room = false;
    if (lightest.polygon == null) { fx = lightest.x0 + 1.0; fy = lightest.y0 + 1.0; room = lightest.x1 - lightest.x0 >= 5 && lightest.y1 - lightest.y0 >= 5; }
    else
        for (double yy = lightest.y0; yy <= lightest.y1 - 5 && !room; yy += 0.5)
            for (double xx = lightest.x0; xx <= lightest.x1 - 5 && !room; xx += 0.5)
                if (StructureModel.BayOverlapM2(lightest, xx, yy, xx + 5, yy + 5) > 25 - 1e-6) { fx = xx + 1; fy = yy + 1; room = true; }
    if (room)
    {
        StructureInputs With(LoadItem extra) => new StructureInputs
        {
            RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, Outline = inputs.Outline, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
            Items = inputs.Items.Concat(new[] { extra }).ToList(), Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = s.capacityKnM2,
        };
        // an L: a 3 x 1 bar and a 1 x 2 leg = 5 m2 inside a 3 x 3 box (9 m2)
        var bed = new LoadItem
        {
            Id = "test_l", Label = "test L bed", Kind = LoadKind.Zone, X = fx, Y = fy, Width = 3, Height = 3, DeadKnM2 = 6.0, LiveKnM2 = StructureModel.RoofLiveKnM2,
            Polygon = new List<double[]> { new[] { fx, fy }, new[] { fx + 3, fy }, new[] { fx + 3, fy + 1 }, new[] { fx + 1, fy + 1 }, new[] { fx + 1, fy + 3 }, new[] { fx, fy + 3 } },
        };
        var withBed = StructureModel.Analyse(With(bed));
        var bedBay = withBed.bays.First(x => x.label == lightest.label);
        Check("an L-shaped bed of 5 m2 in a 3 x 3 m box weighs 6 x 5 kN, not 6 x 9", Math.Abs(bed.AreaM2 - 5) < 1e-9 && Math.Abs(bedBay.deadKn - lightest.deadKn - 30.0) < 1e-3 && Math.Abs(withBed.summary.deadKn - s.deadKn - 30.0) < 1e-3,
              $"(+{bedBay.deadKn - lightest.deadKn:0.000} kN in {lightest.label}, +{withBed.summary.deadKn - s.deadKn:0.000} kN in all)");
        var bedItem = withBed.items.First(x => x.id == "test_l");
        Check("the report gives the L's own area (5 m2) and its 30 kN", Math.Abs(bedItem.areaM2 - 5) < 1e-9 && Math.Abs(bedItem.deadKn - 30.0) < 1e-6, $"({bedItem.areaM2:0.###} m2, {bedItem.deadKn:0.###} kN)");

        var chair = StructureModel.FurnitureItem("test_chair", "test chair", fx + 1, fy + 1, 1, 1, 250.0);
        var withChair = StructureModel.Analyse(With(chair));
        var chairBay = withChair.bays.First(x => x.label == lightest.label);
        var kn = 250.0 * StructureModel.Gravity / 1000.0;
        Check("a 250 kg piece of furniture adds 250 x 9.81 N, all of it, in the bay it stands in", Math.Abs(withChair.summary.deadKn - s.deadKn - kn) < 1e-3 && Math.Abs(chairBay.deadKn - lightest.deadKn - kn) < 1e-3,
              $"(+{withChair.summary.deadKn - s.deadKn:0.000} of {kn:0.000} kN)");
        Check("the piece keeps the roof's own imposed load (it does not make its footprint occupied) and adds no people", Math.Abs(withChair.summary.liveKn - s.liveKn) < 1e-3 && Math.Abs(withChair.summary.expectedPersons - s.expectedPersons) < 1e-6);
    }
}

// 6. the steps recommended for an axis, applied in order, do what each says: the balance and the busiest bay afterwards are the ones it states
foreach (var axis in new[] { "x", "y" })
{
    var steps = report.recommendations.Where(x => (x.kind == "move" || x.kind == "lighten") && x.itemId != null && x.axis == axis).ToList();
    if (steps.Count == 0) continue;
    var items = inputs.Items.Select(i => i.Copy()).ToList();
    var start = axis == "x" ? report.balance.totalEccentricityX : report.balance.totalEccentricityY;
    var n = 0;
    foreach (var rec in steps)
    {
        n++;
        var piece = items.First(i => i.Id == rec.itemId);
        var startCover = shape.Coverage(piece.X, piece.Y, piece.X + piece.Width, piece.Y + piece.Height);
        if (rec.kind == "move")
        {
            if (axis == "x") piece.X += rec.moveM; else piece.Y += rec.moveM;
            if (!shape.IsRectangle)
                Check($"{axis}: step {n} moves '{rec.target}' without taking it off the roof", shape.Coverage(piece.X, piece.Y, piece.X + piece.Width, piece.Y + piece.Height) >= Math.Min(startCover, 0.999) - 1e-9);
        }
        else piece.DeadKnM2 = rec.newDeadKnM2;
        var again = StructureModel.Analyse(new StructureInputs
        {
            RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, Outline = inputs.Outline, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
            Items = items, Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = inputs.CapacityKnM2, GridSource = inputs.GridSource,
        });
        var e = axis == "x" ? again.balance.totalEccentricityX : again.balance.totalEccentricityY;
        Check($"{axis}: step {n} ({rec.kind} '{rec.target}') leaves the balance and the busiest bay where it says",
              Math.Abs(e - rec.eccentricityAfter) < 1e-4 && Math.Abs(again.summary.peakUtilisation - rec.peakUtilisationAfter) < 1e-4 && Math.Abs(e) < Math.Abs(start) + 1e-9,
              $"({start * 100:+0.0;-0.0}% -> {e * 100:+0.0;-0.0}%, busiest bay {again.summary.peakUtilisation * 100:0}%)");
        start = e;
    }
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
