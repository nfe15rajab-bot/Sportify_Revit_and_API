using System.Text.Json;
using SportfyRevit;

// usage: AnalysisParity <layout.json> <materials.json>   prints the add-in's LCA, fire safety and accessibility for the layout as JSON
//        AnalysisParity --defaults                       prints the add-in's offline analysis parameters and the quality-tier materials
// Tools/AnalysisParity/check.js runs the web app's carbon.js and analysisController.js on the same input and compares.
var json = new JsonSerializerOptions { WriteIndented = true };

if (args.Length == 1 && args[0] == "--defaults")
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        parameters = AnalysisReferenceData.FallbackValues,
        quality_reference_material = EmbodiedCarbon.QualityReferenceMaterial,
    }, json));
    return 0;
}

if (args.Length < 2) { Console.Error.WriteLine("usage: AnalysisParity <layout.json> <materials.json> | --defaults"); return 2; }
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(args[0]));
var materials = JsonSerializer.Deserialize<List<MaterialEntry>>(File.ReadAllText(args[1])) ?? new List<MaterialEntry>();
if (layout == null) { Console.Error.WriteLine("the layout was empty"); return 1; }

var carbon = EmbodiedCarbon.Compute(layout.Placements ?? new List<PlacementDto>(), materials);
var fire = SafetyAnalysis.Fire(layout, AnalysisReferenceData.FallbackValues["Fire Safety"]["max_travel_distance_m"]);
var access = SafetyAnalysis.Access(layout, AnalysisReferenceData.FallbackValues["Accessibility"]["min_circulation_width_m"]);

Console.WriteLine(JsonSerializer.Serialize(new
{
    lca = new { total_kg = carbon.TotalKg, covered = carbon.CoveredCount, missing = carbon.MissingCount, total = carbon.TotalCount },
    fire = new { status = fire.Status, max_dist_m = fire.MaxDistM, max_travel_distance_m = fire.MaxTravelDistanceM, within_limit = fire.WithinLimit, unreachable = fire.UnreachableCount },
    access = new { status = access.Status, width_ok = access.WidthOk, reach_ok = access.ReachOk, current_width_m = access.CurrentWidthM, min_width_m = access.MinWidthM },
}, json));
return 0;
