using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sportify.Simulation
{
    /// <summary>
    /// Builds the whole scene from code (the .unity file is deliberately
    /// empty): the roof and what stands on it for the video, plus the invisible
    /// trigger volumes the analysis actually tests shots against.
    /// </summary>
    public static class SceneBuilder
    {
        // How high a circulation gap's protected airspace reaches (the original value).
        // The roof edge has no height limit: a ball crossing the outline at any height
        // has left the roof (the first version's 12 m wall missed high lobs).
        public const float CirculationZoneHeightM = 9f;

        public static readonly Color BackgroundColor = new Color(0.07f, 0.085f, 0.115f);
        public static readonly Color RoofColor = new Color(0.72f, 0.72f, 0.70f);
        public static readonly Color EdgeColor = new Color(0.93f, 0.30f, 0.26f);
        public static readonly Color CirculationColor = new Color(1.00f, 0.78f, 0.14f, 0.40f);
        public static readonly Color GardenColor = new Color(0.30f, 0.60f, 0.28f);
        public static readonly Color ActivityColor = new Color(0.82f, 0.60f, 0.32f);

        static Shader _lit, _unlit, _sprite;
        static Material _lineMaterial;

        static Shader LitShader => _lit != null ? _lit : (_lit = Shader.Find("Standard"));
        static Shader UnlitShader => _unlit != null ? _unlit : (_unlit = Shader.Find("Unlit/Color"));
        static Shader SpriteShader => _sprite != null ? _sprite : (_sprite = Shader.Find("Sprites/Default"));

        // ---------------------------------------------------------------- materials

        public static Material LitMaterial(Color c, float glossiness = 0.10f)
        {
            var m = new Material(LitShader) { color = c };
            m.SetFloat("_Glossiness", glossiness);
            m.SetFloat("_Metallic", 0f);
            return m;
        }

        /// <summary>
        /// Reads as its own colour: a dark base plus emission. (A bright base
        /// under strong emission blows out to white and loses the shot's colour.)
        /// </summary>
        public static Material GlowMaterial(Color c)
        {
            var m = LitMaterial(new Color(c.r * 0.30f, c.g * 0.30f, c.b * 0.30f, 1f), 0f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", new Color(c.r, c.g, c.b, 1f) * 1.15f);
            return m;
        }

        public static Material UnlitMaterial(Color c)
        {
            return new Material(UnlitShader) { color = c };
        }

        /// <summary>Flat, unlit, alpha-blended: for zone floors, HUD panels and lines.</summary>
        public static Material OverlayMaterial(Color c)
        {
            return new Material(SpriteShader) { color = c };
        }

        static Material LineMaterial =>
            _lineMaterial != null ? _lineMaterial : (_lineMaterial = OverlayMaterial(Color.white));

        // ---------------------------------------------------------------- primitives

        /// <summary>
        /// Removes the collider CreatePrimitive adds, for things that are only
        /// drawn. IMMEDIATELY: Destroy() takes effect at the end of the frame, and
        /// the roof-exit sweep runs in the same frame the scene is built - a solid
        /// court or garden box would still be there for the sweep's balls to hit.
        /// (A solid roof also made a landing ball bounce and roll, the original bug.)
        /// </summary>
        public static void RemoveCollider(GameObject go)
        {
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
        }

        public static GameObject Box(string name, Vector3 center, Vector3 size, Material material, Transform parent = null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = center;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material;
            RemoveCollider(go);

            if (parent != null) go.transform.SetParent(parent, true);
            return go;
        }

        public static LineRenderer Line(string name, IList<Vector3> points, Color color, float width, bool loop, bool flat)
        {
            var go = new GameObject(name);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.positionCount = points.Count;
            for (var i = 0; i < points.Count; i++) lr.SetPosition(i, points[i]);
            lr.widthMultiplier = width;
            lr.startColor = color;
            lr.endColor = color;
            lr.sharedMaterial = LineMaterial;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.numCornerVertices = 2;
            lr.numCapVertices = 2;

            if (flat)
            {
                // Lie on the roof: face the line's quad straight up.
                lr.alignment = LineAlignment.TransformZ;
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }

            return lr;
        }

        static Vector3[] RectCorners(float xMin, float xMax, float yMin, float yMax, float height)
        {
            return new[]
            {
                LayoutSpace.ToWorld(xMin, yMin, height), LayoutSpace.ToWorld(xMax, yMin, height),
                LayoutSpace.ToWorld(xMax, yMax, height), LayoutSpace.ToWorld(xMin, yMax, height),
            };
        }

        public static Color SportColor(string sport)
        {
            switch ((sport ?? "").Trim().ToLowerInvariant())
            {
                case "badminton": return new Color(0.16f, 0.52f, 0.36f);
                case "basketball": return new Color(0.78f, 0.52f, 0.26f);
                case "handball": return new Color(0.24f, 0.44f, 0.74f);
                case "volleyball": return new Color(0.28f, 0.56f, 0.72f);
                case "football": return new Color(0.26f, 0.58f, 0.30f);
                default: return new Color(0.50f, 0.50f, 0.72f);
            }
        }

        // ---------------------------------------------------------------- the roof and what's on it

        public static void BuildRoof(RoofContext roof)
        {
            var l = roof.length_m;
            var w = roof.width_m;

            // Top surface at y = 0: everything in the analysis is measured from it.
            Box("Roof", new Vector3(l * 0.5f, -0.25f, -w * 0.5f), new Vector3(l, 0.5f, w), LitMaterial(RoofColor, 0.04f));

            Line("RoofOutline", RectCorners(0f, l, 0f, w, 0.08f), EdgeColor, 0.20f, true, true);
        }

        /// <summary>Garden beds and activities: drawn so the video reads as the designed roof, never tested.</summary>
        public static void BuildContext(List<ContextPatch> patches)
        {
            foreach (var p in patches)
            {
                var isGarden = p.Category == "garden";
                var h = isGarden ? 0.16f : 0.08f;
                var cx = (p.XMin + p.XMax) * 0.5f;
                var cy = (p.YMin + p.YMax) * 0.5f;
                Box("Context_" + p.Category + "_" + (p.Label ?? ""),
                    LayoutSpace.ToWorld(cx, cy, h * 0.5f),
                    new Vector3(p.XMax - p.XMin, h, p.YMax - p.YMin),
                    LitMaterial(isGarden ? GardenColor : ActivityColor, 0.05f));
            }
        }

        public static void BuildEntryPoints(EntryPoint[] points)
        {
            if (points == null) return;
            foreach (var ep in points)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = "EntryPoint_" + ep.edge;
                marker.transform.position = LayoutSpace.ToWorld(ep.x_m, ep.y_m, 0.3f);
                marker.transform.localScale = new Vector3(0.9f, 0.3f, 0.9f);
                marker.GetComponent<Renderer>().sharedMaterial = LitMaterial(new Color(0.96f, 0.76f, 0.12f), 0.2f);
                RemoveCollider(marker);
            }
        }

        // ---------------------------------------------------------------- courts + the airspace above them

        public static void BuildCourts(List<CourtInstance> courts)
        {
            foreach (var c in courts)
            {
                var centre = c.CenterLayout;

                Box("Court_" + c.Index + "_" + c.Sport,
                    LayoutSpace.ToWorld(centre.x, centre.y, 0.02f),
                    new Vector3(c.ExtentX, 0.04f, c.ExtentY),
                    LitMaterial(SportColor(c.Sport), 0.15f));

                Line("CourtLines_" + c.Index, RectCorners(c.XMin, c.XMax, c.YMin, c.YMax, 0.055f),
                    new Color(1f, 1f, 1f, 0.95f), 0.10f, true, true);

                // The airspace another court's shots must not enter: this
                // court's own footprint up to its required clear height.
                var zoneGo = new GameObject("Zone_NeighborCourt_" + c.Index);
                zoneGo.transform.position = new Vector3(c.Center.x, 0f, c.Center.z);
                var col = zoneGo.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.center = new Vector3(0f, c.MinHeightM * 0.5f, 0f);
                col.size = new Vector3(c.ExtentX, c.MinHeightM, c.ExtentY);

                var zone = zoneGo.AddComponent<ZoneVolume>();
                zone.Type = ZoneType.NeighborCourt;
                zone.OwnerCourtIndex = c.Index;
                zone.Label = c.DisplayName;
            }
        }

        /// <summary>
        /// The walkway between two courts that stand side by side along x: a
        /// strip across the full roof width. (Unchanged rule from the first
        /// version; courts stacked in y are not given a walkway strip.)
        /// </summary>
        public static void BuildCirculationZones(List<CourtInstance> courts, RoofContext roof)
        {
            var sorted = courts.OrderBy(c => c.XMin).ToList();
            for (var i = 0; i < sorted.Count - 1; i++)
            {
                var gapStart = sorted[i].XMax;
                var gapEnd = sorted[i + 1].XMin;
                var gap = gapEnd - gapStart;
                if (gap <= 0.01f) continue;

                var midX = (gapStart + gapEnd) * 0.5f;
                var centre = LayoutSpace.ToWorld(midX, roof.width_m * 0.5f);

                var zoneGo = new GameObject("Zone_Circulation_" + i);
                zoneGo.transform.position = centre;
                var col = zoneGo.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.center = new Vector3(0f, CirculationZoneHeightM * 0.5f, 0f);
                col.size = new Vector3(gap, CirculationZoneHeightM, roof.width_m);

                var zone = zoneGo.AddComponent<ZoneVolume>();
                zone.Type = ZoneType.CirculationSpace;
                zone.Label = "Circulation gap " + i + " (" + gap.ToString("F2", CultureInfo.InvariantCulture) + " m)";

                // Show the tested strip on the floor, above garden beds too.
                Box("CirculationFloor_" + i, new Vector3(centre.x, 0.20f, centre.z),
                    new Vector3(gap, 0.02f, roof.width_m), OverlayMaterial(CirculationColor));
            }
        }

        // There is no roof-edge volume: leaving the roof is detected geometrically
        // by AerodynamicProjectile (the ball's centre crossing the roof outline, at
        // any height), which is exact and doesn't depend on trigger ordering.

        public static readonly Color FenceColor = new Color(1.00f, 0.56f, 0.10f, 0.42f);

        /// <summary>A proposed fence, drawn as a translucent wall along the roof edge at its recommended height.</summary>
        public static void BuildFence(FenceRecommendation fence, RoofContext roof)
        {
            const float thickness = 0.30f;
            var h = fence.heightM;
            var mid = (fence.fromM + fence.toM) * 0.5f;

            Vector3 centre, size;
            Vector3 railStart, railEnd;
            switch (fence.edge)
            {
                case "top":
                    centre = new Vector3(mid, h * 0.5f, 0f);
                    size = new Vector3(fence.lengthM, h, thickness);
                    railStart = new Vector3(fence.fromM, h, 0f);
                    railEnd = new Vector3(fence.toM, h, 0f);
                    break;
                case "bottom":
                    centre = new Vector3(mid, h * 0.5f, -roof.width_m);
                    size = new Vector3(fence.lengthM, h, thickness);
                    railStart = new Vector3(fence.fromM, h, -roof.width_m);
                    railEnd = new Vector3(fence.toM, h, -roof.width_m);
                    break;
                case "left":
                    centre = new Vector3(0f, h * 0.5f, -mid);
                    size = new Vector3(thickness, h, fence.lengthM);
                    railStart = new Vector3(0f, h, -fence.fromM);
                    railEnd = new Vector3(0f, h, -fence.toM);
                    break;
                default:
                    centre = new Vector3(roof.length_m, h * 0.5f, -mid);
                    size = new Vector3(thickness, h, fence.lengthM);
                    railStart = new Vector3(roof.length_m, h, -fence.fromM);
                    railEnd = new Vector3(roof.length_m, h, -fence.toM);
                    break;
            }

            // The fence on the edge nearest the camera stands between it and the roof:
            // keep that one faint so it doesn't tint the whole picture.
            var panel = FenceColor;
            if (fence.edge == "bottom") panel.a = 0.14f;

            Box("Fence_" + fence.edge, centre, size, OverlayMaterial(panel));
            Line("FenceRail_" + fence.edge, new[] { railStart, railEnd }, new Color(1f, 0.70f, 0.25f, 1f), 0.16f, false, false);
        }

        // ---------------------------------------------------------------- camera + light

        /// <summary>
        /// A fixed three-quarter view from the plan's bottom edge (so the plan
        /// reads the same way up as in the web app), sized to fit the whole
        /// roof plus the height of a lob. Rendered by hand into the video's
        /// render texture, so the component stays disabled.
        /// </summary>
        public static Camera BuildCamera(RoofContext roof, float aspect)
        {
            var l = roof.length_m;
            var w = roof.width_m;

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();

            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = BackgroundColor;
            cam.fieldOfView = 32f;
            cam.nearClipPlane = 0.5f;
            cam.allowHDR = false;
            cam.allowMSAA = true;

            var elevation = 40f * Mathf.Deg2Rad;
            var halfV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            var halfH = halfV * aspect;

            // The HUD bars cover the top and bottom of the frame; fit the roof into what is left.
            const float usableHeight = 0.70f;
            var fitWidth = l * 0.5f * 1.10f / halfH;
            var depthOnScreen = w * Mathf.Sin(elevation) + 14f * Mathf.Cos(elevation);
            var fitHeight = depthOnScreen * 0.5f * 1.10f / (halfV * usableHeight);
            var distance = Mathf.Max(fitWidth, fitHeight);

            var target = LayoutSpace.ToWorld(l * 0.5f, w * 0.5f, 2.5f);
            camGo.transform.position = target + new Vector3(0f, Mathf.Sin(elevation), -Mathf.Cos(elevation)) * distance;
            camGo.transform.LookAt(target);
            cam.farClipPlane = distance * 4f;

            cam.enabled = false;
            return cam;
        }

        public static void BuildLight(float roofDiagonalM)
        {
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            light.color = new Color(1f, 0.97f, 0.92f);
            light.shadows = LightShadows.Soft;
            light.shadowStrength = 0.75f;
            lightGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.56f, 0.58f, 0.63f);

            // Ball shadows on the roof are what lets a viewer judge height.
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowDistance = roofDiagonalM * 2.5f;
        }
    }
}
