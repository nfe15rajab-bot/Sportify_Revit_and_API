using System.Text.Json;
using System.Text.Json.Serialization;

namespace SportfyRevit
{
    /// <summary>
    /// Mirrors the "Export Combined JSON" payload from the frontend's
    /// Combine tab exactly (main.js, btn-combine-json handler, v1.3) — one
    /// explicit JsonPropertyName per field rather than relying on
    /// case-insensitive matching, since the source is snake_case and C#
    /// properties are PascalCase (different convention, not just casing).
    /// </summary>
    internal class SportifyLayout
    {
        [JsonPropertyName("roof_context")] public RoofContextDto? RoofContext { get; set; }
        [JsonPropertyName("design_rules")] public DesignRulesDto? DesignRules { get; set; }
        [JsonPropertyName("entry_points")] public List<EntryPointDto>? EntryPoints { get; set; }
        [JsonPropertyName("circulation_paths")] public List<CirculationPathDto>? CirculationPaths { get; set; }
        [JsonPropertyName("site_location")] public SiteLocationDto? SiteLocation { get; set; }
        [JsonPropertyName("placements")] public List<PlacementDto>? Placements { get; set; }

        /// <summary>
        /// Distinct provider build-up systems used anywhere in this layout —
        /// sent once at the top level rather than repeated inside every garden
        /// parcel, because Revit creates one floor type per system, not one per
        /// parcel.
        /// </summary>
        [JsonPropertyName("assemblies")] public List<AssemblyDto>? Assemblies { get; set; }

        /// <summary>
        /// Ground zones drawn on the roof — planting, lawn, walkways. A
        /// rectangle and a build-up is everything needed to draw a real layered
        /// floor, which is why these are their own list rather than pretending
        /// to be placements: a zone has no fixed size and is not an object.
        /// </summary>
        [JsonPropertyName("zones")] public List<ZoneDto>? Zones { get; set; }
    }

    internal class SiteLocationDto
    {
        [JsonPropertyName("latitude_deg")] public double LatitudeDeg { get; set; }
        [JsonPropertyName("longitude_deg")] public double LongitudeDeg { get; set; }
        [JsonPropertyName("place_name")] public string? PlaceName { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; } // "YYYY-MM-DD"
        [JsonPropertyName("time")] public string? Time { get; set; } // "HH:mm"
    }

    internal class RoofContextDto
    {
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("source_boundary_polygon")] public List<PointDto>? SourceBoundaryPolygon { get; set; }
        [JsonPropertyName("world_origin_x_m")] public double WorldOriginXM { get; set; }
        [JsonPropertyName("world_origin_y_m")] public double WorldOriginYM { get; set; }

