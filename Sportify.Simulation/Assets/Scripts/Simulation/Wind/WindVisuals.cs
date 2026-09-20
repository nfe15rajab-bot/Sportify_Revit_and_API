using System;
using UnityEngine;

namespace Sportify.Simulation.Wind
{
    /// <summary>
    /// Many flat streaks in one mesh: wind lines and blown dust. One mesh with a quad per streak,
    /// rewritten each frame, is far cheaper than a thousand GameObjects and keeps the video
    /// deterministic (nothing here depends on the particle system's own clock).
    /// </summary>
    public sealed class RibbonBatch
    {
        readonly Mesh _mesh;
        readonly Vector3[] _vertices;
        readonly Color32[] _colors;
        readonly int _count;

        public RibbonBatch(string name, int count, int sortingOrder, Vector3 boundsCentre, Vector3 boundsSize)
        {
            _count = count;
            _vertices = new Vector3[count * 4];
            _colors = new Color32[count * 4];

            var triangles = new int[count * 6];
            for (var i = 0; i < count; i++)
            {
                var v = i * 4;
                var t = i * 6;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 2; triangles[t + 4] = v + 1; triangles[t + 5] = v + 3;
            }

            _mesh = new Mesh { name = name };
            _mesh.MarkDynamic();
            _mesh.vertices = _vertices;
            _mesh.colors32 = _colors;
            _mesh.triangles = triangles;
            _mesh.bounds = new Bounds(boundsCentre, boundsSize);   // fixed: the vertices move, the camera should never cull it

            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = SceneBuilder.OverlayMaterial(Color.white);
            meshRenderer.sortingOrder = sortingOrder;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
        }

        /// <summary>A streak from tail to head, lying flat (its width runs horizontally, perpendicular to its length).</summary>
        public void Set(int i, Vector3 tail, Vector3 head, float width, Color32 color)
        {
            var along = head - tail;
            along.y = 0f;
            if (along.sqrMagnitude < 1e-8f) along = Vector3.right;
            along.Normalize();
            var side = new Vector3(-along.z, 0f, along.x) * (width * 0.5f);

            var v = i * 4;
            _vertices[v] = tail - side;
            _vertices[v + 1] = tail + side;
            _vertices[v + 2] = head - side;
            _vertices[v + 3] = head + side;
            _colors[v] = _colors[v + 1] = _colors[v + 2] = _colors[v + 3] = color;
        }

        /// <summary>A streak from top to bottom facing the camera (its width runs along x), for rain and drops in a front view.</summary>
        public void SetVertical(int i, Vector3 top, Vector3 bottom, float width, Color32 color)
        {
            var side = new Vector3(width * 0.5f, 0f, 0f);
            var v = i * 4;
            _vertices[v] = top - side;
            _vertices[v + 1] = top + side;
            _vertices[v + 2] = bottom - side;
            _vertices[v + 3] = bottom + side;
            _colors[v] = _colors[v + 1] = _colors[v + 2] = _colors[v + 3] = color;
        }

        public void Hide(int i)
        {
            var v = i * 4;
            var nowhere = new Vector3(0f, -1000f, 0f);
            _vertices[v] = _vertices[v + 1] = _vertices[v + 2] = _vertices[v + 3] = nowhere;
        }

        public void Apply()
        {
            _mesh.vertices = _vertices;
            _mesh.colors32 = _colors;
        }

        public int Count => _count;
    }

    /// <summary>
    /// A coloured grid laid over part of the roof: one texel per analysis cell, so what the eye sees
    /// is exactly the grid the numbers were computed on. Unlit and translucent, so the ground under it
    /// still reads.
    /// </summary>
    public sealed class HeatLayer
    {
        readonly Texture2D _texture;
        readonly Color32[] _pixels;
        readonly GameObject _go;

        public readonly int Nx, Ny;
        public readonly float XMin, YMin, CellW, CellH;

        public HeatLayer(string name, float xMin, float yMin, float width, float height, int nx, int ny, float heightM, int sortingOrder)
        {
            Nx = nx;
            Ny = ny;
            XMin = xMin;
            YMin = yMin;
            CellW = width / nx;
            CellH = height / ny;

            _pixels = new Color32[nx * ny];
            _texture = new Texture2D(nx, ny, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };

            _go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _go.name = name;
            SceneBuilder.RemoveCollider(_go);
            _go.transform.position = LayoutSpace.ToWorld(xMin + width * 0.5f, yMin + height * 0.5f, heightM);
            _go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);      // face up
            _go.transform.localScale = new Vector3(width, height, 1f);

