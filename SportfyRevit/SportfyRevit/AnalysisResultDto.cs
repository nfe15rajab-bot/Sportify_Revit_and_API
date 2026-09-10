using System.Text.Json;
using System.Text.Json.Serialization;

namespace SportfyRevit
{
    /// <summary>
    /// What an analysis-result payload looks like once published through
    /// RoofBoundaryServer's GET /analysis-results (mirrors SportifyLayoutDto's
    /// pattern: explicit JsonPropertyName per field, snake_case on the wire).
    /// Sections are nullable because a session only ever has results for
    /// whichever Analyze* commands have actually been run, not all of them
    /// at once — AnalysisResultPublisher merges into whatever was already
    /// published rather than replacing the whole thing on every run.
    /// </summary>
    internal class AnalysisResultPayload
    {
        [JsonPropertyName("fire_safety")] public FireSafetyResultDto? FireSafety { get; set; }
        [JsonPropertyName("accessibility")] public AccessibilityResultDto? Accessibility { get; set; }
        [JsonPropertyName("water_management")] public WaterManagementResultDto? WaterManagement { get; set; }
        [JsonPropertyName("lca")] public LcaResultDto? Lca { get; set; }
        [JsonPropertyName("live_loads")] public LiveLoadsResultDto? LiveLoads { get; set; }
        [JsonPropertyName("carbon_impact")] public CarbonImpactResultDto? CarbonImpact { get; set; }
        [JsonPropertyName("sun_and_shading")] public SunAndShadingResultDto? SunAndShading { get; set; }
    }

    internal class FireSafetyResultDto
    {
        [JsonPropertyName("max_dist_m")] public double MaxDistM { get; set; }
        [JsonPropertyName("max_travel_distance_m")] public double MaxTravelDistanceM { get; set; }
        [JsonPropertyName("within_limit")] public bool WithinLimit { get; set; }
        [JsonPropertyName("unreachable_count")] public int UnreachableCount { get; set; }
    }

    internal class AccessibilityResultDto
    {
        [JsonPropertyName("width_ok")] public bool WidthOk { get; set; }
        [JsonPropertyName("reach_ok")] public bool ReachOk { get; set; }
        [JsonPropertyName("current_width_m")] public double CurrentWidthM { get; set; }
        [JsonPropertyName("min_width_m")] public double MinWidthM { get; set; }
    }

    internal class WaterManagementResultDto
    {
        [JsonPropertyName("total_area_m2")] public double TotalAreaM2 { get; set; }
        [JsonPropertyName("avg_depth_cm")] public double AvgDepthCm { get; set; }
        [JsonPropertyName("retention_percent")] public double RetentionPercent { get; set; }
    }

    internal class LcaResultDto
    {
        [JsonPropertyName("total_kg")] public double TotalKg { get; set; }
        [JsonPropertyName("covered_count")] public int CoveredCount { get; set; }
        [JsonPropertyName("missing_count")] public int MissingCount { get; set; }
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    }

    internal class LiveLoadsResultDto
    {
        [JsonPropertyName("worst_case_kn_per_m2")] public double WorstCaseKnPerM2 { get; set; }
        [JsonPropertyName("reference_kn_per_m2")] public double ReferenceKnPerM2 { get; set; }
        [JsonPropertyName("within_reference")] public bool WithinReference { get; set; }
    }

    internal class CarbonImpactResultDto
    {
        [JsonPropertyName("active_surface_area_m2")] public double ActiveSurfaceAreaM2 { get; set; }
        [JsonPropertyName("estimated_daily_wh")] public double EstimatedDailyWh { get; set; }
    }

    internal class SunAndShadingResultDto
    {
        [JsonPropertyName("location_configured")] public bool LocationConfigured { get; set; }
        [JsonPropertyName("configured_for_date_time")] public string? ConfiguredForDateTime { get; set; }
    }

    /// <summary>
    /// Each Analyze* command only knows its own section, so publishing reads
    /// whatever's already live off RoofBoundaryServer, patches in the one
    /// section that just changed, and republishes the merged whole — a
    /// Water Management run should never blank out an earlier Fire Safety
    /// result the web app hasn't polled yet.
    /// </summary>
    internal static class AnalysisResultPublisher
    {
        public static void PublishFireSafety(FireSafetyResultDto result) => Publish(p => p.FireSafety = result);
        public static void PublishAccessibility(AccessibilityResultDto result) => Publish(p => p.Accessibility = result);
        public static void PublishWaterManagement(WaterManagementResultDto result) => Publish(p => p.WaterManagement = result);
        public static void PublishLca(LcaResultDto result) => Publish(p => p.Lca = result);
        public static void PublishLiveLoads(LiveLoadsResultDto result) => Publish(p => p.LiveLoads = result);
        public static void PublishCarbonImpact(CarbonImpactResultDto result) => Publish(p => p.CarbonImpact = result);
        public static void PublishSunAndShading(SunAndShadingResultDto result) => Publish(p => p.SunAndShading = result);

        private static void Publish(Action<AnalysisResultPayload> apply)
        {
            AnalysisResultPayload? payload = null;
            if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var existingJson) && existingJson != null)
            {
                try { payload = JsonSerializer.Deserialize<AnalysisResultPayload>(existingJson); }
                catch (Exception) { /* malformed previous state — start fresh below */ }
            }
            payload ??= new AnalysisResultPayload();

            apply(payload);

            RoofBoundaryServer.PublishAnalysisResults(JsonSerializer.Serialize(payload));
        }
    }
}
