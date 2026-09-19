using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Structure;

// usage: DynamicCheck <layout.json> [--json]
// Prints a summary of the dynamic report for the layout (or the whole report as JSON), then runs the checks that must hold whatever the constants.
var layoutPath = args[0];
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(layoutPath))!;
var inputs = DynamicLayoutAdapter.ToInputs(layout);
DynamicRun run;
var report = DynamicModel.Analyse(inputs, out run);
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true };

if (args.Contains("--json"))
{
    Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
    return 0;
}

var c = report.crowd;
var w = report.weather;
var r = report.resonance;
Console.WriteLine($"roof {inputs.Structure.RoofLength:0.#} x {inputs.Structure.RoofWidth:0.#} m, {report.crowd.bays.Count} bays, schedule: {c.scheduleName}");
Console.WriteLine();
Console.WriteLine($"CROWDS  peak {c.peakPersons:0} people ({c.peakCrowdKn:0} kN) at {DynamicModel.Clock(c.peakAtHour)}, mean {c.meanPersons:0}, {c.arrivals} arrivals; static analysis assumed {c.staticExpectedPersons:0} at once");
Console.WriteLine($"        busiest bay {c.busiestBay} at {c.busiestBayPeakDensity:0.00} people/m2; the crowd moves the load's centre by up to {c.maxShiftPercent:0.00}% ({c.maxShiftSide}) at {DynamicModel.Clock(c.maxShiftAtHour)}, on top of the permanent load's own {c.baselineOffsetXPercent:+0.0;-0.0}% / {c.baselineOffsetYPercent:+0.0;-0.0}%");
Console.WriteLine("        hour  " + string.Join(" ", c.samples.Where((_, i) => i % 6 == 0).Select(s => $"{DynamicModel.Clock(s.hour)}")));
Console.WriteLine("        people" + string.Join(" ", c.samples.Where((_, i) => i % 6 == 0).Select(s => $"{s.persons,5:0}")));
Console.WriteLine();
Console.WriteLine($"WEATHER snow zone {w.snow.zone}{(w.snow.zoneAssumed ? " (assumed)" : "")} at {w.snow.altitudeM:0} m{(w.snow.altitudeAssumed ? " (assumed)" : "")}: sk {w.snow.skKnM2:0.00}, on the roof {w.snow.roofKnM2:0.00} kN/m2");
Console.WriteLine($"        rain {w.rain.intensityMmH:0} mm/h x {w.rain.minutes:0} min: build-ups {w.rain.dryKn:0} kN dry, {w.rain.fieldCapacityKn:0} at field capacity, +{w.rain.peakAddedKn:0} kN at minute {w.rain.peakAtMin:0} ({w.rain.peakKn:0} kN), {w.rain.saturatedKn:0} saturated");
Console.WriteLine($"        wind zone {w.wind.zone}, qp {w.wind.peakPressurePa:0} Pa at {w.wind.roofHeightM:0.#} m; worst {w.wind.worstDirection}; edge suction up to {w.wind.maxSuctionKnM2:0.00} kN/m2, mean {w.wind.meanSuctionKnM2:0.00}");
foreach (var lc in w.cases)
    Console.WriteLine($"        {lc.name,-18} {lc.totalKn,7:0} kN  busiest bay {lc.worstBay,-8} {lc.peakUtilisation * 100,4:0}%  over {lc.baysOver}  centre {lc.offsetXPercent:+0.0;-0.0}% / {lc.offsetYPercent:+0.0;-0.0}%");
Console.WriteLine();
Console.WriteLine($"RESONANCE {(r.estimated ? "estimated" : "given")} frequency {r.lowestFrequencyHz:0.0}..{r.highestFrequencyHz:0.0} Hz; worst {r.worstBay}, {r.worstActivity}: {r.worstAccelerationG:0.000} g against {r.worstLimitG:0.00} ({r.worstRatio * 100:0}%), harmonic {r.worstHarmonic} of {r.worstFpHz:0.0} Hz at {r.worstFrequencyHz:0.0} Hz; {r.baysExceeding} bays exceed, {r.baysMarginal} marginal");
foreach (var b in r.bays)
    Console.WriteLine($"        {b.label,-8} {b.spanM,5:0.0} m span  {b.depthM:0.00} m deep  {b.massKgM2,5:0} kg/m2  {b.frequencyHz,5:0.0} Hz   " + string.Join("  ", b.activities.Select(a => $"{a.activity} {a.participants:0}p {a.accelerationBandG:0.000}g {a.status}")));
