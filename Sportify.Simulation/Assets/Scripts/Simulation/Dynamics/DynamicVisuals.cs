using System;
using System.Collections.Generic;
using Sportify.Simulation.Structure;
using UnityEngine;

namespace Sportify.Simulation.Dynamics
{
    /// <summary>The colour spectrum: frequency (or anything else) laid out red to violet, like a rainbow.</summary>
    public static class Spectrum
    {
        /// <summary>t = 0 is red, t = 1 is violet.</summary>
        public static Color At(float t, float brightness = 1f)
        {
            return Color.HSVToRGB(Mathf.Lerp(0f, 0.78f, Mathf.Clamp01(t)), 0.92f, Mathf.Clamp01(brightness));
        }
    }

    /// <summary>
    /// A chart drawn on the screen (a child of the camera, in front of the HUD's panels): quads for bars and columns, lines for curves,
    /// text. Coordinates are 0..1 inside the chart; the chart sits in the empty band above the roof.
    /// </summary>
    public sealed class ScreenPlot
    {
        const float Depth = 2.9f;                    // just in front of the HUD, which sits 3 m from the camera
        const int Order = 14;

        readonly Transform _root;
        readonly SimulationHud _hud;
        readonly float _px;                          // world units per screen pixel at that depth
        readonly float _cx, _cy, _w, _h;             // centre and size, in pixels from the screen centre
        readonly List<GameObject> _extra = new List<GameObject>();

        public ScreenPlot(Camera cam, SimulationHud hud, int heightPx, float cxPx, float cyPx, float widthPx, float heightPxChart, Color background)
        {
            _hud = hud;
            _px = 2f * Depth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / heightPx;
            _cx = cxPx; _cy = cyPx; _w = widthPx; _h = heightPxChart;

            _root = new GameObject("Plot").transform;
            _root.SetParent(cam.transform, false);
            var back = Quad(background, Order);
            SetQuad(back, 0f, 0f, 1f, 1f);
        }

        public Vector3 P(float nx, float ny)
        {
            return new Vector3((_cx + (nx - 0.5f) * _w) * _px, (_cy + (ny - 0.5f) * _h) * _px, Depth);
        }

        public GameObject Quad(Color color, int order)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = "PlotQuad";
            SceneBuilder.RemoveCollider(go);
            go.transform.SetParent(_root, false);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = SceneBuilder.OverlayMaterial(color);
            r.sortingOrder = order;
            return go;
        }

        public GameObject Bar(Color color)
        {
            var q = Quad(color, Order + 2);
            q.SetActive(false);
            return q;
        }

        public void SetColor(GameObject quad, Color color)
        {
            quad.GetComponent<Renderer>().sharedMaterial.color = color;
        }

        public void SetQuad(GameObject quad, float nx0, float ny0, float nx1, float ny1)
        {
            var a = P(nx0, ny0);
            var b = P(nx1, ny1);
            quad.transform.localPosition = (a + b) * 0.5f;
            quad.transform.localScale = new Vector3(Mathf.Max(1e-5f, Mathf.Abs(b.x - a.x)), Mathf.Max(1e-5f, Mathf.Abs(b.y - a.y)), 1f);
            quad.SetActive(true);
        }

        public LineRenderer Line(Color color, float widthPx)
        {
            var lr = SceneBuilder.Line("PlotLine", new[] { Vector3.zero, Vector3.zero }, color, widthPx * _px, false, false);
            lr.transform.SetParent(_root, false);
            lr.useWorldSpace = false;
            lr.sortingOrder = Order + 4;
            lr.positionCount = 0;
            return lr;
        }

        public void SetLine(LineRenderer line, IList<Vector2> normalised)
        {
            line.positionCount = normalised.Count;
            for (var i = 0; i < normalised.Count; i++) line.SetPosition(i, P(normalised[i].x, normalised[i].y));
        }

        /// <summary>A label on the chart; heightPx is the text's height on the 1080-pixel-high frame.</summary>
        public TextMesh Text(string text, float nx, float ny, Color color, float heightPx)
        {
            var tm = _hud.WorldLabel(text, Vector3.zero, heightPx * _px, color);
            var outline = tm.transform.Find("Outline");           // the chart's background is dark: no outline copy needed
            if (outline != null) UnityEngine.Object.Destroy(outline.gameObject);
            tm.transform.SetParent(_root, false);
            tm.transform.localPosition = P(nx, ny);
            tm.transform.localRotation = Quaternion.identity;
            foreach (var r in tm.GetComponentsInChildren<Renderer>()) r.sortingOrder += Order + 4;
            _extra.Add(tm.gameObject);
            return tm;
        }

        public void SetActive(bool active)
        {
            _root.gameObject.SetActive(active);
        }

