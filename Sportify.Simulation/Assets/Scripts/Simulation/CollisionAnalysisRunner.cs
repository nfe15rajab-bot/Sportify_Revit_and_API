using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// Runs the whole analysis and records it.
    ///
    ///   layout JSON -> scene -> shots -> fixed-step flight -> crossings -> report + MP4
    ///
    /// The simulation is stepped by hand at 240 Hz (Physics.simulationMode =
    /// Script) and one video frame is rendered every few steps, so the result
    /// and the video depend only on the layout - never on how fast this machine
    /// happens to render. The video plays the flight in slow motion, framed by a
    /// title card and a summary card.
    ///
    /// Command-line options (all optional; Revit passes -layoutFile and -videoFile):
    ///   -layoutFile=PATH   Sportify Combine export to analyse (default: bundled sample)
    ///   -videoFile=PATH    where to write the MP4 (default: Recordings/ball_trajectories.mp4)
    ///   -noVideo           analysis only
    ///   -videoWidth= -videoHeight= -videoFps=   default 1920 x 1080 @ 30
    ///   -slowMo=0.25       video speed relative to real time
    ///   -debugStills       also save PNGs of the title, flight and summary frames
    /// </summary>
    public class CollisionAnalysisRunner : MonoBehaviour
    {
        public static CollisionAnalysisRunner Instance { get; private set; }

        // -------------------------------------------------------------- report (JsonUtility)

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
        public class LandingRecord
        {
            public int courtIndex;
            public string shotLabel;
            public string endedBy;      // landed | left-roof | timeout
            public float flightTimeS;
            public float rangeM;        // horizontal distance from the launch point
            public float x_m;
            public float y_m;
        }

        [Serializable]
        public class VideoInfo
        {
            public string path = "";
            public string posterPath = "";
            public float durationS;
            public int fps;
            public int width;
            public int height;
            public int frames;
            public float slowMotion;
        }

        [Serializable]
        public class ResultsReport
        {
            public string caseStudy = "";
            public int shotsSimulated;
            public List<ViolationRecord> violations = new List<ViolationRecord>();
            public List<LandingRecord> landings = new List<LandingRecord>();

            // Of the shots drawn in the video: how many left over a roof edge.
            public int shotsLeavingRoof;
            public float percentShotsLeavingRoof;

            // Of the far larger swept set: the roof-exit percentage and the fence proposal.
            public RoofExitReport roofExit = new RoofExitReport();

            public VideoInfo video = new VideoInfo();
            public string videoError = "";
            public string error;
        }

        // -------------------------------------------------------------- configuration

        class Config
        {
            public bool RecordVideo = true;
            public string VideoPath;
            public int Width = 1920;
            public int Height = 1080;
            public int Fps = 30;
            public float SlowMotion = 0.25f;
            public bool DebugStills;
        }

        const float PhysicsDt = 1f / 240f;
        const float TitleCardS = 2.0f;
        const float HoldS = 1.0f;
        const float SummaryCardS = 5.0f;
        const float FenceSceneS = 4.0f;
        const float FenceCardS = 6.5f;
        const float SweepMaxFlightS = 12f;
        const int MaxSimFrames = 4000;
        const int ProjectileLayer = 8;
        const int FeedLines = 5;

        static readonly Color Good = new Color(0.45f, 0.90f, 0.55f);

        // -------------------------------------------------------------- state

        readonly ResultsReport _report = new ResultsReport();
        readonly List<AerodynamicProjectile> _active = new List<AerodynamicProjectile>();
        readonly List<ShotVisual> _visuals = new List<ShotVisual>();
        readonly Dictionary<AerodynamicProjectile, ShotVisual> _visualOf = new Dictionary<AerodynamicProjectile, ShotVisual>();
        readonly List<string> _feed = new List<string>();

        /// <summary>Collects what the swept (not drawn) shots do at the roof edge and the other zones.</summary>
        sealed class SweepCollector
        {
            public readonly Dictionary<AerodynamicProjectile, SweepOutcome> Outcomes =
                new Dictionary<AerodynamicProjectile, SweepOutcome>();
        }

        SweepCollector _sweep;   // non-null only while the roof-exit sweep is running
        Config _cfg;
        GoldbeckPayload _payload;
        List<CourtInstance> _courts;
        List<ShotScenario> _shots;
        Camera _cam;
        SimulationHud _hud;
        float _simTime;
        int _finished;
        float _slowMotion;
        bool _reportWritten;

#if UNITY_EDITOR
        VideoRecorder _recorder;
#endif

        bool Recording
        {
            get
            {
#if UNITY_EDITOR
                return _recorder != null;
#else
                return false;
#endif
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!AnalysisMode.IsBallSimulation) return;   // another analysis owns this session
            new GameObject("CollisionAnalysisRunner").AddComponent<CollisionAnalysisRunner>();
        }

        void Awake()
        {
            Instance = this;
        }

        void Start()
        {
            // Everything funnels into WriteReport (success, empty layout, or
            // exception) so a headless -executeMethod run always exits instead
            // of leaving an orphaned Unity process for the caller to time out on.
            try
            {
                Prepare();

                if (_shots.Count == 0)
                {
                    Debug.LogWarning("[Collision] No field placements in the layout - nothing to simulate.");
                    WriteReport(0);
                    return;
                }

                StartCoroutine(Guard(Run()));
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        void OnDestroy()
        {
            // Play mode ended early: still finalise the MP4 so it isn't left unreadable.
            FinishVideo();
        }

        // -------------------------------------------------------------- setup

        void Prepare()
        {
            _cfg = ParseConfig();
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            _payload = LayoutLoader.Load();
            _courts = LayoutLoader.ExtractCourts(_payload);
            var roof = _payload.roof_context;

            _report.caseStudy = LayoutLoader.IsBundledSample
                ? "Goldbeck default - Garden Boundary, Sports Core"
                : _courts.Count + " court" + (_courts.Count == 1 ? "" : "s") + " on a " +
                  Num(roof.length_m, "0.#") + " x " + Num(roof.width_m, "0.#") + " m roof";

            SceneBuilder.BuildRoof(roof);
            SceneBuilder.BuildContext(LayoutLoader.ExtractContext(_payload));
            SceneBuilder.BuildEntryPoints(_payload.entry_points);
            SceneBuilder.BuildCourts(_courts);
            SceneBuilder.BuildCirculationZones(_courts, roof);

            var aspect = (float)_cfg.Width / _cfg.Height;
            _cam = SceneBuilder.BuildCamera(roof, aspect);
            SceneBuilder.BuildLight(Mathf.Sqrt(roof.length_m * roof.length_m + roof.width_m * roof.width_m));

            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height);
            foreach (var court in _courts)
            {
                var c = court.CenterLayout;
                _hud.WorldLabel(court.DisplayName.Replace("(", "- ").Replace(")", ""),
                    LayoutSpace.ToWorld(c.x, court.YMax + 1.1f, 0.3f), 0.95f, new Color(1f, 1f, 1f, 0.95f));
            }

            // Balls must not collide with each other; only with the trigger zones.
            Physics.IgnoreLayerCollision(ProjectileLayer, ProjectileLayer, true);
            Physics.simulationMode = SimulationMode.Script;

            var roofXZ = new Rect(0f, -roof.width_m, roof.length_m, roof.width_m);

            // Before the drawn shots exist: fly the whole fan and work out the roof-exit percentage.
            if (_courts.Count > 0) _report.roofExit = RunRoofExitSweep(roofXZ, roof);

            _shots = ShotScenario.BuildFor(_courts);
            foreach (var shot in _shots) LaunchShot(shot, roofXZ);
            Physics.SyncTransforms();

            _report.shotsSimulated = _shots.Count;
            Debug.Log("[Collision] Loaded " + _courts.Count + " court(s) from " +
                      Num(roof.length_m, "0.##") + "x" + Num(roof.width_m, "0.##") + "m roof; launched " + _shots.Count + " shot(s).");

            // Nothing to fly means nothing to film: don't create an empty video file.
            if (_shots.Count > 0) StartRecorder();
        }

        void LaunchShot(ShotScenario shot, Rect roofXZ)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Ball_court" + shot.CourtIndex + "_" + shot.Label;
            go.layer = ProjectileLayer;
            go.GetComponent<Renderer>().enabled = false;   // the picture comes from ShotVisual

            go.AddComponent<Rigidbody>();
            var projectile = go.AddComponent<AerodynamicProjectile>();
            projectile.Launch(shot, roofXZ, 0f);

            var visual = new ShotVisual(projectile);
            _active.Add(projectile);
            _visuals.Add(visual);
            _visualOf[projectile] = visual;
        }

        void StartRecorder()
        {
#if UNITY_EDITOR
            if (!_cfg.RecordVideo) return;
            try
            {
                _recorder = new VideoRecorder(_cam, _cfg.VideoPath, _cfg.Width, _cfg.Height, _cfg.Fps);
                Debug.Log("[Collision] Recording " + _cfg.Width + "x" + _cfg.Height + " @ " + _cfg.Fps + " fps to " + _cfg.VideoPath);
            }
            catch (Exception ex)
            {
                _report.videoError = "Couldn't start the video encoder: " + ex.Message;
                Debug.LogError("[Collision] " + _report.videoError);
                _recorder = null;
            }
#else
            _report.videoError = "Video recording needs the Unity Editor.";
#endif
        }

        // -------------------------------------------------------------- roof-exit sweep

        /// <summary>
        /// Flies the whole fan of stray shots (never drawn) through the same
        /// physics and zones as the drawn ones, and reduces what happened at the
        /// roof edge to a percentage and a fence proposal.
        /// </summary>
        RoofExitReport RunRoofExitSweep(Rect roofXZ, RoofContext roof)
        {
            var started = Time.realtimeSinceStartup;
            var shots = ShotScenario.BuildSweepFor(_courts);
            var bodies = new List<AerodynamicProjectile>(shots.Count);
            var collector = new SweepCollector();
            _sweep = collector;

            try
            {
                foreach (var shot in shots)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    go.name = "Sweep";
                    go.layer = ProjectileLayer;
                    go.GetComponent<Renderer>().enabled = false;
                    go.AddComponent<Rigidbody>();

                    var projectile = go.AddComponent<AerodynamicProjectile>();
                    projectile.Launch(shot, roofXZ, 0f);
                    bodies.Add(projectile);
                    collector.Outcomes[projectile] = new SweepOutcome { CourtIndex = shot.CourtIndex };
                }
                Physics.SyncTransforms();

                var active = new List<AerodynamicProjectile>(bodies);
                var maxSteps = Mathf.CeilToInt(SweepMaxFlightS / PhysicsDt);
                var t = 0f;
                for (var step = 0; step < maxSteps && active.Count > 0; step++)
                {
                    t += PhysicsDt;
                    for (var i = 0; i < active.Count; i++) active[i].PreStep();
                    Physics.Simulate(PhysicsDt);
                    for (var i = active.Count - 1; i >= 0; i--)
                    {
                        var projectile = active[i];
                        if (!projectile.PostStep(t, PhysicsDt)) continue;

                        active.RemoveAt(i);
                        if (projectile.CrossedEdge == null) continue;

                        var outcome = collector.Outcomes[projectile];
                        outcome.Edge = projectile.CrossedEdge;
                        outcome.Along = AlongEdge(projectile.CrossedEdge, projectile.CrossedEdgePoint);
                        outcome.Height = projectile.CrossedEdgePoint.y;
                    }
                }
            }
            finally
            {
                _sweep = null;
                foreach (var body in bodies)
                    if (body != null) UnityEngine.Object.Destroy(body.gameObject);
            }

            var report = RoofExitAnalysis.Analyse(collector.Outcomes.Values.ToList(), roof.length_m, roof.width_m);
            report.assumptions = "Swept " + report.shotsSwept + " stray shots from " + _courts.Count + " court(s): " +
                                 ShotScenario.SweepDescription +
                                 ". Percentages are shares of this shot set, a way to compare layouts and edges - " +
                                 "not the probability that a real ball leaves the roof.";

            Debug.Log("[Collision] Roof-exit sweep: " + report.shotsSwept + " shots in " +
                      Num(Time.realtimeSinceStartup - started, "0.0") + "s -> " + report.shotsLeavingRoof + " left the roof (" +
                      Num(report.percentLeavingRoof, "0.0") + "%), " + report.fences.Count + " fence segment(s) proposed.");
            return report;
        }

        void RecordSweepZone(AerodynamicProjectile projectile, ZoneVolume zone)
        {
            if (!_sweep.Outcomes.TryGetValue(projectile, out var outcome)) return;

            if (zone.Type == ZoneType.NeighborCourt) outcome.EnteredNeighbour = true;
            else if (zone.Type == ZoneType.CirculationSpace) outcome.EnteredCirculation = true;
        }

        /// <summary>Position along a roof edge in layout metres: x for top/bottom, y for left/right.</summary>
        static float AlongEdge(string edge, Vector3 world)
        {
            return edge == "top" || edge == "bottom" ? world.x : -world.z;
        }

        // -------------------------------------------------------------- the run

        IEnumerator Run()
        {
            var fps = _cfg.Fps;
            var stepsPerFrame = Mathf.Max(1, Mathf.RoundToInt(_cfg.SlowMotion / (fps * PhysicsDt)));
            _slowMotion = stepsPerFrame * PhysicsDt * fps;

            _hud.SetCaseStudy(_report.caseStudy);
            _hud.SetClock(0f, _slowMotion);
            _hud.SetCounts(_shots.Count, 0, 0);
            _hud.SetFeed(_feed);
            SyncVisuals();

            // Title card over the untouched scene.
            if (Recording)
            {
                _hud.ShowCard("Stray-shot analysis", Color.white, _report.caseStudy, TitleLines());
                foreach (var _ in Hold(TitleCardS, "title")) yield return null;
                _hud.HideCard();
            }

            // The flight, in slow motion.
            var frames = 0;
            while (_active.Count > 0 && frames < MaxSimFrames)
            {
                for (var s = 0; s < stepsPerFrame && _active.Count > 0; s++) StepPhysics();

                _hud.SetClock(_simTime, _slowMotion);
                _hud.SetCounts(_shots.Count, _finished, _report.violations.Count);
                SyncVisuals();
                CaptureFrame();
                frames++;
                if (frames == 12) SaveStill("flight");
                yield return null;
            }

            if (_active.Count > 0)
                Debug.LogWarning("[Collision] Stopped after " + MaxSimFrames + " frames with " + _active.Count + " shot(s) still in flight.");

            // Hold on the finished picture (every trail complete), and keep it as the poster image.
            SyncVisuals();
            foreach (var _ in Hold(HoldS, null)) yield return null;
            if (Recording) SavePoster();

            SummariseDrawnShots();

            // Summary card.
            if (Recording)
            {
                ShowSummary();
                foreach (var _ in Hold(SummaryCardS, "summary")) yield return null;

                // Where fences should go: the walls in the scene first, then the list.
                if (_report.roofExit.fences.Count > 0)
                {
                    _hud.HideCard();
                    foreach (var fence in _report.roofExit.fences) SceneBuilder.BuildFence(fence, _payload.roof_context);
                    _hud.SetBanner("PROPOSED ROOF-EDGE FENCES");
                    foreach (var _ in Hold(FenceSceneS, "fences")) yield return null;

                    ShowFenceCard();
                    foreach (var _ in Hold(FenceCardS, "fence-list")) yield return null;
                }
            }

            FinishVideo();
            WriteReport(0);
        }

        /// <summary>How many of the drawn shots left over a roof edge.</summary>
        void SummariseDrawnShots()
        {
            _report.shotsLeavingRoof = _report.violations.Count(v => v.zoneType == ZoneType.RoofEdge.ToString());
            _report.percentShotsLeavingRoof = _report.shotsSimulated == 0
                ? 0f
                : _report.shotsLeavingRoof * 100f / _report.shotsSimulated;
        }

        /// <summary>Repeats the current picture for the given number of seconds of video.</summary>
        IEnumerable<object> Hold(float seconds, string stillName)
        {
            var frames = Mathf.Max(1, Mathf.RoundToInt(seconds * _cfg.Fps));
            for (var i = 0; i < frames; i++)
            {
                CaptureFrame();
                if (stillName != null && i == frames / 2) SaveStill(stillName);
                yield return null;
            }
        }

        void StepPhysics()
        {
            _simTime += PhysicsDt;

            for (var i = 0; i < _active.Count; i++) _active[i].PreStep();

            Physics.Simulate(PhysicsDt);   // fires ZoneVolume.OnTriggerEnter -> OnZoneEntered

            for (var i = _active.Count - 1; i >= 0; i--)
            {
                var projectile = _active[i];
                if (!projectile.PostStep(_simTime, PhysicsDt)) continue;

                _active.RemoveAt(i);
                if (projectile.CrossedEdge != null) OnRoofExit(projectile);
                OnProjectileFinished(projectile);
            }
        }

        void SyncVisuals()
        {
            foreach (var visual in _visuals) visual.Sync();
        }

        // -------------------------------------------------------------- events

        /// <summary>A shot entered a neighbouring court's airspace or a circulation gap.</summary>
        public void OnZoneEntered(AerodynamicProjectile projectile, ZoneVolume zone, Vector3 worldPosition)
        {
            if (_sweep != null)
            {
                RecordSweepZone(projectile, zone);
                return;
            }

            RecordCrossing(projectile, zone.Type.ToString(), zone.Label, ShortZone(zone), worldPosition, _simTime);
        }

        /// <summary>A shot crossed the roof outline; its flight ended right there.</summary>
        void OnRoofExit(AerodynamicProjectile projectile)
        {
            var label = "Roof edge (" + projectile.CrossedEdge + ")";
            RecordCrossing(projectile, ZoneType.RoofEdge.ToString(), label, label, projectile.CrossedEdgePoint, projectile.EndTime);
        }

        void RecordCrossing(AerodynamicProjectile projectile, string zoneType, string zoneLabel, string shortLabel,
                            Vector3 at, float time)
        {
            var layout = LayoutSpace.ToLayout(at);
            _report.violations.Add(new ViolationRecord
            {
                courtIndex = projectile.OriginCourtIndex,
                shotLabel = projectile.ShotLabel,
                zoneType = zoneType,
                zoneLabel = zoneLabel,
                simTime = time,
                x_m = layout.x,
                y_m = layout.y,
                height_m = at.y,
            });

            projectile.Violated = true;
            var number = _report.violations.Count;

            if (_visualOf.TryGetValue(projectile, out var visual)) visual.MarkCrossing(at);
            AddCrossingMarker(at, number);

            _feed.Add(SimulationHud.Tint(number.ToString(), SimulationHud.CrossingText) + "  Court " +
                      projectile.OriginCourtIndex + " " + projectile.ShotLabel + "  ->  " + shortLabel +
                      "   (" + Num(time, "0.00") + " s)");
            while (_feed.Count > FeedLines) _feed.RemoveAt(0);
            _hud.SetFeed(_feed);

            Debug.Log("[Collision] " + projectile.name + " (" + projectile.ShotLabel + ", court " + projectile.OriginCourtIndex +
                      ") entered " + zoneType + " \"" + zoneLabel + "\" at t=" + Num(time, "0.00") +
                      "s, height=" + Num(at.y, "0.00") + "m");
        }

        void OnProjectileFinished(AerodynamicProjectile projectile)
        {
            _finished++;

            var end = LayoutSpace.ToLayout(projectile.EndPosition);
            var origin = projectile.Origin;
            var range = new Vector2(projectile.EndPosition.x - origin.x, projectile.EndPosition.z - origin.z).magnitude;

            _report.landings.Add(new LandingRecord
            {
                courtIndex = projectile.OriginCourtIndex,
                shotLabel = projectile.ShotLabel,
                endedBy = projectile.EndedBy,
                flightTimeS = projectile.FlightTimeS,
                rangeM = range,
                x_m = end.x,
                y_m = end.y,
            });

            if (_visualOf.TryGetValue(projectile, out var visual)) visual.Ended();
        }

        void AddCrossingMarker(Vector3 position, int number)
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Crossing_" + number;
            SceneBuilder.RemoveCollider(marker);
            marker.transform.position = position;
            marker.transform.localScale = Vector3.one * 0.95f;
            marker.GetComponent<Renderer>().sharedMaterial = SceneBuilder.GlowMaterial(ShotVisual.CrossingColor);

            _hud.WorldLabel(number.ToString(), position + new Vector3(0f, 1.5f, 0f), 1.3f, Color.white);
        }

        static string ShortZone(ZoneVolume zone)
        {
            switch (zone.Type)
            {
                case ZoneType.NeighborCourt: return "Court " + zone.OwnerCourtIndex;
                case ZoneType.CirculationSpace:
                    var paren = zone.Label.IndexOf(" (", StringComparison.Ordinal);
                    return paren > 0 ? zone.Label.Substring(0, paren) : zone.Label;
                default: return zone.Label;
            }
        }

        // -------------------------------------------------------------- cards

        List<string> TitleLines()
        {
            var lines = new List<string>();
            var perSport = _courts.GroupBy(c => c.Sport).Select(g => g.Count() + " x " + g.Key);
            lines.Add(_shots.Count + " stray shots from " + _courts.Count + " court" + (_courts.Count == 1 ? "" : "s") +
                      "  (" + string.Join(", ", perSport) + ")");
            lines.Add("");
            lines.Add("Flags every shot that enters a neighbouring court's airspace,");
            lines.Add("a circulation gap, or crosses the roof edge.");
            lines.Add("");
            var muted = new Color(0.66f, 0.70f, 0.76f);
            lines.Add(SimulationHud.Tint("Shown in slow motion (" + Num(_slowMotion, "0.##") + "x). Each shot ends where it first touches the roof.", muted));
            if (_report.roofExit.ran && _report.roofExit.shotsSwept > 0)
            {
                lines.Add("");
                lines.Add(SimulationHud.Tint("A further " + _report.roofExit.shotsSwept + " swept shots estimate how many leave the roof,", muted));
                lines.Add(SimulationHud.Tint("and where fences should go.", muted));
            }
            return lines;
        }

        void ShowSummary()
        {
            var n = _report.violations.Count;
            var headline = n == 0
                ? "No boundary crossings found"
                : n + " boundary crossing" + (n == 1 ? "" : "s") + " found";

            var subtitle = _shots.Count + " shots simulated across " + _courts.Count + " court" + (_courts.Count == 1 ? "" : "s");

            var lines = new List<string>();
            if (n == 0)
            {
                lines.Add("Every simulated shot came down without entering a neighbouring");
                lines.Add("court, a circulation gap, or leaving the roof.");
            }
            else
            {
                var shown = _report.violations.OrderBy(v => v.simTime).Take(9).ToList();
                for (var i = 0; i < shown.Count; i++)
                {
                    var v = shown[i];
                    lines.Add(SimulationHud.Tint((i + 1).ToString(), SimulationHud.CrossingText) + "   Court " + v.courtIndex + " - " +
                              v.shotLabel + "  ->  " + v.zoneLabel + "   (" + Num(v.simTime, "0.00") + " s)");
                }
                if (n > shown.Count) lines.Add("... and " + (n - shown.Count) + " more (see the results file)");
            }

            var farthest = _report.landings.Where(l => l.endedBy == "landed").OrderByDescending(l => l.rangeM).FirstOrDefault();
            if (farthest != null)
            {
                lines.Add("");
                lines.Add(SimulationHud.Tint("Longest flight that stayed on the roof: " + farthest.shotLabel + " from court " +
                                             farthest.courtIndex + " - " + Num(farthest.rangeM, "0.0") + " m",
                                             new Color(0.66f, 0.70f, 0.76f)));
            }

            // The number the fence proposal is based on.
            var exit = _report.roofExit;
            if (exit.ran && exit.shotsSwept > 0)
            {
                var text = exit.shotsLeavingRoof == 0
                    ? "Roof edge: none of " + exit.shotsSwept + " swept shots left the roof - no fence needed"
                    : "Roof edge: " + Num(exit.percentLeavingRoof, "0.#") + "% of " + exit.shotsSwept + " swept shots leave the roof";
                lines.Add(SimulationHud.Tint(text, exit.shotsLeavingRoof == 0 ? Good : SimulationHud.CrossingText));
            }

            _hud.ShowCard(headline, n == 0 ? Good : SimulationHud.CrossingText, subtitle, lines);
        }

        void ShowFenceCard()
        {
            var exit = _report.roofExit;
            var muted = new Color(0.66f, 0.70f, 0.76f);

            var headline = Num(exit.percentLeavingRoof, "0.#") + "% of stray shots leave the roof";
            var subtitle = exit.shotsSwept + " shots swept from " + _courts.Count + " court" + (_courts.Count == 1 ? "" : "s") +
                           "  -  where fences should go:";

            var lines = new List<string>();
            foreach (var fence in exit.fences.OrderByDescending(f => ShareOfExits(f.edge)))
            {
                lines.Add(Capitalise(fence.edge) + " edge:  " + Num(fence.fromM, "0.0") + " to " + Num(fence.toM, "0.0") + " m  (" +
                          Num(fence.lengthM, "0.#") + " m long),  " + Num(fence.heightM, "0.0") + " m high");
                var detail = "stops " + Num(fence.stopsPercentOfExits, "0") + "% of exits over this edge";
                if (fence.fullHeightM > fence.heightM + 0.01f)
                    detail += ";  every exit needs " + Num(fence.fullHeightM, "0.0") + " m";
                lines.Add(SimulationHud.Tint("      " + detail, muted));
            }

            lines.Add("");
            lines.Add(SimulationHud.Tint(
                exit.percentLeavingAfterFences <= 0.05f
                    ? "With these fences no swept shot clears the roof edge."
                    : "With these fences " + Num(exit.percentLeavingAfterFences, "0.#") + "% of swept shots would still clear them (high lobs).",
                muted));

            _hud.ShowCard(headline, SimulationHud.CrossingText, subtitle, lines);
        }

        float ShareOfExits(string edge)
        {
            var e = _report.roofExit.byEdge.FirstOrDefault(x => x.edge == edge);
            return e == null ? 0f : e.percentOfShots;
        }

        static string Capitalise(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        // -------------------------------------------------------------- video plumbing

        void CaptureFrame()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            try
            {
                _recorder.CaptureFrame();
            }
            catch (Exception ex)
            {
                _report.videoError = "Video encoding failed: " + ex.Message;
                Debug.LogError("[Collision] " + _report.videoError);
                DiscardVideo();
            }
#endif
        }

        void SaveStill(string name)
        {
#if UNITY_EDITOR
            if (_recorder == null || !_cfg.DebugStills) return;
            var stillPath = Path.ChangeExtension(_cfg.VideoPath, null) + "_" + name + ".png";
            try { _recorder.SaveStill(stillPath); } catch (Exception ex) { Debug.LogWarning("[Collision] Couldn't save " + stillPath + ": " + ex.Message); }
#endif
        }

        void SavePoster()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var posterPath = Path.ChangeExtension(_cfg.VideoPath, ".png");
            try
            {
                _recorder.SaveStill(posterPath);
                _report.video.posterPath = posterPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Collision] Couldn't save the poster image: " + ex.Message);
            }
