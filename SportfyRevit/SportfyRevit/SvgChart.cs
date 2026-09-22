using System.Globalization;
using System.Text;

namespace SportfyRevit
{
    /// <summary>One line or bar series of a chart.</summary>
    internal sealed class ChartSeries
    {
        public string Name = "";
        public string Color = SvgChart.Blue;
        public List<double> X = new();          // lines only
        public List<double> Y = new();          // lines: y at each x; bars: one value per label
        public bool Dashed;
    }

    /// <summary>An open polyline on a plan (a circulation path, a connection): drawn under the shapes, in the plan's own metres (y down).</summary>
    internal sealed class PlanPolyline
    {
        public List<double[]> Points = new();   // {x, y} ...
        public string Color = SvgChart.Bad;
        public double WidthM = 0.5;
        public bool Dashed;
        public double Opacity = 1;
    }

    /// <summary>One shape on a plan: a rectangle, a polygon or a circle, in the plan's own metres (y down).</summary>
    internal sealed class PlanShape
    {
        public string Kind = "rect";            // rect | poly | circle
        public double X, Y, W, H;               // rect: top-left corner and size; circle: the centre in X, Y and the diameter in W
        public List<double[]> Points = new();   // poly: {x, y} ...
        public string Fill = SvgChart.Neutral;
        public double FillOpacity = 0.85;
        public string Stroke = "#ffffff";
        public double StrokeWidthM = 0.12;
        public bool Dashed;
        public string Label = "";               // in the middle of the shape
        public string LabelColor = "#ffffff";
        public double LabelSize = 0;            // 0 = fitted to the shape
        public double CornerRadiusM = 0;        // rect only: rounded corners (an architectural room-box look), in the plan's own metres
    }

    /// <summary>
    /// Small SVG chart drawing for the PDF report (QuestPDF draws an SVG string). Plain shapes and text only: no font-family is ever written, because the renderer draws
    /// text only with its default font (a named font makes the text vanish); no scripts, no external resources. Everything is drawn from the numbers passed in.
    /// </summary>
    internal static class SvgChart
    {
        public const string Ok = "#3f9d63", Warn = "#e0a030", Bad = "#d9534f", Neutral = "#8a94a6", Blue = "#3b82c4", Teal = "#2a9d8f", Violet = "#7b61c4", Ink = "#222633", Faint = "#d8dbe3", Paper = "#f7f8fb", Muted = "#5b6274";
        public static readonly string[] Palette = { Blue, "#e07b39", Teal, Violet, "#c2185b", "#7f8c2b" };

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string N(double v) => v.ToString("0.###", Inv);
        static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>The colour of a status word used across the analyses (ok, marginal / over, fails ...).</summary>
        public static string ForStatus(string? status)
        {
            switch ((status ?? "").ToLowerInvariant())
            {
                case "ok": case "balanced": case "fine": return Ok;
                case "marginal": return Warn;
                case "over": case "fails": case "unbalanced": case "too-sunny": return Bad;
                case "too-shaded": return Blue;
                default: return Neutral;
            }
        }

        /// <summary>The colour of a ratio to its limit: under 0.8 fine, up to 1 marginal, above it over.</summary>
        public static string ForRatio(double ratio) => ratio > 1 ? Bad : ratio > 0.8 ? Warn : Ok;

        /// <summary>A round upper bound and the step of the ticks under it.</summary>
        static (double max, double step) Nice(double value)
        {
            if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return (1, 0.25);
            // the smallest round step (1, 2, 2.5, 5 x 10^k) that gives at most about five ticks, and the first multiple of it above the value
            var raw = value / 4.5;
            var k = Math.Floor(Math.Log10(raw));
            var step = 0.0;
            foreach (var m in new[] { 1.0, 2.0, 2.5, 5.0, 10.0 })
            {
                step = m * Math.Pow(10, k);
                if (step >= raw - 1e-12) break;
            }
            return (Math.Ceiling(value / step - 1e-9) * step, step);
        }

