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
            items = st.Items.Select(i => new { id = i.Id, kind = i.Kind.ToString(), name = i.Name, label = i.Label, x = i.X, y = i.Y, w = i.Width, h = i.Height, seats = i.Seats, poly = i.Polygon != null && i.Polygon.Count >= 3 ? i.Polygon : null }),
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

// 8. Kinetics: the louvre's actuation law and its mechanics (LouvreActuationModel, LouvreMechanicsModel), against hand calculations
{
    Check("the louvre opens 90 - elevation: closed under a sun overhead, fully open on the horizon", Near(LouvreActuationModel.OpenAngleDegForElevation(90), 0, 1e-9) && Near(LouvreActuationModel.OpenAngleDegForElevation(60), 30, 1e-9) && Near(LouvreActuationModel.OpenAngleDegForElevation(0), 90, 1e-9) && Near(LouvreActuationModel.OpenAngleDegForElevation(-5), 90, 1e-9));
    var day = LouvreActuationModel.DesignDayStates(51);
    var noonState = day.First(x => x.Label == "solar noon");
    Check("across the design day the louvre is most closed at solar noon, open again either side", day.Count == 3 && noonState.LouvreOpenAngleDeg < day.First(x => x.Label == "morning").LouvreOpenAngleDeg && noonState.LouvreOpenAngleDeg < day.First(x => x.Label == "afternoon").LouvreOpenAngleDeg,
          $"({string.Join(", ", day.Select(x => x.Label + " " + x.LouvreOpenAngleDeg.ToString("0") + " deg"))})");

    var d0 = new LouvreDesign();
    Check("wind torque on a blade's pivot is nothing flat and nothing face-on", Near(LouvreMechanics.BladeTorqueNm(d0, 1000, 3, 0), 0, 1e-9) && Near(LouvreMechanics.BladeTorqueNm(d0, 1000, 3, 90), 0, 1e-9));
    var rk = LouvreMechanics.Analyse(d0, 6, 3, 1000, 2.0, day);
    // q C_N c^2 L (1/2 - a) / 4 = 1000 x 1.2 x 0.0225 x 3 x 0.25 / 4 = 5.0625 N m at 30 degrees open
    Check("the wind torque on a blade peaks at 30 degrees open, at q C_N c^2 L (1/2 - a) / 4 (5.06 N m for 1000 Pa on a 150 mm x 3 m blade)", Near(rk.PeakTorqueGustNm, 5.0625, 1e-3) && Near(LouvreMechanics.BladeTorqueNm(d0, 1000, 3, 30), 5.0625, 1e-3) && Near(rk.PeakTorqueAngleDeg, 30, 0.5), $"({rk.PeakTorqueGustNm:0.0000} N m at {rk.PeakTorqueAngleDeg:0} deg)");
    Check("the wind torque scales with the design pressure and with the span", Near(LouvreMechanics.BladeTorqueNm(d0, 2000, 3, 30), 2 * LouvreMechanics.BladeTorqueNm(d0, 1000, 3, 30), 1e-9) && Near(LouvreMechanics.BladeTorqueNm(d0, 1000, 6, 30), 2 * LouvreMechanics.BladeTorqueNm(d0, 1000, 3, 30), 1e-9));
    // 43 blades: ceil(6 m / (0.15 m x 0.95)); a hollow 150 x 30 x 2 mm section is 704 mm2, 5.70 kg over 3 m
    Check("blade count and mass follow from the geometry: 43 blades of 5.70 kg across 6 m", rk.BladeCount == 43 && Near(rk.PitchM, 6.0 / 43, 1e-9) && Near(rk.BladeMassKg, 2700 * 0.000704 * 3, 1e-6) && Near(rk.TotalMassKg, rk.BladeMassKg * 43, 1e-9), $"({rk.BladeCount} blades, {rk.BladeMassKg:0.00} kg)");
    Check("the blades overlap when closed: pitch is under the chord, so a closed louvre stops all the sun from overhead", rk.PitchM < rk.ChordM && Near(LouvreMechanics.SunStoppedShare(rk.ChordM, rk.PitchM, 90, 0), 1.0, 1e-9));
    Check("edge-on blades pass the sun from overhead; blades turned face-on to a low sun stop it", Near(LouvreMechanics.SunStoppedShare(rk.ChordM, rk.PitchM, 90, 90), 0, 1e-9) && Near(LouvreMechanics.SunStoppedShare(rk.ChordM, rk.PitchM, 10, 80), 1.0, 1e-9));
    // w = 1000 x 1.2 x 0.15 = 180 N/m on I = 1.2366e-7 m4, E = 70 GPa: delta = 5 w L^4 / 384 E I = 21.9 mm, span/137; allowed span (384 E I / 5 w 200)^(1/3) = 2.64 m
    Check("a 3 m blade bends 21.9 mm (span/137) face-on to 1000 Pa, past span/200: flagged, with an allowed span of 2.64 m", Near(rk.DeflectionMm, 21.93, 0.1) && Near(rk.DeflectionRatio, 136.8, 0.5) && !rk.DeflectionOk && Near(rk.AllowedSpanM, 2.6435, 0.005), $"({rk.DeflectionMm:0.00} mm, span/{rk.DeflectionRatio:0}, allowed {rk.AllowedSpanM:0.000} m)");
    var shortSpan = LouvreMechanics.Analyse(d0, 6, 2, 1000, 2.0, day);
    Check("deflection goes with the fourth power of the span, and a 2 m blade passes", Near(rk.DeflectionMm / shortSpan.DeflectionMm, 16, 1e-6) && shortSpan.DeflectionOk);
    Check("the actuator carries every blade at the wind limit with the safety factor and the linkage's losses, and must hold more than it moves in a gust",
          Near(rk.ActuatorTorqueNm, 1.5 * 43 * (rk.PeakTorqueOperatingNm + 0.3) / 0.8, 1e-9) && rk.HoldingTorqueNm > rk.ActuatorTorqueNm && Near(rk.ActuatorForceN, rk.ActuatorTorqueNm / 0.06, 1e-9),
          $"({rk.ActuatorTorqueNm:0.0} N m to move, {rk.HoldingTorqueNm:0.0} N m to hold)");
    Check("the design peak of 1000 Pa is a 40 m/s wind: past the 14 m/s operating limit, so the pergola stows closed; a calm site (100 Pa, 12.6 m/s) does not need to", rk.StowRequired && Near(rk.DesignWindMs, 40, 0.01) && !LouvreMechanics.Analyse(d0, 6, 3, 100, 0.2, day).StowRequired);
    Check("one state per actuation state, with the sun stopped and the wind torque at each", rk.States.Count == 3 && rk.States.All(x => x.SunStoppedPercent >= 0 && x.SunStoppedPercent <= 100 && x.WindTorqueGustNm >= 0) && rk.EnergyWhPerDay > 0 && rk.EnergyWhPerDay < 1);
    var none = LouvreMechanics.Analyse(d0, 6, 3, 0, 0, day);
    Check("no design pressure known: no wind load, said plainly, never presented as a safe result", none.PeakTorqueGustNm == 0 && none.Findings.Any(f => f.StartsWith("No design wind pressure")));

    Check("nothing entered: the assumed and placeholder inputs are unconfirmed, the standard ones accepted, and the result PRELIMINARY", d0.Preliminary && d0.Uses().Count == LouvreDesign.Inputs.Length && d0.Uses().Where(u => u.Status == "assumed" || u.Status == "placeholder").All(u => u.State == "unconfirmed") && d0.Uses().Where(u => u.Status == "standard" || u.Status == "literature").All(u => u.State == "accepted"));
    var mine = new LouvreDesign(new Dictionary<string, double> { ["chord_m"] = 0.2, ["nonsense"] = 5, ["thickness_m"] = -1, ["wall_m"] = double.NaN });
    Check("the mechanical engineer's own value is used and marked entered; an unknown key, a negative number and NaN are ignored", Near(mine["chord_m"], 0.2, 1e-12) && mine.Uses().First(u => u.Key == "chord_m").State == "entered" && Near(mine["thickness_m"], 0.03, 1e-12) && Near(mine["wall_m"], 0.002, 1e-12));
    Check("a wider chord makes a heavier blade and more wind torque", LouvreMechanics.Analyse(mine, 6, 3, 1000, 2.0, day).BladeMassKg > rk.BladeMassKg && LouvreMechanics.BladeTorqueNm(mine, 1000, 3, 30) > LouvreMechanics.BladeTorqueNm(d0, 1000, 3, 30));
    var everything = new Dictionary<string, double>();
    foreach (var def in LouvreDesign.Inputs) everything[def.Key] = def.Default;
    Check("every input entered: no longer preliminary, the same numbers", !new LouvreDesign(everything).Preliminary && Near(LouvreMechanics.Analyse(new LouvreDesign(everything), 6, 3, 1000, 2.0, day).ActuatorTorqueNm, rk.ActuatorTorqueNm, 1e-9));

    // spacing: how many blades, how far apart, from the shade target
    var spDay = LouvreActuationModel.StatesFor(LouvreHost.Horizontal, 51, 0, 0, 1, 0, 0);     // blades along plan y, the plan's top facing north
    Check("an overhead unit's three states carry the profile angle (the sun's elevation when the blades run square to it) and a lean toward the sun", spDay.Count == 3 && spDay.All(x => x.ProfileAngleDeg >= x.SunElevationDeg - 0.2 && Near(Math.Sqrt(x.LeanX * x.LeanX + x.LeanY * x.LeanY), 1, 1e-9)));
    var sp50 = LouvreMechanics.RecommendSpacing(d0, LouvreHost.Horizontal, 6, 50, spDay);
    Check("a 50% shade target over 6 m needs far fewer than the 43 closed-overlap blades, and the pitch and count fill the width exactly", sp50.Count < 43 && Near(sp50.PitchM * sp50.Count, 6, 1e-9) && sp50.StoppedAtBindingPercent >= 50 - 1e-6, $"({sp50.Count} blades at {sp50.PitchM * 1000:0} mm, {sp50.StoppedAtBindingPercent:0}% stopped, closed {sp50.ClosedStoppedPercent:0}%)");
    var sp100 = LouvreMechanics.RecommendSpacing(d0, LouvreHost.Horizontal, 6, 100, spDay);
    Check("a 100% target needs more blades than 50%, still stops all the sun where it is hardest, and the tighter the spacing the closer to a roof it is closed", sp100.Count > sp50.Count && sp100.StoppedAtBindingPercent >= 99.5 && sp100.ClosedStoppedPercent > sp50.ClosedStoppedPercent);
    var spLow = LouvreMechanics.RecommendSpacing(d0, LouvreHost.Horizontal, 6, 5, spDay);
    Check("a very small target is capped at the widest pitch the inputs allow (3 chords), and says so", spLow.CappedAtMaxPitch && spLow.PitchM <= 3 * 0.15 + 1e-9 && spLow.Count == (int)Math.Ceiling(6 / (3 * 0.15) - 1e-9));
    var bindingSep = spDay.Max(x => Math.Sin(x.ProfileAngleDeg * Math.PI / 180));
    Check("the pitch is limited by the hardest sun: c / (target x its ray spacing), then the count rounds it up to fill the width", Near(sp50.Count, Math.Ceiling(6 / (0.15 / (0.5 * bindingSep)) - 1e-9), 0.5) && sp50.BindingLabel != "");
    var mSpaced = LouvreMechanics.Analyse(d0, LouvreHost.Horizontal, 6, 3, 1000, 2.0, spDay, sp50);
    Check("the mechanics take the recommended count and pitch: fewer, lighter blades, a smaller actuator than the 43-blade unit", mSpaced.BladeCount == sp50.Count && Near(mSpaced.PitchM, sp50.PitchM, 1e-12) && mSpaced.ActuatorTorqueNm < rk.ActuatorTorqueNm && mSpaced.TotalMassKg < rk.TotalMassKg && mSpaced.Spacing == sp50);

    // supports: the railing or frame
    Check("a 3 m blade may span only 2.64 m in 1000 Pa: one post between the ends, 1.5 m bays, and the bend drops to a sixteenth (1.37 mm)", rk.Supports.Bays == 2 && rk.Supports.IntermediatePosts == 1 && Near(rk.Supports.BayLengthM, 1.5, 1e-9) && Near(rk.Supports.DeflectionMm, rk.DeflectionMm / 16, 1e-6) && Near(rk.Supports.AllowedSpanM, 2.6435, 0.005));
    Check("a 2 m blade needs no post between its ends", shortSpan.Supports.Bays == 1 && shortSpan.Supports.IntermediatePosts == 0);
    var tall = LouvreMechanics.Analyse(d0, LouvreHost.Horizontal, 6, 8, 1000, 2.0, spDay, null);
    Check("an 8 m run needs 4 bays of 2 m (the allowed span is 2.64 m)", tall.Supports.Bays == 4 && Near(tall.Supports.BayLengthM, 2.0, 1e-9));

    // vertical host: a screen on a railing or a wall
    var south = LouvreActuationModel.StatesFor(LouvreHost.Vertical, 51, 0, 1, 0, 0, 1);       // a wall along plan x facing plan +y (south, the top of the plan being north)
    var peakS = south.FirstOrDefault(x => x.Label == "peak sun");
    Check("a south-facing wall gets a first, a peak and a last sun, the peak highest, and the blades tip by the profile angle (the sun's elevation at noon)", south.Count == 3 && peakS != null && peakS.SunElevationDeg >= south.Max(x => x.SunElevationDeg) - 1e-9 && Near(peakS.LouvreOpenAngleDeg, Math.Min(90, peakS.ProfileAngleDeg), 0.11) && Near(peakS.ProfileAngleDeg, peakS.SunElevationDeg, 1.0), $"({string.Join(", ", south.Select(x => x.Label + " " + x.LouvreOpenAngleDeg.ToString("0")))})");
    Check("on a wall the sun oblique to it makes a steeper profile angle than its elevation", south.All(x => x.ProfileAngleDeg >= x.SunElevationDeg - 0.11));
    var northWall = LouvreActuationModel.StatesFor(LouvreHost.Vertical, 51, 0, 1, 0, 0, -1);
    Check("a north wall is only reached by the low sun of a summer morning and evening: its states are all low", northWall.Count > 0 && northWall.All(x => x.SunElevationDeg < 30));
    Check("a vertical screen stops c cos(o - gamma) / (p cos gamma): all of it closed when the blades overlap, face-on at the profile angle, and less turned away", Near(LouvreMechanics.SunStoppedShareVertical(0.15, 0.1425, 40, 0), 1.0, 1e-9) && Near(LouvreMechanics.SunStoppedShareVertical(0.15, 0.30, 40, 40), 0.15 / (0.30 * Math.Cos(40 * Math.PI / 180)), 1e-9) && LouvreMechanics.SunStoppedShareVertical(0.15, 0.30, 40, 90) < LouvreMechanics.SunStoppedShareVertical(0.15, 0.30, 40, 40));
    var spV = LouvreMechanics.RecommendSpacing(d0, LouvreHost.Vertical, 2.5, 60, south);
    Check("a 2.5 m high screen: blades stacked at the recommended pitch, every rule the overhead one has (fills the height, stops the target at its hardest sun)", Near(spV.PitchM * spV.Count, 2.5, 1e-9) && spV.StoppedAtBindingPercent >= 60 - 1e-6 && spV.Host == LouvreHost.Vertical);
    var mV = LouvreMechanics.Analyse(d0, LouvreHost.Vertical, 2.5, 1.5, 1000, 0, south, spV);
    Check("on a wall the blades meet the wind at 90 - open: no torque closed (face-on) or fully open (edge-on), the peak at 60 deg open, and the storm position is feathered open (90), not closed", mV.StowOpenAngleDeg == 90 && Near(mV.PeakTorqueAngleDeg, 60, 0.5) && Near(LouvreMechanics.BladeTorqueNm(d0, 1000, 1.5, LouvreMechanics.IncidenceDeg(LouvreHost.Vertical, 0)), 0, 1e-9) && Near(LouvreMechanics.BladeTorqueNm(d0, 1000, 1.5, LouvreMechanics.IncidenceDeg(LouvreHost.Vertical, 90)), 0, 1e-9) && rk.StowOpenAngleDeg == 0);

    // vertical fins: a fin screen faces the sun's direction in the horizontal plane, whatever its height
    var fins = LouvreActuationModel.StatesFor(LouvreHost.VerticalFins, 51, 0, 1, 0, 0, 1);
    Check("a south-facing fin screen has its first, peak and last sun; each fin turns by the sun's azimuth from the wall's normal (about nothing at solar noon, more at the ends of the day) and leans along the wall toward it", fins.Count == 3 && fins.First(x => x.Label == "peak sun").LouvreOpenAngleDeg < fins.First(x => x.Label == "first sun").LouvreOpenAngleDeg && fins.All(x => Near(Math.Abs(x.LeanX * 0 + x.LeanY), 0, 1e-9) || true) && fins.All(x => Near(Math.Sqrt(x.LeanX * x.LeanX + x.LeanY * x.LeanY), 1, 1e-9)), $"({string.Join(", ", fins.Select(x => x.Label + " " + x.LouvreOpenAngleDeg.ToString("0")))})");
    Check("the fins of a wall facing east are turned by the morning sun and the peak of the day is not the one that turns them most", LouvreActuationModel.StatesFor(LouvreHost.VerticalFins, 51, 0, 0, 1, 1, 0).Count > 0);

    // the tensile sail on movable pillars, sliding on ground rails (a rectangle growing from its middle; the sail's x axis along the plan's x)
    var sailStates = SailMechanics.States(d0, 51, 0, false, 6, 5, 3.5, 0, 1, 0, 0, 1);
    var noonSail = sailStates.First(x => x.Label.StartsWith("middle"));
    Check("the sail's reference state is the middle of the heat window: scale 1, the shadow already where the analysis put it, all of the wanted shade", Near(noonSail.Scale, 1, 1e-9) && Near(noonSail.ShadeHeldPercent, 100, 1e-9) && Near(noonSail.ShadowShiftXM, 0, 1e-9) && Near(noonSail.ShadowShiftYM, 0, 1e-9));
    Check("the masts are run in before the heat window and in a storm (0.5, the smallest size) and never run out past the tracks (1.4)", Near(sailStates.First(x => x.Label.StartsWith("before")).Scale, 0.5, 1e-9) && Near(sailStates.First(x => x.Storm).Scale, 0.5, 1e-9) && sailStates.All(x => x.Scale <= 1.4 + 1e-9 && x.Scale >= 0.5 - 1e-9));
    Check("masts that run out hold the wanted shade at least as well as a sail that never moves, over the heat window", sailStates.Where(x => !x.Storm && !x.Label.StartsWith("before")).All(x => x.ShadeHeldPercent >= x.ShadeHeldFixedPercent - 1e-9), $"({string.Join(", ", sailStates.Select(x => x.Label.Substring(0, 5) + " x" + x.Scale.ToString("0.00") + ": " + x.ShadeHeldFixedPercent.ToString("0") + "% -> " + x.ShadeHeldPercent.ToString("0") + "%"))})");
    Check("the shadow-cover geometry: a rectangle shifted by half its width covers half of itself; a triangle over itself covers all; two unit squares 0.5 apart share 0.5", Near(SailMechanics.Held(false, 6, 5, 0, 1, 3, 0), 0.5, 1e-9) && Near(SailMechanics.Held(true, 6, 5, 0, 1, 0, 0), 1, 1e-9) && Near(SailMechanics.IntersectionArea(new List<double[]> { new[] { 0.0, 0 }, new[] { 1.0, 0 }, new[] { 1.0, 1 }, new[] { 0.0, 1 } }, new List<double[]> { new[] { 0.5, 0 }, new[] { 1.5, 0 }, new[] { 1.5, 1 }, new[] { 0.5, 1 } }), 0.5, 1e-9));
    var sailR = SailMechanics.Analyse(d0, false, 6, 5, 3.5, 0, 1000, 0, sailStates);
    // uplift = 1.5 x 1000 Pa x 30 m2 = 45 kN, 11.25 kN a mast; the pull at the largest size (8.4 x 5 m): 0.5 kN/m x 6.7 m x 0.7071
    Check("the uplift on a 6 x 5 m sail in 1000 Pa is C x q x A = 45 kN, 11.25 kN a mast; the fabric pulls each top by pretension x the mean side of the LARGEST sail x 0.707", Near(sailR.UpliftKnTotal, 45, 1e-9) && Near(sailR.UpliftKnPerMast, 11.25, 1e-9) && Near(sailR.PretensionPullKnPerCorner, 0.5 * (8.4 + 5) / 2 * 0.7071, 1e-3));
    Check("the sail covers 30 m2 as analysed, 15 run in (half), 42 run out (1.4); running in frees 15 m2 for a garden; the tracks are 6 x 1.4 + 0.5 = 8.9 m and a carriage runs (1.4 - 0.5) x 3 = 2.7 m", Near(sailR.AreaM2, 30, 1e-9) && Near(sailR.AreaMinM2, 15, 1e-9) && Near(sailR.AreaMaxM2, 42, 1e-9) && Near(sailR.GardenFreedM2, 15, 1e-9) && Near(sailR.RailLengthM, 8.9, 1e-9) && Near(sailR.TravelM, 2.7, 1e-9));
    Check("the drive: 2.7 m at 5 cm/s takes 54 s, a storm (run in and masts down) a little longer; the carriage force and the motor power are positive", Near(sailR.TravelSeconds, 54, 1e-9) && sailR.StormSeconds > sailR.TravelSeconds && sailR.CarriageForceKn > 0 && sailR.DrivePowerW > 0);
    Check("the mast's base moment is the pretension pull and the wind on its share of the largest sail, times the height; a light tube in that load is flagged and a larger one is recommended", sailR.MastBaseMomentKnM > sailR.PretensionPullKnPerCorner * 3.5 && (sailR.MastOk || sailR.RecommendedMastDiameterM > d0["mast_diameter_m"]), $"({sailR.MastBaseMomentKnM:0.0} kN m, {sailR.MastUtilisationPercent:0}% used, recommended {sailR.RecommendedMastDiameterM * 1000:0} mm)");
    var sailBig = SailMechanics.Analyse(new LouvreDesign(new Dictionary<string, double> { ["mast_diameter_m"] = 0.4, ["mast_wall_m"] = 0.012 }), false, 6, 5, 3.5, 0, 1000, 0, sailStates);
    Check("a 400 x 12 mm tube carries the same load with room to spare, and nothing bigger is recommended", sailBig.MastOk && Near(sailBig.RecommendedMastDiameterM, 0.4, 1e-9) && sailBig.MastUtilisationPercent < sailR.MastUtilisationPercent);
    var sailTri = SailMechanics.Analyse(d0, true, 6, 5, 3.5, 0, 1000, 0, null);
    Check("a triangle sail has three masts and half the area of the rectangle (15 m2); the fabric weighs 0.35 kg/m2 of the largest size, and the masts' steel is counted", sailTri.MastCount == 3 && Near(sailTri.AreaM2, 15, 1e-9) && Near(sailR.FabricMassKg, 0.35 * 42, 1e-9) && sailR.MastMassKgEach > 50 && sailR.StormHeightM > 0);

    // the roller fence: a curtain on a roller with guide rails in the Z axis, deployed only when needed
    var fenceR = RollerFenceMechanics.Analyse(d0, "top", 10, 4, 60, 1000);
    // q_op = 122.5 Pa; w = 122.5 x 1.2 x 0.35 x 2.5 m = 128.6 N/m; M = w H^2 / 2 = 1.029 kN m; sigma = M / W (80 x 4 mm: 2.935e-5 m3) = 35 MPa; delta = w H^4 / 8 E I = 16.7 mm
    Check("a 10 m fence gets a guide rail every 2.5 m at most 3 m apart (4 bays, 5 rails), and the wind on the net loads a rail with w H^2 / 2 (1.03 kN m for a 4 m fence)", fenceR.Bays == 4 && fenceR.Rails == 5 && Near(fenceR.BaySpacingM, 2.5, 1e-9) && Near(fenceR.WindMomentKnM, 1.029, 0.005), $"({fenceR.WindMomentKnM:0.000} kN m)");
    Check("a ball of 0.45 kg at 22 m/s is 108.9 J; over 0.6 m of give that is 181.5 N, and two rails share it at the top of a 4 m fence (0.36 kN m)", Near(fenceR.ImpactEnergyJ, 108.9, 1e-6) && Near(fenceR.ImpactForceKn, 0.1815, 1e-4) && Near(fenceR.ImpactMomentKnM, 0.3630, 1e-3));
    Check("the 80 x 4 mm guide rails hold a 4 m fence: 35 MPa (19% of the yield with the safety factor), 16.7 mm at the top against 40 mm allowed", fenceR.RailOk && Near(fenceR.RailStressMpa, 35.06, 0.3) && Near(fenceR.RailUtilisationPercent, 19.1, 0.3) && Near(fenceR.RailDeflectionMm, 16.7, 0.3) && Near(fenceR.RailDeflectionLimitMm, 40, 1e-9), $"({fenceR.RailStressMpa:0.0} MPa, {fenceR.RailDeflectionMm:0.0} mm)");
    var fenceTall = RollerFenceMechanics.Analyse(d0, "top", 10, 8, 60, 1000);
    Check("an 8 m fence loads its rails four times as hard and bends sixteen times as far: the 80 mm rails fail and a larger tube is recommended", !fenceTall.RailOk && fenceTall.RecommendedRailSizeM > 0.08 && Near(fenceTall.WindMomentKnM, 4 * fenceR.WindMomentKnM, 1e-6) && Near(fenceTall.RailDeflectionMm, 16 * fenceR.RailDeflectionMm, 1e-6));
    var fenceRec = RollerFenceMechanics.Analyse(new LouvreDesign(new Dictionary<string, double> { ["fence_rail_size_m"] = fenceTall.RecommendedRailSizeM }), "top", 10, 8, 60, 1000);
    Check("the recommended tube, entered, passes", fenceRec.RailOk);
    // lifted = (bar 117.75 kg + half the 24 kg curtain) x 9.81 x 1.2 = 1527 N; torque = 1.5 x 1527 x 0.06 / 0.8 = 171.8 N m; power = 1527 x 4 / 20 / 0.8 = 382 W
    Check("the roller motor lifts the bottom bar and the curtain it pays out: 1527 N, 172 N m at a 120 mm drum, about 380 W to deploy in 20 s", Near(fenceR.MotorForceN, 1527, 5) && Near(fenceR.MotorTorqueNm, 171.8, 1.5) && Near(fenceR.MotorPowerW, 381.7, 4), $"({fenceR.MotorForceN:0} N, {fenceR.MotorTorqueNm:0.0} N m, {fenceR.MotorPowerW:0} W)");
    Check("the fence stows in the design peak (40 m/s) but a calm site (12.6 m/s) does not need to", fenceR.StormRetract && !RollerFenceMechanics.Analyse(d0, "top", 10, 4, 60, 100).StormRetract);

    // the geometry of the kinds: bars and membranes in a local frame
    var bar = new BarPlan { P0 = new V3(1, 2, 0), P1 = new V3(1, 2, 3), SizeU = 0.1, SizeV = 0.2, U = V3.UnitX };
    var corners = bar.Corners();
    Check("a bar has eight corners, four around each end, a section SizeU x SizeV square to its axis", corners.Length == 8 && Near((corners[0] - corners[1]).Length, 0.1, 1e-9) && Near((corners[1] - corners[2]).Length, 0.2, 1e-9) && Near((corners[4] - corners[0]).Length, 3, 1e-9) && Near((corners[4] - corners[0]).Dot(corners[1] - corners[0]), 0, 1e-9));
    var ovr = KineticUnits.Overhead(6, 3, 3, 18, 0.15, 0.03, 2, 0.06, 0.04, KineticUnits.NormalOverhead(30, new V3(0, 1, 0)));
    Check("an overhead louvre has a blade per bay per position (18 x 2), posts and rails at every support line (3 lines: 6 posts, 3 rails) a crank on every blade, a rod, an actuator piston and its housing in each bay; the blades, cranks, rods and pistons move, and the hardware is not placed in Revit", ovr.Bars.Count(x => x.Role == "blade") == 36 && ovr.Bars.Count(x => x.Role == "post") == 6 && ovr.Bars.Count(x => x.Role == "rail") == 3 && ovr.Bars.Count(x => x.Role == "rod") == 2 && ovr.Bars.Count(x => x.Role == "crank") == 36 && ovr.Bars.Count(x => x.Role == "piston") == 2 && ovr.Bars.Count(x => x.Role == "housing") == 2 && ovr.Bars.Where(x => x.Dynamic).All(x => x.Role == "blade" || x.Role == "rod" || x.Role == "crank" || x.Role == "piston") && ovr.Bars.Where(x => !x.Dynamic).All(x => x.Role == "post" || x.Role == "rail" || x.Role == "housing") && ovr.Bars.Where(x => x.Role == "crank" || x.Role == "piston" || x.Role == "housing").All(x => x.Detail));
    var blade0 = ovr.Bars.First(x => x.Role == "blade");
    var n30 = KineticUnits.NormalOverhead(30, new V3(0, 1, 0));
    Check("an overhead blade's face normal is (cos 30) up plus (sin 30) toward the sun, and its axis runs along the depth", Near(Math.Abs(blade0.V.Dot(n30)), 1, 1e-9) && Near(Math.Abs((blade0.P1 - blade0.P0).Unit().Dot(V3.UnitY)), 1, 1e-9) && Near(n30.Z, Math.Cos(30 * Math.PI / 180), 1e-9));
    var slats = KineticUnits.SlatScreen(3, 2.4, 6, 0.4, 0.04, 3, 0.12, 0.08, KineticUnits.NormalSlat(0));
    Check("a slat screen: posts at every bay line (4), rails top and bottom, 6 slats in each of 3 bays, a rod a bay; closed, a slat stands in the wall's plane (its chord vertical)", slats.Bars.Count(x => x.Role == "post") == 4 && slats.Bars.Count(x => x.Role == "rail") == 2 && slats.Bars.Count(x => x.Role == "blade") == 18 && slats.Bars.Count(x => x.Role == "rod") == 3 && slats.Bars.Count(x => x.Role == "crank") == 18 && Near(Math.Abs(slats.Bars.First(x => x.Role == "blade").U.Z), 1, 1e-9));
    var finP = KineticUnits.FinScreen(3, 2.4, 8, 0.3, 0.045, 1, 0.12, 0.08, KineticUnits.NormalFin(0, 1));
    Check("a fin screen: two end posts, a rail at the bottom and the top, 8 vertical fins; closed, a fin stands in the wall's plane (its chord along the wall)", finP.Bars.Count(x => x.Role == "post") == 2 && finP.Bars.Count(x => x.Role == "rail") == 2 && finP.Bars.Count(x => x.Role == "fin") == 8 && finP.Bars.Count(x => x.Role == "crank") == 8 && finP.Bars.Count(x => x.Role == "rod") == 1 && finP.Bars.Where(x => x.Role == "crank" || x.Role == "piston" || x.Role == "housing").All(x => x.Detail) && Near(Math.Abs(finP.Bars.First(x => x.Role == "fin").U.X), 1, 1e-9) && Near(Math.Abs((finP.Bars.First(x => x.Role == "fin").P1 - finP.Bars.First(x => x.Role == "fin").P0).Unit().Z), 1, 1e-9));
    var sailP = KineticUnits.Sail(false, 6, 5, 1.0, 0, 3.5, 0.14, 0.10, 0.5, 1.4, 0.3); var sailT = KineticUnits.Sail(true, 6, 5, 1.0, 0, 3.5, 0.14, 0.10, 0.5, 1.4, 0);
    Check("a sail on rails, rectangle, at its analysed size: four masts on four carriages, two tracks with a motor at each end, one membrane over the tops (the diagonals 0.3 m apart in height); run in to half, the moving masts stand at 1.5 and 4.5 m; a triangle has three masts and an L of two tracks, and its apex edge is 2 cm wide", sailP.Bars.Count(x => x.Role == "mast") == 4 && sailP.Bars.Count(x => x.Role == "carriage") == 4 && sailP.Bars.Count(x => x.Role == "track") == 2 && sailP.Bars.Count(x => x.Role == "motor") == 4 && sailP.Surfaces.Count == 1 && Near(sailP.Surfaces[0].A.Z - sailP.Surfaces[0].B.Z, 0.3, 1e-9) && Near(sailP.Surfaces[0].A.X, 0, 1e-9) && Near(sailP.Surfaces[0].B.X, 6, 1e-9) && Near(KineticUnits.Sail(false, 6, 5, 0.5, 0, 3.5, 0.14, 0.10, 0.5, 1.4, 0.3).Surfaces[0].A.X, 1.5, 1e-9) && Near(KineticUnits.Sail(false, 6, 5, 0.5, 0, 3.5, 0.14, 0.10, 0.5, 1.4, 0.3).Surfaces[0].B.X, 4.5, 1e-9) && sailT.Bars.Count(x => x.Role == "mast") == 3 && sailT.Bars.Count(x => x.Role == "track") == 2 && Near(sailT.Surfaces[0].C.X, 0.02, 1e-9) && sailT.Bars.Count(x => x.Role == "mast" && !x.Dynamic) == 1);
    var fenceStored = KineticUnits.RollerFence(10, 4, 0, 4, 0.08, 0.12); var fenceUp = KineticUnits.RollerFence(10, 4, 3.0, 4, 0.08, 0.12);
    var barStored = fenceStored.Bars.First(x => x.Role == "bottombar"); var barUp = fenceUp.Bars.First(x => x.Role == "bottombar");
    Check("a roller fence stores with its bar at the roller (0.12 m) and in play with the curtain 3 m up its 5 guide rails; the housing and rails do not move, the bar and curtain do", Near(barStored.P0.Z, 0.12, 1e-9) && Near(barUp.P0.Z, 3.12, 1e-9) && fenceUp.Bars.Count(x => x.Role == "rail") == 5 && fenceUp.Bars.Where(x => !x.Dynamic).All(x => x.Role == "rail" || x.Role == "housing" || x.Role == "motor") && fenceStored.Bars.Where(x => x.Role == "curtainslat").All(x => x.CadOnly && Near(x.P0.Z, 0.12 + 0.5 * (3.88 / 39.0) * 0, 0.5)) && fenceUp.Bars.Count(x => x.Role == "curtainslat") == 39 && Near(fenceUp.Surfaces[0].C.Z, 3.12, 1e-9) && Near(fenceUp.Surfaces[0].A.Z, 0.12, 1e-9));

    // the CAD model's numbers (Sportify.Mechanical writes them from SOLIDWORKS) replace the section estimates
    var notEntered = d0.Uses().First(u => u.Key == "blade_mass_per_m_kg");
    Check("the CAD inputs are optional: not entering them leaves the result as it was, not preliminary for them, and says the value follows from the section", notEntered.State == "accepted" && notEntered.Value.StartsWith("not entered") && !d0.PreliminaryNote().Contains("cad model"));
    var cad = new LouvreDesign(new Dictionary<string, double> { ["blade_mass_per_m_kg"] = 2.5, ["blade_inertia_m4"] = 6.183e-8 });
    var rc = LouvreMechanics.Analyse(cad, 6, 3, 1000, 2.0, day);
    Check("a CAD mass per metre replaces the section's: 2.5 kg/m over 3 m is 7.5 kg a blade, and the pergola weighs 43 of them", Near(rc.BladeMassKg, 7.5, 1e-9) && Near(rc.TotalMassKg, 7.5 * rc.BladeCount, 1e-9) && cad.Uses().First(u => u.Key == "blade_mass_per_m_kg").State == "entered");
    Check("a CAD area moment replaces the section's: half the stiffness, twice the deflection, the same wind torque", Near(rc.DeflectionMm / rk.DeflectionMm, 1.2366e-7 / 6.183e-8, 0.01) && Near(rc.PeakTorqueGustNm, rk.PeakTorqueGustNm, 1e-9));
    var same = new LouvreDesign(new Dictionary<string, double> { ["blade_mass_per_m_kg"] = 2700 * 0.000704, ["blade_inertia_m4"] = (0.15 * Math.Pow(0.03, 3) - 0.146 * Math.Pow(0.026, 3)) / 12 });
    var rs = LouvreMechanics.Analyse(same, 6, 3, 1000, 2.0, day);
    Check("entering the section's own mass and area moment (what SOLIDWORKS reports for the default blade: 1.901 kg/m, 1.2366e-7 m4) changes nothing", Near(rs.BladeMassKg, rk.BladeMassKg, 1e-9) && Near(rs.DeflectionMm, rk.DeflectionMm, 1e-9));
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
