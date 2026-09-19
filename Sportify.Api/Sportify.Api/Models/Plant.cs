using System.Text.Json.Serialization;

namespace Sportify.Api.Models
{
    public class Plant
    {
        public int Id { get; set; }
        public string CommonName { get; set; } = string.Empty;
        public string ScientificName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string SunRequirement { get; set; } = string.Empty;
        public string DroughtTolerance { get; set; } = string.Empty;

        // ── Roof planting dimensions ──
        // A species has a size; "small tree" does not. These are what decide
        // whether a plant fits on a roof at all, and none of them can be
        // recovered from a plan drawing afterwards.

        /// <summary>tree | shrub | grass | groundcover — how it is drawn and whether it has a trunk.</summary>
        public string? Form { get; set; }

        /// <summary>Mature height in metres. Needed by any shading, wind or clearance study.</summary>
        public double? MatureHeightM { get; set; }

        /// <summary>Human-readable range as the nursery publishes it, e.g. "5-6 m".</summary>
        public string? HeightRange { get; set; }

        /// <summary>Typical mature crown diameter in metres — the footprint on the roof.</summary>
        public double? CrownM { get; set; }

        /// <summary>The size range a nursery actually supplies it at.</summary>
        public double? CrownMinM { get; set; }
        public double? CrownMaxM { get; set; }

        /// <summary>
        /// Root depth the build-up must provide. This is the figure that
        /// connects a plant to the green roof beneath it: a tree needing 800 mm
        /// on a 100 mm extensive roof is not a judgement call, it is wrong.
        /// </summary>
        public double? MinSubstrateMm { get; set; }

        public string? Notes { get; set; }

        /// <summary>Who published the dimensions, and where — so a figure can be checked.</summary>
        public string? Source { get; set; }
        public string? SourceUrl { get; set; }

        /// <summary>False where a dimension is a horticultural norm rather than a published nursery figure.</summary>
        public bool DimensionsPublished { get; set; }

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


        [JsonIgnore] public List<PlantPalette> Palettes { get; set; } = new();
    }
}
