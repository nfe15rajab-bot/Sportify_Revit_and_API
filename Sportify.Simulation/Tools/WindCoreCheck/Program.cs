using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Wind;

var path = args[0];
var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(path))!;
var inputs = WindLayoutAdapter.ToInputs(layout);
if (args.Length > 1 && args[1] == "--inputs")
{
    Console.WriteLine(JsonSerializer.Serialize(inputs, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    return;
}
var report = WindModel.Analyse(inputs);
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
