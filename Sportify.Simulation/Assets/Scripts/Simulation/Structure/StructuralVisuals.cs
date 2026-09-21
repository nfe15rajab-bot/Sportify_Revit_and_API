using System;
using System.Collections.Generic;
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
            var n = item.Name ?? "";
            return n.StartsWith("Green roof: ") ? n.Substring("Green roof: ".Length) : n;
        }

        /// <summary>The label for a piece, at its centre and the given height above the roof; null for trees (too many, and they are not the point).</summary>
        public static TextMesh Label(SimulationHud hud, LoadItem item, float height, float size = 0.85f)
        {
            if (item.IsObject || string.IsNullOrEmpty(item.Name)) return null;
            var tm = hud.WorldLabel(Short(item.Name, 26), Position(item, height), size, Color.white);
            return tm;
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
        readonly string _subtitle;
        readonly float _cx, _cy, _footX, _footY;
        float _fullHeight;
        bool _over;

        /// <param name="subtitle">A second line under the percentage: what stands on the bay (its heaviest piece).</param>
        public BayTower(BayResult bay, SimulationHud hud, string subtitle = "")
        {
            _subtitle = subtitle ?? "";
            // a rectangle's own middle and sides; a skewed or cut bay: its centroid and mean sides, so the block stays on the roof
            double cx, cy, sx, sy;
            StructureModel.BayCentre(bay, out cx, out cy);
            StructureModel.BaySpans(bay, out sx, out sy);
            _cx = (float)cx;
            _cy = (float)cy;
            _footX = (float)sx * 0.74f;
            _footY = (float)sy * 0.74f;
            _fullHeight = Mathf.Max(0.08f, bay.utilisation * HeightAtCapacityM);
            _over = bay.status == "over";

            _material = SceneBuilder.LitMaterial(LoadColors.Status(bay.status) * 0.78f, 0.05f);
            _box = SceneBuilder.Box("Bay_" + bay.label, LayoutSpace.ToWorld(_cx, _cy, 0.05f), new Vector3(_footX, 0.1f, _footY), _material);
            _label = hud.WorldLabel(Text(bay.utilisation), LayoutSpace.ToWorld(_cx, _cy, _fullHeight + LabelLift), _subtitle == "" ? 1.0f : 0.8f, Color.white);
            SetGrow(0f);
        }

        public void SetActive(bool active)
        {
            _box.SetActive(active);
            _label.gameObject.SetActive(active);
        }

        /// <summary>Retargets the block to another load against the capacity (a different case): its height, colour and percentage.</summary>
        public void SetUtilisation(float utilisation)
        {
            _fullHeight = Mathf.Max(0.08f, utilisation * HeightAtCapacityM);
            _over = utilisation > 1f;
            _material.color = LoadColors.Status(utilisation > 1f ? "over" : (utilisation > 0.8f ? "marginal" : "ok")) * 0.78f;
            SimulationHud.SetLabelText(_label, Text(utilisation));
        }

        float LabelLift { get { return _subtitle == "" ? 0.9f : 1.4f; } }

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
            _label.gameObject.SetActive(grow > 0.6f);
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