foreach (var rec in report.recommendations) Console.WriteLine($"  REC [{rec.scenario}/{rec.kind}] {rec.text}");

// ---------------------------------------------------------------- checks (true whatever the constants)
var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }
Console.WriteLine();

// 1. the same layout gives the same day, twice
DynamicRun run2;
var again = DynamicModel.Analyse(DynamicLayoutAdapter.ToInputs(layout), out run2);
Check("deterministic: the same layout gives the identical report twice", JsonSerializer.Serialize(report, jsonOptions) == JsonSerializer.Serialize(again, jsonOptions));

// 2. the crowd simulation's bookkeeping
{
    var sim = new CrowdSim(inputs.Structure, DaySchedule.Get(inputs.Schedule));
    var bad = 0; var worstGap = 0; var maxPersons = 0;
    var targetMaxSum = Enumerable.Range(0, sim.Destinations.Count).Max(d => Enumerable.Range(0, DaySchedule.Hours).Max(h => sim.Target(d, h)));
    var totalTargetMax = Enumerable.Range(0, DaySchedule.Hours).Max(h => Enumerable.Range(0, sim.Destinations.Count).Sum(d => sim.Target(d, h)));
    var steps = (int)(CrowdSim.EndS - CrowdSim.StartS);
    var minutesInHour = 0;
    for (var s = 0; s < steps; s++)
    {
        sim.Step(1.0);
        maxPersons = Math.Max(maxPersons, sim.Agents.Count);
        if (sim.Arrivals - sim.Departures != sim.Agents.Count) bad++;
        if (sim.Agents.Any(a => a.X < -1e-6 || a.X > sim.RoofLength + 1e-6 || a.Y < -1e-6 || a.Y > sim.RoofWidth + 1e-6)) bad++;
        var settledCount = Enumerable.Range(0, sim.Destinations.Count).Sum(d => sim.Assigned(d));
        if (settledCount != sim.Agents.Count(a => a.State != 2)) bad++;
        // half an hour into an hour the crowd has reached what the schedule asks for
        if (s % 3600 == 1800)
        {
            var hi = sim.HourIndex;
            for (var d = 0; d < sim.Destinations.Count; d++) worstGap = Math.Max(worstGap, Math.Abs(sim.Assigned(d) - sim.Target(d, hi)));
            minutesInHour++;
        }
    }
    Check("every second: arrivals - departures = people on the roof, everybody inside the roof, settled + leaving = all", bad == 0, $"({bad} violations)");
    Check("half an hour into every hour each piece holds what the schedule asks", worstGap <= 1, $"(worst gap {worstGap} over {minutesInHour} hours)");
    Check("nobody exceeds the busiest hour's total", maxPersons <= totalTargetMax + 3, $"(max {maxPersons} on the roof, schedule's busiest hour {totalTargetMax})");
    Check("the roof is empty when the day is over", sim.Agents.Count == 0 && sim.Arrivals == sim.Departures, $"({sim.Arrivals} arrivals, {sim.Departures} departures)");
    Check("crowd report agrees with the simulation", Math.Abs(c.arrivals - sim.Arrivals) == 0 && c.peakPersons <= totalTargetMax + 3);
}
Check("bays add up: peak persons of the busiest instant sum to the roof's peak", Math.Abs(run.PeakBayPersons.Sum() - c.peakPersons) < 1e-9, $"({run.PeakBayPersons.Sum():0} vs {c.peakPersons:0})");

// 3. snow: ground loads from DIN EN 1991-1-3/NA
double S(string z, double a) => DynamicModel.SnowGroundLoad(z, a)!.Value;
Check("snow zone 2 below 285 m is the 0.85 minimum", Math.Abs(S("2", 100) - 0.85) < 1e-9 && Math.Abs(S("2", 285) - 0.85) < 0.01);
Check("snow zone 3 at 500 m: 0.31 + 2.91 ((500+140)/760)^2", Math.Abs(S("3", 500) - (0.31 + 2.91 * Math.Pow(640.0 / 760.0, 2))) < 1e-9, $"({S("3", 500):0.000})");
Check("snow zone 1 at 600 m: 0.19 + 0.91 ((600+140)/760)^2; 1a is 1.25 x zone 1", Math.Abs(S("1", 600) - (0.19 + 0.91 * Math.Pow(740.0 / 760.0, 2))) < 1e-9 && Math.Abs(S("1a", 600) - 1.25 * S("1", 600)) < 1e-9);
Check("an unknown snow zone is no zone", DynamicModel.SnowGroundLoad("7", 0) == null);
Check("the roof's snow load is mu1 x sk", Math.Abs(w.snow.roofKnM2 - 0.8 * w.snow.skKnM2) < 1e-5);

