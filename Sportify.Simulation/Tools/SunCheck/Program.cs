using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Sun;

// usage: SunCheck <layout.json> [--json]
// Prints a summary of the sun and shade report for the layout (or the inputs and the whole report as JSON), then runs the checks that must hold whatever the constants.
var layoutPath = args[0];
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(layoutPath))!;
var inputs = SunLayoutAdapter.ToInputs(layout);
var report = SunModel.Analyse(inputs);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };

if (args.Contains("--json"))
{
    var st = inputs.Structure;
    var dump = new
    {
        inputs = new
        {
            latitude = inputs.LatitudeDeg, north = inputs.NorthDeg, shadeTarget = inputs.ShadeTargetPercent, gardenMinSun = inputs.GardenMinSunHours, equipment = inputs.Equipment,
            roofLength = st.RoofLength, roofWidth = st.RoofWidth, outline = inputs.Outline, obstacles = inputs.Obstacles, drains = inputs.Drains, openings = inputs.Openings,
            items = st.Items.Select(i => new { id = i.Id, kind = i.Kind.ToString(), name = i.Name, label = i.Label, x = i.X, y = i.Y, w = i.Width, h = i.Height, seats = i.Seats }),
            plants = inputs.Wind.Plants.Select(p => new { id = p.Id, form = p.Form, x = p.X, y = p.Y, height = p.HeightM, crown = p.CrownM }),
            entries = st.Entries, paths = st.Paths.Select(p => new { points = p.Points, width = p.WidthM }),
        },
        report,
    };
    Console.WriteLine(JsonSerializer.Serialize(dump, jsonOptions));
    return 0;
}

var s = report.summary;
Console.WriteLine(SunModel.CaseStudy(inputs, report) + (s.latitudeAssumed ? " (latitude assumed)" : "") + (s.northAssumed ? " (orientation assumed)" : ""));
foreach (var d in report.days)
    Console.WriteLine($"  {d.name,-12} sun {d.sunriseH:0.0}-{d.sunsetH:0.0} h, noon {d.noonElevationDeg:0.0} deg at {d.noonBearingDeg:0} deg, roof mean {d.roofMeanSunHours:0.0} h -> {d.roofMeanSunHoursAfter:0.0} h with the equipment");
foreach (var z in report.zones)
    Console.WriteLine($"  {z.kind,-10} {z.label,-30} {z.areaM2,6:0} m2  sun {z.sunHoursJune,4:0.0} / {z.sunHoursMarch,4:0.0} / {z.sunHoursDecember,4:0.0} h  peak shade {z.peakShadePercent,3:0}% -> {z.afterPeakShadePercent,3:0}%  {z.status} -> {z.afterStatus}");
foreach (var p in report.equipment)
    Console.WriteLine($"  PIECE {p.name} {p.widthM:0.#} x {p.depthM:0.#} m at ({p.x:0.#}, {p.y:0.#}) for {p.zoneLabel}: shade {p.shadeBeforePercent:0}% -> {p.shadeAfterPercent:0}%, +{p.addedLoadKn:0.#} kN, wind {p.windUpliftKn:0.#} kN");
Console.WriteLine($"  deck: +{report.structure.addedKn:0.#} kN, peak {report.structure.peakUtilisationBefore * 100:0}% -> {report.structure.peakUtilisationAfter * 100:0}%, bays over {report.structure.baysOverBefore} -> {report.structure.baysOverAfter}");
foreach (var rec in report.recommendations) Console.WriteLine($"  REC [{rec.kind}] {rec.text}");
Console.WriteLine();

var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }
bool Near(double a, double b, double tol) => Math.Abs(a - b) <= tol;
const double Deg = Math.PI / 180.0;

