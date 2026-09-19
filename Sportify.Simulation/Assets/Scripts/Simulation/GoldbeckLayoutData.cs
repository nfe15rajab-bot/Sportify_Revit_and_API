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
    }

    [Serializable]
    public class PlacementParameters
    {
        public FieldInfo field;
        public VegetationData vegetation;
        public GardenParameters garden;
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
        public Placement[] placements;

        // Green-roof ground zones and the build-ups they use (newer exports only).
        public ZoneData[] zones;
        public AssemblyData[] assemblies;

        public SiteConditions site_conditions;
    }
}