#endif
        }

        /// <summary>Closes the encoder (this is what makes the MP4 playable) and records what was written.</summary>
        void FinishVideo()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;

            var recorder = _recorder;
            _recorder = null;
            try
            {
                recorder.Dispose();
            }
            catch (Exception ex)
            {
                _report.videoError = "Couldn't finalise the MP4: " + ex.Message;
                Debug.LogError("[Collision] " + _report.videoError);
                return;
            }

            if (recorder.FramesWritten == 0 || !File.Exists(recorder.Path))
            {
                _report.videoError = "The encoder produced no video.";
                try { if (File.Exists(recorder.Path)) File.Delete(recorder.Path); } catch { /* best effort */ }
                return;
            }

            _report.video.path = recorder.Path;
            _report.video.fps = recorder.Fps;
            _report.video.width = recorder.Width;
            _report.video.height = recorder.Height;
            _report.video.frames = recorder.FramesWritten;
            _report.video.durationS = recorder.FramesWritten / (float)recorder.Fps;
            _report.video.slowMotion = _slowMotion;
            Debug.Log("[Collision] Video: " + recorder.Path + " (" + recorder.FramesWritten + " frames, " +
                      Num(_report.video.durationS, "0.0") + " s)");
#endif
        }

        /// <summary>Drops a recording that can't be completed, including its partial file.</summary>
        void DiscardVideo()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;

            var recorder = _recorder;
            _recorder = null;
            try { recorder.Dispose(); } catch { /* already failing */ }
            try { if (File.Exists(recorder.Path)) File.Delete(recorder.Path); } catch { /* best effort */ }
