using System;

namespace Sportify.Simulation
{
    [Serializable]
    public class RoofContext
    {
        public float length_m;
        public float width_m;
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
    public class PlacementParameters
    {
        public FieldInfo field;
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
    }
}