// 1. the sun
{
    var noon = SunModel.SunAt(51, 172, 12);
    var decl = SunModel.DeclinationDeg(172);
    Check("the summer declination is about 23.4 degrees, the winter one about -23.4", Near(decl, 23.44, 0.3) && Near(SunModel.DeclinationDeg(355), -23.44, 0.3), $"({decl:0.00}, {SunModel.DeclinationDeg(355):0.00})");
    Check("solar noon: the sun is due south at 90 - |latitude - declination|", Near(noon.ElevationDeg, 90 - Math.Abs(51 - decl), 1e-9) && Near(noon.BearingDeg, 180, 1e-9), $"({noon.ElevationDeg:0.00} deg, {noon.BearingDeg:0.0})");
    var ok = true;
    foreach (var t in new[] { 6.25, 8.0, 9.5, 10.25, 11.75 })
    {
        var a = SunModel.SunAt(53.5, 172, t); var b = SunModel.SunAt(53.5, 172, 24 - t);
        ok &= Near(a.ElevationDeg, b.ElevationDeg, 1e-9) && Near(a.BearingDeg + b.BearingDeg, 360, 1e-9);
    }
    Check("the morning mirrors the afternoon: same height, bearings that add to 360", ok);
    Check("the sun rises in the north-east and sets in the north-west in June, east and west in March", SunModel.SunAt(51, 172, 4.75).BearingDeg is > 40 and < 70 && Near(SunModel.SunAt(51, 80, 6.0).BearingDeg, 90, 3.5), $"({SunModel.SunAt(51, 172, 4.75).BearingDeg:0}, {SunModel.SunAt(51, 80, 6.0).BearingDeg:0})");
    var hours = SunModel.DayTimes(51, 172).Count * SunModel.StepH;
    var analytic = 2 * Math.Acos((Math.Sin(SunModel.MinElevationDeg * Deg) - Math.Sin(51 * Deg) * Math.Sin(decl * Deg)) / (Math.Cos(51 * Deg) * Math.Cos(decl * Deg))) / (15 * Deg);
    Check("the day's length agrees with the closed form for the hour angle of the horizon", Near(hours, analytic, 0.5), $"({hours:0.0} h against {analytic:0.00} h)");
    Check("polar day and night do not break it", SunModel.DayTimes(80, 172).Count >= 0 && SunModel.DayTimes(-80, 172).Count >= 0);
}

// 2. the shadows
{
    var sun = SunModel.SunAt(51, 172, 12);
    var k = 1.0 / Math.Tan(sun.ElevationDeg * Deg);
    double tx, ty;
    SunModel.TowardSun(sun, 0, out tx, out ty);
    Check("north at the top of the plan: the noon sun is toward +y (the bottom, south)", Near(tx, 0, 1e-9) && Near(ty, 1, 1e-9));
    var shapes = new List<Shape>();
    SunModel.WallShadow(new ObstacleInput { X0 = 10, Y0 = 10, X1 = 20, Y1 = 10, HeightM = 2, ThicknessM = 0.2 }, tx, ty, k, shapes);
    var len = 2 * k;
    Check("a 2 m wall at noon casts a shadow of h / tan(alt) toward the north (up the plan), 10 m wide", shapes.Count == 1 && shapes[0].Contains(15, 10 - len * 0.5) && shapes[0].Contains(10.5, 10 - len + 0.1) && !shapes[0].Contains(15, 10 + 1) && !shapes[0].Contains(25, 10 - len * 0.5) && !shapes[0].Contains(15, 10 - len - 0.2), $"(length {len:0.00} m)");
    SunModel.TowardSun(sun, 90, out var tx2, out var ty2);
    var east = new List<Shape>();
    SunModel.WallShadow(new ObstacleInput { X0 = 10, Y0 = 10, X1 = 10, Y1 = 20, HeightM = 2, ThicknessM = 0.2 }, tx2, ty2, k, east);
    Check("with the plan's top facing east the noon sun is on the right and the shadow falls to the left", Near(tx2, 1, 1e-9) && east[0].Contains(10 - len * 0.5, 15) && !east[0].Contains(10 + 1, 15));

    var sun45 = new SunPosition { ElevationDeg = 45, BearingDeg = 180, Up = true };
    SunModel.TowardSun(sun45, 0, out var ux, out var uy);
    var tree = SunModel.EllipseShadow(20, 20, 6, 2, ux, uy, 1.0, 45, 0.25);
    Check("a tree canopy (sphere r 2 at 6 m) at 45 degrees: an ellipse 6 m up the plan, r / sin(45) long, r wide", tree.Contains(20, 14) && tree.Contains(20, 14 - 2.8) && tree.Contains(20 + 1.9, 14) && !tree.Contains(20 + 2.1, 14) && !tree.Contains(20, 14 - 2.9) && Near(tree.A, 2 / Math.Sin(45 * Deg), 1e-9));
    var plate = new EquipmentPiece { key = "sail", shape = "rect", x = 10, y = 10, widthM = 4, depthM = 3, heightM = 3.5f, transmission = 0.1f };
    var pl = new List<Shape>();
    SunModel.EquipmentShadow(plate, ux, uy, 1.0, 45, 0.25, pl);
    Check("a plate 3.5 m up at 45 degrees is shifted 3.5 m away from the sun, and keeps its size", pl[0].Contains(12, 10 - 3.5 + 1) && Near(pl[0].MaxX - pl[0].MinX, 4, 1e-9) && Near(pl[0].MaxY - pl[0].MinY, 3, 1e-9) && Near(pl[0].Tau, 0.1, 1e-6));
    var two = new List<Shape> { new Shape { Tau = 0.5, MinX = 0, MaxX = 1, MinY = 0, MaxY = 1, Px = new[] { 0.0, 1, 1, 0 }, Py = new[] { 0.0, 0, 1, 1 } }, new Shape { Tau = 0.25, MinX = 0, MaxX = 1, MinY = 0, MaxY = 1, Px = new[] { 0.0, 1, 1, 0 }, Py = new[] { 0.0, 0, 1, 1 } } };
    Check("overlapping shadows multiply what they let through", Near(SunScene.Fraction(two, 0.5, 0.5), 0.125, 1e-12) && Near(SunScene.Fraction(two, 5, 5), 1, 1e-12));
}