            var material = SceneBuilder.OverlayMaterial(Color.white);
            material.mainTexture = _texture;
            var layerRenderer = _go.GetComponent<Renderer>();
            layerRenderer.sharedMaterial = material;
            layerRenderer.sortingOrder = sortingOrder;
            layerRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            layerRenderer.receiveShadows = false;

            Clear();
        }

        /// <summary>Layout coordinates of the centre of a cell (column, row counted down the plan from the top).</summary>
        public Vector2 CellCentre(int ix, int iy)
        {
            return new Vector2(XMin + (ix + 0.5f) * CellW, YMin + (iy + 0.5f) * CellH);
        }

        /// <summary>Colours every cell from its centre's layout coordinates and cell indices.</summary>
        public void Fill(Func<int, int, Vector2, Color32> colorAt)
        {
            for (var iy = 0; iy < Ny; iy++)
            {
                for (var ix = 0; ix < Nx; ix++)
                {
                    // Texture row 0 is the quad's local bottom, which lies at the plan's LOWEST edge (largest y).
                    var row = Ny - 1 - iy;
                    var centre = CellCentre(ix, iy);
                    // where the roof has no roof (the notch of an L) there is nothing to colour
                    var roof = SceneBuilder.CurrentRoof;
                    _pixels[row * Nx + ix] = roof != null && !roof.IsRectangle && !roof.Contains(centre.x, centre.y) ? new Color32(0, 0, 0, 0) : colorAt(ix, iy, centre);
                }
            }
            Upload();
        }

        public void Clear()
        {
            for (var i = 0; i < _pixels.Length; i++) _pixels[i] = new Color32(0, 0, 0, 0);
            Upload();
        }