#endif
        }

        // -------------------------------------------------------------- finishing

        /// <summary>
        /// Lets a failure inside the coroutine reach Fail(): C# can't put a
        /// yield inside a try/catch, so the try/catch wraps each step instead.
        /// </summary>
        IEnumerator Guard(IEnumerator inner)
        {
            while (true)
            {
                try
                {
                    if (!inner.MoveNext()) yield break;
                }
                catch (Exception ex)
                {
                    Fail(ex);
                    yield break;
                }

                yield return inner.Current;
            }
        }

        void Fail(Exception ex)
        {
            Debug.LogError("[Collision] Failed: " + ex);
            _report.error = ex.Message;
            DiscardVideo();
            WriteReport(1);
        }

        void WriteReport(int exitCode)
        {
            if (_reportWritten) return;
            _reportWritten = true;

            FinishVideo();

            SummariseDrawnShots();
            _report.landings = _report.landings.OrderBy(l => l.courtIndex).ThenBy(l => l.flightTimeS).ToList();

            var dir = Path.Combine(Application.dataPath, "..", "Recordings");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "collision_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_report, true));
            Debug.Log("[Collision] Done. " + _report.violations.Count + " violation(s) from " + _report.shotsSimulated + " shots. Report: " + path);

#if UNITY_EDITOR
            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(exitCode);
            }
            else if (_cam != null)
            {
                // Started by hand: leave the finished scene visible in the Game view.
                _cam.targetTexture = null;
                _cam.enabled = true;
            }
