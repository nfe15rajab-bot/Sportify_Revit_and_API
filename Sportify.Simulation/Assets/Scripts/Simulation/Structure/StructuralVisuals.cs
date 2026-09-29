using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sportify.Simulation.Structure
{
    /// <summary>Colours for loads: every load map reads as a share of the deck's capacity, cool to hot.</summary>
    public static class LoadColors
    {
        static readonly Color[] Stops =
        {
            new Color(0.16f, 0.30f, 0.55f),   // 0 %    light
            new Color(0.15f, 0.62f, 0.72f),   // 30 %
            new Color(0.30f, 0.78f, 0.42f),   // 55 %
            new Color(0.98f, 0.82f, 0.22f),   // 80 %
            new Color(0.93f, 0.28f, 0.22f),   // 100 %  at the capacity
            new Color(0.55f, 0.10f, 0.14f),   // 150 %  far over
        };
        static readonly float[] At = { 0f, 0.30f, 0.55f, 0.80f, 1.0f, 1.5f };

        public static Color Ramp(float share)
        {
            if (share <= At[0]) return Stops[0];
            for (var i = 1; i < At.Length; i++)
                if (share <= At[i]) return Color.Lerp(Stops[i - 1], Stops[i], (share - At[i - 1]) / (At[i] - At[i - 1]));
            return Stops[Stops.Length - 1];
        }

        public static Color Status(string status)
        {
            switch (status)
            {
                case "over": return new Color(0.93f, 0.28f, 0.22f);
                case "marginal": return new Color(0.98f, 0.78f, 0.20f);
                default: return new Color(0.32f, 0.78f, 0.45f);
            }
        }
    }

    /// <summary>
    /// The people the analysis expects, as dots scattered over the cells in proportion to how many it puts in each. The dots are a
    /// picture of the distribution (a fixed random seed, so a rerun looks the same), not a simulation of anyone walking: that is
    /// the dynamic analysis.
    /// </summary>
    public sealed class PeopleDots
    {
        readonly List<Transform> _dots = new List<Transform>();
        readonly List<Vector2> _base = new List<Vector2>();
        readonly List<float> _phase = new List<float>();
        const float Height = 0.32f;

        public int Count { get { return _dots.Count; } }

        public PeopleDots(LoadField field, float persons, int maxDots)
        {
            var n = Mathf.Min(maxDots, Mathf.RoundToInt(persons));
            if (n <= 0) return;

            var cumulative = new double[field.Persons.Length];
            double sum = 0;
            for (var i = 0; i < cumulative.Length; i++) { sum += field.Persons[i]; cumulative[i] = sum; }
            if (sum <= 0) return;

            var rng = new System.Random(4242);
            var material = SceneBuilder.GlowMaterial(new Color(1f, 0.92f, 0.55f));
            for (var d = 0; d < n; d++)
            {
                var pick = rng.NextDouble() * sum;
                var lo = 0;
                var hi = cumulative.Length - 1;
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (cumulative[mid] < pick) lo = mid + 1; else hi = mid;
                }
                var ix = lo % field.Nx;
                var iy = lo / field.Nx;
                var x = (float)((ix + rng.NextDouble()) * field.CellW);
                var y = (float)((iy + rng.NextDouble()) * field.CellH);

                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Person";
                SceneBuilder.RemoveCollider(go);
                go.transform.localScale = Vector3.one * 0.55f;
                go.GetComponent<Renderer>().sharedMaterial = material;
                go.transform.position = LayoutSpace.ToWorld(x, y, Height);
                go.SetActive(false);
                _dots.Add(go.transform);
                _base.Add(new Vector2(x, y));
                _phase.Add((float)(rng.NextDouble() * 6.28));
            }
        }

        public void SetActive(bool active)
        {
            foreach (var d in _dots) d.gameObject.SetActive(active);
        }

        /// <summary>Dots appear one after another (reveal 0..1) and shuffle about a little, so the eye reads a crowd rather than a plot.</summary>
        public void Animate(float time, float reveal)
        {
            for (var i = 0; i < _dots.Count; i++)
            {
                var visible = i < reveal * _dots.Count;
                _dots[i].gameObject.SetActive(visible);
                if (!visible) continue;
                var p = _phase[i];
                _dots[i].position = LayoutSpace.ToWorld(_base[i].x + 0.14f * Mathf.Sin(time * 2.1f + p), _base[i].y + 0.14f * Mathf.Cos(time * 2.6f + p), Height);
            }
        }
    }

    /// <summary>
    /// How tall a label standing over a bay or a piece may be so it stays inside that bay's or piece's own width and depth, and so never runs into
    /// its neighbour's; 0 when it would be too small to read there, and then it is left out rather than drawn over the others. A fixed size used to
    /// put a 10 m wide "22% / Bauder EXTENSIVE Light" over each 2.4 m bay of the Goldbeck roof's 193 (user, 2026-09-29). WorldLabel's size is a
    /// line's height in metres; a character of the TextMesh's Arial is about half as wide (CharWidth, rounded up).
    /// </summary>
    public static class LabelFit
    {
        const float CharWidth = 0.55f;
        const float WidthShare = 0.85f;    // of the bay or piece, so two neighbours' labels keep a gap
        const float DepthShare = 0.45f;    // all the lines together: the camera looks down at 40 degrees, so a depth reads at about two thirds on screen

        /// <summary>The smallest line worth drawing, in metres: about 12 pixels of a 1920-pixel frame. Set from the camera (ForView).</summary>
        public static float Legible = 0.2f;

        /// <summary>Sets Legible from how much the camera sees across the frame at the roof's middle: a bigger roof, fewer pixels per metre.</summary>
        public static void ForView(Camera cam, Vector3 target, float aspect)
        {
            var across = 2f * Vector3.Distance(cam.transform.position, target) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * aspect;
            Legible = Mathf.Max(0.12f, across * 12f / 1920f);
        }

        /// <summary>The line height for the text (lines split on \n) over a widthM x depthM place, at most preferred; 0 when that is under Legible.</summary>
        public static float Size(string text, float widthM, float depthM, float preferred)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            var lines = text.Split('\n');
            var longest = 1;
            foreach (var line in lines) longest = Mathf.Max(longest, line.Length);
            var size = Mathf.Min(preferred, Mathf.Min(widthM * WidthShare / (longest * CharWidth), depthM * DepthShare / lines.Length));
            return size >= Legible ? size : 0f;
        }

        /// <summary>The text on two lines, broken at the space nearest its middle ("Modular Tower" over "Slide"); the text itself when it has no space.</summary>
        public static string TwoLines(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Contains("\n")) return text;
            var best = -1;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == ' ' && (best < 0 || Mathf.Abs(i - text.Length / 2) < Mathf.Abs(best - text.Length / 2))) best = i;
            return best < 0 ? text : text.Substring(0, best) + "\n" + text.Substring(best + 1);
        }

        /// <summary>About how wide and tall the text stands at that line height, in metres (width x, height y).</summary>
        public static Vector2 Extent(string text, float size)
        {
            var lines = (text ?? "").Split('\n');
            var longest = 1;
            foreach (var line in lines) longest = Mathf.Max(longest, line.Length);
            return new Vector2(longest * CharWidth * size, lines.Length * size);
        }
    }

    /// <summary>
    /// The frame's labels, so none runs into another: each label claims its rectangle on the frame, and a label whose rectangle meets one claimed before
    /// it is left out. Claimed in order of importance (the most loaded bay first), so what is left out is what matters least. The camera never moves in
    /// these films, so it is decided once. Measured with the camera's own view and the video's aspect (the camera's own aspect is the batch-mode
    /// screen's until the recorder renders).
    /// </summary>
    public sealed class ScreenSpace
    {
        readonly Camera _cam;
        readonly float _w, _h;
        readonly List<Rect> _taken = new List<Rect>();

        public ScreenSpace(Camera cam, int widthPx, int heightPx)
        {
            _cam = cam;
            _w = widthPx;
            _h = heightPx;
        }

        /// <summary>A frame that starts with what another has claimed (the grid names, which stay on screen once shown).</summary>
        public ScreenSpace(ScreenSpace from)
        {
            _cam = from._cam;
            _w = from._w;
            _h = from._h;
            _taken.AddRange(from._taken);
        }

        /// <summary>Where a point in the scene lands on the frame, in pixels.</summary>
        public Vector2 ToFrame(Vector3 world)
        {
            var p = _cam.transform.InverseTransformPoint(world);
            var t = Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var z = Mathf.Max(0.01f, p.z);
            return new Vector2((p.x / (z * t * (_w / _h)) * 0.5f + 0.5f) * _w, (p.y / (z * t) * 0.5f + 0.5f) * _h);
        }

        /// <summary>Claims the frame rectangle of a label centred at the point (text at that line height); false, and nothing claimed, when it meets one claimed before.</summary>
        public bool Claim(Vector3 at, string text, float size, float marginPx = 5f)
        {
            var extent = LabelFit.Extent(text, size);
            var centre = ToFrame(at);
            var corner = ToFrame(at + _cam.transform.right * extent.x * 0.5f + _cam.transform.up * extent.y * 0.5f);
            var hw = Mathf.Abs(corner.x - centre.x) + marginPx;
            var hh = Mathf.Abs(corner.y - centre.y) + marginPx;
            var rect = new Rect(centre.x - hw, centre.y - hh, 2f * hw, 2f * hh);
            foreach (var taken in _taken)
                if (taken.Overlaps(rect)) return false;
            _taken.Add(rect);
            return true;
        }
    }

    /// <summary>
    /// Labels shown together (one scene's, or the ones that stay on for the whole film), placed so none runs into another: each in the first of its
    /// wordings that is legible over its place (LabelFit) and still has room on the frame (ScreenSpace), as big as fits; left out when none does.
    /// Add the most important first. Used by every analysis film, so a crowded roof (the Goldbeck roof's thin edge beds, two courts side by side)
    /// reads in all of them.
    /// </summary>
    public sealed class LabelGroup
    {
        readonly SimulationHud _hud;
        readonly ScreenSpace _space;

        /// <summary>The labels placed, to show, hide or destroy together.</summary>
        public readonly List<GameObject> Labels = new List<GameObject>();

        public LabelGroup(SimulationHud hud, ScreenSpace space)
        {
            _hud = hud;
            _space = space;
        }

        /// <summary>A label at the point, over a place widthM x depthM, in the first wording that fits and has room; null when none does.</summary>
        public TextMesh Add(Vector3 at, float widthM, float depthM, float preferred, Color color, params string[] wordings)
        {
            foreach (var text in wordings)
            {
                if (string.IsNullOrEmpty(text)) continue;
                var size = LabelFit.Size(text, widthM, depthM, preferred);
                if (size <= 0f || !_space.Claim(at, text, size)) continue;
                var label = _hud.WorldLabel(text, at, size, color);
                Labels.Add(label.gameObject);
                return label;
            }
            return null;
        }
    }

    /// <summary>What is on the roof, in words: a label with each piece's name (its sport, activity or build-up), for every recording.</summary>
    public static class PieceNames
    {
        public static string Short(string text, int max = 24)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "...";
        }

        /// <summary>The name without its "Green roof: " prefix, for tight places (the bay blocks): "Sloped Sedum", "Deep tree bed (800 mm)", "Badminton (standard)".</summary>
        public static string Compact(LoadItem item)
        {
            return Compact(item.Name);
        }

        public static string Compact(string name)
        {
            var n = name ?? "";
            return n.StartsWith("Green roof: ") ? n.Substring("Green roof: ".Length) : n;
        }

        /// <summary>
        /// The label for a piece, at its centre and the given height above the roof, as big as fits the piece (at most size): its name, else without
        /// the "Green roof: " prefix, else shortened. Null for trees (too many, and they are not the point) and for a piece too small to carry a legible name.
        /// </summary>
        public static TextMesh Label(SimulationHud hud, LoadItem item, float height, ScreenSpace space = null, float size = 0.85f)
        {
            if (item.IsObject || string.IsNullOrEmpty(item.Name)) return null;
            string text;
            float fitted;
            if (!Fit(item, size, true, space, Position(item, height), out text, out fitted, Short(item.Name, 26), Short(Compact(item), 26), LabelFit.TwoLines(Short(Compact(item), 30)), Short(Compact(item), 14))) return null;
            return hud.WorldLabel(text, Position(item, height), fitted, Color.white);
        }

        /// <summary>
        /// Which of the texts goes over the piece, and how big: of those legible there (LabelFit) the first (largest: the one that can be drawn biggest,
        /// the first of equals), and, given a frame, only one whose room at the point is still free (ScreenSpace). False when none is.
        /// </summary>
        public static bool Fit(LoadItem item, float preferred, bool largest, ScreenSpace space, Vector3 at, out string text, out float size, params string[] candidates)
        {
            var sized = candidates.Select((c, i) => new { c, i, s = LabelFit.Size(c, (float)item.Width, (float)item.Height, preferred) }).Where(x => x.s > 0f);
            if (largest) sized = sized.OrderByDescending(x => Mathf.Round(x.s * 100f)).ThenBy(x => x.i);
            foreach (var x in sized)
            {
                if (space != null && !space.Claim(at, x.c, x.s)) continue;
                text = x.c;
                size = x.s;
                return true;
            }
            text = null;
            size = 0f;
            return false;
        }

        public static Vector3 Position(LoadItem item, float height)
        {
            return LayoutSpace.ToWorld((float)(item.X + item.Width * 0.5), (float)(item.Y + item.Height * 0.5), height);
        }
    }

    /// <summary>One bay of the structural grid as a block whose height is its load against the deck's capacity, with its percentage above it.</summary>
    public sealed class BayTower
    {
        public const float HeightAtCapacityM = 1.5f;

        readonly GameObject _box;
        readonly TextMesh _label;
        readonly Material _material;
        readonly float _cx, _cy, _footX, _footY;
        readonly float _labelSize;
        string _subtitle;
        bool _labelFits;
        float _utilisation;
        float _fullHeight;
        bool _over;

        /// <summary>The bay's load against the capacity it shows now: the order bays claim room for their labels in (ScreenSpace).</summary>
        public float Utilisation { get { return _utilisation; } }

        /// <param name="subtitle">A second line under the percentage: what stands on the bay (its heaviest piece; see Subtitles).</param>
        public BayTower(BayResult bay, SimulationHud hud, string subtitle = "")
        {
            // a rectangle's own middle and sides; a skewed or cut bay: its centroid and mean sides, so the block stays on the roof
            double cx, cy, sx, sy;
            StructureModel.BayCentre(bay, out cx, out cy);
            StructureModel.BaySpans(bay, out sx, out sy);
            _cx = (float)cx;
            _cy = (float)cy;
            _footX = (float)sx * 0.74f;
            _footY = (float)sy * 0.74f;
            _utilisation = bay.utilisation;
            _fullHeight = Mathf.Max(0.08f, bay.utilisation * HeightAtCapacityM);
            _over = bay.status == "over";

            _material = SceneBuilder.LitMaterial(LoadColors.Status(bay.status) * 0.78f, 0.05f);
            _box = SceneBuilder.Box("Bay_" + bay.label, LayoutSpace.ToWorld(_cx, _cy, 0.05f), new Vector3(_footX, 0.1f, _footY), _material);
            // The label fits the bay (LabelFit): the percentage and the piece's name when both fit, the percentage alone when only that does, none
            // when not even that is legible at the bay's size. Sized for "100%" whatever the number, so neighbouring percentages match.
            _subtitle = subtitle ?? "";
            _labelSize = _subtitle == "" ? 0f : LabelFit.Size("100%\n" + _subtitle, (float)sx, (float)sy, 0.8f);
            if (_labelSize <= 0f)
            {
                _subtitle = "";
                _labelSize = LabelFit.Size("100%", (float)sx, (float)sy, 0.9f);
            }
            _labelFits = _labelSize > 0f;
            _label = hud.WorldLabel(Text(bay.utilisation), LayoutSpace.ToWorld(_cx, _cy, _fullHeight + LabelLift), _labelFits ? _labelSize : LabelFit.Legible, Color.white);
            SetGrow(0f);
        }

        /// <summary>
        /// The second line for each bay: the name of the piece that loads it most, once per piece, on the bay that piece loads hardest. The same
        /// name over thirty bays of one green roof said nothing the first did not, and ran into every neighbour's.
        /// </summary>
        public static string[] Subtitles(IList<BayResult> bays, IEnumerable<LoadItem> items)
        {
            var result = new string[bays.Count];
            var named = new HashSet<string>();
            foreach (var i in Enumerable.Range(0, bays.Count).OrderByDescending(i => bays[i].utilisation))
            {
                result[i] = "";
                var label = bays[i].topContributor;
                if (string.IsNullOrEmpty(label) || !named.Add(label)) continue;
                var top = items.FirstOrDefault(it => it.Label == label);
                if (top != null) result[i] = PieceNames.Short(PieceNames.Compact(top), 22);
            }
            return result;
        }

        public void SetActive(bool active)
        {
            _box.SetActive(active);
            _label.gameObject.SetActive(active && _labelFits);
        }

        /// <summary>
        /// Claims the label's room on the frame, where it stands at the block's full height: with the piece's name, else the percentage alone, else
        /// (the room is taken by a more loaded bay's) no label - the block's height and colour still say it.
        /// </summary>
        public void Claim(ScreenSpace space)
        {
            if (!_labelFits) return;
            if (_subtitle != "" && space.Claim(LabelPoint(), "100%\n" + _subtitle, _labelSize)) return;
            if (_subtitle != "")
            {
                _subtitle = "";
                SimulationHud.SetLabelText(_label, Text(_utilisation));
            }
            _labelFits = space.Claim(LabelPoint(), "100%", _labelSize);
        }

        Vector3 LabelPoint() { return LayoutSpace.ToWorld(_cx, _cy, _fullHeight + LabelLift); }

        /// <summary>Retargets the block to another load against the capacity (a different case): its height, colour and percentage.</summary>
        public void SetUtilisation(float utilisation)
        {
            _utilisation = utilisation;
            _fullHeight = Mathf.Max(0.08f, utilisation * HeightAtCapacityM);
            _over = utilisation > 1f;
            _material.color = LoadColors.Status(utilisation > 1f ? "over" : (utilisation > 0.8f ? "marginal" : "ok")) * 0.78f;
            SimulationHud.SetLabelText(_label, Text(utilisation));
        }

        // how far the label's middle sits above the block's top: clear of it by a little more than half the label's own height
        float LabelLift { get { return 0.3f + 0.6f * _labelSize * (_subtitle == "" ? 1f : 2f); } }

        string Text(float utilisation)
        {
            return Mathf.RoundToInt(utilisation * 100f) + "%" + (_subtitle == "" ? "" : "\n" + _subtitle);
        }

        /// <summary>grow 0..1: the block rises to its height; the label appears near the end.</summary>
        public void SetGrow(float grow, float pulseTime = 0f)
        {
            var h = Mathf.Max(0.05f, _fullHeight * Mathf.Clamp01(grow));
            var pulse = _over && grow >= 1f ? 1f + 0.03f * Mathf.Sin(pulseTime * 6f) : 1f;
            _box.transform.position = LayoutSpace.ToWorld(_cx, _cy, h * 0.5f);
            _box.transform.localScale = new Vector3(_footX * pulse, h, _footY * pulse);
            _label.transform.position = LayoutSpace.ToWorld(_cx, _cy, h + LabelLift);
            _label.gameObject.SetActive(grow > 0.6f && _labelFits);
        }
    }

    /// <summary>A flat ring on the roof, for the centres the balance compares.</summary>
    public static class Markers
    {
        public static LineRenderer Ring(string name, Color color, float radius, float width)
        {
            var points = new Vector3[41];
            for (var i = 0; i < points.Length; i++)
            {
                var a = i / 40f * Mathf.PI * 2f;
                points[i] = new Vector3(Mathf.Cos(a) * radius, 0.4f, Mathf.Sin(a) * radius);
            }
            var line = SceneBuilder.Line(name, points, color, width, true, false);
            line.useWorldSpace = false;
            return line;
        }
    }
}