        public void Destroy()
        {
            foreach (var e in _extra) if (e != null) UnityEngine.Object.Destroy(e);
            if (_root != null) UnityEngine.Object.Destroy(_root.gameObject);
        }
    }

    /// <summary>A pool of dots for people: walking in, settled, leaving.</summary>
    public sealed class AgentPool
    {
        readonly List<Transform> _dots = new List<Transform>();
        readonly Material[] _materials;
        readonly Renderer[] _renderers = new Renderer[0];
        readonly List<Renderer> _rend = new List<Renderer>();
        int _shown;

        public AgentPool()
        {
            _materials = new[]
            {
                SceneBuilder.GlowMaterial(new Color(0.55f, 0.90f, 1.00f)),   // walking in
                SceneBuilder.GlowMaterial(new Color(1.00f, 0.92f, 0.55f)),   // settled
                SceneBuilder.GlowMaterial(new Color(0.78f, 0.78f, 0.84f)),   // leaving
            };
        }

        public void Show(IList<Agent> agents)
        {
            while (_dots.Count < agents.Count)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "Person";
                SceneBuilder.RemoveCollider(go);
                go.transform.localScale = Vector3.one * 0.7f;
                _dots.Add(go.transform);
                _rend.Add(go.GetComponent<Renderer>());
            }
            for (var i = 0; i < _dots.Count; i++)
            {
                var on = i < agents.Count;
                if (_dots[i].gameObject.activeSelf != on) _dots[i].gameObject.SetActive(on);
                if (!on) continue;
                var a = agents[i];
                _dots[i].position = LayoutSpace.ToWorld((float)a.X, (float)a.Y, 0.4f);
                _rend[i].sharedMaterial = _materials[Mathf.Clamp(a.State, 0, 2)];
            }
            _shown = agents.Count;
        }

        public void Hide()
        {
            foreach (var d in _dots) d.gameObject.SetActive(false);
            _shown = 0;
        }

        public int Shown { get { return _shown; } }
    }

    /// <summary>
    /// The roof as a sheet that moves: every bay bends in its first mode (a half sine along its long span) by an amplitude of its
    /// own, coloured by how that amplitude compares with the comfort limit. The bending is drawn slowed down and exaggerated: it is
    /// a picture of the computed response, not a simulation of the shaking.
    /// </summary>
    public sealed class VibratingDeck
    {
        readonly Mesh _mesh;
        readonly GameObject _go;
        readonly Vector3[] _v;
        readonly Color32[] _c;
        readonly int _nx, _ny;
        readonly float _height;
        readonly int[] _bay;
        readonly float[] _phi, _slope;

        public VibratingDeck(LoadField f, BaySystem bays, IList<BayResult> results, float height)
        {
            _nx = f.Nx + 1;
            _ny = f.Ny + 1;
            _height = height;
            var n = _nx * _ny;
            _v = new Vector3[n];
            _c = new Color32[n];
            _bay = new int[n];
            _phi = new float[n];
            _slope = new float[n];

            for (var iy = 0; iy < _ny; iy++)
            {
                for (var ix = 0; ix < _nx; ix++)
                {
                    var i = iy * _nx + ix;
                    var x = (float)(ix * f.CellW);
                    var y = (float)(iy * f.CellH);
                    var b = bays.IndexAt(x + 1e-4, y + 1e-4);                     // a vertex on a grid line belongs to the bay that starts there
                    _bay[i] = b;

                    // the bay's box and spans: a rectangle's own; a skewed or cut bay's bounding box and mean sides
                    var bay = results[b];
                    double sx, sy;
                    StructureModel.BaySpans(bay, out sx, out sy);
                    var alongY = sy >= sx;
                    var span = Mathf.Max(0.1f, (float)(alongY ? sy : sx));
                    var extent = Mathf.Max(0.1f, alongY ? bay.y1 - bay.y0 : bay.x1 - bay.x0);
                    var s = Mathf.Clamp01((alongY ? y - bay.y0 : x - bay.x0) / extent);
                    _phi[i] = Mathf.Sin(Mathf.PI * s);
                    var d = Mathf.PI / span * Mathf.Cos(Mathf.PI * s);          // the slope of the mode along the long span
                    _slope[i] = alongY ? d : 0.4f * d;
                    _v[i] = LayoutSpace.ToWorld(x, y, height);
                }
            }

            var tris = new int[(_nx - 1) * (_ny - 1) * 6];
            var t = 0;
            for (var iy = 0; iy < _ny - 1; iy++)
                for (var ix = 0; ix < _nx - 1; ix++)
                {
                    if (f.Coverage[f.Index(ix, iy)] <= 0) continue;               // no deck where there is no roof
                    var a = iy * _nx + ix;
                    var b = a + 1;
                    var cc = a + _nx;
                    var d2 = cc + 1;
                    // wound to face up in Unity's left-handed world (the plan's y runs toward -z)
                    tris[t++] = a; tris[t++] = b; tris[t++] = cc;
                    tris[t++] = b; tris[t++] = d2; tris[t++] = cc;
                }
            System.Array.Resize(ref tris, t);

            _mesh = new Mesh { name = "VibratingDeck", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            _mesh.vertices = _v;
            _mesh.colors32 = _c;
            _mesh.triangles = tris;
            _mesh.RecalculateBounds();

            _go = new GameObject("Deck");
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = _go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = SceneBuilder.OverlayMaterial(Color.white);
            mr.sortingOrder = 5;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _go.SetActive(false);
        }

        public void SetActive(bool active) { _go.SetActive(active); }

        /// <summary>amplitudeM: how far each bay's middle rises and falls at most; colour: each bay's colour; swing: sin of the phase, -1..1.</summary>
        public void Update(float[] amplitudeM, Color[] colour, float swing)
        {
            for (var i = 0; i < _v.Length; i++)
            {
                var b = _bay[i];
                var lift = amplitudeM[b] * _phi[i] * swing;
                _v[i].y = _height + lift;
                var light = Mathf.Clamp(1f + 2.2f * amplitudeM[b] * _slope[i] * swing, 0.45f, 1.5f);
                var col = colour[b];
                _c[i] = new Color(Mathf.Min(1f, col.r * light), Mathf.Min(1f, col.g * light), Mathf.Min(1f, col.b * light), 0.62f);   // see-through, so the courts and gardens under it still show
            }
            _mesh.vertices = _v;
            _mesh.colors32 = _c;
        }
    }
}