// 4. weather load cases hang together
{
    var st = run.Static;
    LoadCase K(string key) => w.cases.First(x => x.key == key);
    var roofArea = inputs.Structure.RoofLength * inputs.Structure.RoofWidth;
    var deadDry = K("busy").totalKn - c.peakCrowdKn;
    var deadField = K("snow").totalKn - w.snow.roofKnM2 * roofArea;
    var deadSat = st.summary.deadKn;
    Check("build-ups: dry <= field capacity <= saturated (the static analysis carries the envelope)", deadDry <= deadField + 1e-3 && deadField <= deadSat + 1e-3, $"({deadDry:0} <= {deadField:0} <= {deadSat:0} kN)");
    Check("the cloudburst lies between field capacity and saturation", w.rain.fieldCapacityKn - 1e-3 <= w.rain.peakKn && w.rain.peakKn <= w.rain.saturatedKn + 1e-3 && w.rain.peakAddedKn >= 0, $"({w.rain.peakKn:0} kN)");
    Check("the rain case is wetter than field capacity and no wetter than saturation", K("rain").totalKn >= deadField - 1e-3 && K("rain").totalKn <= deadSat + 1e-3, $"({K("rain").totalKn:0} kN between {deadField:0} and {deadSat:0})");
    Check("the gale lifts: net load below the dry permanent load", K("gale").totalKn < deadDry, $"({K("gale").totalKn:0} < {deadDry:0} kN)");
    Check("the event case is at least as heavy as snow alone and as the busy hour with the same build-ups", K("event").totalKn >= K("snow").totalKn - 1e-6);
    Check("case utilisation = load per m2 / capacity, for every bay", w.cases.All(lc => Enumerable.Range(0, st.bays.Count).All(b => Math.Abs(lc.bayUtilisation[b] - Math.Max(0, lc.bayKnM2[b]) / st.summary.capacityKnM2) < 1e-4)));
    Check("the governing case of each bay is the highest of the four gravity cases", w.governing.All(g => { var b = st.bays.FindIndex(x => x.label == g.label); return w.cases.Where(lc => lc.key != "gale").Max(lc => lc.bayUtilisation[b]) - g.utilisation < 1e-6; }));
}

