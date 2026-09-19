using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Structure;

// usage: StructuralCheck <layout.json> [--json]
// Prints a summary of the structural load report for the layout (or the whole report as JSON), then runs the checks that must hold whatever the constants.
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
for (var r = 0; r < report.horizontalLinesM.Length - 1; r++)
    Console.WriteLine("    " + string.Join(" ", Enumerable.Range(0, xs).Select(c => $"{report.bays[r * xs + c].utilisation * 100,4:0}%{(report.bays[r * xs + c].status == "over" ? "!" : report.bays[r * xs + c].status == "marginal" ? "~" : " ")}")));
foreach (var c in report.columns.Where(c => c.status == "high")) Console.WriteLine($"  HIGH {c.label} ({c.x:0.#}, {c.y:0.#}) {c.loadKn:0} kN = {c.ratioToMean:0.00}x mean");
foreach (var rec in report.recommendations) Console.WriteLine($"  REC [{rec.kind}] {rec.text}");

// ---------------------------------------------------------------- checks (true whatever the constants)
var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }
Console.WriteLine();

// 1. conservation: what the layout puts on the roof is what the bays, the columns and the summary add up to
double Overlap(LoadItem i)
{
    var w = Math.Max(0, Math.Min(i.X + i.Width, inputs.RoofLength) - Math.Max(i.X, 0));
    var h = Math.Max(0, Math.Min(i.Y + i.Height, inputs.RoofWidth) - Math.Max(i.Y, 0));
    return w * h;
}
var deadExpected = StructureModel.RoofFinishesKnM2 * inputs.RoofLength * inputs.RoofWidth
                   + inputs.Items.Sum(i => i.DeadKnM2 * Overlap(i) + (i.PointKn > 0 ? i.PointKn : 0));
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
    };
    foreach (var l in a.VerticalLines) m.VerticalLines.Add(new GridLineInput { Name = l.Name, Position = axis == "x" ? len - l.Position : l.Position });
    foreach (var l in a.HorizontalLines) m.HorizontalLines.Add(new GridLineInput { Name = l.Name, Position = axis == "y" ? len - l.Position : l.Position });
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
    RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
    Items = inputs.Items, Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = s.capacityKnM2 * 2,
});
Check("twice the capacity, half the utilisation, same loads", Math.Abs(doubled.summary.peakUtilisation * 2 - s.peakUtilisation) < 1e-4 && Math.Abs(doubled.summary.totalKn - s.totalKn) < 1e-3);

// 4. the bays' boundaries don't change the totals: the same layout on its assumed grid carries the same load
var gridless = StructureModel.Analyse(new StructureInputs
{
    RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, Items = inputs.Items, Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = inputs.CapacityKnM2,
});
Check("without a grid: the same load, on an assumed grid that says so", Math.Abs(gridless.summary.totalKn - s.totalKn) < 1e-3 && gridless.summary.gridAssumed && gridless.recommendations.Any(x => x.kind == "grid"),
      $"({gridless.bays.Count} bays)");

// 5. put a heavy piece in the lightest bay: that bay gains what the piece weighs there, the others don't
{
    var lightest = report.bays.OrderBy(x => x.totalKn).First();
    // a metre clear of the bay's edges: the model's cells are half a metre wide, so load within a cell of a grid line is shared with the next bay
    var slab = new LoadItem { Id = "test", Label = "test slab", Kind = LoadKind.Zone, X = lightest.x0 + 1.0, Y = lightest.y0 + 1.0, Width = Math.Min(3, lightest.x1 - lightest.x0 - 2.0), Height = Math.Min(3, lightest.y1 - lightest.y0 - 2.0), DeadKnM2 = 6.0, LiveKnM2 = StructureModel.RoofLiveKnM2 };
    var more = new StructureInputs
    {
        RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
        Items = inputs.Items.Concat(new[] { slab }).ToList(), Paths = inputs.Paths, Entries = inputs.Entries, CapacityKnM2 = s.capacityKnM2,
    };
    var after = StructureModel.Analyse(more);
    var bayAfter = after.bays.First(x => x.label == lightest.label);
    var added = 6.0 * slab.Width * slab.Height;
    Check("a 6 kN/m2 slab in the lightest bay adds its weight to that bay only", Math.Abs(bayAfter.deadKn - lightest.deadKn - added) < 1e-3 && after.bays.Where(x => x.label != lightest.label).All(x => Math.Abs(x.deadKn - report.bays.First(y => y.label == x.label).deadKn) < 1e-6),
          $"(+{bayAfter.deadKn - lightest.deadKn:0.000} of {added:0.000} kN in {lightest.label})");
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
        if (rec.kind == "move") { if (axis == "x") piece.X += rec.moveM; else piece.Y += rec.moveM; }
        else piece.DeadKnM2 = rec.newDeadKnM2;
        var again = StructureModel.Analyse(new StructureInputs
        {
            RoofLength = inputs.RoofLength, RoofWidth = inputs.RoofWidth, VerticalLines = inputs.VerticalLines, HorizontalLines = inputs.HorizontalLines, Columns = inputs.Columns,
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
