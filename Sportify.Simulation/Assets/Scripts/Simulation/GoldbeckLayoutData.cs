using System;

namespace Sportify.Simulation
{
    [Serializable]
    public class RoofContext
    {
        public float length_m;
        public float width_m;

        // Height of the roof surface in the Revit project; 0 when the roof was typed in by hand.
        public float world_origin_z_m;

        // Height of the roof above the ground (Revit model, or typed in the Site tab); 0 when not known.
        public float height_above_ground_m;
        public string height_source;

        // The roof's outline (Revit's convention: y up from the roof's minimum corner) and what the model says about it; absent for a roof typed in by hand.
        public PolyPointData[] source_boundary_polygon;
        public RoofFeaturesData features;
    }

    [Serializable]
    public class PolyPointData
    {
        public float x_m;
        public float y_m;
    }

    [Serializable]
    public class RoofObstacleData
    {
        public string name;
        public PolyPointData start_m;
        public PolyPointData end_m;
        public float height_m;
        public float thickness_m;
    }

    [Serializable]
    public class RoofDrainData
    {
        public float x_m;
        public float y_m;
    }

    [Serializable]
    public class RoofOpeningData
    {
        public PolyPointData[] polygon_m;
    }

    /// <summary>The parts of roof_context.features the sun and shade analysis reads (the rest is for the web app).</summary>
    [Serializable]
    public class RoofFeaturesData
    {
        public RoofObstacleData[] obstacles;
        public RoofDrainData[] drains;
        public RoofOpeningData[] openings;
        public RoofSlabData slab;
        public RoofEquipmentData[] equipment;
    }

    [Serializable]
    public class RoofEquipmentData
    {
        public string name;
        public float x_m;
        public float y_m;
        public float width_m;
        public float depth_m;
        public float height_m;
    }

    /// <summary>The slab of the pushed roof: its structural thickness is what the deck's resonance estimate needs.</summary>
    [Serializable]
    public class RoofSlabData
    {
        public float thickness_m;
        public float structural_thickness_m;
    }

    [Serializable]
    public class SiteLocationData
    {
        public float latitude_deg;
        public float longitude_deg;
    }

    /// <summary>
    /// What the wind analysis needs to know about the site. JsonUtility reads a JSON null as 0, so "unknown" is 0 for
    /// wind_zone, and the orientation carries its own north_set flag (0 degrees is a real answer).
    /// </summary>
    [Serializable]
    public class SiteConditions
    {
        public int wind_zone;
        public bool wind_zone_manual;
        public string wind_zone_confidence;
        public string wind_zone_source;
        public bool north_set;
        public float north_deg;
        public string snow_zone;          // "1", "1a", "2", "2a", "3" or "" when not given
        public bool altitude_set;
        public float altitude_m;
        public string day_schedule;
    }

    [Serializable]
    public class DesignRules
    {
        public float clearance_m;
        public float boundary_setback_m;
        public float circulation_width_m;
        public int min_entry_points;
        public float quiet_buffer_m;
    }

    [Serializable]
    public class EntryPoint
    {
        public float x_m;
        public float y_m;
        public string edge;
    }

    [Serializable]
    public class BoundingBox
    {
        public float top_left_x_m;
        public float top_left_y_m;
        public float width_m;
        public float height_m;
    }

    [Serializable]
    public class Transform2D
    {
        public float rotation_deg;
    }

    [Serializable]
    public class FieldDimensions
    {
        public float length_m;
        public float width_m;
        public float runoff_m;
        public float min_height_m;
    }

    [Serializable]
    public class FieldInfo
    {
        public string sport;
        public string variant;
        public string norm;
        public FieldDimensions dimensions;
        public FieldCapacity capacity;
    }

    [Serializable]
    public class FieldCapacity
    {
        public int seats;
    }

    [Serializable]
    public class PointM
    {
        public float x_m;
        public float y_m;
    }

    [Serializable]
    public class CirculationPathData
    {
        public PointM[] points_m;
    }

