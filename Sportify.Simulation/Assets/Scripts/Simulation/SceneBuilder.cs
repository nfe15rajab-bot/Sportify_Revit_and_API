using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Sportify.Simulation
{
    public static class SceneBuilder
    {
        const float ZoneHeightM = 9f;
        const float EdgeWallHeightM = 12f;

        public static void BuildRoof(RoofContext roof)
        {
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Roof";
            ground.transform.position = new Vector3(roof.length_m / 2f, 0f, roof.width_m / 2f);
            ground.transform.localScale = new Vector3(roof.length_m / 10f, 1f, roof.width_m / 10f);
            ground.GetComponent<Renderer>().material.color = new Color(0.55f, 0.55f, 0.58f);
        }

        public static void BuildCourts(List<CourtInstance> courts)
        {
            foreach (var c in courts)
            {
                var visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                visual.name = $"Court_{c.Index}_{c.Sport}";
                visual.transform.position = new Vector3(c.Center.x, 0.02f, c.Center.z);
                visual.transform.rotation = Quaternion.Euler(0, c.RotationDeg, 0);
                visual.transform.localScale = new Vector3(c.Size.x, 0.04f, c.Size.z);
                visual.GetComponent<Renderer>().material.color = new Color(0.15f, 0.45f, 0.85f);
                Object.Destroy(visual.GetComponent<Collider>());

                var zoneGo = new GameObject($"Zone_NeighborCourt_{c.Index}");
                zoneGo.transform.position = new Vector3(c.Center.x, 0f, c.Center.z);
                zoneGo.transform.rotation = Quaternion.Euler(0, c.RotationDeg, 0);
                var col = zoneGo.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.center = new Vector3(0, ZoneHeightM / 2f, 0);
                col.size = new Vector3(c.Size.x, ZoneHeightM, c.Size.z);

                var zone = zoneGo.AddComponent<ZoneVolume>();
                zone.Type = ZoneType.NeighborCourt;
                zone.OwnerCourtIndex = c.Index;
                zone.Label = $"Court {c.Index} ({c.Sport})";
            }
        }

        public static void BuildCirculationZones(List<CourtInstance> courts, RoofContext roof)
        {
            var sorted = courts.OrderBy(c => c.Center.x).ToList();
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                var aRight = sorted[i].Center.x + sorted[i].Size.x / 2f;
                var bLeft = sorted[i + 1].Center.x - sorted[i + 1].Size.x / 2f;
                var gap = bLeft - aRight;
                if (gap <= 0.01f) continue;

                var zoneGo = new GameObject($"Zone_Circulation_{i}");
                zoneGo.transform.position = new Vector3((aRight + bLeft) / 2f, 0f, roof.width_m / 2f);
                var col = zoneGo.AddComponent<BoxCollider>();
                col.isTrigger = true;
                col.center = new Vector3(0, ZoneHeightM / 2f, 0);
                col.size = new Vector3(gap, ZoneHeightM, roof.width_m);

                var zone = zoneGo.AddComponent<ZoneVolume>();
                zone.Type = ZoneType.CirculationSpace;
                zone.Label = $"Circulation gap {i} ({gap:F2} m)";
            }
        }

        public static void BuildRoofEdgeWalls(RoofContext roof)
        {
            const float thickness = 0.5f;
            AddEdgeWall("North", new Vector3(roof.length_m / 2f, EdgeWallHeightM / 2f, -thickness / 2f), new Vector3(roof.length_m + thickness * 2, EdgeWallHeightM, thickness));
            AddEdgeWall("South", new Vector3(roof.length_m / 2f, EdgeWallHeightM / 2f, roof.width_m + thickness / 2f), new Vector3(roof.length_m + thickness * 2, EdgeWallHeightM, thickness));
            AddEdgeWall("West", new Vector3(-thickness / 2f, EdgeWallHeightM / 2f, roof.width_m / 2f), new Vector3(thickness, EdgeWallHeightM, roof.width_m + thickness * 2));
            AddEdgeWall("East", new Vector3(roof.length_m + thickness / 2f, EdgeWallHeightM / 2f, roof.width_m / 2f), new Vector3(thickness, EdgeWallHeightM, roof.width_m + thickness * 2));
        }

        static void AddEdgeWall(string label, Vector3 center, Vector3 size)
        {
            var zoneGo = new GameObject($"Zone_RoofEdge_{label}");
            zoneGo.transform.position = center;
            var col = zoneGo.AddComponent<BoxCollider>();
            col.isTrigger = true;
            col.size = size;

            var zone = zoneGo.AddComponent<ZoneVolume>();
            zone.Type = ZoneType.RoofEdge;
            zone.Label = $"Roof edge ({label})";
        }

        public static void BuildEntryPoints(EntryPoint[] points)
        {
            if (points == null) return;
            foreach (var ep in points)
            {
                var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                marker.name = $"EntryPoint_{ep.edge}";
                marker.transform.position = new Vector3(ep.x_m, 0.3f, ep.y_m);
                marker.transform.localScale = new Vector3(0.6f, 0.3f, 0.6f);
                marker.GetComponent<Renderer>().material.color = new Color(0.95f, 0.75f, 0.1f);
                Object.Destroy(marker.GetComponent<Collider>());
            }
        }

        public static void BuildCamera(RoofContext roof)
        {
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();
            var cam = camGo.AddComponent<Camera>();

            var center = new Vector3(roof.length_m / 2f, 0, roof.width_m / 2f);
            var distance = Mathf.Max(roof.length_m, roof.width_m) * 0.9f;
            camGo.transform.position = center + new Vector3(0, distance * 0.65f, -distance * 0.55f);
            camGo.transform.LookAt(center);
            cam.farClipPlane = distance * 4f;
        }

        public static void BuildLight()
        {
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            lightGo.transform.rotation = Quaternion.Euler(50, -30, 0);
        }
    }
}