        /// <summary>
        /// Elevation of the pushed roof's top face. Absent (0) for a roof typed
        /// in by hand, which correctly means ground level — so an older export
        /// without this field behaves exactly as it did before.
        /// </summary>
        [JsonPropertyName("world_origin_z_m")] public double WorldOriginZM { get; set; }
    }

    internal class DesignRulesDto
    {
        [JsonPropertyName("clearance_m")] public double ClearanceM { get; set; }
        [JsonPropertyName("boundary_setback_m")] public double BoundarySetbackM { get; set; }
        [JsonPropertyName("circulation_width_m")] public double CirculationWidthM { get; set; }
        [JsonPropertyName("min_entry_points")] public int MinEntryPoints { get; set; }
    }

    internal class EntryPointDto
    {
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("edge")] public string? Edge { get; set; }
    }

    internal class CirculationPathDto
    {
        [JsonPropertyName("item_id")] public string? ItemId { get; set; }

        /// <summary>
        /// "auto" (rule-engine BFS path, tied to ItemId) or "manual"
        /// (combineField.js's user-drawn, draggable circulation axis, no
        /// ItemId) — added in export v1.5; older exports have neither
        /// field. SportifyLayoutBuilder.CreateCirculationPaths draws either
        /// kind identically (it only ever reads PointsM), so this isn't
        /// required for the geometry to appear — it's here so a consumer
        /// that DOES want to tell them apart (e.g. a future line-style
        /// override, a schedule) can.
        /// </summary>
        [JsonPropertyName("source")] public string? Source { get; set; }

        [JsonPropertyName("points_m")] public List<PointDto>? PointsM { get; set; }

        /// <summary>Manual axes only — the curve's original draggable midpoint, alongside the already-sampled PointsM polyline every consumer already reads.</summary>
        [JsonPropertyName("control_point_m")] public PointDto? ControlPointM { get; set; }
    }

    internal class PointDto
    {
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
    }

    internal class PlacementDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; } // "field" | "activity" | "garden"
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("insertion_point")] public InsertionPointDto? InsertionPoint { get; set; }
        [JsonPropertyName("bounding_box")] public BoundingBoxDto? BoundingBox { get; set; }
        [JsonPropertyName("transform")] public TransformDto? Transform { get; set; }
        [JsonPropertyName("parameters")] public ParametersDto? Parameters { get; set; }

        /// <summary>
        /// buildAnalysisForItem() (combineController.js) — the same per-item
        /// math the Analysis tab's component explorer shows, computed once
        /// more at export time so this file is self-contained. A sibling of
        /// Parameters, not nested inside it, mirroring the export's own shape.
        /// </summary>
        [JsonPropertyName("analysis")] public PlacementAnalysisDto? Analysis { get; set; }
    }

    /// <summary>
    /// main.js's own comment on this field says it plainly: "Revit Family
    /// placement is usually easiest via the Center Point." Was being sent
    /// since v1.3 but had no matching property here, so it was silently
    /// dropped on deserialize — SportifyLayoutBuilder's family-instance path
    /// is the first consumer.
    /// </summary>
    internal class InsertionPointDto
    {
        [JsonPropertyName("center_x_m")] public double CenterXM { get; set; }
        [JsonPropertyName("center_y_m")] public double CenterYM { get; set; }
    }

    internal class BoundingBoxDto
    {
        [JsonPropertyName("top_left_x_m")] public double TopLeftXM { get; set; }
        [JsonPropertyName("top_left_y_m")] public double TopLeftYM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
    }

    internal class TransformDto
    {
        [JsonPropertyName("rotation_deg")] public double RotationDeg { get; set; }
    }

    /// <summary>
    /// The frontend's per-category "parameters" blob (buildSportPayload()/
    /// buildGardenPayload() in main.js) carries a lot more than this, but
    /// quality_key is the one field every category already builds the same
    /// way (getQualityKey()/getGardenQualityKey()/getActivityQualityKey() in
    /// data.js/gardenData.js/Activitiesdata.js) specifically as, per data.js's
    /// own comment, "the primary lookup key" — so it's also the strongest
    /// signal for matching a loaded FamilySymbol's name, stronger than
    /// parsing the free-text label. Everything else in the blob is ignored
    /// here on purpose rather than mirrored field-for-field.
    /// </summary>
    internal class ParametersDto
    {
        [JsonPropertyName("quality_key")] public string? QualityKey { get; set; }

        /// <summary>
        /// Present only for pieces pushed from the web app's Revit Families
        /// tab — the user's OWN loaded content rather than one of the app's
        /// built-in catalog presets. When set it names the exact family and
        /// type to place, so there is nothing to match or guess: quality_key
        /// matching and family generation are both bypassed.
        /// </summary>
        [JsonPropertyName("revit_family")] public RevitFamilyRefDto? RevitFamily { get; set; }

        /// <summary>
        /// Cross-category dimensions/area (buildSportPayload/buildActivityPayload/
        /// buildGardenPayload in the frontend) — one uniform place to read "how
        /// big is this" regardless of category, alongside the category-specific
        /// nested dimensions below (Field/Garden/Activity), which stay as-is.
        /// </summary>
        [JsonPropertyName("generalities")] public GeneralitiesDto? Generalities { get; set; }

        /// <summary>
        /// Only present for garden placements — buildGardenPayload() (web
        /// app's gardenController.js) already resolves the active theme's
        /// layer thicknesses into this array before export, so Water
        /// Management can sum buildup depth directly from the synced
        /// layout instead of needing GARDEN_THEMES reference data on the
        /// Revit side at all.
        /// </summary>
        [JsonPropertyName("garden")] public GardenParametersDto? Garden { get; set; }

        /// <summary>
        /// Sport and Activity both put their materials block at this top
        /// level (buildSportPayload()/buildActivityPayload() in
        /// sportController.js) — Garden nests its own copy one level
        /// deeper instead (see GardenParametersDto.Materials), so LCA has
        /// to check both places for reference_material.
        /// </summary>
        [JsonPropertyName("materials")] public MaterialsRefDto? Materials { get; set; }

        /// <summary>Only present for sport ("field") placements.</summary>
        [JsonPropertyName("field")] public FieldParametersDto? Field { get; set; }

        /// <summary>Only present for activity placements (buildActivityPayload() in sportController.js).</summary>
        [JsonPropertyName("activity")] public ActivityParametersDto? Activity { get; set; }
    }

    /// <summary>
    /// A direct reference to a family already loaded in this document, as
    /// published by LoadFamiliesCommand and configured in the web app's
    /// Families tab.
    ///
    /// Parameters is deliberately untyped (JsonElement): the names and value
    /// shapes come from whatever family the customer loaded, so there is no
    /// fixed schema to model — a number is a length in meters, an object with
    /// material_id is a material assignment. Anything else is ignored rather
    /// than rejected, since a family can expose parameters this app has no
    /// opinion about.
    /// </summary>
    internal class RevitFamilyRefDto
    {
        [JsonPropertyName("family_name")] public string? FamilyName { get; set; }
        [JsonPropertyName("type_name")] public string? TypeName { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("is_resizable")] public bool IsResizable { get; set; }
        [JsonPropertyName("parameters")] public Dictionary<string, JsonElement>? Parameters { get; set; }
    }

    /// <summary>
    /// One provider's named roof build-up — ZinCo Roof Garden, Bauder
    /// EXTENSIVE Lightweight Sedum. Carries the whole layer stack rather than a
    /// key, so the import needs no catalog of its own and an older export keeps
    /// working after the web app's catalog changes.
    /// </summary>
    internal class AssemblyDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("system_name")] public string? SystemName { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("revit_type_name")] public string? RevitTypeName { get; set; }
        [JsonPropertyName("build_up_mm")] public double? BuildUpMm { get; set; }
        [JsonPropertyName("saturated_kg_m2")] public double? SaturatedKgM2 { get; set; }
        [JsonPropertyName("water_storage_l_m2")] public double? WaterStorageLM2 { get; set; }
        [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
        [JsonPropertyName("total_thickness_m")] public double TotalThicknessM { get; set; }
        [JsonPropertyName("layers")] public List<AssemblyLayerDto>? Layers { get; set; }
    }

    internal class ZoneDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("bounding_box")] public BoundingBoxDto? BoundingBox { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("assembly_key")] public string? AssemblyKey { get; set; }
    }

    internal class AssemblyLayerDto
    {
        [JsonPropertyName("order")] public int Order { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("function")] public string? Function { get; set; }
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }
        /// <summary>"published" or "typical" — whether the provider printed this figure.</summary>
        [JsonPropertyName("thickness_source")] public string? ThicknessSource { get; set; }
    }

    internal class GardenParametersDto
    {
        [JsonPropertyName("type_id")] public string? TypeId { get; set; }
        [JsonPropertyName("theme")] public string? Theme { get; set; }
        [JsonPropertyName("dimensions")] public DimensionsDto? Dimensions { get; set; }
        [JsonPropertyName("layers")] public List<GardenLayerDto>? Layers { get; set; }
        [JsonPropertyName("materials")] public MaterialsRefDto? Materials { get; set; }

        /// <summary>
        /// The provider build-up this parcel is made of — what turns it into a
        /// real Floor rather than a placeholder box.
        /// </summary>
        [JsonPropertyName("assembly")] public AssemblyDto? Assembly { get; set; }
    }

    internal class GardenLayerDto
    {
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }
    }

    internal class MaterialsRefDto
    {
        [JsonPropertyName("quality_level")] public string? QualityLevel { get; set; }
        [JsonPropertyName("reference_material")] public string? ReferenceMaterial { get; set; }
        [JsonPropertyName("reference_provider")] public string? ReferenceProvider { get; set; }

        /// <summary>
        /// The full matched Sportify.Api Material/Provider record (readReferenceSelectionDetail()
        /// in dataTab.js), not just its name — null for manual-text entries or nothing picked,
        /// same as the plain name fields above being null in that case.
        /// </summary>
        [JsonPropertyName("reference_material_detail")] public MaterialDetailDto? ReferenceMaterialDetail { get; set; }
        [JsonPropertyName("reference_provider_detail")] public ProviderDetailDto? ReferenceProviderDetail { get; set; }
    }

    /// <summary>
    /// Mirrors Sportify.Api.Models.Material field-for-field. Deserialized
    /// straight from that controller's own JSON response (ASP.NET Core's
    /// default camelCase naming policy) with no snake_case translation on
    /// the way through the frontend — so, deliberately unlike every other
    /// DTO in this file, these property names are camelCase on the wire,
    /// not snake_case. Leave as-is; this isn't an inconsistency to "fix".
    /// </summary>
    internal class MaterialDetailDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("normCode")] public string? NormCode { get; set; }
        [JsonPropertyName("performanceClass")] public string? PerformanceClass { get; set; }
        [JsonPropertyName("forceReduction")] public string? ForceReduction { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
        [JsonPropertyName("embodiedCarbonValue")] public double? EmbodiedCarbonValue { get; set; }
        [JsonPropertyName("embodiedCarbonUnit")] public string? EmbodiedCarbonUnit { get; set; }
        [JsonPropertyName("embodiedCarbonSource")] public string? EmbodiedCarbonSource { get; set; }
    }

    /// <summary>Mirrors Sportify.Api.Models.Provider field-for-field — see MaterialDetailDto's own note on camelCase.</summary>
    internal class ProviderDetailDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
        [JsonPropertyName("specialty")] public string? Specialty { get; set; }
        [JsonPropertyName("website")] public string? Website { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
    }

    internal class GeneralitiesDto
    {
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        /// <summary>Fields/activities only.</summary>
        [JsonPropertyName("min_height_m")] public double? MinHeightM { get; set; }
        /// <summary>Gardens only — sum of the active theme's layer thicknesses.</summary>
        [JsonPropertyName("buildup_depth_m")] public double? BuildupDepthM { get; set; }
    }

    internal class FieldParametersDto
    {
        [JsonPropertyName("sport")] public string? Sport { get; set; }
        [JsonPropertyName("variant")] public string? Variant { get; set; }
        [JsonPropertyName("norm")] public string? Norm { get; set; }
        [JsonPropertyName("dimensions")] public DimensionsDto? Dimensions { get; set; }
        [JsonPropertyName("capacity")] public CapacityDto? Capacity { get; set; }
    }

    internal class ActivityParametersDto
    {
        [JsonPropertyName("type_id")] public string? TypeId { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("norm")] public string? Norm { get; set; }
        [JsonPropertyName("dimensions")] public DimensionsDto? Dimensions { get; set; }
    }

    internal class DimensionsDto
    {
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
    }

    internal class CapacityDto
    {
        [JsonPropertyName("seats")] public int Seats { get; set; }
    }

    /// <summary>
    /// Mirrors buildAnalysisForItem() (combineController.js) exactly — the
    /// web app's own per-item analysis estimate at export time. Revit
    /// recomputes fire safety (CirculationEngine) and live loads
    /// (AnalysisReferenceData) itself rather than trusting these numbers
    /// blindly, same "full-BIM-fidelity port, must never disagree"
    /// philosophy as every Analyze* command — these are kept as the export's
    /// own self-contained record and as a fallback.
    /// </summary>
    internal class PlacementAnalysisDto
    {
        [JsonPropertyName("fire_safety")] public FireSafetyPlacementDto? FireSafety { get; set; }
        [JsonPropertyName("accessibility")] public AccessibilityPlacementDto? Accessibility { get; set; }
        [JsonPropertyName("wind_exposure")] public WindExposurePlacementDto? WindExposure { get; set; }
        [JsonPropertyName("water_management")] public WaterManagementPlacementDto? WaterManagement { get; set; }
        [JsonPropertyName("lca")] public LcaPlacementDto? Lca { get; set; }
        [JsonPropertyName("live_load")] public LiveLoadPlacementDto? LiveLoad { get; set; }
    }

    internal class FireSafetyPlacementDto
    {
        [JsonPropertyName("unreachable")] public bool Unreachable { get; set; }
        [JsonPropertyName("distance_to_nearest_entry_m")] public double? DistanceToNearestEntryM { get; set; }
        [JsonPropertyName("within_limit")] public bool WithinLimit { get; set; }
        [JsonPropertyName("max_travel_distance_m")] public double MaxTravelDistanceM { get; set; }
    }

    internal class AccessibilityPlacementDto
    {
        [JsonPropertyName("reachable")] public bool Reachable { get; set; }
    }

    internal class WindExposurePlacementDto
    {
        [JsonPropertyName("out_of_bounds")] public bool OutOfBounds { get; set; }
        [JsonPropertyName("distance_to_edge_m")] public double? DistanceToEdgeM { get; set; }
        [JsonPropertyName("exposed")] public bool? Exposed { get; set; }
        [JsonPropertyName("exposure_zone_m")] public double ExposureZoneM { get; set; }
    }

    /// <summary>Garden placements only.</summary>
    internal class WaterManagementPlacementDto
    {
        [JsonPropertyName("buildup_depth_cm")] public double BuildupDepthCm { get; set; }
        [JsonPropertyName("retention_percent")] public double RetentionPercent { get; set; }
    }

    internal class LcaPlacementDto
    {
        [JsonPropertyName("reference_material")] public string? ReferenceMaterial { get; set; }
        [JsonPropertyName("embodied_carbon_value_per_m2")] public double EmbodiedCarbonValuePerM2 { get; set; }
        [JsonPropertyName("embodied_carbon_unit")] public string? EmbodiedCarbonUnit { get; set; }
        [JsonPropertyName("embodied_carbon_source")] public string? EmbodiedCarbonSource { get; set; }
        [JsonPropertyName("total_kg")] public double TotalKg { get; set; }
    }

    /// <summary>Fields with a set spectator capacity only.</summary>
    internal class LiveLoadPlacementDto
    {
        [JsonPropertyName("seats")] public int Seats { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("kn_per_m2")] public double KnPerM2 { get; set; }
        [JsonPropertyName("reference_kn_per_m2")] public double ReferenceKnPerM2 { get; set; }
        [JsonPropertyName("within_reference")] public bool WithinReference { get; set; }
    }
}
