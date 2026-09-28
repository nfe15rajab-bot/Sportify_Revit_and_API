using System.Globalization;
using System.Text;

namespace SportfyRevit
{
    /// <summary>One space in a bubble diagram: how big its circle is (by real footprint area) and what it connects to.</summary>
    internal sealed record BubbleNode(string Id, string Label, double AreaM2);

    /// <summary>A connection drawn as a dotted line between two nodes' circles. Strong = the item's own walkable route to its entry (CirculationEngine); Weak = the nearest-neighbour
    /// guess used to give an isolated item at least one line, same as an architect's bubble diagram implies a relationship even where none was computed.</summary>
    internal sealed record BubbleEdge(string A, string B, bool Strong);

    /// <summary>
    /// The bubble (relationship) diagram: circles sized by real footprint area, radially arranged by how many steps of the connection graph separate them from the most-connected
    /// space (the "hub" — usually the entry/hall, exactly like a house's bubble diagram grows outward from the hall), joined by dotted lines. Unlike SvgChart.Plan this is NOT
    /// to scale or positioned by real coordinates on purpose: a bubble diagram shows relationships, not geometry (GenerateFunctionalDiagramsCommand draws the to-scale version
    /// as a "spine" diagram with SvgChart.Plan instead). Revit-free (Tools/AddinCheck), no font-family (see SvgChart's own note): same drawing conventions as SvgChart for a
    /// consistent look in the PDF report and the Diagrams folder.
    /// </summary>
    internal static class FunctionalDiagramSvg
    {
        const double MinR = 9, MaxR = 46, AreaToRadius = 2.6;
        const double RingStep = 108;                 // layout units between BFS layers, before the final scale-to-fit
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string N(double v) => v.ToString("0.###", Inv);
        static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        static double Radius(double areaM2) => Math.Clamp(Math.Sqrt(Math.Max(0, areaM2)) * AreaToRadius, MinR, MaxR);

        /// <summary>A single-hue scale (pale pink -&gt; dark maroon) by how big a circle is relative to the others in this diagram: the biggest, most important space reads darkest,
        /// exactly the palette an architect's hand-drawn bubble diagram uses for the primary rooms.</summary>
        static string Maroon(double t)
        {
            t = Math.Clamp(t, 0, 1);
            int r = (int)Math.Round(243 + (107 - 243) * t), g = (int)Math.Round(217 + (18 - 217) * t), b = (int)Math.Round(222 + (32 - 222) * t);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        static string TextColorFor(double t) => t > 0.45 ? "#ffffff" : "#3a1018";

        public static string Bubble(string title, IReadOnlyList<BubbleNode> nodes, IReadOnlyList<BubbleEdge> edges, int maxW = 620)
        {
            if (nodes.Count == 0) return $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {maxW} 120'><text x='{maxW / 2}' y='60' font-size='11' text-anchor='middle' fill='#8a94a6'>nothing to diagram</text></svg>";

            var adjacency = new Dictionary<string, List<string>>();
            foreach (var n in nodes) adjacency[n.Id] = new List<string>();
            foreach (var e in edges)
            {
                if (!adjacency.ContainsKey(e.A) || !adjacency.ContainsKey(e.B)) continue;
                adjacency[e.A].Add(e.B);
                adjacency[e.B].Add(e.A);
            }

            // the hub: the space with the most connections (ties broken by area) — where the diagram grows outward from, same as a hall in a house's own bubble diagram
            var hub = nodes.OrderByDescending(n => adjacency[n.Id].Count).ThenByDescending(n => n.AreaM2).First();

            // BFS layers from the hub; anything never reached (a disconnected island) goes in one final outer ring
            var layer = new Dictionary<string, int> { [hub.Id] = 0 };
            var queue = new Queue<string>(); queue.Enqueue(hub.Id);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var nb in adjacency[cur]) if (!layer.ContainsKey(nb)) { layer[nb] = layer[cur] + 1; queue.Enqueue(nb); }
            }
            var maxLayer = layer.Count > 0 ? layer.Values.Max() : 0;
            foreach (var n in nodes) if (!layer.ContainsKey(n.Id)) layer[n.Id] = maxLayer + 1;

            // one angle slot per node in its own layer (the hub's single slot is simply the centre); a deterministic offset per layer avoids every ring's first item lining up
            var byLayer = nodes.GroupBy(n => layer[n.Id]).OrderBy(g => g.Key).ToList();
            var posX = new Dictionary<string, double>(); var posY = new Dictionary<string, double>();
            posX[hub.Id] = 0; posY[hub.Id] = 0;
            foreach (var g in byLayer)
            {
                if (g.Key == 0) continue;
                var members = g.OrderByDescending(n => n.AreaM2).ToList();
                var ringRadius = RingStep * g.Key;
                var startAngle = g.Key * 0.35;               // radians — a small stagger per ring so labels do not all fall on the same spokes
                for (var i = 0; i < members.Count; i++)
                {
                    var a = startAngle + 2 * Math.PI * i / members.Count;
                    posX[members[i].Id] = ringRadius * Math.Cos(a);
                    posY[members[i].Id] = ringRadius * Math.Sin(a);
                }
            }

