using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    public class Material
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

        // EN 14904 (indoor multi-use sports floors) classifies surfaces by
        // force reduction — how much impact energy the floor absorbs vs a
        // rigid reference. Outdoor turf isn't covered by EN 14904 at all
        // (EN 15330 instead), so these are null for it.
        public string? NormCode { get; set; } // e.g. "EN 14904"
        public string? PerformanceClass { get; set; } // e.g. "Type 1 — area-elastic"
        public string? ForceReduction { get; set; } // e.g. "≥55%"
        public string? Notes { get; set; }

        // LCA / embodied-carbon reference data, feeding the Analysis tab's
        // LCA card. Deliberately nullable and left unset for most seeded
        // materials — a real per-m² cradle-to-gate figure wasn't found for
        // every material researched, and this project's standing rule is
        // to leave a gap visibly missing rather than invent a plausible-
        // looking number. Fill gaps via the Data tab's admin edit form.
        public double? EmbodiedCarbonValue { get; set; } // e.g. 9.1
        public string? EmbodiedCarbonUnit { get; set; } // e.g. "kg CO2e/m2"
        public string? EmbodiedCarbonSource { get; set; }

        [JsonIgnore] public List<Sport> Sports { get; set; } = new();
        [JsonIgnore] public List<PlantPalette> Palettes { get; set; } = new();
    }
}