// 3. an empty roof: every cell gets the whole day; mirror symmetry of a walled roof
{
    var empty = new SunInputs { Structure = new StructureInputs { RoofLength = 30, RoofWidth = 20 }, Wind = new Sportify.Simulation.Wind.WindInputs { RoofLength = 30, RoofWidth = 20 } };
    var grid = SunModel.BuildGrid(empty);
    var scene = SunModel.BuildScene(empty, null);
    var f = new double[grid.Count];
    var scratch = new List<Shape>();
    double total = 0;
    foreach (var t in SunModel.DayTimes(51, 172)) { SunModel.FillFractions(grid, scene, SunModel.SunAt(51, 172, t), 0.25, f, scratch); total += f[0] * SunModel.StepH; }
    Check("with nothing on the roof every cell gets the whole day's sun", Near(total, SunModel.DayTimes(51, 172).Count * SunModel.StepH, 1e-9), $"({total:0.0} h)");

    SunScene Walled(bool mirrored)
    {
        var sc = SunModel.BuildScene(empty, null);
        sc.Obstacles.Add(mirrored ? new ObstacleInput { X0 = 26, Y0 = 8, X1 = 20, Y1 = 8, HeightM = 3, ThicknessM = 0.3 } : new ObstacleInput { X0 = 4, Y0 = 8, X1 = 10, Y1 = 8, HeightM = 3, ThicknessM = 0.3 });
        sc.Obstacles.Add(mirrored ? new ObstacleInput { X0 = 26, Y0 = 8, X1 = 26, Y1 = 14, HeightM = 3, ThicknessM = 0.3 } : new ObstacleInput { X0 = 4, Y0 = 8, X1 = 4, Y1 = 14, HeightM = 3, ThicknessM = 0.3 });
        return sc;
    }
    double SunHours(SunScene sc, double x, double y)
    {
        double h = 0;
        var shapes = new List<Shape>();
        foreach (var t in SunModel.DayTimes(51, 172)) { sc.Shapes(SunModel.SunAt(51, 172, t), 0.25, shapes); h += SunScene.Fraction(shapes, x, y) * SunModel.StepH; }
        return h;
    }
    var okMirror = true;
    foreach (var p in new[] { new[] { 6.0, 5.0 }, new[] { 8.0, 10.0 }, new[] { 12.0, 15.0 }, new[] { 2.0, 12.0 } })
        okMirror &= Near(SunHours(Walled(false), p[0], p[1]), SunHours(Walled(true), 30 - p[0], p[1]), 1e-9);
    Check("mirroring a walled roof east to west (north at the top) mirrors its sun hours over the whole day", okMirror);
    var noonShapes = new List<Shape>();
    Walled(false).Shapes(SunModel.SunAt(51, 172, 12.25), 0.25, noonShapes);
    Check("at noon in June a 3 m wall shades the cells just north of it (up the plan), not those south of it", SunScene.Fraction(noonShapes, 7, 7.3) == 0 && SunScene.Fraction(noonShapes, 7, 9.5) == 1);
}