        void Upload()
        {
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);
        }

        public void SetActive(bool active)
        {
            _go.SetActive(active);
        }
    }

    /// <summary>
    /// One plant standing on its bed: a trunk and crown (or a low mound), a ring showing its root plate,
    /// swaying in the wind. The tilt is a picture of the load the analysis found, not a simulation of the
    /// plant: a tree the analysis says would be blown over leans further, and turns red.
    /// </summary>
    public sealed class PlantVisual
    {
        static readonly Color[] Greens =
        {
            new Color(0.20f, 0.50f, 0.25f), new Color(0.27f, 0.58f, 0.28f),
            new Color(0.18f, 0.42f, 0.30f), new Color(0.33f, 0.55f, 0.22f),
        };

        public static readonly Color Ok = new Color(0.32f, 0.80f, 0.42f, 0.70f);
        public static readonly Color Marginal = new Color(1.00f, 0.78f, 0.20f, 0.80f);
        public static readonly Color Fails = new Color(0.95f, 0.26f, 0.22f, 0.85f);

        readonly PlantWindResult _data;
        readonly Transform _root;
        readonly Material _crownMaterial;
        readonly Material _ringMaterial;
        readonly Color _baseColor;
        readonly float _phase;
        readonly float _swayDeg;
        readonly bool _stability;

        public PlantWindResult Data => _data;
        public Vector3 CrownTop { get; }

        public PlantVisual(PlantWindResult data, float baseY, bool stability)
        {
            _data = data;
            _stability = stability;

            var hash = Math.Abs((data.species ?? "").GetHashCode());
            _baseColor = Greens[hash % Greens.Length];
            _phase = (hash % 628) / 100f;

            var form = (data.form ?? "").ToLowerInvariant();
            var height = Mathf.Max(0.05f, data.heightM);
            var crown = Mathf.Max(0.2f, data.crownM);

            var rootGo = new GameObject("Plant_" + data.species);
            rootGo.transform.position = LayoutSpace.ToWorld(data.xM, data.yM, baseY);
            _root = rootGo.transform;
            _crownMaterial = SceneBuilder.LitMaterial(_baseColor, 0.05f);

            if (form == "tree" || height >= 2f)
            {
                var trunkH = height * 0.5f;
                var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                trunk.name = "Trunk";
                SceneBuilder.RemoveCollider(trunk);
                trunk.transform.SetParent(_root, false);
                trunk.transform.localPosition = new Vector3(0f, trunkH * 0.5f, 0f);
                var trunkR = 0.10f + 0.012f * height;
                trunk.transform.localScale = new Vector3(trunkR * 2f, trunkH * 0.5f, trunkR * 2f);   // a cylinder primitive is 2 m tall
                trunk.GetComponent<Renderer>().sharedMaterial = SceneBuilder.LitMaterial(new Color(0.36f, 0.26f, 0.18f), 0.02f);

                var crownH = height * 0.65f;
                Ellipsoid("Crown", new Vector3(0f, height * 0.35f + crownH * 0.5f, 0f), new Vector3(crown, crownH, crown));
                CrownTop = LayoutSpace.ToWorld(data.xM, data.yM, baseY + height);
            }
            else if (form == "groundcover")
            {
                var mat = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                mat.name = "Mat";
                SceneBuilder.RemoveCollider(mat);
                mat.transform.SetParent(_root, false);
                mat.transform.localPosition = new Vector3(0f, 0.04f, 0f);
                mat.transform.localScale = new Vector3(crown, 0.04f, crown);
                _baseColor = new Color(0.55f, 0.62f, 0.36f);
                _crownMaterial.color = _baseColor;
                mat.GetComponent<Renderer>().sharedMaterial = _crownMaterial;
                CrownTop = LayoutSpace.ToWorld(data.xM, data.yM, baseY + 0.2f);
            }
            else
            {
                if (form == "grass") { _baseColor = new Color(0.46f, 0.64f, 0.60f); _crownMaterial.color = _baseColor; }
                if (form == "shrub") { _baseColor = new Color(0.44f, 0.46f, 0.66f); _crownMaterial.color = _baseColor; }
                Ellipsoid("Mound", new Vector3(0f, height * 0.4f, 0f), new Vector3(crown, height * 0.9f, crown));
                CrownTop = LayoutSpace.ToWorld(data.xM, data.yM, baseY + height);
            }

            _swayDeg = form == "tree" || height >= 2f ? 3.2f : form == "shrub" ? 5f : form == "grass" ? 8f : 0f;

            // Root plate of a tree: the disc of substrate that holds it up.
            if (stability)
            {
                var plateR = Mathf.Min(0.25f * crown, 2.5f);
                var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                ring.name = "RootPlate";
                SceneBuilder.RemoveCollider(ring);
                ring.transform.position = LayoutSpace.ToWorld(data.xM, data.yM, baseY + 0.04f);
                ring.transform.localScale = new Vector3(plateR * 2f, 0.01f, plateR * 2f);
                _ringMaterial = SceneBuilder.OverlayMaterial(Ok);
                var ringRenderer = ring.GetComponent<Renderer>();
                ringRenderer.sharedMaterial = _ringMaterial;
                ringRenderer.sortingOrder = 4;
            }
        }

        void Ellipsoid(string name, Vector3 localCentre, Vector3 size)
        {
            var e = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            e.name = name;
            SceneBuilder.RemoveCollider(e);
            e.transform.SetParent(_root, false);
            e.transform.localPosition = localCentre;
            e.transform.localScale = size;
            e.GetComponent<Renderer>().sharedMaterial = _crownMaterial;
        }

        public static Color StatusColor(float utilisation)
        {
            return utilisation > 1f ? Fails : (utilisation > (float)WindModel.MarginalFrom ? Marginal : Ok);
        }

        /// <param name="windDir">Unit vector, world space, the way the wind blows; zero for calm.</param>
        /// <param name="speedFactor">Local speed relative to the calm interior (1 to about 1.5).</param>
        /// <param name="utilisation">This plant's load against what it can resist, for the direction shown.</param>
        /// <param name="showLoad">Lean and colour the plant by its load (the wind sweep); otherwise it only sways.</param>
        public void Update(float time, Vector3 windDir, float speedFactor, float utilisation, bool showLoad)
        {
            var gust = 0.75f + 0.25f * Mathf.Sin(time * 2.1f + _phase) + 0.10f * Mathf.Sin(time * 5.3f + _phase * 2f);
            var angle = _swayDeg * speedFactor * speedFactor * gust;
            var loadFactor = 0f;

            if (showLoad && _stability)
            {
                if (utilisation > 1f) angle += Mathf.Min(utilisation, 2f) * 7f;
                else if (utilisation > (float)WindModel.MarginalFrom) angle += (utilisation - 0.8f) / 0.2f * 3f;

                loadFactor = Mathf.Clamp01((utilisation - 0.8f) / 0.4f);
                if (_ringMaterial != null) _ringMaterial.color = StatusColor(utilisation);
            }

            if (!showLoad && _ringMaterial != null) _ringMaterial.color = StatusColor(_data.utilisation);

            var rad = angle * Mathf.Deg2Rad;
            var tilted = windDir.sqrMagnitude > 1e-6f
                ? (windDir.normalized * Mathf.Sin(rad) + Vector3.up * Mathf.Cos(rad)).normalized
                : Vector3.up;
            _root.rotation = Quaternion.FromToRotation(Vector3.up, tilted);

            _crownMaterial.color = Color.Lerp(_baseColor, new Color(0.86f, 0.22f, 0.18f), loadFactor);
        }

        /// <summary>Colours the root plate by the plant's worst load over all directions (the summary scenes).</summary>
        public void ShowWorstStatus()
        {
            if (_ringMaterial != null) _ringMaterial.color = StatusColor(_data.utilisation);
        }
    }
}
