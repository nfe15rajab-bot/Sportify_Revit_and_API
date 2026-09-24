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
        [JsonPropertyName("kinetics")] public KineticsResultDto? Kinetics { get; set; }

        // What the document knows about itself (not analyses: results-keys-parity.js leaves these three out).
        /// <summary>The layout the newest section was computed for.</summary>
        [JsonPropertyName("layout_id")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? LayoutId { get; set; }
        [JsonPropertyName("updated_at")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? UpdatedAtUtc { get; set; }
        /// <summary>Per section: the layout it was computed for and when. A section without an entry is of unknown origin.</summary>
        [JsonPropertyName("sections")] public Dictionary<string, ResultSectionInfoDto> Sections { get; set; } = new();

        /// <summary>The section keys, as they are on the wire.</summary>
        internal static readonly string[] SectionKeys =
        {
            "fire_safety", "accessibility", "lca", "carbon_impact", "sun_and_shading", "ball_trajectory", "wind_erosion", "soil_percolation", "structural_loads", "dynamic_analysis", "kinetics",
        };

        internal bool Has(string key) => key switch
        {
            "fire_safety" => FireSafety != null, "accessibility" => Accessibility != null, "lca" => Lca != null, "carbon_impact" => CarbonImpact != null,
            "sun_and_shading" => SunAndShading != null, "ball_trajectory" => BallTrajectory != null, "wind_erosion" => WindErosion != null,
            "soil_percolation" => SoilPercolation != null, "structural_loads" => StructuralLoads != null, "dynamic_analysis" => DynamicAnalysis != null,
            "kinetics" => Kinetics != null,
            _ => false,
        };

        internal void Clear(string key)
        {
            switch (key)
            {
                case "fire_safety": FireSafety = null; break;
                case "accessibility": Accessibility = null; break;
                case "lca": Lca = null; break;
                case "carbon_impact": CarbonImpact = null; break;
                case "sun_and_shading": SunAndShading = null; break;
                case "ball_trajectory": BallTrajectory = null; break;
                case "wind_erosion": WindErosion = null; break;
                case "soil_percolation": SoilPercolation = null; break;
                case "structural_loads": StructuralLoads = null; break;
                case "dynamic_analysis": DynamicAnalysis = null; break;
                case "kinetics": Kinetics = null; break;
            }
            Sections.Remove(key);
        }

        /// <summary>
        /// Removes every section that was NOT computed for <paramref name="layoutId"/>. A layout whose analyses no longer apply (its garden was removed)
        /// used to leave the old wind and rain results in the document next to the new ones, with nothing to say they were about another layout.
        /// Returns the keys that were dropped. Unknown layout (null): nothing can be judged, nothing is dropped.
        /// </summary>
        internal List<string> DropSectionsNotFor(string? layoutId)
        {
            var dropped = new List<string>();
            if (layoutId == null) return dropped;
            foreach (var key in SectionKeys)
            {
                if (!Has(key)) continue;
                if (Sections.TryGetValue(key, out var info) && info.LayoutId == layoutId) continue;
                Clear(key);
                dropped.Add(key);
            }
            return dropped;
        }
    }

    /// <summary>Which layout a published section was computed for, and when (UTC, ISO 8601).</summary>
    internal class ResultSectionInfoDto
    {
        [JsonPropertyName("layout_id")] public string? LayoutId { get; set; }
        [JsonPropertyName("computed_at")] public string? ComputedAtUtc { get; set; }
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
    /// Kinetics (Post Analysis): a dynamic family placed and actuated from another analysis's own numbers -- v1 is the
    /// louvre pergola the sun &amp; shade analysis already recommends (LouvreActuationModel.cs), driven by the sun's own
    /// elevation through the day. "priority" names which analysis is currently driving Kinetics (sun / wind_erosion /
    /// structural); only sun has a family behind it so far, so a piece list is only ever populated for that priority.
    /// </summary>
    internal class KineticsResultDto
    {
        [JsonPropertyName("priority")] public string? Priority { get; set; }
        [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
        [JsonPropertyName("preliminary")] public bool Preliminary { get; set; }
        [JsonPropertyName("preliminary_note")] public string? PreliminaryNote { get; set; }
        [JsonPropertyName("pieces")] public List<KineticPieceDto>? Pieces { get; set; }
        /// <summary>The mechanical inputs behind every number of the pieces (LouvreDesign.Inputs), and whether the mechanical engineer entered each in SportifyKineticsInputs.json or the built-in value stands.</summary>
        [JsonPropertyName("mechanical_inputs")] public List<AssumptionUseDto>? MechanicalInputs { get; set; }
        /// <summary>The SOLIDWORKS motion study recorded behind the scenes (Simulate command): the dynamic family working as a mechanism, not as a picture.</summary>
        [JsonPropertyName("simulation_video_path")] public string? SimulationVideoPath { get; set; }
        /// <summary>What the last placement did with phases and worksets: where the dynamic furniture went, and what the project could not offer.</summary>
        [JsonPropertyName("placement_notes")] public List<string>? PlacementNotes { get; set; }
    }

    /// <summary>One placed/adapted dynamic-family instance: which equipment it answers, how it was sourced, and the states it was driven through.</summary>
    internal class KineticPieceDto
    {
        [JsonPropertyName("equipment_key")] public string? EquipmentKey { get; set; }
        [JsonPropertyName("equipment_name")] public string? EquipmentName { get; set; }
        [JsonPropertyName("family_name")] public string? FamilyName { get; set; }
        /// <summary>"chosen" (an already-loaded family matched) | "generated" (none matched, one was authored) | "not_placed" (no adaptation run yet).</summary>
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("depth_m")] public double DepthM { get; set; }
        [JsonPropertyName("blade_count")] public int BladeCount { get; set; }
        [JsonPropertyName("states")] public List<KineticStateDto>? States { get; set; }
        [JsonPropertyName("mechanics")] public KineticMechanicsDto? Mechanics { get; set; }
        /// <summary>overhead | slats | fins | sail | fence: which kind of dynamic unit this is (the family is the same adaptive bar or membrane for all of them).</summary>
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("kind_label")] public string? KindLabel { get; set; }
        /// <summary>What it stands on or answers: the pergola the analysis recommended, a railing or wall of the model, a roof edge the ball analysis fenced.</summary>
        [JsonPropertyName("host")] public string? Host { get; set; }
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        /// <summary>How many blades or fins, how far apart, and why (LouvreMechanics.RecommendSpacing).</summary>
        [JsonPropertyName("spacing")] public KineticSpacingDto? Spacing { get; set; }
        /// <summary>The railing or frame that carries them: bays and posts (LouvreMechanics.RecommendSupports).</summary>
        [JsonPropertyName("supports")] public KineticSupportsDto? Supports { get; set; }
        [JsonPropertyName("sail")] public KineticSailDto? Sail { get; set; }
        [JsonPropertyName("fence")] public KineticFenceDto? Fence { get; set; }
        /// <summary>What was placed in Revit for this unit: adaptive bars and membranes, and how many of them are the moving parts.</summary>
        [JsonPropertyName("parts_placed")] public int PartsPlaced { get; set; }
        [JsonPropertyName("moving_parts")] public int MovingParts { get; set; }
        [JsonPropertyName("phase")] public string? Phase { get; set; }
    }

    internal class KineticSpacingDto
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("pitch_mm")] public double PitchMm { get; set; }
        [JsonPropertyName("stack_length_m")] public double StackLengthM { get; set; }
        [JsonPropertyName("target_stopped_percent")] public double TargetStoppedPercent { get; set; }
        [JsonPropertyName("stopped_at_binding_percent")] public double StoppedAtBindingPercent { get; set; }
        [JsonPropertyName("binding_state")] public string? BindingState { get; set; }
        [JsonPropertyName("binding_profile_deg")] public double BindingProfileDeg { get; set; }
        [JsonPropertyName("closed_stopped_percent")] public double ClosedStoppedPercent { get; set; }
        [JsonPropertyName("capped_at_max_pitch")] public bool CappedAtMaxPitch { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }

    internal class KineticSupportsDto
    {
        [JsonPropertyName("span_m")] public double SpanM { get; set; }
        [JsonPropertyName("allowed_span_m")] public double AllowedSpanM { get; set; }
        [JsonPropertyName("bays")] public int Bays { get; set; }
        [JsonPropertyName("bay_length_m")] public double BayLengthM { get; set; }
        [JsonPropertyName("intermediate_posts")] public int IntermediatePosts { get; set; }
        [JsonPropertyName("deflection_mm")] public double DeflectionMm { get; set; }
    }

    /// <summary>A tensile sail on four movable pillars (SailMechanics): how the masts lean through the heat window, what the mast must be, the lean actuator, the storm position.</summary>
    internal class KineticSailDto
    {
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("depth_m")] public double DepthM { get; set; }
        [JsonPropertyName("mast_height_m")] public double MastHeightM { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("mast_diameter_mm")] public double MastDiameterMm { get; set; }
        [JsonPropertyName("recommended_mast_diameter_mm")] public double RecommendedMastDiameterMm { get; set; }
        [JsonPropertyName("mast_ok")] public bool MastOk { get; set; }
        [JsonPropertyName("mast_utilisation_percent")] public double MastUtilisationPercent { get; set; }
        [JsonPropertyName("mast_base_moment_kn_m")] public double MastBaseMomentKnM { get; set; }
        [JsonPropertyName("pretension_pull_kn_per_corner")] public double PretensionPullKnPerCorner { get; set; }
        [JsonPropertyName("uplift_kn_per_mast")] public double UpliftKnPerMast { get; set; }
        [JsonPropertyName("shape")] public string? Shape { get; set; }
        [JsonPropertyName("min_scale")] public double MinScale { get; set; }
        [JsonPropertyName("max_scale")] public double MaxScale { get; set; }
        [JsonPropertyName("area_min_m2")] public double AreaMinM2 { get; set; }
        [JsonPropertyName("area_max_m2")] public double AreaMaxM2 { get; set; }
        [JsonPropertyName("garden_freed_m2")] public double GardenFreedM2 { get; set; }
        [JsonPropertyName("rail_length_m")] public double RailLengthM { get; set; }
        [JsonPropertyName("travel_m")] public double TravelM { get; set; }
        [JsonPropertyName("tracks")] public int Tracks { get; set; }
        [JsonPropertyName("carriage_force_kn")] public double CarriageForceKn { get; set; }
        [JsonPropertyName("drive_speed_cm_s")] public double DriveSpeedCmS { get; set; }
        [JsonPropertyName("travel_seconds")] public double TravelSeconds { get; set; }
        [JsonPropertyName("drive_power_w")] public double DrivePowerW { get; set; }
        [JsonPropertyName("storm_height_m")] public double StormHeightM { get; set; }
        [JsonPropertyName("fabric_mass_kg")] public double FabricMassKg { get; set; }
        [JsonPropertyName("shade_fixed_min_percent")] public double ShadeFixedMinPercent { get; set; }
        [JsonPropertyName("shade_tracked_min_percent")] public double ShadeTrackedMinPercent { get; set; }
        [JsonPropertyName("shade_fixed_mean_percent")] public double ShadeFixedMeanPercent { get; set; }
        [JsonPropertyName("shade_tracked_mean_percent")] public double ShadeTrackedMeanPercent { get; set; }
        [JsonPropertyName("states")] public List<KineticSailStateDto>? States { get; set; }
        [JsonPropertyName("findings")] public List<string>? Findings { get; set; }
    }

    internal class KineticSailStateDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("solar_time_h")] public double SolarTimeH { get; set; }
        [JsonPropertyName("sun_elevation_deg")] public double SunElevationDeg { get; set; }
        [JsonPropertyName("scale")] public double Scale { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("travel_m")] public double TravelM { get; set; }
        [JsonPropertyName("shade_held_fixed_percent")] public double ShadeHeldFixedPercent { get; set; }
        [JsonPropertyName("shade_held_tracked_percent")] public double ShadeHeldTrackedPercent { get; set; }
    }

    /// <summary>A roller fence on the roof edge (RollerFenceMechanics): the rails, the curtain, the roller motor, the storm rule.</summary>
    internal class KineticFenceDto
    {
        [JsonPropertyName("edge")] public string? Edge { get; set; }
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("rails")] public int Rails { get; set; }
        [JsonPropertyName("bay_spacing_m")] public double BaySpacingM { get; set; }
        [JsonPropertyName("rail_size_mm")] public double RailSizeMm { get; set; }
        [JsonPropertyName("recommended_rail_size_mm")] public double RecommendedRailSizeMm { get; set; }
        [JsonPropertyName("rail_ok")] public bool RailOk { get; set; }
        [JsonPropertyName("rail_utilisation_percent")] public double RailUtilisationPercent { get; set; }
        [JsonPropertyName("rail_deflection_mm")] public double RailDeflectionMm { get; set; }
        [JsonPropertyName("rail_deflection_limit_mm")] public double RailDeflectionLimitMm { get; set; }
        [JsonPropertyName("wind_moment_kn_m")] public double WindMomentKnM { get; set; }
        [JsonPropertyName("impact_moment_kn_m")] public double ImpactMomentKnM { get; set; }
        [JsonPropertyName("impact_energy_j")] public double ImpactEnergyJ { get; set; }
        [JsonPropertyName("impact_force_kn")] public double ImpactForceKn { get; set; }
        [JsonPropertyName("curtain_mass_kg")] public double CurtainMassKg { get; set; }
        [JsonPropertyName("motor_force_n")] public double MotorForceN { get; set; }
        [JsonPropertyName("motor_torque_nm")] public double MotorTorqueNm { get; set; }
        [JsonPropertyName("motor_power_w")] public double MotorPowerW { get; set; }
        [JsonPropertyName("deploy_seconds")] public double DeploySeconds { get; set; }
        [JsonPropertyName("storm_retract")] public bool StormRetract { get; set; }
        [JsonPropertyName("stops_percent_of_exits")] public double StopsPercentOfExits { get; set; }
        [JsonPropertyName("findings")] public List<string>? Findings { get; set; }
    }

    /// <summary>The mechanics of one louvre pergola (LouvreMechanics.Analyse): blades, wind torque, the actuator, deflection, stow. Loads in N and N m, lengths in mm unless the name says m.</summary>
    internal class KineticMechanicsDto
    {
        [JsonPropertyName("chord_mm")] public double ChordMm { get; set; }
        [JsonPropertyName("thickness_mm")] public double ThicknessMm { get; set; }
        [JsonPropertyName("pitch_mm")] public double PitchMm { get; set; }
        [JsonPropertyName("span_m")] public double SpanM { get; set; }
        [JsonPropertyName("blade_mass_kg")] public double BladeMassKg { get; set; }
        [JsonPropertyName("total_mass_kg")] public double TotalMassKg { get; set; }
        [JsonPropertyName("design_pressure_pa")] public double DesignPressurePa { get; set; }
        [JsonPropertyName("design_wind_ms")] public double DesignWindMs { get; set; }
        [JsonPropertyName("operating_wind_ms")] public double OperatingWindMs { get; set; }
        [JsonPropertyName("peak_torque_operating_nm")] public double PeakTorqueOperatingNm { get; set; }
        [JsonPropertyName("peak_torque_angle_deg")] public double PeakTorqueAngleDeg { get; set; }
        [JsonPropertyName("peak_torque_gust_nm")] public double PeakTorqueGustNm { get; set; }
        [JsonPropertyName("face_on_force_gust_n")] public double FaceOnForceGustN { get; set; }
        [JsonPropertyName("actuator_torque_nm")] public double ActuatorTorqueNm { get; set; }
        [JsonPropertyName("actuator_force_n")] public double ActuatorForceN { get; set; }
        [JsonPropertyName("holding_torque_nm")] public double HoldingTorqueNm { get; set; }
        [JsonPropertyName("deflection_mm")] public double DeflectionMm { get; set; }
        [JsonPropertyName("deflection_ratio")] public double DeflectionRatio { get; set; }
        [JsonPropertyName("allowed_span_m")] public double AllowedSpanM { get; set; }
        [JsonPropertyName("deflection_ok")] public bool DeflectionOk { get; set; }
        [JsonPropertyName("stow_required")] public bool StowRequired { get; set; }
        [JsonPropertyName("swing_seconds")] public double SwingSeconds { get; set; }
        [JsonPropertyName("actuator_power_w")] public double ActuatorPowerW { get; set; }
        [JsonPropertyName("energy_wh_per_day")] public double EnergyWhPerDay { get; set; }
        [JsonPropertyName("findings")] public List<string>? Findings { get; set; }
    }

    /// <summary>One actuation state: the sun's own elevation at that hour, and the louvre opening it drives (LouvreActuationModel.OpenAngleDegForElevation -- 0 = closed/flat, 90 = open/vertical).</summary>
    internal class KineticStateDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("solar_time_h")] public double SolarTimeH { get; set; }
        [JsonPropertyName("sun_elevation_deg")] public double SunElevationDeg { get; set; }
        [JsonPropertyName("louvre_open_angle_deg")] public double LouvreOpenAngleDeg { get; set; }
        [JsonPropertyName("sun_stopped_percent")] public double SunStoppedPercent { get; set; }
        [JsonPropertyName("wind_torque_operating_nm")] public double WindTorqueOperatingNm { get; set; }
        [JsonPropertyName("wind_torque_gust_nm")] public double WindTorqueGustNm { get; set; }
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
        public static void PublishFireSafety(FireSafetyResultDto result) => Publish("fire_safety", p => p.FireSafety = result);
        public static void PublishAccessibility(AccessibilityResultDto result) => Publish("accessibility", p => p.Accessibility = result);
        public static void PublishLca(LcaResultDto result) => Publish("lca", p => p.Lca = result);
        public static void PublishCarbonImpact(CarbonImpactResultDto result) => Publish("carbon_impact", p => p.CarbonImpact = result);
        public static void PublishSunAndShading(SunAndShadingResultDto result) => Publish("sun_and_shading", p => p.SunAndShading = result);
        public static void PublishBallTrajectory(BallTrajectoryResultDto result) => Publish("ball_trajectory", p => p.BallTrajectory = result);
        public static void PublishWindErosion(WindErosionResultDto result) => Publish("wind_erosion", p => p.WindErosion = result);
        public static void PublishSoilPercolation(SoilPercolationResultDto result) => Publish("soil_percolation", p => p.SoilPercolation = result);
        public static void PublishStructuralLoads(StructuralLoadsResultDto result) => Publish("structural_loads", p => p.StructuralLoads = result);
        public static void PublishDynamicAnalysis(DynamicAnalysisResultDto result) => Publish("dynamic_analysis", p => p.DynamicAnalysis = result);
        public static void PublishKinetics(KineticsResultDto result) => Publish("kinetics", p => p.Kinetics = result);

        private static AnalysisResultPayload Load()
        {
            AnalysisResultPayload? payload = null;
            if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var existingJson) && existingJson != null)
            {
                try { payload = JsonSerializer.Deserialize<AnalysisResultPayload>(existingJson); }
                catch (Exception ex) { SportifyLog.Warn("results", "the published results could not be read back; starting again: " + ex.Message); }
            }
            return payload ?? new AnalysisResultPayload();
        }

        /// <summary>
        /// Every section carries the layout it was computed for and the time. Publishing a section for a layout drops the sections that belong to
        /// another one: the document always describes ONE layout, so the web app can never show, side by side, the rain result of a layout with a
        /// garden and the structural result of one without.
        /// </summary>
        private static void Publish(string key, Action<AnalysisResultPayload> apply)
        {
            var layoutId = RoofBoundaryServer.LayoutIdForPublishing;
            var payload = Load();

            var dropped = payload.DropSectionsNotFor(layoutId);
            if (dropped.Count > 0) SportifyLog.Info("results", "layout " + layoutId + ": dropped the results computed for another layout: " + string.Join(", ", dropped));

            apply(payload);
            var now = DateTime.UtcNow.ToString("o");
            payload.Sections[key] = new ResultSectionInfoDto { LayoutId = layoutId, ComputedAtUtc = now };
            payload.LayoutId = layoutId;
            payload.UpdatedAtUtc = now;

            RoofBoundaryServer.PublishAnalysisResults(JsonSerializer.Serialize(payload));
        }

        /// <summary>
        /// Drops the sections that are not about the layout the analyses are about to read, before any new section is published: a run in which
        /// nothing is sent (no garden in the layout) would otherwise leave the previous layout's results in place. Returns what was dropped.
        /// </summary>
        public static List<string> RetireStale()
        {
            var layoutId = RoofBoundaryServer.LayoutIdForPublishing;
            var payload = Load();
            var dropped = payload.DropSectionsNotFor(layoutId);
            if (dropped.Count == 0) return dropped;
            payload.LayoutId = layoutId;
            RoofBoundaryServer.PublishAnalysisResults(JsonSerializer.Serialize(payload));
            SportifyLog.Info("results", "layout " + layoutId + ": retired the results computed for another layout: " + string.Join(", ", dropped));
            return dropped;
        }
    }
}
