using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// The mechanical engineer's lever: %AppData%\Autodesk\Revit\Addins\2025\SportifyKineticsInputs.json. Its "overrides" object holds the values
    /// they enter by key (a blade section and mass from SolidWorks, a friction torque from a bench test, the real crank arm), each a positive number;
    /// everything not in it keeps the built-in value (LouvreDesign.Inputs), and the results say which is which. The file is written once with the
    /// full list of keys, units and built-in values under "defaults_for_reference" and an empty "overrides", so there is something to find and edit.
    /// Read on every Kinetics run: no restart, no rebuild.
    /// </summary>
    internal static class KineticsInputsFile
    {
        internal static string Path => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", "2025", "SportifyKineticsInputs.json");

        /// <summary>The mechanical engineer's overrides (empty when there are none, or the file cannot be read: the built-in values then stand, and the log says why).</summary>
        internal static Dictionary<string, double> LoadOverrides()
        {
            var overrides = new Dictionary<string, double>();
            try
            {
                if (!File.Exists(Path)) { WriteTemplate(); return overrides; }
                using var doc = JsonDocument.Parse(File.ReadAllText(Path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("overrides", out var o) && o.ValueKind == JsonValueKind.Object)
                    foreach (var p in o.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v)) overrides[p.Name] = v;
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("kinetics", "SportifyKineticsInputs.json could not be read; the built-in values stand: " + ex.Message);
            }
            return overrides;
        }

        internal static LouvreDesign LoadDesign() => new LouvreDesign(LoadOverrides());

        private static void WriteTemplate()
        {
            try
            {
                var defaults = new Dictionary<string, object>();
                foreach (var d in LouvreDesign.Inputs)
                    defaults[d.Key] = new { label = d.Label, unit = d.Unit, value = d.Default, status = d.Status, reference = d.Reference };
                var template = new Dictionary<string, object>
                {
                    ["_readme"] = "Put your own values in \"overrides\", by key, as positive numbers (e.g. { \"chord_m\": 0.2, \"friction_torque_nm\": 0.45 }). Anything not there keeps the built-in value listed under defaults_for_reference. Kinetics reads this file on every run.",
                    ["overrides"] = new Dictionary<string, double>(),
                    ["defaults_for_reference"] = defaults,
                };
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("kinetics", "the template SportifyKineticsInputs.json could not be written: " + ex.Message);
            }
        }
    }
}
