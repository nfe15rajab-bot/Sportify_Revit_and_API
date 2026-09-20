using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;

// usage: PercolationCheck <layout.json> [--json]
// Prints a summary of the percolation report for the layout (or the whole report as JSON), then runs the physics checks.
var layoutPath = args[0];
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(layoutPath))!;
var windInputs = WindLayoutAdapter.ToInputs(layout);
var inputs = new WaterInputs { RoofLength = windInputs.RoofLength, RoofWidth = windInputs.RoofWidth, RoofAreaM2 = windInputs.Shape.Area, Zones = windInputs.Zones };

if (args.Contains("--json"))
{
    Console.WriteLine(JsonSerializer.Serialize(PercolationModel.Analyse(inputs), new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    return 0;
}

var report = PercolationModel.Analyse(inputs);
Console.WriteLine($"{inputs.Zones.Count} zones, green {report.summary.greenAreaM2:0} m2, other {report.summary.otherAreaM2:0} m2");
foreach (var z in report.zones)
{
    Console.WriteLine($"  {z.label,-12} {z.system,-40} substrate {z.substrateMm,4:0} mm  storage sat {z.storageAtSaturationMm,5:0.0} / field {z.storageAtFieldCapacityMm,5:0.0} mm ({z.storageSource}{(z.publishedStorageMm > 0 ? ", published " + z.publishedStorageMm : "")})");
    foreach (var s in z.scenarios)
        Console.WriteLine($"      {s.scenario,-13} rain {s.rainMm,5:0.0}  runoff {s.runoffMm,5:0.0}  retained {s.retainedPercent,5:0}%  peak {s.peakRunoffMmH,5:0.0} mm/h ({s.peakReductionPercent,4:0}% less, {s.peakDelayMin,4:0.0} min later)  first {s.firstRunoffMin,5:0.0} min  fill {s.maxSubstrateFillPercent,3:0}%{(s.saturated ? "  SATURATED" : "")}{(s.surfaceRunoffMm > 0.05 ? $"  surface {s.surfaceRunoffMm:0.0} mm" : "")}");
}
foreach (var r in report.roof)
    Console.WriteLine($"  ROOF {r.scenario,-13} retained {r.retainedPercent,5:0}%  peak {r.peakFlowLps,6:0.0} l/s vs {r.referencePeakLps,6:0.0} ({r.peakReductionPercent:0}% less, {r.peakDelayMin:0.0} min later)");
foreach (var rec in report.recommendations) Console.WriteLine("  REC " + rec.kind + " | " + rec.text);

// ---------------------------------------------------------------- physics checks (true whatever the constants)
var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }
Console.WriteLine();

// 1. mass balance: rain = runoff + what the column holds, for every zone and every intensity
double worst = 0;
foreach (var zone in inputs.Zones)
{
    var spec = PercolationModel.SpecFor(zone.Assembly);
    foreach (var mmH in new[] { 5.0, 40.0, 108.0, 300.0 })
    {
        var col = new SoilColumn(spec);
        for (var t = 0; t < 3600 * 2; t++) col.Step(1.0, t < 1800 ? mmH / 3600.0 : 0.0);
        worst = Math.Max(worst, Math.Abs(col.MassBalanceErrorMm()));
    }
}
Check("mass balance closes (rain = runoff + stored) for every zone and intensity", worst < 1e-6, $"(worst error {worst:E1} mm)");

// 2. more rain never means a smaller share stays: retention falls (or holds) as the same storm gets harder
var sample = inputs.Zones.FirstOrDefault(z => PercolationModel.SpecFor(z.Assembly).HasSubstrate);
if (sample != null)
{
    var spec = PercolationModel.SpecFor(sample.Assembly);
    double Retained(double mmH, double minutes)
    {
        var col = new SoilColumn(spec);
        var steps = (int)((minutes + 60) * 60);
        for (var t = 0; t < steps; t++) col.Step(1.0, t < minutes * 60 ? mmH / 3600.0 : 0.0);
        return 1.0 - col.RunoffMm / Math.Max(1e-9, col.RainMm);
    }
    Check("a longer storm of the same intensity is retained less", Retained(20, 30) >= Retained(20, 120) - 1e-9);
    Check("a small storm is retained better than a big one", Retained(10, 10) >= Retained(10, 90) - 1e-9);

    // 3. a thicker substrate never retains less
    var thicker = new AssemblyInput { Key = sample.Assembly.Key, System = sample.Assembly.System, Category = sample.Assembly.Category };
    foreach (var l in sample.Assembly.Layers)
        thicker.Layers.Add(new LayerInput { Function = l.Function, Name = l.Name, ThicknessMm = l.Function == "substrate" ? l.ThicknessMm + 100 : l.ThicknessMm });
    var thin = PercolationModel.SpecFor(new AssemblyInput { Key = sample.Assembly.Key, Category = sample.Assembly.Category, Layers = sample.Assembly.Layers });
    var thick = PercolationModel.SpecFor(thicker);
    double Held(ColumnSpec s)
    {
        var col = new SoilColumn(s);
        for (var t = 0; t < 5400; t++) col.Step(1.0, t < 1800 ? 40.0 / 3600.0 : 0.0);
        return col.RunoffMm;
    }
    Check("100 mm more substrate never runs off more", Held(thick) <= Held(thin) + 1e-9, $"(runoff {Held(thin):0.0} -> {Held(thick):0.0} mm)");
}

// 4. the report's roof totals close: retained share consistent with the volumes
foreach (var r in report.roof)
{
    var expected = r.referenceRunoffM3 > 0 ? 100.0 * (1.0 - r.runoffM3 / r.referenceRunoffM3) : 0;
    Check($"roof retention is consistent with its volumes ({r.scenario})", Math.Abs(expected - r.retainedPercent) < 0.01);
}

// 5. a paved zone retains only its film
var paved = new AssemblyInput { Key = "p", Category = "walkway" };
paved.Layers.Add(new LayerInput { Function = "wearing", Name = "Concrete paving slab", ThicknessMm = 40 });
var pr = PercolationModel.Analyse(new WaterInputs { RoofLength = 10, RoofWidth = 10, Zones = { new ZoneInput { Id = "z", Label = "Paved", X = 0, Y = 0, Width = 10, Height = 10, Assembly = paved } } });
Check("a paved zone keeps almost nothing of a heavy shower", pr.zones[0].scenarios[1].retainedPercent < 10, $"({pr.zones[0].scenarios[1].retainedPercent:0.0}%)");

// 6. a thin, light system (Bauder-style: 20 mm substrate) is flagged and given a thicker-substrate figure
var thinAssembly = new AssemblyInput { Key = "thin", System = "Bauder Lightweight Sedum", Category = "extensive" };
thinAssembly.Layers.Add(new LayerInput { Function = "vegetation", Name = "Mature sedum blanket", ThicknessMm = 25 });
thinAssembly.Layers.Add(new LayerInput { Function = "substrate", Name = "Extensive substrate", ThicknessMm = 20 });
thinAssembly.Layers.Add(new LayerInput { Function = "drainage", Name = "Water retention and filter layer", ThicknessMm = 20 });
thinAssembly.Layers.Add(new LayerInput { Function = "waterproofing", Name = "Root-resistant waterproofing", ThicknessMm = 4 });
var tr = PercolationModel.Analyse(new WaterInputs { RoofLength = 10, RoofWidth = 10, Zones = { new ZoneInput { Id = "t", Label = "Thin", X = 0, Y = 0, Width = 10, Height = 10, Assembly = thinAssembly } } });
Console.WriteLine($"   thin system: heavy shower retained {tr.zones[0].scenarios[1].retainedPercent:0}%, extra substrate {tr.zones[0].extraSubstrateForTargetMm:0} mm / extra drainage {tr.zones[0].extraDrainageForTargetMm:0} mm for the target");
foreach (var rec in tr.recommendations) Console.WriteLine("   REC " + rec.kind + " | " + rec.text);
Check("a thin system is flagged below the target", tr.summary.zonesBelowTarget == 1);
Check("and is told something that reaches it (more drainage layer or more substrate)", tr.zones[0].extraDrainageForTargetMm > 0 || tr.zones[0].extraSubstrateForTargetMm > 0,
      $"(drainage +{tr.zones[0].extraDrainageForTargetMm:0} mm, substrate +{tr.zones[0].extraSubstrateForTargetMm:0} mm)");

Console.WriteLine(fails == 0 ? "\nALL PERCOLATION CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
