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

    /// <summary>One bay of the structural grid as a block whose height is its load against the deck's capacity, with its percentage above it.</summary>
    public sealed class BayTower
    {
        public const float HeightAtCapacityM = 1.5f;

        readonly GameObject _box;
        readonly TextMesh _label;
        readonly float _cx, _cy, _footX, _footY, _fullHeight;
        readonly bool _over;

        public BayTower(BayResult bay, SimulationHud hud)
        {
            _cx = (bay.x0 + bay.x1) * 0.5f;
            _cy = (bay.y0 + bay.y1) * 0.5f;
            _footX = (bay.x1 - bay.x0) * 0.74f;
            _footY = (bay.y1 - bay.y0) * 0.74f;
            _fullHeight = Mathf.Max(0.08f, bay.utilisation * HeightAtCapacityM);
            _over = bay.status == "over";

            _box = SceneBuilder.Box("Bay_" + bay.label, LayoutSpace.ToWorld(_cx, _cy, 0.05f), new Vector3(_footX, 0.1f, _footY),
                SceneBuilder.LitMaterial(LoadColors.Status(bay.status) * 0.78f, 0.05f));
            _label = hud.WorldLabel(Mathf.RoundToInt(bay.utilisation * 100f) + "%", LayoutSpace.ToWorld(_cx, _cy, _fullHeight + 0.9f), 1.0f, Color.white);
            SetGrow(0f);
        }

        public void SetActive(bool active)
        {
            _box.SetActive(active);
            _label.gameObject.SetActive(active);
        }

        /// <summary>grow 0..1: the block rises to its height; the label appears near the end.</summary>
        public void SetGrow(float grow, float pulseTime = 0f)
        {
            var h = Mathf.Max(0.05f, _fullHeight * Mathf.Clamp01(grow));
            var pulse = _over && grow >= 1f ? 1f + 0.03f * Mathf.Sin(pulseTime * 6f) : 1f;
            _box.transform.position = LayoutSpace.ToWorld(_cx, _cy, h * 0.5f);
            _box.transform.localScale = new Vector3(_footX * pulse, h, _footY * pulse);
            _label.transform.position = LayoutSpace.ToWorld(_cx, _cy, h + 0.9f);
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
