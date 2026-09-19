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

        // ── Cost ──
        // Same posture as the embodied-carbon fields above and the published
        // /typical rule the build-up thicknesses already follow: a price is
        // either QUOTED by a supplier or ESTIMATED, and which one it is has to
        // travel with the number. Nullable, so a missing price stays visibly
        // missing instead of costing nothing.
        //
        // PriceUnit also decides how the quantity is measured — "EUR/m3" means
        // the layer is taken off by volume, "EUR/m2" by area. One field for
        // both so the two can never disagree.
        public double? PriceValue { get; set; }
        public string? PriceUnit { get; set; }        // "EUR/m2" | "EUR/m3" | "EUR/each"
        public string? PriceSource { get; set; }
        public bool PriceIsQuoted { get; set; }

        /// <summary>
        /// DIN 276 cost group, e.g. "570" for planted areas or "363" for roof
        /// coverings. A German cost estimate has to arrive sorted into these;
        /// a bare total cannot be compared, benchmarked, or used to derive a
        /// fee. TO BE CONFIRMED against a Kostenplaner — these are reasonable
        /// classifications, not authoritative ones.
        /// </summary>
        public string? CostGroupDin276 { get; set; }


        [JsonIgnore] public List<Sport> Sports { get; set; } = new();
        [JsonIgnore] public List<PlantPalette> Palettes { get; set; } = new();
    }
}
