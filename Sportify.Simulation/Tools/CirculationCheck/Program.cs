using System.Text.Json;
using SportfyRevit;

// usage: CirculationCheck <layout.json>
// Prints {"distances": {"<placement id>": metres, ...}, "unreachable": ["<placement id>", ...]} as the add-in's CirculationEngine computes it for the layout.
// Tools/CirculationCheck/oracle.js runs the web app's own rules.js on the same layout and compares.
if (args.Length < 1) { Console.Error.WriteLine("usage: CirculationCheck <layout.json>"); return 2; }
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(args[0]));
if (layout == null) { Console.Error.WriteLine("the layout was empty"); return 1; }

var (distances, unreachable) = CirculationEngine.ComputeTravelDistances(layout);
var report = new
{
    distances = distances.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
    unreachable = unreachable.OrderBy(x => x, StringComparer.Ordinal).ToList(),
};
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
return 0;