// 4. the report is consistent
{
    Check("every people zone that is judged has a status, gardens too; courts are not judged", report.zones.All(z => z.kind == "court" ? z.status == "n/a" : true) && report.zones.Where(z => z.kind == "garden").All(z => z.status is "ok" or "too-shaded" or "n/a"));
    Check("equipment only takes sun away: every day's roof mean, every zone's June sun and peak shade move the right way",
        report.days.All(d => d.roofMeanSunHoursAfter <= d.roofMeanSunHours + 1e-6) && report.zones.All(z => z.afterSunHoursJune <= z.sunHoursJune + 1e-6 && z.afterPeakShadePercent >= z.peakShadePercent - 1e-6));
    Check("no more pieces than the cap, and no zone gets more than its share", report.equipment.Count <= SunModel.MaxPieces && report.equipment.GroupBy(p => p.zoneId).All(g => g.Count() <= SunModel.MaxPerZone), $"({report.equipment.Count} pieces)");

    var courts = inputs.Structure.Items.Where(i => i.Kind == LoadKind.Court).ToList();
    bool Overlaps(double ax0, double ay0, double ax1, double ay1, double bx0, double by0, double bx1, double by1) => ax0 < bx1 && ax1 > bx0 && ay0 < by1 && ay1 > by0;
    var fitsAll = true;
    for (var i = 0; i < report.equipment.Count; i++)
    {
        var p = report.equipment[i];
        fitsAll &= p.x >= SunModel.EdgeMarginM - 1e-6 && p.y >= SunModel.EdgeMarginM - 1e-6 && p.x + p.widthM <= inputs.Structure.RoofLength - SunModel.EdgeMarginM + 1e-6 && p.y + p.depthM <= inputs.Structure.RoofWidth - SunModel.EdgeMarginM + 1e-6;
        fitsAll &= !courts.Any(c => Overlaps(p.x, p.y, p.x + p.widthM, p.y + p.depthM, c.X, c.Y, c.X + c.Width, c.Y + c.Height));
        fitsAll &= !inputs.Drains.Any(d => d[0] >= p.x - 0.3 && d[0] <= p.x + p.widthM + 0.3 && d[1] >= p.y - 0.3 && d[1] <= p.y + p.depthM + 0.3);
        fitsAll &= !inputs.Structure.Entries.Any(e => e[0] >= p.x - 2 && e[0] <= p.x + p.widthM + 2 && e[1] >= p.y - 2 && e[1] <= p.y + p.depthM + 2);
        for (var j = i + 1; j < report.equipment.Count; j++)
        {
            var q = report.equipment[j];
            fitsAll &= !Overlaps(p.x, p.y, p.x + p.widthM, p.y + p.depthM, q.x, q.y, q.x + q.widthM, q.y + q.depthM);
        }
    }
    Check("every piece is inside the roof, off the courts, drains and entries, and off the other pieces", fitsAll);
    Check("no garden ends below its need, or, if it began below it, more than a quarter hour lower",
        report.zones.Where(z => z.kind == "garden" && z.status != "n/a").All(z => z.afterSunHoursJune >= s.gardenMinSunHours - 1e-6 || z.afterSunHoursJune >= z.sunHoursJune - SunModel.GardenTolerance - 1e-6));
    Check("the summary counts what the zones say", s.peopleZonesTooSunny == report.zones.Count(z => (z.kind == "people" || z.kind == "spectators") && z.status == "too-sunny") && s.peopleZonesTooSunnyAfter <= s.peopleZonesTooSunny + 0);
    Check("a piece's added load and wind uplift are its weight and the peak pressure x its coefficient x its area",
        report.equipment.All(p => p.key == "tree" ? Near(p.addedLoadKn, p.weightKnM2 * p.widthM * p.depthM, 1e-3) && p.windUpliftKn == 0
            : Near(p.addedLoadKn, p.weightKnM2 * (p.shape == "disc" ? Math.PI * p.widthM * p.widthM / 4 : p.widthM * p.depthM), 1e-3) && Near(p.windUpliftKn, p.windCp * s.peakWindPressurePa * 1e-3 * (p.shape == "disc" ? Math.PI * p.widthM * p.widthM / 4 : p.widthM * p.depthM), 1e-2)));
    Check("the deck check adds the pieces' weight and cannot come out lighter", report.equipment.Count == 0 || (Near(report.structure.addedKn, report.equipment.Sum(p => p.addedLoadKn), 1e-3) && report.structure.peakUtilisationAfter >= report.structure.peakUtilisationBefore - 1e-6));
    Check("the same layout gives the identical report twice", JsonSerializer.Serialize(SunModel.Analyse(inputs), jsonOptions) == JsonSerializer.Serialize(report, jsonOptions));
}

