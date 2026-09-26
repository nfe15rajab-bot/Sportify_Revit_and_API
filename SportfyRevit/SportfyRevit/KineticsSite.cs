using System.Text.Json;
using Sportify.Simulation.Structure;

namespace SportfyRevit
{
    /// <summary>A garden (green-roof zone) or a field (court) of the layout, as a box in the roof's plan (metres, x right, y down).</summary>
    internal sealed class SiteRect
    {
        public string Label = "";
        public bool IsGarden;
        public double X, Y, W, H;
        public double CentreX => X + W / 2;
        public double CentreY => Y + H / 2;
    }

    /// <summary>
    /// What the sails need to know about their surroundings: the gardens and the fields of the layout the web app last pushed. A sail goes near them: it runs in away from a garden
    /// that needs the sun and out over the people beside a field, and a triangle's slanted edge faces the garden. Read from the same structure the sun and shade analysis reads.
    /// </summary>
    internal static class KineticsSite
    {
        /// <summary>The gardens and fields of the last pushed layout (empty when there is none or it cannot be read).</summary>
        internal static List<SiteRect> Read()
        {
            var list = new List<SiteRect>();
            try
            {
                RoofBoundaryServer.TryGetLatestCombinedLayout(out var json, out _);
                if (json == null) return list;
                var layout = JsonSerializer.Deserialize<SportifyLayout>(json);
                if (layout == null) return list;
                var inputs = SunLayoutAdapter.ToInputs(layout);
                foreach (var item in inputs.Structure.Items)
                {
                    if (item.Kind != LoadKind.Zone && item.Kind != LoadKind.Court) continue;
                    if (item.Width <= 0 || item.Height <= 0) continue;
                    list.Add(new SiteRect { Label = item.Label ?? item.Name ?? "", IsGarden = item.Kind == LoadKind.Zone, X = item.X, Y = item.Y, W = item.Width, H = item.Height });
                }
            }
            catch (Exception ex) { SportifyLog.Warn("kinetics", "the gardens and fields of the layout could not be read: " + ex.Message); }
            return list;
        }

        /// <summary>The gap between two boxes (0 when they touch or overlap).</summary>
        internal static double Gap(SiteRect a, SiteRect b)
        {
            double dx = Math.Max(0, Math.Max(a.X - (b.X + b.W), b.X - (a.X + a.W)));
            double dy = Math.Max(0, Math.Max(a.Y - (b.Y + b.H), b.Y - (a.Y + a.H)));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>The nearest garden (or field) to a box, with the gap and the plan direction (unit) from the box's centre toward it; null when there is none.</summary>
        internal static SiteRect? Nearest(IEnumerable<SiteRect> site, bool garden, SiteRect from, out double gap, out double dirX, out double dirY)
        {
            SiteRect? best = null; gap = double.MaxValue; dirX = 0; dirY = 0;
            foreach (var s in site)
            {
                if (s.IsGarden != garden) continue;
                var g = Gap(from, s);
                if (g < gap) { gap = g; best = s; }
            }
            if (best == null) { gap = 0; return null; }
            var dx = best.CentreX - from.CentreX; var dy = best.CentreY - from.CentreY;
            var l = Math.Sqrt(dx * dx + dy * dy);
            if (l > 1e-9) { dirX = dx / l; dirY = dy / l; }
            return best;
        }
    }
}
