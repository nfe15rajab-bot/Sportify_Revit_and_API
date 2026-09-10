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
        [JsonPropertyName("points_m")] public List<PointDto>? PointsM { get; set; }
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

        /// <summary>Only present for sport ("field") placements — Live Loads reads capacity.seats from here.</summary>
        [JsonPropertyName("field")] public FieldParametersDto? Field { get; set; }
    }

    internal class GardenParametersDto
    {
        [JsonPropertyName("layers")] public List<GardenLayerDto>? Layers { get; set; }
        [JsonPropertyName("materials")] public MaterialsRefDto? Materials { get; set; }
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
    }

    internal class FieldParametersDto
    {
        [JsonPropertyName("capacity")] public CapacityDto? Capacity { get; set; }
    }

    internal class CapacityDto
    {
        [JsonPropertyName("seats")] public int Seats { get; set; }
    }
}
