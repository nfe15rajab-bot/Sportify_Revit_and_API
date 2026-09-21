namespace SportfyRevit
{
    /// <summary>
    /// The ribbon's icons as vector art on a 24 x 24 grid. Each icon is a soft tinted shape (Fill) under a 2-unit outline (Stroke), both in the colour of the panel the icon belongs to,
    /// so the panels can be told apart at a glance: setup and Push (slate), algorithmic analysis (blue), physical analysis (teal), BIM & documentation (amber), export (violet).
    /// The Sportify mark ("app") is a badge: a filled rounded square with a white court on it. RibbonIcons draws these at 32 x 32 (large buttons and drop-downs) and 16 x 16 (the items
    /// of a drop-down). No Revit or WPF types here so that Tools/AddinCheck can check the table. A path is the SVG path mini-language (M, L, H, V, C, A, Z and their lowercase forms).
    /// </summary>
    internal static class RibbonIconData
    {
        internal sealed record IconSpec(string Group, string Stroke, string? Fill = null, bool Badge = false);

        /// <summary>Colour of each group (R, G, B).</summary>
        public static readonly IReadOnlyDictionary<string, (byte R, byte G, byte B)> GroupColors = new Dictionary<string, (byte, byte, byte)>
        {
            ["setup"] = (0x5A, 0x76, 0x9C),
            ["algorithmic"] = (0x2F, 0x6F, 0xA8),
            ["physical"] = (0x1B, 0x8A, 0x78),
            ["bim"] = (0xD9, 0x7E, 0x1F),
            ["export"] = (0x7A, 0x5C, 0xB8),
        };

        private const string Cloud = "M7 17a4 4 0 0 1 -.5 -7.9a5 5 0 0 1 9.6 -1.1a4 4 0 0 1 1.4 9z";
        private const string SunBody = "M12 8a4 4 0 1 1 0 8a4 4 0 1 1 0 -8";
        private const string SunRays = " M12 2v2 M12 20v2 M2 12h2 M20 12h2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M4.9 19.1l1.4 -1.4 M17.7 6.3l1.4 -1.4";
        private const string Table = "M4 5h16v14h-16z M4 10h16 M4 15h16 M10 5v14";
        private const string Layers = "M12 3l9 5l-9 5l-9 -5z M3 13l9 5l9 -5";

        public static readonly IReadOnlyDictionary<string, IconSpec> Icons = new Dictionary<string, IconSpec>
        {
            // ---- setup: App & Data Import
            // the Sportify mark: a court (outline, halfway line, centre circle) on a rounded square
            ["app"] = new("setup", "M5 7h14v10h-14z M12 7v10 M12 10a2 2 0 1 1 0 4a2 2 0 1 1 0 -4", "M5 2h14a3 3 0 0 1 3 3v14a3 3 0 0 1 -3 3h-14a3 3 0 0 1 -3 -3v-14a3 3 0 0 1 3 -3z", Badge: true),
            ["import"] = new("setup", "M12 4v12 M7 11l5 5l5 -5 M5 20h14", "M5 18h14v3h-14z"),
            ["families"] = new("setup", "M12 3l8 4.5v9l-8 4.5l-8 -4.5v-9z M12 12l8 -4.5 M12 12v9 M12 12l-8 -4.5", "M12 3l8 4.5l-8 4.5l-8 -4.5z"),
            ["dxf"] = new("setup", "M6 3h8l4 4v14h-12z M14 3v4h4 M9 14h6 M9 17h4", "M6 3h8l4 4v14h-12z"),
            ["location_sun"] = new("setup", "M12 21c-4 -4.5 -6 -7.5 -6 -10.5a6 6 0 0 1 12 0c0 3 -2 6 -6 10.5z M12 8.5a2.2 2.2 0 1 1 0 4.4a2.2 2.2 0 1 1 0 -4.4", "M12 21c-4 -4.5 -6 -7.5 -6 -10.5a6 6 0 0 1 12 0c0 3 -2 6 -6 10.5z"),
            ["sync"] = new("setup", "M20 11a8 8 0 0 0 -14.5 -3 M4 4v4h4 M4 13a8 8 0 0 0 14.5 3 M20 20v-4h-4", "M12 6a6 6 0 1 1 0 12a6 6 0 1 1 0 -12"),

            // ---- push to Sportify (the drop-down and its items)
            ["push"] = new("setup", "M12 15v-12 M7 8l5 -5l5 5 M4 14v6h16v-6", "M4 14h16v6h-16z"),
            ["push_all"] = new("setup", Layers + " M3 17l9 5l9 -5", "M12 3l9 5l-9 5l-9 -5z"),
            ["push_outline"] = new("setup", "M4 8l8 -4l8 4v9l-8 4l-8 -4z", "M4 8l8 -4l8 4v9l-8 4l-8 -4z"),
            ["push_structure"] = new("setup", "M4 4h16v16h-16z M4 12h16 M12 4v16", "M4 4h16v16h-16z"),
            ["push_entries"] = new("setup", "M6 21v-16h9v16 M3 21h18 M12 13h.01", "M6 5h9v16h-9z"),
            ["push_openings"] = new("setup", "M4 4h16v16h-16z M9 9h6v6h-6z", "M4 4h16v5h-16z"),
            ["push_edge"] = new("setup", "M3 8h18 M3 20h18 M6 8v12 M12 8v12 M18 8v12", "M3 8h18v3h-18z"),
            ["push_drains"] = new("setup", "M12 3c3 4 6 7 6 11a6 6 0 0 1 -12 0c0 -4 3 -7 6 -11z", "M12 3c3 4 6 7 6 11a6 6 0 0 1 -12 0c0 -4 3 -7 6 -11z"),
            ["push_equipment"] = new("setup", "M5 8h14v11h-14z M9 8v-3h6v3 M9 13h6", "M5 8h14v11h-14z"),
            ["push_slab"] = new("setup", "M4 10h16v4h-16z M4 16h16v3h-16z", "M4 10h16v4h-16z"),

            // ---- algorithmic analysis
            ["fire"] = new("algorithmic", "M12 3c1 4 5 5 5 10a5 5 0 0 1 -10 0c0 -2 1 -3 2 -4c0 2 1 2 1.5 2c0 -3 0 -5 1.5 -8z", "M12 3c1 4 5 5 5 10a5 5 0 0 1 -10 0c0 -2 1 -3 2 -4c0 2 1 2 1.5 2c0 -3 0 -5 1.5 -8z"),
            ["carbon"] = new("algorithmic", Cloud + " M9 20h6", Cloud),
            ["leaf"] = new("algorithmic", "M5 19c0 -9 5 -14 14 -14c0 9 -5 14 -14 14z M5 19l8 -8", "M5 19c0 -9 5 -14 14 -14c0 9 -5 14 -14 14z"),
            ["accessibility"] = new("algorithmic", "M12 3a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0 -3 M6 9l6 1l6 -1 M12 10v4l-3 7 M12 14l3 7", "M12 3a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0 -3"),

            // ---- physical analysis (Simulation & Analytics)
            ["send"] = new("physical", "M4 12l16 -8l-6 16l-3 -7z M11 13l9 -9", "M4 12l16 -8l-6 16l-3 -7z"),
            ["structural"] = new("physical", "M4 21v-16l8 -3l8 3v16 M2 21h20 M9 21v-5h6v5 M8 9h2 M14 9h2 M8 13h2 M14 13h2", "M4 21v-16l8 -3l8 3v16z"),
            ["dynamic"] = new("physical", "M2 12h3l2 -6l4 12l3 -9l2 3h6", null),
            ["environmental"] = new("physical", "M7 3.5a3 3 0 1 1 0 6a3 3 0 1 1 0 -6 M7 0.5v1 M1.5 6.5h1 M2.6 2.1l.8 .8 M11.4 2.1l-.8 .8 M9 20a3.5 3.5 0 0 1 -.4 -7a4.5 4.5 0 0 1 8.6 -.9a3.7 3.7 0 0 1 1.3 7.9z", "M9 20a3.5 3.5 0 0 1 -.4 -7a4.5 4.5 0 0 1 8.6 -.9a3.7 3.7 0 0 1 1.3 7.9z"),
            ["sun"] = new("physical", SunBody + SunRays, SunBody),
            ["wind"] = new("physical", "M3 8h10a3 3 0 1 0 -3 -3 M3 12h15a3 3 0 1 1 -3 3 M3 16h7a2 2 0 1 1 -2 2", null),
            ["rain"] = new("physical", Cloud + " M8 19l-1 2 M12 19l-1 2 M16 19l-1 2", Cloud),
            ["ball"] = new("physical", "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0 -18 M3.5 9c4 1.5 13 1.5 17 0 M3.5 15c4 -1.5 13 -1.5 17 0", "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0 -18"),

            // ---- BIM & documentation
            ["schedule"] = new("bim", Table, "M4 5h16v5h-16z"),
            ["filter"] = new("bim", "M4 4h16l-6 8v6l-4 2v-8z", "M4 4h16l-6 8v-1h-4v1z"),
            ["phasing"] = new("bim", "M4 6h16v14h-16z M4 11h16 M8 3v4 M16 3v4", "M4 6h16v5h-16z"),
            ["phase_assign"] = new("bim", "M4 6h16v14h-16z M4 11h16 M8 3v4 M16 3v4 M9 16l2 2l4 -4", "M4 6h16v5h-16z"),
            ["worksets"] = new("bim", Layers + " M3 17l9 5l9 -5", "M12 3l9 5l-9 5l-9 -5z"),
            ["workset_assign"] = new("bim", "M12 3l9 5l-9 5l-9 -5z M3 13l9 5l9 -5 M9 8l2 2l4 -4", "M12 3l9 5l-9 5l-9 -5z"),

            // ---- export
            ["report"] = new("export", "M6 3h9l4 4v14h-13z M9 12h6 M9 16h6", "M6 3h9l4 4v14h-13z"),
            ["diagram"] = new("export", "M4 6h6v6h-6z M14 12h6v6h-6z M10 9h4v6", "M4 6h6v6h-6z M14 12h6v6h-6z"),
            ["csv"] = new("export", Table, "M4 5h16v5h-16z"),
            ["folder"] = new("export", "M3 7h6l2 2h10v10h-18z", "M3 7h6l2 2h10v10h-18z"),
        };

        /// <summary>The outline of each icon (kept as it was for the checks and anything that only wants the shape).</summary>
        public static readonly IReadOnlyDictionary<string, string> Paths = Icons.ToDictionary(kv => kv.Key, kv => kv.Value.Stroke);
    }
}
