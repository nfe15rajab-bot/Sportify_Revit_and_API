using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sportify.Simulation.Structure;

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
        [JsonPropertyName("lca")] public LcaResultDto? Lca { get; set; }
        [JsonPropertyName("carbon_impact")] public CarbonImpactResultDto? CarbonImpact { get; set; }
        [JsonPropertyName("sun_and_shading")] public SunAndShadingResultDto? SunAndShading { get; set; }
        [JsonPropertyName("ball_trajectory")] public BallTrajectoryResultDto? BallTrajectory { get; set; }
        [JsonPropertyName("wind_erosion")] public WindErosionResultDto? WindErosion { get; set; }
        [JsonPropertyName("soil_percolation")] public SoilPercolationResultDto? SoilPercolation { get; set; }
        [JsonPropertyName("structural_loads")] public StructuralLoadsResultDto? StructuralLoads { get; set; }
        [JsonPropertyName("dynamic_analysis")] public DynamicAnalysisResultDto? DynamicAnalysis { get; set; }
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

    internal class LcaResultDto
    {
        [JsonPropertyName("total_kg")] public double TotalKg { get; set; }
        [JsonPropertyName("covered_count")] public int CoveredCount { get; set; }
        [JsonPropertyName("missing_count")] public int MissingCount { get; set; }
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
    }

    internal class CarbonImpactResultDto
    {
        [JsonPropertyName("active_surface_area_m2")] public double ActiveSurfaceAreaM2 { get; set; }
        [JsonPropertyName("estimated_daily_wh")] public double EstimatedDailyWh { get; set; }
    }

    /// <summary>
    /// What the sun and shade analysis found (SunShadeCore.cs, computed in-process by this add-in; Unity only adds the video): the hours of direct
    /// sun on the three design days, which people zones are too sunny and which gardens too shaded, and the shading equipment that would fix it
    /// without taking the sun from the gardens, with its weight, wind and the deck's answer.
    /// </summary>
    internal class SunAndShadingResultDto
    {
        [JsonPropertyName("case_study")] public string? CaseStudy { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
        [JsonPropertyName("preliminary")] public bool Preliminary { get; set; }
        [JsonPropertyName("preliminary_note")] public string? PreliminaryNote { get; set; }
        [JsonPropertyName("inputs")] public List<AssumptionUseDto>? Inputs { get; set; }

        [JsonPropertyName("latitude_deg")] public double LatitudeDeg { get; set; }
        [JsonPropertyName("latitude_assumed")] public bool LatitudeAssumed { get; set; }
        [JsonPropertyName("north_deg")] public double NorthDeg { get; set; }
        [JsonPropertyName("north_assumed")] public bool NorthAssumed { get; set; }
        [JsonPropertyName("shade_target_percent")] public double ShadeTargetPercent { get; set; }
        [JsonPropertyName("garden_min_sun_hours")] public double GardenMinSunHours { get; set; }

        [JsonPropertyName("people_zones")] public int PeopleZones { get; set; }
        [JsonPropertyName("people_zones_too_sunny")] public int PeopleZonesTooSunny { get; set; }
        [JsonPropertyName("people_zones_too_sunny_after")] public int PeopleZonesTooSunnyAfter { get; set; }
        [JsonPropertyName("garden_zones")] public int GardenZones { get; set; }
        [JsonPropertyName("garden_zones_too_shaded")] public int GardenZonesTooShaded { get; set; }
        [JsonPropertyName("garden_zones_too_shaded_after")] public int GardenZonesTooShadedAfter { get; set; }
        [JsonPropertyName("pieces")] public int Pieces { get; set; }
        [JsonPropertyName("added_load_kn")] public double AddedLoadKn { get; set; }
        [JsonPropertyName("peak_wind_pressure_pa")] public double PeakWindPressurePa { get; set; }

        [JsonPropertyName("days")] public List<SunDayDto>? Days { get; set; }
        [JsonPropertyName("zones")] public List<SunZoneDto>? Zones { get; set; }
        [JsonPropertyName("equipment")] public List<SunEquipmentDto>? Equipment { get; set; }
        [JsonPropertyName("deck_added_kn")] public double DeckAddedKn { get; set; }
        [JsonPropertyName("deck_peak_utilisation_before_percent")] public double DeckPeakUtilisationBeforePercent { get; set; }
        [JsonPropertyName("deck_peak_utilisation_after_percent")] public double DeckPeakUtilisationAfterPercent { get; set; }
        [JsonPropertyName("deck_bays_over_before")] public int DeckBaysOverBefore { get; set; }
        [JsonPropertyName("deck_bays_over_after")] public int DeckBaysOverAfter { get; set; }
        [JsonPropertyName("findings")] public List<WindFindingDto>? Findings { get; set; }
        [JsonPropertyName("assumptions")] public List<string>? Assumptions { get; set; }
    }

    internal class SunDayDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("sunrise_h")] public double SunriseH { get; set; }
        [JsonPropertyName("sunset_h")] public double SunsetH { get; set; }
        [JsonPropertyName("noon_elevation_deg")] public double NoonElevationDeg { get; set; }
        [JsonPropertyName("roof_mean_sun_hours")] public double RoofMeanSunHours { get; set; }
        [JsonPropertyName("roof_mean_sun_hours_after")] public double RoofMeanSunHoursAfter { get; set; }
    }

    internal class SunZoneDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        /// <summary>"people" | "spectators" | "court" | "garden".</summary>
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("sun_hours_june")] public double SunHoursJune { get; set; }
        [JsonPropertyName("sun_hours_march")] public double SunHoursMarch { get; set; }
        [JsonPropertyName("sun_hours_december")] public double SunHoursDecember { get; set; }
        [JsonPropertyName("peak_shade_percent")] public double PeakShadePercent { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("after_sun_hours_june")] public double AfterSunHoursJune { get; set; }
        [JsonPropertyName("after_peak_shade_percent")] public double AfterPeakShadePercent { get; set; }
        [JsonPropertyName("after_status")] public string? AfterStatus { get; set; }
    }

    internal class SunEquipmentDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("depth_m")] public double DepthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("zone")] public string? Zone { get; set; }
        [JsonPropertyName("shade_before_percent")] public double ShadeBeforePercent { get; set; }
        [JsonPropertyName("shade_after_percent")] public double ShadeAfterPercent { get; set; }
        [JsonPropertyName("added_load_kn")] public double AddedLoadKn { get; set; }
        [JsonPropertyName("wind_uplift_kn")] public double WindUpliftKn { get; set; }
        [JsonPropertyName("note")] public string? Note { get; set; }
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

    /// <summary>
    /// What the rain and percolation screening found (PercolationCore.cs, computed in-process by this add-in). Retention
    /// is the share of the rain kept, against the same rain on a bare roof, for three generic events: steady rain
    /// (10 mm/h), a heavy shower (40 mm/h) and a cloudburst (108 mm/h).
    /// </summary>
    internal class SoilPercolationResultDto
    {
        [JsonPropertyName("case_study")] public string? CaseStudy { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
        [JsonPropertyName("zones_checked")] public int ZonesChecked { get; set; }
        [JsonPropertyName("green_area_m2")] public double GreenAreaM2 { get; set; }
        [JsonPropertyName("steady_retained_percent")] public double SteadyRetainedPercent { get; set; }
        [JsonPropertyName("heavy_shower_retained_percent")] public double HeavyShowerRetainedPercent { get; set; }
        [JsonPropertyName("heavy_shower_peak_reduction_percent")] public double HeavyShowerPeakReductionPercent { get; set; }
        [JsonPropertyName("cloudburst_retained_percent")] public double CloudburstRetainedPercent { get; set; }
        [JsonPropertyName("cloudburst_peak_reduction_percent")] public double CloudburstPeakReductionPercent { get; set; }
        [JsonPropertyName("cloudburst_peak_lps")] public double CloudburstPeakLps { get; set; }
        [JsonPropertyName("cloudburst_reference_peak_lps")] public double CloudburstReferencePeakLps { get; set; }
        [JsonPropertyName("zones_below_target")] public int ZonesBelowTarget { get; set; }
        [JsonPropertyName("zones_saturated_in_cloudburst")] public int ZonesSaturatedInCloudburst { get; set; }
        [JsonPropertyName("zones")] public List<SoilPercolationZoneDto>? Zones { get; set; }
        [JsonPropertyName("findings")] public List<WindFindingDto>? Findings { get; set; }
        [JsonPropertyName("assumptions")] public List<string>? Assumptions { get; set; }
    }

    internal class SoilPercolationZoneDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("system")] public string? System { get; set; }
        [JsonPropertyName("substrate_mm")] public double SubstrateMm { get; set; }
        [JsonPropertyName("retained_steady_percent")] public double RetainedSteadyPercent { get; set; }
        [JsonPropertyName("retained_heavy_shower_percent")] public double RetainedHeavyShowerPercent { get; set; }
        [JsonPropertyName("retained_cloudburst_percent")] public double RetainedCloudburstPercent { get; set; }
    }

    /// <summary>
    /// What the static structural load screening found (StructuralLoadCore.cs, computed in-process by this add-in): where the weight and
    /// the people are, which bays of the structural grid are most loaded against the deck capacity, and whether the load sits to one side.
    /// </summary>
    internal class StructuralLoadsResultDto
    {
        [JsonPropertyName("case_study")] public string? CaseStudy { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
        [JsonPropertyName("grid_source")] public string? GridSource { get; set; }
        [JsonPropertyName("grid_assumed")] public bool GridAssumed { get; set; }
        [JsonPropertyName("deck_capacity_kn_m2")] public double DeckCapacityKnM2 { get; set; }
        [JsonPropertyName("deck_capacity_assumed")] public bool DeckCapacityAssumed { get; set; }

        /// <summary>True while an input is still a built-in value the designer neither entered nor accepted: the result is then not a verdict.</summary>
        [JsonPropertyName("preliminary")] public bool Preliminary { get; set; }
        [JsonPropertyName("preliminary_note")] public string? PreliminaryNote { get; set; }

        /// <summary>The inputs behind the numbers and whether the designer entered each, accepted the built-in value, or did neither.</summary>
        [JsonPropertyName("inputs")] public List<AssumptionUseDto>? Inputs { get; set; }
        [JsonPropertyName("roof_area_m2")] public double RoofAreaM2 { get; set; }
        [JsonPropertyName("permanent_load_kn")] public double PermanentLoadKn { get; set; }
        [JsonPropertyName("imposed_load_kn")] public double ImposedLoadKn { get; set; }
        [JsonPropertyName("mean_load_kn_m2")] public double MeanLoadKnM2 { get; set; }
        [JsonPropertyName("peak_bay_load_kn_m2")] public double PeakBayLoadKnM2 { get; set; }
        [JsonPropertyName("peak_utilisation_percent")] public double PeakUtilisationPercent { get; set; }
        [JsonPropertyName("worst_bay")] public string? WorstBay { get; set; }
        [JsonPropertyName("bays_checked")] public int BaysChecked { get; set; }
        [JsonPropertyName("bays_over_capacity")] public int BaysOverCapacity { get; set; }
        [JsonPropertyName("bays_marginal")] public int BaysMarginal { get; set; }
        [JsonPropertyName("columns_checked")] public int ColumnsChecked { get; set; }
        [JsonPropertyName("columns_high")] public int ColumnsHigh { get; set; }
        [JsonPropertyName("expected_persons")] public double ExpectedPersons { get; set; }
        [JsonPropertyName("balance_status")] public string? BalanceStatus { get; set; }
        [JsonPropertyName("heavy_side")] public string? HeavySide { get; set; }
        [JsonPropertyName("load_centre_offset_x_percent")] public double LoadCentreOffsetXPercent { get; set; }
        [JsonPropertyName("load_centre_offset_y_percent")] public double LoadCentreOffsetYPercent { get; set; }
        [JsonPropertyName("bays")] public List<StructuralBayDto>? Bays { get; set; }
        [JsonPropertyName("findings")] public List<WindFindingDto>? Findings { get; set; }
        [JsonPropertyName("assumptions")] public List<string>? Assumptions { get; set; }
    }

    /// <summary>
    /// What the dynamic structural analysis found (DynamicLoadCore.cs, computed in-process by this add-in): the crowd through the day,
    /// the weather load cases, and the resonance of the deck under rhythmic crowd movement.
    /// </summary>
    internal class DynamicAnalysisResultDto
    {
        [JsonPropertyName("case_study")] public string? CaseStudy { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
        [JsonPropertyName("preliminary")] public bool Preliminary { get; set; }
        [JsonPropertyName("preliminary_note")] public string? PreliminaryNote { get; set; }
        [JsonPropertyName("inputs")] public List<AssumptionUseDto>? Inputs { get; set; }

        [JsonPropertyName("schedule")] public string? Schedule { get; set; }
        [JsonPropertyName("peak_persons")] public double PeakPersons { get; set; }
        [JsonPropertyName("peak_at_hour")] public double PeakAtHour { get; set; }
        [JsonPropertyName("peak_crowd_kn")] public double PeakCrowdKn { get; set; }
        [JsonPropertyName("crowd_share_of_load_percent")] public double CrowdShareOfLoadPercent { get; set; }
        [JsonPropertyName("max_load_centre_shift_percent")] public double MaxLoadCentreShiftPercent { get; set; }
        [JsonPropertyName("busiest_bay")] public string? BusiestBay { get; set; }
        [JsonPropertyName("busiest_bay_peak_density")] public double BusiestBayPeakDensity { get; set; }

        [JsonPropertyName("snow_zone")] public string? SnowZone { get; set; }
        [JsonPropertyName("snow_assumed")] public bool SnowAssumed { get; set; }
        [JsonPropertyName("snow_sk_kn_m2")] public double SnowSkKnM2 { get; set; }
        [JsonPropertyName("rain_peak_added_kn")] public double RainPeakAddedKn { get; set; }
        [JsonPropertyName("rain_saturated_kn")] public double RainSaturatedKn { get; set; }
        [JsonPropertyName("worst_case")] public string? WorstCase { get; set; }
        [JsonPropertyName("worst_case_bay")] public string? WorstCaseBay { get; set; }
        [JsonPropertyName("worst_case_utilisation_percent")] public double WorstCaseUtilisationPercent { get; set; }
        [JsonPropertyName("deck_capacity_kn_m2")] public double DeckCapacityKnM2 { get; set; }
        [JsonPropertyName("cases")] public List<DynamicCaseDto>? Cases { get; set; }

        [JsonPropertyName("frequency_estimated")] public bool FrequencyEstimated { get; set; }

        /// <summary>The deck's depth in the estimate is the slab's structural thickness from the Revit model (not the span/25 rule); the thickness in mm when so.</summary>
        [JsonPropertyName("slab_depth_given")] public bool SlabDepthGiven { get; set; }
        [JsonPropertyName("slab_depth_mm")] public double SlabDepthMm { get; set; }
        [JsonPropertyName("lowest_frequency_hz")] public double LowestFrequencyHz { get; set; }
        [JsonPropertyName("highest_frequency_hz")] public double HighestFrequencyHz { get; set; }
        [JsonPropertyName("worst_resonance_bay")] public string? WorstResonanceBay { get; set; }
        [JsonPropertyName("worst_resonance_activity")] public string? WorstResonanceActivity { get; set; }
        [JsonPropertyName("worst_acceleration_g")] public double WorstAccelerationG { get; set; }
        [JsonPropertyName("worst_limit_g")] public double WorstLimitG { get; set; }
        [JsonPropertyName("bays_exceeding_comfort")] public int BaysExceedingComfort { get; set; }
        [JsonPropertyName("bays_checked")] public int BaysChecked { get; set; }

        [JsonPropertyName("findings")] public List<WindFindingDto>? Findings { get; set; }
        [JsonPropertyName("assumptions")] public List<string>? Assumptions { get; set; }
    }

    internal class DynamicCaseDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("total_kn")] public double TotalKn { get; set; }
        [JsonPropertyName("peak_utilisation_percent")] public double PeakUtilisationPercent { get; set; }
        [JsonPropertyName("worst_bay")] public string? WorstBay { get; set; }
        [JsonPropertyName("bays_over_capacity")] public int BaysOverCapacity { get; set; }
    }

    internal class StructuralBayDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("grid_names")] public string? GridNames { get; set; }

        /// <summary>The bay's rectangle in the roof's plan coordinates (x right, y down, metres), so the web app can draw the plan.</summary>
        [JsonPropertyName("x0_m")] public double X0M { get; set; }
        [JsonPropertyName("x1_m")] public double X1M { get; set; }
        [JsonPropertyName("y0_m")] public double Y0M { get; set; }
        [JsonPropertyName("y1_m")] public double Y1M { get; set; }

        /// <summary>The bay's own outline (x, y pairs in the plan) when it is not that rectangle: a skewed grid, a roof with an outline. Absent for a plain bay.</summary>
        [JsonPropertyName("polygon_m")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<PointDto>? PolygonM { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("load_kn_m2")] public double LoadKnM2 { get; set; }
        [JsonPropertyName("utilisation_percent")] public double UtilisationPercent { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("persons")] public double Persons { get; set; }
    }

    /// <summary>One input of a structural analysis: its value, and whether the designer entered it, accepted the built-in one, or neither.</summary>
    internal class AssumptionUseDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
        /// <summary>"entered" | "accepted" | "unconfirmed".</summary>
        [JsonPropertyName("state")] public string? State { get; set; }
        /// <summary>Where the built-in value comes from: "standard" | "literature" | "assumed" | "placeholder" | "estimated".</summary>
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }

        public static List<AssumptionUseDto> From(IEnumerable<AssumptionUse> uses)
        {
            return uses.Select(u => new AssumptionUseDto { Key = u.key, Label = u.label, Value = u.value, State = u.state, Status = u.status, Reference = u.reference }).ToList();
        }
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
    /// Soil Percolation run should never blank out an earlier Fire Safety
    /// result the web app hasn't polled yet.
    /// </summary>
    internal static class AnalysisResultPublisher
    {
        public static void PublishFireSafety(FireSafetyResultDto result) => Publish(p => p.FireSafety = result);
        public static void PublishAccessibility(AccessibilityResultDto result) => Publish(p => p.Accessibility = result);
        public static void PublishLca(LcaResultDto result) => Publish(p => p.Lca = result);
        public static void PublishCarbonImpact(CarbonImpactResultDto result) => Publish(p => p.CarbonImpact = result);
        public static void PublishSunAndShading(SunAndShadingResultDto result) => Publish(p => p.SunAndShading = result);
        public static void PublishBallTrajectory(BallTrajectoryResultDto result) => Publish(p => p.BallTrajectory = result);
        public static void PublishWindErosion(WindErosionResultDto result) => Publish(p => p.WindErosion = result);
        public static void PublishSoilPercolation(SoilPercolationResultDto result) => Publish(p => p.SoilPercolation = result);
        public static void PublishStructuralLoads(StructuralLoadsResultDto result) => Publish(p => p.StructuralLoads = result);
        public static void PublishDynamicAnalysis(DynamicAnalysisResultDto result) => Publish(p => p.DynamicAnalysis = result);

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
