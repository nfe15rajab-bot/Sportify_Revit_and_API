using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SportfyRevit
{
    /// <summary>
    /// Draws the ribbon's icons from RibbonIconData: 32 x 32 for the large buttons and drop-downs, 16 x 16 for the items inside a drop-down. Each is a soft tint of the panel's colour under a
    /// rounded outline in the same colour (the Sportify mark: a white court on a filled badge). Drawn in code (no image files to ship or to lose) at the size asked for, so both are
    /// sharp. Returns null for an unknown name or if drawing fails: a button without an icon is better than no add-in.
    /// </summary>
    internal static class RibbonIcons
    {
        private static readonly Dictionary<(string, int), BitmapSource?> Cache = new();

        public static BitmapSource? Large(string name) => Render(name, 32);
        public static BitmapSource? Small(string name) => Render(name, 16);

        /// <summary>The icon at any size (the checks and the preview sheet use it larger than the ribbon does).</summary>
        internal static BitmapSource? Render(string name, int size)
        {
            if (Cache.TryGetValue((name, size), out var cached)) return cached;
            BitmapSource? result = null;
            try
            {
                if (RibbonIconData.Icons.TryGetValue(name, out var icon))
                {
                    var (r, g, b) = RibbonIconData.GroupColors[icon.Group];
                    var ink = System.Windows.Media.Color.FromRgb(r, g, b);
                    var tint = System.Windows.Media.Color.FromArgb(icon.Badge ? (byte)255 : (byte)0x40, r, g, b);
                    var line = icon.Badge ? Colors.White : ink;
                    var pen = new System.Windows.Media.Pen(new SolidColorBrush(line), icon.Badge ? 1.8 : 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    pen.Freeze();
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.PushTransform(new ScaleTransform(size / 24.0, size / 24.0));      // the 24-unit grid onto the bitmap; the stroke scales with it
                        if (icon.Fill != null) dc.DrawGeometry(new SolidColorBrush(tint), null, Geometry.Parse(icon.Fill));
                        dc.DrawGeometry(null, pen, Geometry.Parse(icon.Stroke));
                        dc.Pop();
                    }
                    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    bitmap.Freeze();
                    result = bitmap;
                }
                else SportifyLog.Warn("ribbon", "no icon named \"" + name + "\"");
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("ribbon", "the icon \"" + name + "\" could not be drawn: " + ex.Message);
            }
            Cache[(name, size)] = result;
            return result;
        }
    }
}
