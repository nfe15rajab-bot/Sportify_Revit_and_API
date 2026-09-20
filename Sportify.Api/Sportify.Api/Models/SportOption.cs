namespace Sportify.Api.Models
{
    /// <summary>
    /// A choice a sport offers — a padel wall system, a court surface, a basket
    /// mounting — with what choosing it costs and weighs.
    ///
    /// The split this table exists to hold: a padel court is 20 x 10 m because
    /// the FIP says so, and that belongs in code the same way "a membrane layer
    /// must be zero thickness" does. But WHICH surface, WHICH wall system, is a
    /// product decision, and products belong in a catalog someone can maintain
    /// without a developer. These started life as constants in the web app,
    /// which meant adding a fourth surface meant changing JavaScript.
    ///
    /// Deliberately not one table per sport. The groups differ (padel has a
    /// wall system, basketball has a basket mounting) but the shape does not:
    /// a key, a label, what it weighs, what it costs. A sport added later needs
    /// rows here, not a migration.
    /// </summary>
    public class SportOption
    {
        public int Id { get; set; }

        /// <summary>"padel", "basketball" — matches the web app's own sport key.</summary>
        public string Sport { get; set; } = string.Empty;

        /// <summary>"surface", "wall_system", "mounting", "surface_colour", "court_type".</summary>
        public string OptionGroup { get; set; } = string.Empty;

        /// <summary>Stable key the export and the geometry reference, e.g. "artificial_grass".</summary>
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        /// <summary>The sentence shown under the picker — what choosing this actually means.</summary>
        public string? Note { get; set; }

        public int SortOrder { get; set; }

        // ── What the choice changes ──
        // Nullable because a colour has no weight and a basket has no area. A
        // figure that does not apply stays absent rather than zero, which is a
        // different claim.

        /// <summary>For anything measured by area — a playing surface.</summary>
        public double? WeightKgM2 { get; set; }

        /// <summary>For anything counted — a basket, a net post.</summary>
        public double? WeightKgEach { get; set; }

        /// <summary>Glass thickness on a padel wall system, and anything like it.</summary>
        public double? ThicknessMm { get; set; }

        /// <summary>For a colour choice, and for the Revit material that follows it.</summary>
        public string? ColourHex { get; set; }

        /// <summary>
        /// How the surface is drawn and rendered: pile, speckle, sheen, tiles,
        /// flat. Turf, concrete and acrylic are not one material in three
        /// colours, and a flat fill would say they were interchangeable.
        /// </summary>
        public string? TextureHint { get; set; }

        // ── Cost, in the same shape as everything else in this catalog ──
        public double? PriceValue { get; set; }
        public string? PriceUnit { get; set; }        // "EUR/m2" | "EUR/each"
        public string? PriceSource { get; set; }
        public bool PriceIsQuoted { get; set; }
        public string? CostGroupDin276 { get; set; }
    }
}
