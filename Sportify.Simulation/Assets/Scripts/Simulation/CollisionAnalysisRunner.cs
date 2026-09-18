using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Sportify.Simulation
{
    public class CollisionAnalysisRunner : MonoBehaviour
    {
        public static CollisionAnalysisRunner Instance { get; private set; }

        [Serializable]
        public class ViolationRecord
        {
            public int courtIndex;
            public string shotLabel;
            public string zoneType;
            public string zoneLabel;
            public float simTime;
            public float x_m;
            public float y_m;
            public float height_m;
        }

        [Serializable]
        public class ResultsReport
        {
            public string caseStudy = "Goldbeck default - Garden Boundary, Sports Core";
            public int shotsSimulated;
            public List<ViolationRecord> violations = new List<ViolationRecord>();
            public string error;
        }

        readonly ResultsReport _report = new ResultsReport();
        readonly List<AerodynamicProjectile> _active = new List<AerodynamicProjectile>();
        float _simStartTime;
        bool _reportWritten;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            new GameObject("CollisionAnalysisRunner").AddComponent<CollisionAnalysisRunner>();
        }

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            _simStartTime = Time.time;

            // Everything here must funnel into WriteReport (success, empty-input,
            // or exception) so a headless -executeMethod run always exits instead
            // of leaving an orphaned Unity process behind for the caller to time out on.
            try
            {
                var payload = LayoutLoader.Load();
                var courts = LayoutLoader.ExtractCourts(payload);

                SceneBuilder.BuildRoof(payload.roof_context);
                SceneBuilder.BuildEntryPoints(payload.entry_points);
                SceneBuilder.BuildCourts(courts);
                SceneBuilder.BuildCirculationZones(courts, payload.roof_context);
                SceneBuilder.BuildRoofEdgeWalls(payload.roof_context);
                SceneBuilder.BuildCamera(payload.roof_context);
                SceneBuilder.BuildLight();

                var shots = ShotScenario.BuildDefaultBadmintonShots(courts);
                foreach (var shot in shots)
                    LaunchShot(shot);

                _report.shotsSimulated = shots.Count;
                Debug.Log($"[Collision] Loaded {courts.Count} court(s) from {payload.roof_context.length_m}x{payload.roof_context.width_m}m roof; launched {shots.Count} shot(s).");

                if (shots.Count == 0)
                {
                    Debug.LogWarning("[Collision] No field placements in the layout — nothing to simulate.");
                    WriteReport(0);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Collision] Failed to start: {ex}");
                _report.error = ex.Message;
                WriteReport(1);
            }
        }

        void LaunchShot(ShotScenario shot)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"Shuttle_court{shot.CourtIndex}_{shot.Label}";
            go.transform.localScale = Vector3.one * 0.068f;
            go.transform.position = shot.Origin;

            var rb = go.AddComponent<Rigidbody>();
            var proj = go.AddComponent<AerodynamicProjectile>();
            proj.OriginCourtIndex = shot.CourtIndex;
            proj.ShotLabel = shot.Label;
            rb.linearVelocity = shot.LaunchVelocity;

            _active.Add(proj);
        }

        public void OnZoneEntered(AerodynamicProjectile proj, ZoneVolume zone, Vector3 worldPos)
        {
            _report.violations.Add(new ViolationRecord
            {
                courtIndex = proj.OriginCourtIndex,
                shotLabel = proj.ShotLabel,
                zoneType = zone.Type.ToString(),
                zoneLabel = zone.Label,
                simTime = Time.time - _simStartTime,
                x_m = worldPos.x,
                y_m = worldPos.z,
                height_m = worldPos.y,
            });

            Debug.Log($"[Collision] {proj.name} ({proj.ShotLabel}, court {proj.OriginCourtIndex}) entered {zone.Type} \"{zone.Label}\" at t={Time.time - _simStartTime:F2}s, height={worldPos.y:F2}m");
        }

        public void OnProjectileExpired(AerodynamicProjectile proj, Vector3 worldPos)
        {
            _active.Remove(proj);
            Destroy(proj.gameObject);

            if (_active.Count == 0)
                WriteReport(0);
        }

        void WriteReport(int exitCode)
        {
            if (_reportWritten) return;
            _reportWritten = true;

            var dir = Path.Combine(Application.dataPath, "..", "Recordings");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "collision_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_report, true));
            Debug.Log($"[Collision] Done. {_report.violations.Count} violation(s) from {_report.shotsSimulated} shots. Report: {path}");

#if UNITY_EDITOR
            if (Application.isBatchMode)
                UnityEditor.EditorApplication.Exit(exitCode);
#endif
        }
    }
}