// 5. the inputs and what they change
{
    SunInputs Copy()
    {
        var c = SunLayoutAdapter.ToInputs(layout);
        return c;
    }

    var none = Copy(); none.ShadeTargetPercent = 0;
    var r0 = SunModel.Analyse(none);
    Check("a shade target of 0 needs no equipment", r0.equipment.Count == 0 && r0.summary.peopleZonesTooSunny == 0);

    var impossible = Copy(); impossible.ShadeTargetPercent = 100;
    var r100 = SunModel.Analyse(impossible);
    Check("a target of 100% stops at the cap and never worsens a zone", r100.equipment.Count <= SunModel.MaxPieces && r100.zones.All(z => z.afterPeakShadePercent >= z.peakShadePercent - 1e-6));

    var low = Copy(); low.LatitudeDeg = 40; low.NorthDeg = 0;
    var high = Copy(); high.LatitudeDeg = 60; high.NorthDeg = 0;
    var rl = SunModel.Analyse(low); var rh = SunModel.Analyse(high);
    Check("further north: a lower noon sun, longer June days, shorter December ones", rl.days[0].noonElevationDeg > rh.days[0].noonElevationDeg + 15 && rh.days[0].sunsetH - rh.days[0].sunriseH > rl.days[0].sunsetH - rl.days[0].sunriseH + 2 && rh.days[2].roofMeanSunHours < rl.days[2].roofMeanSunHours);

    var flipped = Copy(); flipped.NorthDeg = ((inputs.NorthDeg ?? 0) + 180) % 360;
    var rf = SunModel.Analyse(flipped);
    Check("turning the roof half round changes where the shade falls", Math.Abs(rf.zones.Sum(z => z.sunHoursJune) - report.zones.Sum(z => z.sunHoursJune)) > 0 || report.zones.Count == 0 || inputs.Obstacles.Count + inputs.Wind.Plants.Count == 0);

    foreach (var mode in new[] { "light", "fixed" })
    {
        var c = Copy(); c.Equipment = mode; c.ShadeTargetPercent = 90;
        var rr = SunModel.Analyse(c);
        var allowedKeys = mode == "light" ? new[] { "sail", "parasol" } : new[] { "pergola", "canopy" };
        Check($"only {mode} equipment is recommended when the designer says so", rr.equipment.All(p => allowedKeys.Contains(p.key)), $"({string.Join(", ", rr.equipment.Select(p => p.key))})");
    }

    var polar = Copy(); polar.LatitudeDeg = 70;
    Check("beyond the polar circle the analysis still runs and places nothing", SunModel.Analyse(polar).equipment.Count == 0);

    var bare = Copy(); bare.LatitudeDeg = null; bare.NorthDeg = null; bare.ShadeTargetPercent = null; bare.GardenMinSunHours = null; bare.Equipment = null; bare.Structure.CapacityKnM2 = null; bare.Structure.AcceptedAssumptions.Clear();
    var rb = SunModel.Analyse(bare);
    Check("nothing entered: six inputs, all unconfirmed, PRELIMINARY, and every one is in the register", rb.assumptionUses.Count == 6 && rb.assumptionUses.All(u => u.state == "unconfirmed" && AnalysisAssumptions.Find(u.key) != null) && rb.summary.preliminary && rb.summary.preliminaryNote.StartsWith("PRELIMINARY"));
    var acc = Copy(); acc.LatitudeDeg = null; acc.NorthDeg = null; acc.ShadeTargetPercent = null; acc.GardenMinSunHours = null; acc.Equipment = null; acc.Structure.CapacityKnM2 = null;
    acc.Structure.AcceptedAssumptions.Clear(); acc.Structure.AcceptedAssumptions.AddRange(AnalysisAssumptions.Editable.Select(d => d.Key));
    var ra = SunModel.Analyse(acc);
    Check("all accepted: the same numbers, no longer preliminary", !ra.summary.preliminary && ra.assumptionUses.All(u => u.state == "accepted") && Near(ra.zones.Sum(z => z.sunHoursJune), rb.zones.Sum(z => z.sunHoursJune), 1e-9));
    var ent = Copy(); ent.LatitudeDeg = 52; ent.NorthDeg = 10; ent.ShadeTargetPercent = 40; ent.GardenMinSunHours = 3; ent.Equipment = "all"; ent.Structure.CapacityKnM2 = 6;
    var re = SunModel.Analyse(ent);
    Check("all entered: entered, not preliminary", re.assumptionUses.All(u => u.state == "entered") && !re.summary.preliminary);
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
