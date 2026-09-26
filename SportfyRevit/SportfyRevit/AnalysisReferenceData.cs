using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SportfyRevit
{
    internal class AnalysisParameterEntry
    {
        [JsonPropertyName("category")] public string Category { get; set; } = "";
        [JsonPropertyName("key")] public string Key { get; set; } = "";
        [JsonPropertyName("value")] public double Value { get; set; }
    }

    internal class MaterialEntry
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("embodiedCarbonValue")] public double? EmbodiedCarbonValue { get; set; }
    }

    /// <summary>
    /// Same reference figures the web app's Analysis tab reads from
    /// Sportify.Api's AnalysisParameters table (analysisController.js,
    /// getAnalysisParam/ANALYSIS_PARAM_DEFAULTS) — fetched from the same
    /// endpoint here too, so a number can never quietly drift between the
    /// two apps. Falls back to the same hardcoded defaults
    /// analysisController.js uses when it can't reach the API, for the
    /// same reason: these commands must still work if Sportify.Api isn't
    /// running. Fetched once per Revit session and cached — an admin
    /// editing a figure mid-session won't be picked up until Revit
    /// restarts, a deliberate trade rather than round-tripping the API on
    /// every single analysis run.
    /// </summary>
    internal static class AnalysisReferenceData
    {
        private const string BaseUrl = "http://localhost:5107/api";
        private static List<AnalysisParameterEntry>? _cache;

        private static readonly Dictionary<string, Dictionary<string, double>> Defaults = new()
        {
            ["Fire Safety"] = new() { ["max_travel_distance_m"] = 35 },
            ["Accessibility"] = new() { ["min_circulation_width_m"] = 1.5 },
            ["Water Management"] = new()
            {
                ["retention_base_percent"] = 30,
                ["retention_depth_coefficient_percent_per_cm"] = 2,
                ["retention_max_percent"] = 90,
            },
            ["Wind Exposure"] = new() { ["edge_exposure_zone_m"] = 2.0 },
            // Same figures seeded in Sportify.Api's AnalysisParameter table (ReferenceDataSeeder) —
            // SportifyFamilyParameters.ComputeLiveLoad (the live-load figure written onto each placed family) reads these
            // via GetParam, so without a default here it would silently compute 0/0 offline. (The Live Loads command that also read them was
            // retired: the Structural Loads analysis is the reference for loads on the roof.)
            ["Live Loads"] = new() { ["assumed_load_per_person_kg"] = 90, ["reference_capacity_kn_per_m2"] = 4.0 },
            // The Carbon Impact analysis reads these two; with no default here it computed 0 kWh whenever the API was not running.
            ["Carbon Impact"] = new() { ["energy_density_wh_per_m2_per_hour"] = 0.5, ["assumed_daily_usage_hours"] = 4 },
        };

        /// <summary>
        /// The offline fallbacks as data, for Tools/SourceParity: the database (Sportify.Api's ReferenceDataSeeder), the web app (analysisController.js ANALYSIS_PARAM_DEFAULTS)
        /// and this list must hold the same categories, keys and values. The database is the source at run time; these are what is used when it cannot be reached.
        /// </summary>
        public static IReadOnlyDictionary<string, Dictionary<string, double>> FallbackValues => Defaults;

        public static double GetParam(string category, string key)
        {
            var list = _cache ??= FetchParameters();
            var found = list.FirstOrDefault(p => p.Category == category && p.Key == key);
            if (found != null) return found.Value;

            return Defaults.TryGetValue(category, out var byKey) && byKey.TryGetValue(key, out var fallback)
                ? fallback
                : 0;
        }

        private static List<AnalysisParameterEntry> FetchParameters()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = client.GetStringAsync($"{BaseUrl}/AnalysisParameters").GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<List<AnalysisParameterEntry>>(json) ?? new List<AnalysisParameterEntry>();
            }
            catch (Exception)
            {
                // Sportify.Api isn't running, or unreachable — fall back to
                // Defaults above rather than fail the whole analysis.
                return new List<AnalysisParameterEntry>();
            }
        }

        private static List<MaterialEntry>? _materialsCache;

        /// <summary>Same "sports/materials" list the web app's Data tab and LCA card read — no offline fallback here since there's no equivalent hardcoded materials table to fall back to.</summary>
        public static List<MaterialEntry> GetMaterials()
        {
            return _materialsCache ??= FetchMaterials();
        }

        private static List<MaterialEntry> FetchMaterials()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var json = client.GetStringAsync($"{BaseUrl}/sports/materials").GetAwaiter().GetResult();
                return JsonSerializer.Deserialize<List<MaterialEntry>>(json) ?? new List<MaterialEntry>();
            }
            catch (Exception)
            {
                return new List<MaterialEntry>();
            }
        }
    }
}