// 5. resonance: closed forms
{
    var zeta = DynamicModel.Damping;
    Check("magnification at resonance is 1 / (2 zeta); near zero frequency it is 1", Math.Abs(DynamicModel.Magnification(1.0, zeta) - 1.0 / (2 * zeta)) < 1e-9 && Math.Abs(DynamicModel.Magnification(0.001, zeta) - 1.0) < 1e-4);
    var a13 = DynamicModel.FourierAlphas(1.0 / 3.0, 3);
    Check("jumping (contact ratio 1/3): dynamic load factors 1.8, 1.29, 0.67", Math.Abs(a13[0] - 1.8) < 0.005 && Math.Abs(a13[1] - 1.2857) < 0.005 && Math.Abs(a13[2] - 0.6667) < 0.005, $"({a13[0]:0.000}, {a13[1]:0.000}, {a13[2]:0.000})");
    var a50 = DynamicModel.FourierAlphas(0.5, 4);
    Check("half-wave rectified sine (contact ratio 1/2): pi/2, 2/3, 0, 2/15", Math.Abs(a50[0] - Math.PI / 2) < 1e-6 && Math.Abs(a50[1] - 2.0 / 3.0) < 1e-6 && Math.Abs(a50[2]) < 1e-6 && Math.Abs(a50[3] - 2.0 / 15.0) < 1e-6);
    // the harmonics of a half-sine pulse train, by numerical integration of the pulse itself
    {
        double alphaNumeric(double a, int k)
        {
            const int n = 200000; double re = 0, im = 0;
            var tp = a; var kp = Math.PI / (2 * a);
            for (var i = 0; i < n; i++)
            {
                var t = (i + 0.5) / n;                      // period 1
                var f = t < tp ? kp * Math.Sin(Math.PI * t / tp) : 0;
                re += f * Math.Cos(2 * Math.PI * k * t); im += f * Math.Sin(2 * Math.PI * k * t);
            }
            return 2.0 / n * Math.Sqrt(re * re + im * im);
        }
        var okAll = true;
        foreach (var a in new[] { 0.25, 1.0 / 3.0, 0.4, 0.5 })
        {
            var f = DynamicModel.FourierAlphas(a, 4);
            for (var k = 1; k <= 4; k++) okAll &= Math.Abs(f[k - 1] - alphaNumeric(a, k)) < 2e-3;
        }
        Check("dynamic load factors match a numerical Fourier analysis of the half-sine pulse train (contact ratios 1/4, 1/3, 2/5, 1/2; harmonics 1-4)", okAll);
    }
    var alpha = new[] { 1.0f };
    var atRes = DynamicModel.AccelerationG(alpha, 1000, 1000, 5.0, 5.0, zeta);
    Check("one harmonic at resonance: (4/pi) (q/m) alpha / (2 zeta) / g", Math.Abs(atRes - 4 / Math.PI * 1.0 / (2 * zeta) / DynamicModel.Gravity) < 1e-9, $"({atRes:0.000} g)");
    Check("acceleration is linear in the force, and falls with more damping", Math.Abs(DynamicModel.AccelerationG(alpha, 2000, 1000, 5, 4, zeta) - 2 * DynamicModel.AccelerationG(alpha, 1000, 1000, 5, 4, zeta)) < 1e-12 && DynamicModel.AccelerationG(alpha, 1000, 1000, 5, 5, 0.06) < atRes);
    Check("frequency: a longer span or more mass is lower, a deeper deck higher", DynamicModel.FrequencyHz(10, 0.4, 1500) < DynamicModel.FrequencyHz(8, 0.4, 1500) && DynamicModel.FrequencyHz(8, 0.4, 2000) < DynamicModel.FrequencyHz(8, 0.4, 1500) && DynamicModel.FrequencyHz(8, 0.5, 1500) > DynamicModel.FrequencyHz(8, 0.4, 1500));
    Check("frequency: mass x2 lowers it by sqrt(2)", Math.Abs(DynamicModel.FrequencyHz(8, 0.4, 3000) * Math.Sqrt(2) - DynamicModel.FrequencyHz(8, 0.4, 1500)) < 1e-9);

    // the response peaks where a harmonic meets the deck: sweep the deck frequency for a jumping crowd
    var jump = DynamicModel.Activities.First(x => x.key == "event");
    double best = 0, bestF = 0;
    for (var f = 2.0; f <= 12.0; f += 0.05) { double fp; int k; var g = DynamicModel.WorstAcceleration(jump, 800, 1400, f, out fp, out k); if (g > best) { best = g; bestF = f; } }
    Check("the jumping spectrum peaks at a deck frequency a harmonic can reach (k x 1.5 to 2.8 Hz)", Enumerable.Range(1, 4).Any(k => bestF >= k * jump.fpLowHz - 0.1 && bestF <= k * jump.fpHighHz + 0.1), $"(peak {best:0.00} g at {bestF:0.0} Hz)");
    Check("a stiff deck (above 4 x the top rhythm) hardly moves", DynamicModel.WorstAcceleration(jump, 800, 1400, 4 * jump.fpHighHz + 4, out _, out _) < 0.1 * best);
}

// 6. the engineer's frequency replaces the estimate everywhere; the schedule and snow inputs are read
{
    var given = DynamicLayoutAdapter.ToInputs(layout);
    given.NaturalFrequencyHz = 7.5;
    var other = inputs.Schedule == "event_day" ? "community_day" : "event_day";
    given.SnowZone = "3"; given.AltitudeM = 600; given.Schedule = other;
    var g = DynamicModel.Analyse(given);
    Check("a given natural frequency is used for every bay and is not an estimate", !g.resonance.estimated && g.resonance.bays.All(b => Math.Abs(b.frequencyHz - 7.5) < 1e-6));
    Check("a given snow zone and altitude are used", !g.weather.snow.zoneAssumed && !g.weather.snow.altitudeAssumed && Math.Abs(g.weather.snow.skKnM2 - S("3", 600)) < 1e-4);
    Check("another day schedule gives another day", g.crowd.schedule == other && (g.crowd.peakAtHour != c.peakAtHour || g.crowd.peakPersons != c.peakPersons || g.crowd.meanPersons != c.meanPersons));
}

Console.WriteLine(fails == 0 ? "\nALL CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