    /// <summary>A grid line of the roof's structure: a segment in the roof's own plan coordinates (x right, y down), named as in the Revit model.</summary>
    [Serializable]
    public class GridLineData
    {
        public string name;
        public PointM start_m;
        public PointM end_m;
    }

    [Serializable]
    public class StructuralColumnData
    {
        public string label;
        public float x_m;
        public float y_m;
    }

    /// <summary>The roof's structural grid and columns, and the deck capacity when the engineer's figure has been entered (0 = not given).</summary>
    [Serializable]
    public class StructureData
    {
        public string source;
        public float deck_capacity_kn_m2;
        public float natural_frequency_hz;   // 0 = not given
        public GridLineData[] grid_lines;
        public StructuralColumnData[] columns;
    }

    [Serializable]
    public class VegetationData
    {
        public string species_key;
        public string botanical_name;
        public string common_name;
        public string form;
        public float crown_m;
        public float height_m;
        public float min_substrate_mm;
    }

    [Serializable]
    public class AssemblyLayerData
    {
        public int order;
        public string name;
        public string function;
        public float thickness_m;
    }

    /// <summary>A provider build-up. A figure the provider does not publish arrives as null, which JsonUtility reads as 0.</summary>
    [Serializable]
    public class AssemblyData
    {
        public string key;
        public string provider;
        public string system_name;
        public string category;
        public float saturated_kg_m2;
        public float water_storage_l_m2;
        public float total_thickness_m;
        public AssemblyLayerData[] layers;
    }

    /// <summary>A layer of an older-format garden parcel, which names its layers instead of giving them a function.</summary>
    [Serializable]
    public class GardenLayerData
    {
        public string layer_name;
        public string material;
        public float thickness_m;
    }

    [Serializable]
    public class GardenParameters
    {
        public AssemblyData assembly;
        public GardenLayerData[] layers;
    }

    [Serializable]
    public class ZoneData
    {
        public string id;
        public string kind;
        public string label;
        public BoundingBox bounding_box;
        public string assembly_key;

        /// <summary>The zone's real outline (plan metres), when it is not its bounding box: a bed whose corners were moved. Absent in older exports.</summary>
        public PointM[] points;
    }

    /// <summary>A catalogue product of site furniture (parameters.furniture). weight_kg is 0 when the catalogue has none.</summary>
    [Serializable]
    public class FurnitureData
    {
        public string key;
        public string label;
        public string category;
        public float length_m;
        public float width_m;
        public float height_m;
        public float weight_kg;
    }

    [Serializable]
    public class PlacementParameters
    {
        public FieldInfo field;
        public VegetationData vegetation;
        public GardenParameters garden;
        public FurnitureData furniture;
    }

    [Serializable]
    public class Placement
    {
        public string id;
        public string category;
        public string label;
        public BoundingBox bounding_box;
        public Transform2D transform;
        public PlacementParameters parameters;
    }

    [Serializable]
    public class GoldbeckPayload
    {
        public string version;
        public RoofContext roof_context;
        public DesignRules design_rules;
        public EntryPoint[] entry_points;
        public CirculationPathData[] circulation_paths;
        public Placement[] placements;

        // Green-roof ground zones and the build-ups they use (newer exports only).
        public ZoneData[] zones;
        public AssemblyData[] assemblies;

        public SiteConditions site_conditions;
        public SiteLocationData site_location;

        // The structural grid and columns (pushed from Revit or drawn in the app); absent in older exports.
        public StructureData structure;

        // Which built-in analysis assumptions the designer accepted, and the comfort limits they set; absent in older exports.
        public AnalysisAssumptionsData analysis_assumptions;
    }

    /// <summary>What the designer decided about the analyses' built-in assumptions (see Structure/AnalysisAssumptions.cs). Limits: 0 = not set.</summary>
    [Serializable]
    public class AnalysisAssumptionsData
    {
        public string[] accepted;
        public float comfort_limit_walking_g;
        public float comfort_limit_rhythmic_g;

        // For the sun and shade analysis (0 / empty = not set)
        public float shade_target_percent;
        public float garden_min_sun_hours;
        public string shade_equipment;
        public float site_latitude_deg;
    }
}
