using System.Collections.Generic;
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
        [JsonPropertyName("ball_trajectory")] public BallTrajectoryResultDto? BallTrajectory { get; set; }
        [JsonPropertyName("wind_erosion")] public WindErosionResultDto? WindErosion { get; set; }
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
    /// What the Unity ball-trajectory simulation found (Sportify.Simulation).
    /// video_path is the MP4 it recorded, or null if only the analysis ran.
    /// </summary>
    internal class BallTrajectoryResultDto
    {
        [JsonPropertyName("case_study")] public string? CaseStudy { get; set; }
        [JsonPropertyName("shots_simulated")] public int ShotsSimulated { get; set; }
        [JsonPropertyName("crossing_count")] public int CrossingCount { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }

        // Roof-exit sweep: how many extra stray shots were flown, what share of them
        // left the roof, and where fences would stop them. Null/0 if the sweep didn't run.
        [JsonPropertyName("swept_shots")] public int SweptShots { get; set; }
        [JsonPropertyName("percent_leaving_roof")] public double PercentLeavingRoof { get; set; }
        [JsonPropertyName("percent_leaving_after_fences")] public double PercentLeavingAfterFences { get; set; }
        [JsonPropertyName("fences")] public List<RoofFenceDto>? Fences { get; set; }
    }

    /// <summary>
    /// What the wind and erosion screening found (WindAnalysisCore.cs, computed in-process by this
    /// add-in; Unity only adds the video). video_path is the MP4, or null if none was rendered.
    /// </summary>
    internal class WindErosionResultDto
    {
        [JsonPropertyName("case_study")] public string? CaseStudy { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }

        [JsonPropertyName("wind_zone")] public string? WindZone { get; set; }
        [JsonPropertyName("wind_zone_source")] public string? WindZoneSource { get; set; }
        [JsonPropertyName("roof_height_source")] public string? RoofHeightSource { get; set; }
        [JsonPropertyName("north_deg")] public double? NorthDeg { get; set; }
        [JsonPropertyName("basic_wind_speed_ms")] public double BasicWindSpeedMs { get; set; }
        [JsonPropertyName("terrain_category")] public string? TerrainCategory { get; set; }
        [JsonPropertyName("roof_height_m")] public double RoofHeightM { get; set; }
        [JsonPropertyName("roof_height_assumed")] public bool RoofHeightAssumed { get; set; }
        [JsonPropertyName("peak_pressure_pa")] public double PeakPressurePa { get; set; }

        [JsonPropertyName("trees_checked")] public int TreesChecked { get; set; }
        [JsonPropertyName("trees_failing")] public int TreesFailing { get; set; }
        [JsonPropertyName("trees_marginal")] public int TreesMarginal { get; set; }
        [JsonPropertyName("zones_checked")] public int ZonesChecked { get; set; }
        [JsonPropertyName("zones_uplift_flagged")] public int ZonesUpliftFlagged { get; set; }
        [JsonPropertyName("percent_planted_area_uplift_flagged")] public double PercentPlantedAreaUpliftFlagged { get; set; }
        [JsonPropertyName("percent_planted_area_erosion_flagged")] public double PercentPlantedAreaErosionFlagged { get; set; }
        [JsonPropertyName("lowest_bare_onset_ms")] public double LowestBareOnsetMs { get; set; }

        [JsonPropertyName("findings")] public List<WindFindingDto>? Findings { get; set; }

        /// <summary>Every judgement call behind the numbers, so a reader of the report can argue with them.</summary>
        [JsonPropertyName("assumptions")] public List<string>? Assumptions { get; set; }
    }

    /// <summary>One thing to change: ballast, a heavier build-up, anchoring or moving a tree, protecting the substrate.</summary>
    internal class WindFindingDto
    {
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
        [JsonPropertyName("edge")] public string? Edge { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    /// <summary>One proposed roof-edge fence: which edge, the stretch of it (metres along the edge), and how high.</summary>
    internal class RoofFenceDto
    {
        [JsonPropertyName("edge")] public string? Edge { get; set; }
        [JsonPropertyName("from_m")] public double FromM { get; set; }
        [JsonPropertyName("to_m")] public double ToM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("full_height_m")] public double FullHeightM { get; set; }
        [JsonPropertyName("stops_percent_of_exits")] public double StopsPercentOfExits { get; set; }
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
        public static void PublishBallTrajectory(BallTrajectoryResultDto result) => Publish(p => p.BallTrajectory = result);
        public static void PublishWindErosion(WindErosionResultDto result) => Publish(p => p.WindErosion = result);

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