        static string Open(int w, int h, string title)
        {
            var sb = new StringBuilder();
            sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {w} {h}'>");
            sb.Append($"<rect x='0' y='0' width='{w}' height='{h}' fill='{Paper}'/>");
            if (title.Length > 0) sb.Append($"<text x='10' y='16' font-size='11' font-weight='bold' fill='{Ink}'>{Esc(title)}</text>");
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------------ bars

        /// <summary>
        /// Vertical bars, one per label; with several series they are grouped. <paramref name="colors"/> colours a single series bar by bar. A dashed reference line
        /// (a capacity, a limit, a target) can be drawn across.
        /// </summary>
        public static string Bars(string title, string unit, IList<string> labels, IList<ChartSeries> series, IList<string>? colors = null, double? refLine = null, string refLabel = "", int w = 520, int h = 190)
        {
            var sb = new StringBuilder(Open(w, h, title));
            double left = 44, right = 12, top = 30, bottom = labels.Count > 12 ? 58 : 44;
            double pw = w - left - right, ph = h - top - bottom;
            var maxV = series.SelectMany(s => s.Y).DefaultIfEmpty(0).Max();
            if (refLine.HasValue) maxV = Math.Max(maxV, refLine.Value);
            var (max, step) = Nice(maxV * 1.08);
            var unitX = w - right - (refLine.HasValue && refLabel.Length > 0 ? refLabel.Length * 4.3 + 24 : 0);
            if (unit.Length > 0) sb.Append($"<text x='{N(unitX)}' y='16' font-size='8' text-anchor='end' fill='{Muted}'>{Esc(unit)}</text>");
            if (refLine.HasValue && refLabel.Length > 0)
                sb.Append($"<line x1='{N(w - right - refLabel.Length * 4.3 - 16)}' y1='13' x2='{N(w - right - refLabel.Length * 4.3 - 4)}' y2='13' stroke='{Bad}' stroke-width='1.2' stroke-dasharray='4,2'/><text x='{N(w - right)}' y='16' font-size='8' text-anchor='end' fill='{Bad}'>{Esc(refLabel)}</text>");

            for (var t = 0.0; t <= max + step / 1000; t += step)
            {
                var y = top + ph - ph * t / max;
                sb.Append($"<line x1='{N(left)}' y1='{N(y)}' x2='{N(left + pw)}' y2='{N(y)}' stroke='{Faint}' stroke-width='0.6'/>");
                sb.Append($"<text x='{N(left - 4)}' y='{N(y + 3)}' font-size='8' text-anchor='end' fill='{Muted}'>{Esc(Tick(t, step))}</text>");
            }

            var n = Math.Max(1, labels.Count);
            var slot = pw / n;
            var groupW = Math.Min(slot * 0.78, 44 * Math.Max(1, series.Count));
            var barW = groupW / Math.Max(1, series.Count);
            for (var i = 0; i < labels.Count; i++)
            {
                var cx = left + slot * (i + 0.5);
                for (var s = 0; s < series.Count; s++)
                {
                    if (i >= series[s].Y.Count) continue;
                    var v = series[s].Y[i];
                    var bh = ph * Math.Max(0, v) / max;
                    var color = series.Count == 1 && colors != null && i < colors.Count ? colors[i] : series[s].Color;
                    var x = cx - groupW / 2 + barW * s;
                    sb.Append($"<rect x='{N(x)}' y='{N(top + ph - bh)}' width='{N(Math.Max(1, barW - 1))}' height='{N(bh)}' fill='{color}'/>");
                    if (labels.Count <= 14 && series.Count <= 2)
                        sb.Append($"<text x='{N(x + barW / 2)}' y='{N(top + ph - bh - 2)}' font-size='7' text-anchor='middle' fill='{Ink}'>{Esc(Tick(v, step))}</text>");
                }
                var lab = Short(labels[i], labels.Count > 12 ? 12 : labels.Count > 6 ? 16 : 26);
                if (labels.Count > 12) sb.Append($"<text transform='translate({N(cx)},{N(top + ph + 6)}) rotate(50)' font-size='7' fill='{Ink}'>{Esc(lab)}</text>");
                else sb.Append($"<text x='{N(cx)}' y='{N(top + ph + 12)}' font-size='8' text-anchor='middle' fill='{Ink}'>{Esc(lab)}</text>");
            }
            sb.Append($"<line x1='{N(left)}' y1='{N(top + ph)}' x2='{N(left + pw)}' y2='{N(top + ph)}' stroke='{Muted}' stroke-width='0.8'/>");

            if (refLine.HasValue)
            {
                var y = top + ph - ph * refLine.Value / max;
                sb.Append($"<line x1='{N(left)}' y1='{N(y)}' x2='{N(left + pw)}' y2='{N(y)}' stroke='{Bad}' stroke-width='1.2' stroke-dasharray='5,3'/>");
            }
            Legend(sb, series, w, h - 8);
            sb.Append("</svg>");
            return sb.ToString();
        }

        static void Legend(StringBuilder sb, IList<ChartSeries> series, int w, int y)
        {
            if (series.Count < 2) return;
            var x = 10.0;
            foreach (var s in series)
            {
                sb.Append($"<rect x='{N(x)}' y='{y - 7}' width='9' height='9' fill='{s.Color}'/><text x='{N(x + 12)}' y='{y}' font-size='8' fill='{Ink}'>{Esc(s.Name)}</text>");
                x += 22 + s.Name.Length * 4.4;
            }
        }

        // ------------------------------------------------------------------------------------------------------------ lines

        /// <summary>Lines over a numeric x axis, with an optional shaded band on x and horizontal reference lines.</summary>
        public static string Lines(string title, string xLabel, string yLabel, IList<ChartSeries> series, (double from, double to, string label)? band = null, IList<(double y, string label, string color)>? refs = null, int w = 520, int h = 190)
        {
            var sb = new StringBuilder(Open(w, h, title));
            double left = 46, right = 14, top = 30, bottom = 46;
            double pw = w - left - right, ph = h - top - bottom;
            var xs = series.SelectMany(s => s.X).ToList();
            if (xs.Count == 0) { sb.Append($"<text x='{w / 2}' y='{h / 2}' font-size='10' text-anchor='middle' fill='{Muted}'>no data</text></svg>"); return sb.ToString(); }
            double x0 = xs.Min(), x1 = xs.Max();
            if (x1 - x0 < 1e-9) x1 = x0 + 1;
            var maxY = series.SelectMany(s => s.Y).DefaultIfEmpty(0).Max();
            if (refs != null && refs.Count > 0) maxY = Math.Max(maxY, refs.Max(r => r.y));
            var (max, step) = Nice(maxY * 1.1);

            double X(double v) => left + pw * (v - x0) / (x1 - x0);
            double Y(double v) => top + ph - ph * v / max;

            for (var t = 0.0; t <= max + step / 1000; t += step)
            {
                sb.Append($"<line x1='{N(left)}' y1='{N(Y(t))}' x2='{N(left + pw)}' y2='{N(Y(t))}' stroke='{Faint}' stroke-width='0.6'/>");
                sb.Append($"<text x='{N(left - 4)}' y='{N(Y(t) + 3)}' font-size='8' text-anchor='end' fill='{Muted}'>{Esc(Tick(t, step))}</text>");
            }
            var (xmax, xstep) = Nice(x1 - x0);
            for (var t = x0; t <= x1 + 1e-9; t += xstep)
            {
                sb.Append($"<line x1='{N(X(t))}' y1='{N(top + ph)}' x2='{N(X(t))}' y2='{N(top + ph + 3)}' stroke='{Muted}' stroke-width='0.8'/>");
                sb.Append($"<text x='{N(X(t))}' y='{N(top + ph + 12)}' font-size='8' text-anchor='middle' fill='{Muted}'>{Esc(Tick(t, xstep))}</text>");
            }
            if (band.HasValue)
            {
                var bx0 = X(Math.Max(x0, band.Value.from)); var bx1 = X(Math.Min(x1, band.Value.to));
                if (bx1 > bx0)
                {
                    sb.Append($"<rect x='{N(bx0)}' y='{N(top)}' width='{N(bx1 - bx0)}' height='{N(ph)}' fill='{Warn}' fill-opacity='0.18'/>");
                    if (band.Value.label.Length > 0) sb.Append($"<text x='{N((bx0 + bx1) / 2)}' y='{N(top + 9)}' font-size='8' text-anchor='middle' fill='{Muted}'>{Esc(band.Value.label)}</text>");
                }
            }
            sb.Append($"<line x1='{N(left)}' y1='{N(top + ph)}' x2='{N(left + pw)}' y2='{N(top + ph)}' stroke='{Muted}' stroke-width='0.8'/><line x1='{N(left)}' y1='{N(top)}' x2='{N(left)}' y2='{N(top + ph)}' stroke='{Muted}' stroke-width='0.8'/>");
            if (refs != null)
                foreach (var r in refs)
                {
                    sb.Append($"<line x1='{N(left)}' y1='{N(Y(r.y))}' x2='{N(left + pw)}' y2='{N(Y(r.y))}' stroke='{r.color}' stroke-width='1' stroke-dasharray='4,3'/>");
                    if (r.label.Length > 0) sb.Append($"<text x='{N(left + pw)}' y='{N(Y(r.y) - 2)}' font-size='7' text-anchor='end' fill='{r.color}'>{Esc(r.label)}</text>");
                }
            foreach (var s in series)
            {
                var pts = string.Join(" ", s.X.Zip(s.Y, (a, b) => N(X(a)) + "," + N(Y(b))));
                sb.Append($"<polyline points='{pts}' fill='none' stroke='{s.Color}' stroke-width='1.6' {(s.Dashed ? "stroke-dasharray='5,3'" : "")}/>");
            }
            sb.Append($"<text x='{N(left + pw / 2)}' y='{N(top + ph + 26)}' font-size='8' text-anchor='middle' fill='{Muted}'>{Esc(xLabel)}</text>");
            sb.Append($"<text x='{N(w - 14)}' y='16' font-size='8' text-anchor='end' fill='{Muted}'>{Esc(yLabel)}</text>");
            Legend(sb, series.Where(s => s.Name.Length > 0).ToList(), w, h - 6);
            sb.Append("</svg>");
            return sb.ToString();
        }

        // ------------------------------------------------------------------------------------------------------------ plan

        /// <summary>The roof seen from above: its outline (or rectangle) and coloured shapes on it, in metres, y down as the app draws it. `paths` (open polylines — a circulation
        /// spine) are drawn first, under the shapes, so an opaque shape's fill hides where a path runs into it rather than crossing over its label.</summary>
        public static string Plan(string title, double roofLength, double roofWidth, IList<double[]>? outline, IEnumerable<PlanShape> shapes, IEnumerable<(string text, string color)>? legend = null, int w = 520, IEnumerable<PlanPolyline>? paths = null)
        {
            double margin = 10, head = 24, foot = 26;
            var scale = (w - 2 * margin) / Math.Max(1, roofLength);
            var h = (int)Math.Ceiling(head + roofWidth * scale + foot);
            var sb = new StringBuilder(Open(w, h, title));
            string P(double x, double y) => N(margin + x * scale) + "," + N(head + y * scale);

            if (outline != null && outline.Count >= 3)
                sb.Append($"<polygon points='{string.Join(" ", outline.Select(p => P(p[0], p[1])))}' fill='#ffffff' stroke='{Muted}' stroke-width='1.2'/>");
            else
                sb.Append($"<rect x='{N(margin)}' y='{N(head)}' width='{N(roofLength * scale)}' height='{N(roofWidth * scale)}' fill='#ffffff' stroke='{Muted}' stroke-width='1.2'/>");

            if (paths != null)
                foreach (var p in paths)
                {
                    if (p.Points.Count < 2) continue;
                    var pts = string.Join(" ", p.Points.Select(q => P(q[0], q[1])));
                    var dash = p.Dashed ? " stroke-dasharray='5,3'" : "";
                    sb.Append($"<polyline points='{pts}' fill='none' stroke='{p.Color}' stroke-width='{N(Math.Max(1, p.WidthM * scale))}' stroke-linecap='round' stroke-linejoin='round' opacity='{N(p.Opacity)}'{dash}/>");
                }

            var labels = new StringBuilder();
            foreach (var s in shapes)
            {
                var dash = s.Dashed ? " stroke-dasharray='4,3'" : "";
                var sw = N(Math.Max(0.5, s.StrokeWidthM * scale));
                double lx, ly, fit;
                switch (s.Kind)
                {
                    case "poly":
                        if (s.Points.Count < 3) continue;
                        sb.Append($"<polygon points='{string.Join(" ", s.Points.Select(p => P(p[0], p[1])))}' fill='{s.Fill}' fill-opacity='{N(s.FillOpacity)}' stroke='{s.Stroke}' stroke-width='{sw}'{dash}/>");
                        lx = s.Points.Average(p => p[0]); ly = s.Points.Average(p => p[1]);
                        fit = Math.Sqrt(Math.Abs(Area(s.Points))) * scale;
                        break;
                    case "circle":
                        sb.Append($"<circle cx='{N(margin + s.X * scale)}' cy='{N(head + s.Y * scale)}' r='{N(Math.Max(2, s.W * scale / 2))}' fill='{s.Fill}' fill-opacity='{N(s.FillOpacity)}' stroke='{s.Stroke}' stroke-width='{sw}'{dash}/>");
                        lx = s.X; ly = s.Y; fit = s.W * scale;
                        break;
                    default:
                        var rxy = s.CornerRadiusM > 0 ? $" rx='{N(Math.Min(s.CornerRadiusM * scale, Math.Min(s.W, s.H) * scale / 2))}'" : "";
                        sb.Append($"<rect x='{N(margin + s.X * scale)}' y='{N(head + s.Y * scale)}' width='{N(Math.Max(1, s.W * scale))}' height='{N(Math.Max(1, s.H * scale))}'{rxy} fill='{s.Fill}' fill-opacity='{N(s.FillOpacity)}' stroke='{s.Stroke}' stroke-width='{sw}'{dash}/>");
                        lx = s.X + s.W / 2; ly = s.Y + s.H / 2; fit = Math.Min(s.W, s.H) * scale;
                        break;
                }
                if (s.Label.Length > 0)
                {
                    var size = s.LabelSize > 0 ? s.LabelSize : Math.Max(5, Math.Min(10, fit / Math.Max(3, s.Label.Length) * 1.7));
                    if (size >= 5 && fit > 12)
                        labels.Append($"<text x='{N(margin + lx * scale)}' y='{N(head + ly * scale + size / 3)}' font-size='{N(size)}' font-weight='bold' text-anchor='middle' fill='{s.LabelColor}'>{Esc(s.Label)}</text>");
                }
            }
            sb.Append(labels);

            sb.Append($"<text x='{N(margin)}' y='{N(head + roofWidth * scale + 12)}' font-size='8' fill='{Muted}'>{N(roofLength)} × {N(roofWidth)} m</text>");
            if (legend != null)
            {
                var x = w - margin;
                foreach (var l in legend.Reverse())
                {
                    x -= 14 + l.text.Length * 4.4;
                    sb.Append($"<rect x='{N(x)}' y='{N(head + roofWidth * scale + 5)}' width='8' height='8' fill='{l.color}'/><text x='{N(x + 11)}' y='{N(head + roofWidth * scale + 12)}' font-size='8' fill='{Ink}'>{Esc(l.text)}</text>");
                }
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>The plan's aspect ratio (width / height) for the layout of the page, the same arithmetic Plan uses.</summary>
        public static float PlanAspect(double roofLength, double roofWidth, int w = 520)
        {
            var scale = (w - 20) / Math.Max(1, roofLength);
            return (float)(w / Math.Ceiling(24 + roofWidth * scale + 26));
        }

        static double Area(List<double[]> p)
        {
            double a = 0;
            for (var i = 0; i < p.Count; i++) { var q = p[(i + 1) % p.Count]; a += p[i][0] * q[1] - q[0] * p[i][1]; }
            return a / 2;
        }

        static string Tick(double v, double step)
        {
            var d = step >= 1 ? 0 : step >= 0.1 ? 1 : step >= 0.01 ? 2 : 3;
            return v.ToString("0." + new string('#', Math.Max(d, 0)), Inv);
        }

        static string Short(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";
    }
}
