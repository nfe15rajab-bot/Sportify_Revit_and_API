using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SportfyRevit
{
    /// <summary>
    /// Draws the ribbon's icons from RibbonIconData: 32 x 32 for the large buttons and drop-downs, 16 x 16 for the items inside a drop-down. Drawn in code (no image files to ship
    /// or to lose) at the size asked for, so both are sharp. Returns null for an unknown name or if drawing fails: a button without an icon is better than no add-in.
    /// </summary>
    internal static class RibbonIcons
    {
        private static readonly System.Windows.Media.Color Ink = System.Windows.Media.Color.FromRgb(0x2F, 0x6F, 0xA8);      // readable on Revit's light and dark ribbon
        private static readonly Dictionary<(string, int), BitmapSource?> Cache = new();

        public static BitmapSource? Large(string name) => Render(name, 32);
        public static BitmapSource? Small(string name) => Render(name, 16);

        private static BitmapSource? Render(string name, int size)
        {
            if (Cache.TryGetValue((name, size), out var cached)) return cached;
            BitmapSource? result = null;
            try
            {
                if (RibbonIconData.Paths.TryGetValue(name, out var data))
                {
                    var scale = size / 24.0;
                    var pen = new System.Windows.Media.Pen(new SolidColorBrush(Ink), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                    pen.Freeze();
                    var geometry = Geometry.Parse(data);
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.PushTransform(new ScaleTransform(scale, scale));      // the 24-unit grid onto the bitmap; the 2-unit stroke scales with it
                        dc.DrawGeometry(null, pen, geometry);
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
