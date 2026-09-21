namespace SportfyRevit
{
    /// <summary>
    /// The ribbon's icons as vector paths on a 24 x 24 grid, drawn as 2-unit strokes with round ends (the outline style of the web app's Tabler icons). Revit needs bitmaps: RibbonIcons
    /// draws these at 32 x 32 (the large buttons) and 16 x 16 (the items of a drop-down). No Revit or WPF types here so that Tools/AddinCheck can check the table.
    /// A path is the SVG path mini-language (M, L, H, V, C, A, Z and their lowercase forms).
    /// </summary>
    internal static class RibbonIconData
    {
        private const string SunCircleAndRays =
            "M12 8a4 4 0 1 1 0 8a4 4 0 1 1 0 -8 M12 2v2 M12 20v2 M2 12h2 M20 12h2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M4.9 19.1l1.4 -1.4 M17.7 6.3l1.4 -1.4";
        private const string Table = "M4 5h16v14h-16z M4 10h16 M4 15h16 M10 5v14";
        private const string Cloud = "M7 17a4 4 0 0 1 -.5 -7.9a5 5 0 0 1 9.6 -1.1a4 4 0 0 1 1.4 9z";

        public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["app"] = "M3 5h18v14h-18z M3 9h18 M6 7h.01 M9 7h.01",
            ["import"] = "M12 4v12 M7 11l5 5l5 -5 M5 20h14",
            ["families"] = "M12 3l8 4.5v9l-8 4.5l-8 -4.5v-9z M12 12l8 -4.5 M12 12v9 M12 12l-8 -4.5",
            ["dxf"] = "M6 3h8l4 4v14h-12z M14 3v4h4 M9 14h6 M9 17h4",
            ["sun"] = SunCircleAndRays,
            ["sync"] = "M20 11a8 8 0 0 0 -14.5 -3 M4 4v4h4 M4 13a8 8 0 0 0 14.5 3 M20 20v-4h-4",
            ["send"] = "M4 12l16 -8l-6 16l-3 -7z M11 13l9 -9",
            ["structural"] = "M4 21v-16l8 -3l8 3v16 M2 21h20 M9 21v-5h6v5 M8 9h2 M14 9h2 M8 13h2 M14 13h2",
            ["dynamic"] = "M2 12h3l2 -6l4 12l3 -9l2 3h6",
            ["wind"] = "M3 8h10a3 3 0 1 0 -3 -3 M3 12h15a3 3 0 1 1 -3 3 M3 16h7a2 2 0 1 1 -2 2",
            ["rain"] = Cloud + " M8 19l-1 2 M12 19l-1 2 M16 19l-1 2",
            ["compliance"] = "M12 3l8 3v6c0 5 -3.5 8.5 -8 9c-4.5 -.5 -8 -4 -8 -9v-6z M9 12l2 2l4 -4",
            ["fire"] = "M12 3c1 4 5 5 5 10a5 5 0 0 1 -10 0c0 -2 1 -3 2 -4c0 2 1 2 1.5 2c0 -3 0 -5 1.5 -8z",
            ["accessibility"] = "M12 3a1.5 1.5 0 1 1 0 3a1.5 1.5 0 1 1 0 -3 M6 9l6 1l6 -1 M12 10v4l-3 7 M12 14l3 7",
            ["ball"] = "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0 -18 M3.5 9c4 1.5 13 1.5 17 0 M3.5 15c4 -1.5 13 -1.5 17 0",
            ["leaf"] = "M5 19c0 -9 5 -14 14 -14c0 9 -5 14 -14 14z M5 19l8 -8",
            ["carbon"] = Cloud,
            ["schedule"] = Table,
            ["filter"] = "M4 4h16l-6 8v6l-4 2v-8z",
            ["phasing"] = "M4 6h16v14h-16z M4 11h16 M8 3v4 M16 3v4",
            ["worksets"] = "M12 3l9 5l-9 5l-9 -5z M3 13l9 5l9 -5 M3 17l9 5l9 -5",
            ["report"] = "M6 3h9l4 4v14h-13z M9 12h6 M9 16h6",
            ["diagram"] = "M4 6h6v6h-6z M14 12h6v6h-6z M10 9h4v6",
            ["csv"] = Table,
            ["folder"] = "M3 7h6l2 2h10v10h-18z",
        };
    }
}