#endif
        }

        // -------------------------------------------------------------- helpers

        static string Num(float value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        static Config ParseConfig()
        {
            var cfg = new Config
            {
                VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "ball_trajectories.mp4")),
            };

            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-noVideo") cfg.RecordVideo = false;
                else if (arg == "-debugStills") cfg.DebugStills = true;
                else if (arg.StartsWith("-videoFile=")) cfg.VideoPath = Path.GetFullPath(arg.Substring("-videoFile=".Length));
                else if (arg.StartsWith("-videoWidth=")) cfg.Width = ParseInt(arg, "-videoWidth=", cfg.Width);
                else if (arg.StartsWith("-videoHeight=")) cfg.Height = ParseInt(arg, "-videoHeight=", cfg.Height);
                else if (arg.StartsWith("-videoFps=")) cfg.Fps = ParseInt(arg, "-videoFps=", cfg.Fps);
                else if (arg.StartsWith("-slowMo=")) cfg.SlowMotion = ParseFloat(arg, "-slowMo=", cfg.SlowMotion);
            }

            // Encoders want even dimensions.
            cfg.Width = Mathf.Clamp(cfg.Width, 320, 3840) & ~1;
            cfg.Height = Mathf.Clamp(cfg.Height, 240, 2160) & ~1;
            cfg.Fps = Mathf.Clamp(cfg.Fps, 10, 60);
            cfg.SlowMotion = Mathf.Clamp(cfg.SlowMotion, 0.05f, 1f);
            return cfg;
        }

        static int ParseInt(string arg, string prefix, int fallback)
        {
            return int.TryParse(arg.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }

        static float ParseFloat(string arg, string prefix, float fallback)
        {
            return float.TryParse(arg.Substring(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }
}