            // scale-to-fit: the raw layout units (above) to the canvas, leaving room for each circle's own radius and its label
            var minX = nodes.Min(n => posX[n.Id] - Radius(n.AreaM2) - 60); var maxX = nodes.Max(n => posX[n.Id] + Radius(n.AreaM2) + 60);
            var minY = nodes.Min(n => posY[n.Id] - Radius(n.AreaM2) - 20); var maxY = nodes.Max(n => posY[n.Id] + Radius(n.AreaM2) + 20);
            var rawW = Math.Max(1, maxX - minX); var rawH = Math.Max(1, maxY - minY);
            var scale = Math.Min(1, (maxW - 20) / rawW);
            var w = (int)Math.Ceiling(rawW * scale + 20);
            var h = (int)Math.Ceiling(rawH * scale + 44);
            double X(string id) => 10 - minX * scale + posX[id] * scale;
            double Y(string id) => 34 - minY * scale + posY[id] * scale;

            var areaMin = nodes.Min(n => n.AreaM2); var areaMax = nodes.Max(n => n.AreaM2);
            double Tone(double area) => areaMax > areaMin + 1e-9 ? (area - areaMin) / (areaMax - areaMin) : 0.6;

            var sb = new StringBuilder();
            sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {w} {h}'>");
            sb.Append($"<rect x='0' y='0' width='{w}' height='{h}' fill='#ffffff'/>");
            if (title.Length > 0) sb.Append($"<text x='10' y='16' font-size='11' font-weight='bold' fill='#222633'>{Esc(title)}</text>");

            foreach (var e in edges)
            {
                if (!posX.ContainsKey(e.A) || !posX.ContainsKey(e.B)) continue;
                sb.Append($"<line x1='{N(X(e.A))}' y1='{N(Y(e.A))}' x2='{N(X(e.B))}' y2='{N(Y(e.B))}' stroke='#33313a' stroke-width='{(e.Strong ? "1.4" : "1")}' stroke-dasharray='{(e.Strong ? "1,3" : "1,4.5")}' stroke-linecap='round'/>");
            }
            foreach (var n in nodes)
            {
                var r = Radius(n.AreaM2) * scale;
                var t = Tone(n.AreaM2);
                var fill = Maroon(t);
                var cx = X(n.Id); var cy = Y(n.Id);
                sb.Append($"<circle cx='{N(cx)}' cy='{N(cy)}' r='{N(r)}' fill='{fill}'/>");
                if (r >= MinR)
                {
                    var fontSize = Math.Max(6, Math.Min(11, r / 3.4));
                    var label = WrapForCircle(n.Label, r);
                    var lines = label.Split('\n');
                    var lineH = fontSize * 1.15;
                    var y0 = cy - (lines.Length - 1) * lineH / 2;
                    for (var i = 0; i < lines.Length; i++)
                        sb.Append($"<text x='{N(cx)}' y='{N(y0 + i * lineH + fontSize / 3)}' font-size='{N(fontSize)}' text-anchor='middle' fill='{TextColorFor(t)}'>{Esc(lines[i])}</text>");
                }
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>Breaks a label onto at most two lines so it has a chance of fitting inside its own circle; a circle too small for even one word's first line drops the label
        /// entirely at the caller (r &lt;= 9 above), same threshold SvgChart.Plan uses ("fit &gt; 12").</summary>
        static string WrapForCircle(string label, double r)
        {
            var maxChars = Math.Max(3, (int)(r / 3.1));
            if (label.Length <= maxChars) return label;
            var words = label.Split(' ');
            if (words.Length < 2) return label.Length <= maxChars + 2 ? label : label.Substring(0, Math.Max(1, maxChars - 1)) + "…";
            var line1 = new StringBuilder(); var i = 0;
            while (i < words.Length && line1.Length + words[i].Length + (line1.Length > 0 ? 1 : 0) <= maxChars) { if (line1.Length > 0) line1.Append(' '); line1.Append(words[i]); i++; }
            if (line1.Length == 0) { line1.Append(words[0]); i = 1; }
            var line2 = string.Join(" ", words.Skip(i));
            if (line2.Length == 0) return line1.ToString();
            if (line2.Length > maxChars) line2 = line2.Substring(0, Math.Max(1, maxChars - 1)) + "…";
            return line1 + "\n" + line2;
        }
    }
}
