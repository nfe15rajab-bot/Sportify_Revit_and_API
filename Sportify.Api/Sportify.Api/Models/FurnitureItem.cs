namespace Sportify.Api.Models
{
    /// <summary>
    /// A piece of site furniture — a bench, a bin, a bollard, a light.
    ///
    /// Unlike a sport, none of this belongs in code. A padel court is 20 x 10 m
    /// because the FIP says so, whoever builds it; a bench is 1,800 mm because
    /// the manufacturer made it that way. There is no standard to encode, so
    /// the whole thing is catalog.
    ///
    /// Which also means the honesty rules matter more here, not less: a product
    /// name without a source is a claim, and a dimension nobody published is a
    /// guess. Both are marked.
    /// </summary>
    public class FurnitureItem
    {
        public int Id { get; set; }

        /// <summary>Stable key the export and the Revit family reference, e.g. "abes_parkbank_1114".</summary>
        public string Key { get; set; } = string.Empty;

        public string Manufacturer { get; set; } = string.Empty;
        public string? ManufacturerCountry { get; set; }

        /// <summary>The manufacturer's own product name, as they write it.</summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>bench | table | bin | bollard | light — what it is, and how it draws.</summary>
        public string Category { get; set; } = string.Empty;

        public string? Description { get; set; }
        public string? Material { get; set; }

        // ── Size ──
        public double LengthM { get; set; }
        public double WidthM { get; set; }
        public double HeightM { get; set; }

        /// <summary>
        /// True when these came off the manufacturer's own datasheet. False
        /// means they are typical for the product type and want checking — the
        /// same distinction the build-up thicknesses draw between published and
        /// typical, for the same reason.
        /// </summary>
        public bool DimensionsPublished { get; set; }

        // ── What it does and what it weighs ──

        /// <summary>
        /// How many people can sit on it. Zero for a bin or a bollard.
        ///
        /// Worth carrying because "how many people can sit up here" is one of
        /// the first questions a client asks, and it falls straight out of the
        /// furniture — nobody has to count benches by hand.
        /// </summary>
        public int Seats { get; set; }

        public double? WeightKg { get; set; }
        public bool WeightPublished { get; set; }

        /// <summary>Litres, for a bin. Null for anything else.</summary>
        public double? CapacityLitres { get; set; }

        // ── Cost, in the same shape as the rest of the catalog ──
        public double? PriceValue { get; set; }
        public string? PriceUnit { get; set; }          // "EUR/each"
        public string? PriceSource { get; set; }
        public bool PriceIsQuoted { get; set; }

        /// <summary>
        /// DIN 276. Site furniture is 560 — Einbauten in Außenanlagen. A
        /// bollard light is 550, Technische Anlagen in Außenanlagen, because it
        /// is an electrical installation rather than a fitting.
        /// </summary>
        public string? CostGroupDin276 { get; set; }

        public string? SourceUrl { get; set; }
    }
}
