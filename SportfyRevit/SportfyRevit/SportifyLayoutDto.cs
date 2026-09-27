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

        /// <summary>
        /// What the wind analysis needs to know about the site: the wind zone (looked up from where the site is, or set by
        /// hand), and the roof's orientation. Absent in exports from before the web app carried it.
        /// </summary>
        [JsonPropertyName("site_conditions")] public SiteConditionsDto? SiteConditions { get; set; }
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
        [JsonPropertyName("roof_finish")] public RoofFinishDto? RoofFinish { get; set; }

        /// <summary>
        /// Build-up keys the export referred to but could not describe, because
        /// the web app's catalog was not loaded. Without this, "no build-up
        /// chosen" and "the catalog was offline when this was exported" arrive
        /// here looking identical, and only one of them is the user's doing.
        /// </summary>
        [JsonPropertyName("unresolved_assemblies")] public List<string>? UnresolvedAssemblies { get; set; }

        /// <summary>
        /// The roof's structural grid and columns, pulled from the Revit model by the push (or drawn by hand in the Combine tab),
        /// and the deck capacity when the structural engineer's figure has been entered. Absent in older exports and when the
        /// push found no grids: the structural analysis then assumes a regular grid and says so.
        /// </summary>
        [JsonPropertyName("structure")] public StructureDto? Structure { get; set; }

        /// <summary>
        /// What the designer decided about the structural analyses' built-in assumptions (the Structure inputs and Site conditions tabs, or the
        /// Revit dialog before an analysis): which built-in values they accepted, and the comfort limits they set. Absent in older exports.
        /// </summary>
        [JsonPropertyName("analysis_assumptions")] public AnalysisAssumptionsDto? AnalysisAssumptions { get; set; }
    }

    internal class AnalysisAssumptionsDto
    {
        /// <summary>Keys (Sportify.Simulation.Structure.AnalysisAssumptions) whose built-in value was accepted.</summary>
        [JsonPropertyName("accepted")] public List<string>? Accepted { get; set; }

        /// <summary>Acceleration limits in g. Null = the built-in ones.</summary>
        [JsonPropertyName("comfort_limit_walking_g")] public double? ComfortLimitWalkingG { get; set; }
        [JsonPropertyName("comfort_limit_rhythmic_g")] public double? ComfortLimitRhythmicG { get; set; }

        /// <summary>For the sun and shade analysis: the shade target of a people zone (%), the sun a garden needs (hours), which kinds of equipment may be recommended ("all" | "light" | "fixed"), and a site latitude typed in (degrees north). Null = the built-in ones.</summary>
        [JsonPropertyName("shade_target_percent")] public double? ShadeTargetPercent { get; set; }
        [JsonPropertyName("garden_min_sun_hours")] public double? GardenMinSunHours { get; set; }
        [JsonPropertyName("shade_equipment")] public string? ShadeEquipment { get; set; }
        [JsonPropertyName("site_latitude_deg")] public double? SiteLatitudeDeg { get; set; }
    }

    internal class StructureDto
    {
        /// <summary>"revit" (pulled from the model) or "manual" (drawn in the app).</summary>
        [JsonPropertyName("source")] public string? Source { get; set; }

        /// <summary>Characteristic G + Q the roof deck can carry, kN/m2. Null = not given.</summary>
        [JsonPropertyName("deck_capacity_kn_m2")] public double? DeckCapacityKnM2 { get; set; }

        /// <summary>The deck's first natural frequency (vertical), Hz, from the structural engineer. Null = estimated from the spans.</summary>
        [JsonPropertyName("natural_frequency_hz")] public double? NaturalFrequencyHz { get; set; }

        [JsonPropertyName("grid_lines")] public List<GridLineDto>? GridLines { get; set; }
        [JsonPropertyName("columns")] public List<StructuralColumnDto>? Columns { get; set; }

        /// <summary>Beams under (or in) the roof slab, as segments in the plan with their section. Absent when none were read.</summary>
        [JsonPropertyName("beams")] public List<StructuralBeamDto>? Beams { get; set; }

        /// <summary>Walls whose top meets the roof slab (the ones that hold it up, or close enough to it to matter), as segments in the plan.</summary>
        [JsonPropertyName("walls")] public List<StructuralWallDto>? Walls { get; set; }
    }

    /// <summary>A beam under the roof: its axis in the plan (x right, y down, metres) and its section.</summary>
    internal class StructuralBeamDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("start_m")] public PointDto? StartM { get; set; }
        [JsonPropertyName("end_m")] public PointDto? EndM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("depth_m")] public double DepthM { get; set; }

        /// <summary>Elevation of the beam's top in the project, metres.</summary>
        [JsonPropertyName("top_elevation_m")] public double TopElevationM { get; set; }
    }

    /// <summary>A wall under the roof: its axis in the plan, thickness and height, and whether Revit marks it as load-bearing.</summary>
    internal class StructuralWallDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("start_m")] public PointDto? StartM { get; set; }
        [JsonPropertyName("end_m")] public PointDto? EndM { get; set; }
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("bearing")] public bool Bearing { get; set; }
    }

    /// <summary>A grid line as a segment in the roof's own plan coordinates (x right, y down, metres), with its name from the model ("A", "1"...).</summary>
    internal class GridLineDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("start_m")] public PointDto? StartM { get; set; }
        [JsonPropertyName("end_m")] public PointDto? EndM { get; set; }
    }

    internal class StructuralColumnDto
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
    }

    internal class SiteLocationDto
    {
        [JsonPropertyName("latitude_deg")] public double LatitudeDeg { get; set; }
        [JsonPropertyName("longitude_deg")] public double LongitudeDeg { get; set; }
        [JsonPropertyName("place_name")] public string? PlaceName { get; set; }
        [JsonPropertyName("date")] public string? Date { get; set; } // "YYYY-MM-DD"
        [JsonPropertyName("time")] public string? Time { get; set; } // "HH:mm"
    }

    internal class SiteConditionsDto
    {
        /// <summary>German wind zone 1 to 4, or null when it could not be told (a site outside Germany, or none set).</summary>
        [JsonPropertyName("wind_zone")] public int? WindZone { get; set; }
        [JsonPropertyName("wind_zone_manual")] public bool WindZoneManual { get; set; }
        /// <summary>"gemeinde" | "kreis" | "conservative" | "manual" | "none".</summary>
        [JsonPropertyName("wind_zone_confidence")] public string? WindZoneConfidence { get; set; }
        [JsonPropertyName("wind_zone_source")] public string? WindZoneSource { get; set; }

        /// <summary>Compass bearing of the top of the plan (the sun compass's convention); null until the designer sets it.</summary>
        [JsonPropertyName("north_deg")] public double? NorthDeg { get; set; }

        /// <summary>
        /// The web writes north_deg as null until it is set, and also this flag. Unity's JsonUtility cannot read a null, so its reader goes by the flag alone
        /// (SiteConditions.north_set); this reader has to agree, so the orientation counts only when the flag is true (Tools/ReaderParity checks it on
        /// fixtures with the flag off and a stale north_deg, and with north_deg but no flag at all).
        /// </summary>
        [JsonPropertyName("north_set")] public bool NorthSet { get; set; }

        /// <summary>Whether altitude_m was given (the same flag-not-null arrangement as north_set).</summary>
        [JsonPropertyName("altitude_set")] public bool AltitudeSet { get; set; }

        /// <summary>German snow load zone "1", "1a", "2", "2a" or "3", set by the designer; null when not given (the dynamic analysis then assumes one and says so).</summary>
        [JsonPropertyName("snow_zone")] public string? SnowZone { get; set; }

        /// <summary>Altitude of the site above sea level, metres; null when not given.</summary>
        [JsonPropertyName("altitude_m")] public double? AltitudeM { get; set; }

        /// <summary>How the roof is used through the day: "sports_day" | "event_day" | "community_day"; null = sports_day.</summary>
        [JsonPropertyName("day_schedule")] public string? DaySchedule { get; set; }
    }

    internal class RoofContextDto
    {
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("source_boundary_polygon")] public List<PointDto>? SourceBoundaryPolygon { get; set; }
        [JsonPropertyName("world_origin_x_m")] public double WorldOriginXM { get; set; }
        [JsonPropertyName("world_origin_y_m")] public double WorldOriginYM { get; set; }

        /// <summary>
        /// How far the plan is turned from the Revit model's X axis, counter-clockwise, in degrees (see RoofFrame): a roof turned against the
        /// model's axes has a plan of its own, and the import turns everything back by this. 0 (or absent) for a roof square to the model,
        /// which is exactly how an older export behaves.
        /// </summary>
        [JsonPropertyName("rotation_deg")] public double RotationDeg { get; set; }

        /// <summary>
        /// Elevation of the pushed roof's top face. Absent (0) for a roof typed
        /// in by hand, which correctly means ground level — so an older export
        /// without this field behaves exactly as it did before.
        /// </summary>
        [JsonPropertyName("world_origin_z_m")] public double WorldOriginZM { get; set; }

        /// <summary>
        /// How high the roof stands above the ground, from the Revit model (topography or the ground-floor level) or typed
        /// in the Site tab. 0 when not known. Unlike world_origin_z_m this is a HEIGHT, which is what wind loading needs.
        /// </summary>
        [JsonPropertyName("height_above_ground_m")] public double HeightAboveGroundM { get; set; }
        [JsonPropertyName("height_source")] public string? HeightSource { get; set; }

        /// <summary>
        /// What the Revit model says about the roof besides its outline and structure (openings, entries, edge, drains, slab, levels), in the
        /// roof's own plan coordinates. Absent for a hand-made roof and in older exports. See RoofFeaturesGeometry.cs and ROOF_FEATURES.md.
        /// </summary>
        [JsonPropertyName("features")] public RoofFeaturesDto? Features { get; set; }
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
        /// math an earlier web app showed per piece (its per-component explorer), computed once
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
        /// Present only for pieces pushed from the web app's Revit families
        /// tab — the user's OWN loaded content rather than one of the app's
        /// built-in catalog presets. When set it names the exact family and
        /// type to place, so there is nothing to match or guess: quality_key
        /// matching and family generation are both bypassed.
        /// </summary>
        [JsonPropertyName("revit_family")] public RevitFamilyRefDto? RevitFamily { get; set; }

        /// <summary>
        /// Present when the placement is a padel court. Like RevitFamily it
        /// short-circuits the generic path — a padel court is not an extrusion
        /// of its footprint, it is a specified object with glass, mesh and a net.
        /// </summary>
        [JsonPropertyName("padel")] public PadelDto? Padel { get; set; }

        /// <summary>Present when the placement is a basketball court.</summary>
        [JsonPropertyName("basketball")] public BasketballDto? Basketball { get; set; }

        /// <summary>Present when the placement is a volleyball court.</summary>
        [JsonPropertyName("volleyball")] public VolleyballDto? Volleyball { get; set; }

        /// <summary>
        /// Present for placed plants. A tree is a family, not a build-up — this
        /// is what a family gets generated from, one per species.
        /// </summary>
        [JsonPropertyName("vegetation")] public VegetationDto? Vegetation { get; set; }

        /// <summary>Present for placed furniture (a bench, a table, a bin, a bollard, a light): the catalogue product, its size and weight. See SportifyFurnitureFamilyBuilder.</summary>
        [JsonPropertyName("furniture")] public FurnitureDto? Furniture { get; set; }

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
    /// Revit families tab.
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

    /// <summary>
    /// The surface everything else sits in — what is left of the roof once the
    /// courts and the planted zones are taken out. One floor with holes in it,
    /// not a slab underneath the others: coplanar, nothing overlapping, which
    /// is how the exposed surface would be drawn by hand.
    /// </summary>
    /// <summary>
    /// A padel court's specification, as configured. The FIP geometry travels
    /// with the placement rather than being re-derived here: a court saved last
    /// month should rebuild as the court it was, even if the rules are revised.
    /// </summary>
    internal class PadelDto
    {
        [JsonPropertyName("court_type")] public string? CourtType { get; set; }
        [JsonPropertyName("wall_system")] public string? WallSystem { get; set; }
        [JsonPropertyName("surface")] public string? Surface { get; set; }
        [JsonPropertyName("surface_colour")] public string? SurfaceColour { get; set; }
        [JsonPropertyName("appearance_hex")] public string? AppearanceHex { get; set; }

        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }

        [JsonPropertyName("net_centre_height_m")] public double NetCentreHeightM { get; set; }
        [JsonPropertyName("net_post_height_m")] public double NetPostHeightM { get; set; }
        [JsonPropertyName("service_line_from_net_m")] public double ServiceLineFromNetM { get; set; }

        [JsonPropertyName("back_wall_glass_height_m")] public double BackWallGlassHeightM { get; set; }
        [JsonPropertyName("back_wall_mesh_height_m")] public double BackWallMeshHeightM { get; set; }
        [JsonPropertyName("side_corner_glass")] public PadelPanelDto? SideCornerGlass { get; set; }
        [JsonPropertyName("side_step_glass")] public PadelPanelDto? SideStepGlass { get; set; }
        [JsonPropertyName("side_centre_mesh_height_m")] public double SideCentreMeshHeightM { get; set; }
        [JsonPropertyName("glass_thickness_mm")] public double GlassThicknessMm { get; set; }

        [JsonPropertyName("clear_height_min_m")] public double ClearHeightMinM { get; set; }
        [JsonPropertyName("clear_height_recommended_m")] public double ClearHeightRecommendedM { get; set; }

        [JsonPropertyName("weight_kg")] public double WeightKg { get; set; }
        [JsonPropertyName("weight_kg_m2")] public double WeightKgM2 { get; set; }
        [JsonPropertyName("weight_basis")] public string? WeightBasis { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    /// <summary>
    /// A basketball court's specification, as configured. Like padel's, the
    /// figures travel with the placement so a court rebuilds as the court it
    /// was — the importer holds no opinion about what FIBA currently says.
    /// </summary>
    internal class BasketballDto
    {
        [JsonPropertyName("variant")] public string? Variant { get; set; }
        [JsonPropertyName("hoops")] public int Hoops { get; set; }
        [JsonPropertyName("mounting")] public string? Mounting { get; set; }
        [JsonPropertyName("surface")] public string? Surface { get; set; }
        [JsonPropertyName("court_colour")] public string? CourtColour { get; set; }
        [JsonPropertyName("key_colour")] public string? KeyColour { get; set; }
        [JsonPropertyName("appearance_hex")] public string? AppearanceHex { get; set; }
        [JsonPropertyName("key_fill_hex")] public string? KeyFillHex { get; set; }

        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("play_length_m")] public double PlayLengthM { get; set; }
        [JsonPropertyName("play_width_m")] public double PlayWidthM { get; set; }

        [JsonPropertyName("rim_height_m")] public double RimHeightM { get; set; }
        [JsonPropertyName("rim_inner_diameter_m")] public double RimInnerDiameterM { get; set; }
        [JsonPropertyName("basket_centre_from_endline_m")] public double BasketCentreFromEndlineM { get; set; }
        [JsonPropertyName("backboard_face_from_endline_m")] public double BackboardFaceFromEndlineM { get; set; }
        [JsonPropertyName("backboard_width_m")] public double BackboardWidthM { get; set; }
        [JsonPropertyName("backboard_height_m")] public double BackboardHeightM { get; set; }
        [JsonPropertyName("backboard_lower_edge_m")] public double BackboardLowerEdgeM { get; set; }

        [JsonPropertyName("key_width_m")] public double KeyWidthM { get; set; }
        [JsonPropertyName("key_depth_m")] public double KeyDepthM { get; set; }
        [JsonPropertyName("centre_circle_radius_m")] public double CentreCircleRadiusM { get; set; }
        [JsonPropertyName("free_throw_circle_radius_m")] public double FreeThrowCircleRadiusM { get; set; }
        [JsonPropertyName("no_charge_radius_m")] public double NoChargeRadiusM { get; set; }
        [JsonPropertyName("three_point_radius_m")] public double ThreePointRadiusM { get; set; }
        [JsonPropertyName("three_point_corner_from_sideline_m")] public double ThreePointCornerFromSidelineM { get; set; }

        [JsonPropertyName("clear_height_min_m")] public double ClearHeightMinM { get; set; }
        [JsonPropertyName("weight_kg")] public double WeightKg { get; set; }
        [JsonPropertyName("weight_kg_m2")] public double WeightKgM2 { get; set; }
        [JsonPropertyName("weight_basis")] public string? WeightBasis { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    /// <summary>
    /// A volleyball court. The free zone is carried because it is part of the
    /// court rather than a margin — play happens out there and it is the same
    /// surface — and the sand is carried separately because on a roof it is
    /// the whole question.
    /// </summary>
    internal class VolleyballDto
    {
        [JsonPropertyName("variant")] public string? Variant { get; set; }
        [JsonPropertyName("play_type")] public string? PlayType { get; set; }
        [JsonPropertyName("net_height_m")] public double NetHeightM { get; set; }
        [JsonPropertyName("surface")] public string? Surface { get; set; }
        [JsonPropertyName("court_colour")] public string? CourtColour { get; set; }
        [JsonPropertyName("appearance_hex")] public string? AppearanceHex { get; set; }

        [JsonPropertyName("court_length_m")] public double CourtLengthM { get; set; }
        [JsonPropertyName("court_width_m")] public double CourtWidthM { get; set; }
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("free_zone_sides_m")] public double FreeZoneSidesM { get; set; }
        [JsonPropertyName("free_zone_ends_m")] public double FreeZoneEndsM { get; set; }

        /// <summary>Zero on a beach court, which has no attack line.</summary>
        [JsonPropertyName("attack_line_from_centre_m")] public double AttackLineFromCentreM { get; set; }
        [JsonPropertyName("net_depth_m")] public double NetDepthM { get; set; }
        [JsonPropertyName("post_outside_sideline_m")] public double PostOutsideSidelineM { get; set; }
        [JsonPropertyName("post_height_m")] public double PostHeightM { get; set; }
        [JsonPropertyName("antenna_length_m")] public double AntennaLengthM { get; set; }
        [JsonPropertyName("antenna_above_net_m")] public double AntennaAboveNetM { get; set; }

        [JsonPropertyName("clear_height_min_m")] public double ClearHeightMinM { get; set; }
        [JsonPropertyName("sand_depth_m")] public double SandDepthM { get; set; }
        [JsonPropertyName("sand_volume_m3")] public double SandVolumeM3 { get; set; }

        [JsonPropertyName("weight_kg")] public double WeightKg { get; set; }
        [JsonPropertyName("weight_kg_m2")] public double WeightKgM2 { get; set; }
        [JsonPropertyName("weight_basis")] public string? WeightBasis { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    internal class PadelPanelDto
    {
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
    }

    /// <summary>A catalogue product of site furniture, as the web app exports it (parameters.furniture).</summary>
    internal class FurnitureDto
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
        [JsonPropertyName("product")] public string? Product { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        /// <summary>"bench" | "table" | "bin" | "bollard" | "light" (or another the catalogue grows).</summary>
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("dimensions_published")] public bool DimensionsPublished { get; set; }
        [JsonPropertyName("seats")] public int Seats { get; set; }
        /// <summary>The product's weight, kg; null when the catalogue has none. What the structural and dynamic analyses put on the deck.</summary>
        [JsonPropertyName("weight_kg")] public double? WeightKg { get; set; }
        [JsonPropertyName("weight_published")] public bool WeightPublished { get; set; }
        [JsonPropertyName("capacity_l")] public double? CapacityL { get; set; }
        [JsonPropertyName("material")] public string? Material { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
        [JsonPropertyName("revit_family_name")] public string? RevitFamilyName { get; set; }
    }

    internal class RoofFinishDto
    {
        [JsonPropertyName("assembly_key")] public string? AssemblyKey { get; set; }
        [JsonPropertyName("revit_type_name")] public string? RevitTypeName { get; set; }
        [JsonPropertyName("net_area_m2")] public double NetAreaM2 { get; set; }
        [JsonPropertyName("roof_area_m2")] public double RoofAreaM2 { get; set; }

        /// <summary>Every zone and court, as a hole in the finish.</summary>
        [JsonPropertyName("openings")] public List<OpeningDto>? Openings { get; set; }
    }

    internal class OpeningDto
    {
        [JsonPropertyName("source")] public string? Source { get; set; }   // "zone" | "piece"
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("length_m")] public double LengthM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }

        /// <summary>The hole's real outline, plan coordinates like x_m / y_m, when it is not a rectangle (a zone whose corners were moved). x_m..width_m stay its bounding box for a reader that predates this.</summary>
        [JsonPropertyName("points")] public List<PointDto>? Points { get; set; }
    }

    internal class ZoneDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("bounding_box")] public BoundingBoxDto? BoundingBox { get; set; }
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("assembly_key")] public string? AssemblyKey { get; set; }

        /// <summary>
        /// The zone's real outline. A bed is not a rectangle once its corners
        /// can be moved, and bounding_box would draw the floor as the box it
        /// happens to fit inside. Absent on exports from before zones had
        /// corners, which is why the box is still carried.
        /// </summary>
        [JsonPropertyName("points")] public List<PointDto>? Points { get; set; }
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

    /// <summary>
    /// One plant, as a species rather than a size category: Cornus mas, not
    /// "small tree". Dimensions come with the species because they are
    /// properties of the plant.
    /// </summary>
    internal class VegetationDto
    {
        [JsonPropertyName("species_key")] public string? SpeciesKey { get; set; }
        [JsonPropertyName("botanical_name")] public string? BotanicalName { get; set; }
        [JsonPropertyName("common_name")] public string? CommonName { get; set; }
        [JsonPropertyName("form")] public string? Form { get; set; }
        [JsonPropertyName("crown_m")] public double CrownM { get; set; }

        /// <summary>
        /// Mature height. Carried specifically so a shading, wind or clearance
        /// study has it — none of that can be recovered from a plan footprint.
        /// </summary>
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("height_range")] public string? HeightRange { get; set; }

        /// <summary>What decides whether the roof build-up can actually carry this planting.</summary>
        [JsonPropertyName("min_substrate_mm")] public double MinSubstrateMm { get; set; }

        [JsonPropertyName("revit_family_name")] public string? RevitFamilyName { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
        [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
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
        [JsonPropertyName("layer_name")] public string? LayerName { get; set; }
        [JsonPropertyName("material")] public string? Material { get; set; }
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

    // ---- what the Revit model says about the roof besides its outline and structure (see RoofFeaturesGeometry.cs, ROOF_FEATURES.md)

    internal class RoofFeaturesDto
    {
        /// <summary>"revit". A hand-made roof has no features.</summary>
        [JsonPropertyName("source")] public string Source { get; set; } = "revit";

        /// <summary>What could not be read or was approximated, in words, for the designer.</summary>
        [JsonPropertyName("notes")] public List<string> Notes { get; set; } = new List<string>();

        [JsonPropertyName("openings")] public List<RoofOpeningDto> Openings { get; set; } = new List<RoofOpeningDto>();
        [JsonPropertyName("entries")] public List<RoofEntryDto> Entries { get; set; } = new List<RoofEntryDto>();
        [JsonPropertyName("edges")] public List<RoofEdgeDto> Edges { get; set; } = new List<RoofEdgeDto>();
        [JsonPropertyName("drains")] public List<RoofDrainDto> Drains { get; set; } = new List<RoofDrainDto>();

        /// <summary>Walls standing on the roof (parapets, screens, the walls of a stair house or plant room): what casts shadows on it.</summary>
        [JsonPropertyName("obstacles")] public List<RoofObstacleDto> Obstacles { get; set; } = new List<RoofObstacleDto>();

        /// <summary>Plant and equipment standing on the roof (mechanical, electrical): footprint, height and, where the model has it, weight.</summary>
        [JsonPropertyName("equipment")] public List<RoofEquipmentDto> Equipment { get; set; } = new List<RoofEquipmentDto>();
        [JsonPropertyName("slab")] public RoofSlabDto? Slab { get; set; }
        [JsonPropertyName("levels")] public List<RoofLevelDto> Levels { get; set; } = new List<RoofLevelDto>();
    }

    /// <summary>A hole in the roof's top face (a skylight, a shaft, a rooflight): nothing may stand there.</summary>
    internal class RoofOpeningDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("polygon_m")] public List<PointDto> PolygonM { get; set; } = new List<PointDto>();
        [JsonPropertyName("area_m2")] public double AreaM2 { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
    }

    /// <summary>A stair, a core (lift) or a door by which people reach the roof.</summary>
    internal class RoofEntryDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";

        /// <summary>"stair" | "core" | "door" | "ramp".</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }

        /// <summary>Clear width of a door, m; 0 when the model does not say.</summary>
        [JsonPropertyName("width_m")] public double WidthM { get; set; }

        /// <summary>True when the position lies on the roof; false for a stair house or door just beyond its edge.</summary>
        [JsonPropertyName("on_roof")] public bool OnRoof { get; set; }
        [JsonPropertyName("source_element_id")] public long SourceElementId { get; set; }
    }

    /// <summary>One straight stretch of the roof's outline and what stands along it.</summary>
    internal class RoofEdgeDto
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("start_m")] public PointDto StartM { get; set; } = new PointDto();
        [JsonPropertyName("end_m")] public PointDto EndM { get; set; } = new PointDto();
        [JsonPropertyName("length_m")] public double LengthM { get; set; }

        /// <summary>"parapet" | "railing" | "partial" (some of it covered) | "open" (nothing stops a fall or a ball).</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "open";

        /// <summary>Height above the roof's top face, m, of what stands along the edge (a length-weighted mean); 0 when open.</summary>
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }

        /// <summary>The share (0 to 1) of the edge's length covered by a parapet / by a railing.</summary>
        [JsonPropertyName("parapet_coverage")] public double ParapetCoverage { get; set; }
        [JsonPropertyName("railing_coverage")] public double RailingCoverage { get; set; }
    }

    /// <summary>A wall standing on the roof, as its centre line, thickness and height above the roof's top face.</summary>
    internal class RoofObstacleDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("start_m")] public PointDto StartM { get; set; } = new PointDto();
        [JsonPropertyName("end_m")] public PointDto EndM { get; set; } = new PointDto();
        [JsonPropertyName("height_m")] public double HeightM { get; set; }
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }
    }

    internal class RoofDrainDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";

        /// <summary>"drain" | "overflow" | "scupper", from the family's name.</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "drain";
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("source_element_id")] public long SourceElementId { get; set; }
    }

    /// <summary>The build-up of the roof or floor slab that was pushed.</summary>
    /// <summary>A piece of plant on the roof (an air handler, a chiller, a switchboard): a box in the plan.</summary>
    internal class RoofEquipmentDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";

        /// <summary>"mechanical" | "electrical".</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "mechanical";
        [JsonPropertyName("name")] public string? Name { get; set; }

        /// <summary>The middle of its footprint in the plan, metres.</summary>
        [JsonPropertyName("x_m")] public double XM { get; set; }
        [JsonPropertyName("y_m")] public double YM { get; set; }
        [JsonPropertyName("width_m")] public double WidthM { get; set; }
        [JsonPropertyName("depth_m")] public double DepthM { get; set; }
        [JsonPropertyName("height_m")] public double HeightM { get; set; }

        /// <summary>Its weight in kN when the family carries a weight or mass parameter; absent when the model does not say.</summary>
        [JsonPropertyName("weight_kn")] public double? WeightKn { get; set; }
        [JsonPropertyName("source_element_id")] public long SourceElementId { get; set; }
    }

    internal class RoofSlabDto
    {
        [JsonPropertyName("type_name")] public string? TypeName { get; set; }
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }

        /// <summary>The layers whose function is structure or structural deck: the depth that carries load.</summary>
        [JsonPropertyName("structural_thickness_m")] public double StructuralThicknessM { get; set; }
        [JsonPropertyName("layers")] public List<RoofSlabLayerDto> Layers { get; set; } = new List<RoofSlabLayerDto>();
    }

    internal class RoofSlabLayerDto
    {
        /// <summary>Revit's material function: Structure, Substrate, Insulation, Finish1, Finish2, Membrane, StructuralDeck.</summary>
        [JsonPropertyName("function")] public string? Function { get; set; }
        [JsonPropertyName("material")] public string? Material { get; set; }
        [JsonPropertyName("thickness_m")] public double ThicknessM { get; set; }
    }

    internal class RoofLevelDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        /// <summary>Elevation in the project's own coordinates, m.</summary>
        [JsonPropertyName("elevation_m")] public double ElevationM { get; set; }

        /// <summary>Elevation above the ground the roof height was measured from; null when no ground was found.</summary>
        [JsonPropertyName("above_ground_m")] public double? AboveGroundM { get; set; }

        /// <summary>The level the roof sits on (the highest at or below its top face).</summary>
        [JsonPropertyName("is_roof_level")] public bool IsRoofLevel { get; set; }
    }

    /// <summary>
    /// One of compareController.js's savedCompareConfigs, exactly as the web app's "Send to Revit as Design Options" button POSTs the whole array
    /// to /iterations: a full layout snapshot (same shape as a combined-layout export) plus the name and tagline the web app already gives it.
    /// </summary>
    internal class SavedIterationDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("tagline")] public string? Tagline { get; set; }
        [JsonPropertyName("payload")] public SportifyLayout? Payload { get; set; }
    }
}
