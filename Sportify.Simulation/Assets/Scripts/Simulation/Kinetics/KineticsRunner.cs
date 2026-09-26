using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Sportify.Simulation.Kinetics
{
    /// <summary>
    /// Kinetics' own isolated video: not the whole roof, just the one dynamic unit (an overhead louvre, a slat or fin screen, a tensile sail on movable pillars, a roller
    /// fence) moving through the states the Revit side worked out. Reads a KineticsRenderRequest (not a full layout export: Kinetics has no roof of its own to draw): the
    /// unit's bars and membranes in ITS OWN frame (x along the unit, y outward or across its depth, z up, metres) at every state, bar i of one state being bar i of the
    /// next, so this only has to move between them. Started by BatchRunner.RunKineticsAnalysis. Options (all optional, same names as the other runners): -layoutFile=PATH
    /// -videoFile=PATH -noVideo -videoWidth= -videoHeight= -videoFps= -debugStills.
    /// </summary>
    public class KineticsRunner : MonoBehaviour
    {
        [Serializable]
        public class BarInput
        {
            public string role;
            public bool dynamic, detail, cadOnly;
            public float[] p0, p1, u;          // the axis ends and the section's first direction
            public float sizeU, sizeV;
        }

        [Serializable]
        public class SurfaceInput
        {
            public string role;
            public bool dynamic;
            public float[] a, b, c, d;         // the four corners in order around the membrane
        }

        [Serializable]
        public class StateInput
        {
            public string label;
            public float solarTimeH, sunElevationDeg;
            public float sunX, sunY;                                    // the horizontal direction toward the sun in the unit's frame
            public float openDeg;                                       // the louvre opening, or the fraction of the analysed size a sail's masts are run out to
            public float sunStoppedPercent, windTorqueOperatingNm;      // the mechanics at this state (LouvreMechanics): the direct sun stopped, and the wind torque on one blade's pivot at the operating wind limit
            public BarInput[] bars = Array.Empty<BarInput>();
            public SurfaceInput[] surfaces = Array.Empty<SurfaceInput>();
        }

        [Serializable]
        public class RenderRequest
        {
            public string pieceName = "";
            public string kind = "overhead";
            public string kindLabel = "";
            public float lengthM, depthM, heightM;
            public float chordM = 0.15f, thicknessM = 0.03f;
            public int count, bays;
            public float pitchM;
            public StateInput[] states = Array.Empty<StateInput>();
            public string[] mechanicsLines = Array.Empty<string>();     // the mechanics in words, for the title card
        }

        [Serializable]
        public class ResultsFile
        {
            public string pieceName = "";
            public string message = "";
            public CollisionAnalysisRunner.VideoInfo video = new CollisionAnalysisRunner.VideoInfo();
            public string videoError = "";
            public string error;
        }

        class Config
        {
            public bool RecordVideo = true;
            public string VideoPath;
            public int Width = 1280;
            public int Height = 720;
            public int Fps = 30;
        }

        /// <summary>A bar or membrane in the scene, with the object that draws it.</summary>
        class Part
        {
            public GameObject Go;
            public bool Cylinder;
            public bool IsSurface;
            public Mesh Mesh;
        }

        const float TitleCardS = 2.5f;
        const float MoveS = 2.2f;
        const float HoldS = 1.6f;
        const float SummaryCardS = 3.0f;

        static readonly Color TimberColor = new Color(0.62f, 0.44f, 0.28f);
        static readonly Color FrameColor = new Color(0.82f, 0.83f, 0.86f);
        static readonly Color SteelColor = new Color(0.30f, 0.32f, 0.36f);
        static readonly Color MastColor = new Color(0.96f, 0.76f, 0.10f);
        static readonly Color SailColor = new Color(0.92f, 0.93f, 0.95f);
        static readonly Color NetColor = new Color(0.32f, 0.42f, 0.36f);
        static readonly Color SlabColor = new Color(0.55f, 0.56f, 0.58f);

        readonly ResultsFile _results = new ResultsFile();
        Config _cfg;
        RenderRequest _request;
        readonly List<Part> _parts = new List<Part>();
        Light _light;
        Camera _cam;
        SimulationHud _hud;
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
            if (!AnalysisMode.IsKinetics) return;
            new GameObject("KineticsRunner").AddComponent<KineticsRunner>();
        }

        void Start()
        {
            try
            {
                if (!Prepare() || !Recording) { WriteReport(0); return; }
                StartCoroutine(Guard(Run()));
            }
            catch (Exception ex) { Fail(ex); }
        }

        void OnDestroy() { FinishVideo(); }

        bool Prepare()
        {
            _cfg = ParseConfig();
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            var layoutPath = LayoutFileArg();
            if (layoutPath == null || !File.Exists(layoutPath))
            {
                _results.message = "No Kinetics request file given (-layoutFile=).";
                Debug.LogWarning("[Kinetics] " + _results.message);
                return false;
            }

            _request = JsonUtility.FromJson<RenderRequest>(File.ReadAllText(layoutPath));
            _results.pieceName = _request?.pieceName ?? "";
            if (_request == null || _request.states == null || _request.states.Length == 0 ||
                (_request.states[0].bars.Length == 0 && _request.states[0].surfaces.Length == 0))
            {
                _results.message = "The Kinetics request had no piece or no states to animate.";
                Debug.LogWarning("[Kinetics] " + _results.message);
                return false;
            }

            // the slats a roller curtain is made of exist only in the CAD model: here the curtain is its membrane
            foreach (var st in _request.states) st.bars = Array.FindAll(st.bars, b => !b.cadOnly);
            BuildScene();
            if (_cfg.RecordVideo) StartRecorder();
            return true;
        }

        // -------------------------------------------------------------- the scene

        /// <summary>The unit's frame is right-handed with z up; Unity's is y up. (x, y, z) becomes (x, z, y): the real unit, not its mirror image (as LayoutSpace does for the plan).</summary>
        static Vector3 U3(float[] v) => new Vector3(v[0], v[2], v[1]);

        static Color ColorOf(string role)
        {
            switch (role)
            {
                case "blade": case "fin": case "slat": return TimberColor;
                case "mast": return MastColor;
                case "sail": return SailColor;
                case "curtain": return NetColor;
                case "rod": case "bottombar": return SteelColor;
                case "crank": case "piston": return new Color(0.75f, 0.35f, 0.15f);
                case "track": case "housing": return new Color(0.45f, 0.47f, 0.50f);
                case "carriage": return new Color(0.20f, 0.45f, 0.75f);
                case "motor": return new Color(0.15f, 0.55f, 0.35f);
                default: return FrameColor;             // posts, rails, housings
            }
        }

        void BuildScene()
        {
            var r = _request;
            var first = r.states[0];
            var mats = new Dictionary<string, Material>();
            Material MatOf(string role)
            {
                var c = ColorOf(role);
                var key = c.ToString();
                if (!mats.TryGetValue(key, out var m)) { m = SceneBuilder.LitMaterial(c, role == "mast" || role == "rod" || role == "bottombar" ? 0.35f : 0.15f); mats[key] = m; }
                return m;
            }

            Bounds bounds = new Bounds(U3(first.bars.Length > 0 ? first.bars[0].p0 : first.surfaces[0].a), Vector3.zero);
            foreach (var s in r.states)
            {
                foreach (var b in s.bars) { bounds.Encapsulate(U3(b.p0)); bounds.Encapsulate(U3(b.p1)); }
                foreach (var f in s.surfaces) { bounds.Encapsulate(U3(f.a)); bounds.Encapsulate(U3(f.b)); bounds.Encapsulate(U3(f.c)); bounds.Encapsulate(U3(f.d)); }
            }

            foreach (var b in first.bars)
            {
                var cyl = b.role == "mast" || b.role == "rod" || b.role == "housing" || b.role == "piston";
                var go = GameObject.CreatePrimitive(cyl ? PrimitiveType.Cylinder : PrimitiveType.Cube);
                go.name = b.role;
                go.GetComponent<Renderer>().sharedMaterial = MatOf(b.role);
                SceneBuilder.RemoveCollider(go);
                _parts.Add(new Part { Go = go, Cylinder = cyl });
            }
            foreach (var f in first.surfaces)
            {
                var go = new GameObject(f.role);
                var mesh = new Mesh { name = f.role };
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = MatOf(f.role);
                _parts.Add(new Part { Go = go, IsSurface = true, Mesh = mesh });
            }
            Pose(first, first, 0f);

            // the roof slab under the unit, a little wider than it
            var margin = 1.2f;
            var slabSize = new Vector3(bounds.size.x + 2 * margin, 0.12f, bounds.size.z + 2 * margin);
            SceneBuilder.Box("Slab", new Vector3(bounds.center.x, bounds.min.y - 0.06f, bounds.center.z), slabSize, SceneBuilder.LitMaterial(SlabColor, 0.05f));

            var target = bounds.center;
            var radius = Mathf.Max(bounds.extents.magnitude, 1.5f);
            var distance = radius * 2.6f;
            const float elevation = 24f * Mathf.Deg2Rad;
            var camGo = new GameObject("Cam");
            _cam = camGo.AddComponent<Camera>();
            // an overhead louvre or a sail is seen from its front (-y, Unity -z); a screen or a fence from the outside, where the sun is (+y, Unity +z), a little to one side
            var fromOutside = r.kind == "slats" || r.kind == "fins" || r.kind == "fence";
            var dir = fromOutside ? new Vector3(0.45f, 0f, 1f).normalized : new Vector3(0.25f, 0f, -1f).normalized;
            _cam.transform.position = target + (new Vector3(dir.x * Mathf.Cos(elevation), Mathf.Sin(elevation), dir.z * Mathf.Cos(elevation))) * distance;
            _cam.transform.LookAt(target);
            _cam.farClipPlane = distance * 6f;
            _cam.fieldOfView = 42f;

            SceneBuilder.BuildLight(radius * 2f);
            _light = UnityEngine.Object.FindFirstObjectByType<Light>();
            if (_light != null) _light.shadows = LightShadows.Soft;
            SetSun(first, first, 0f);

            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height, "Kinetics", false);
            _hud.SetCaseStudy((_request.pieceName ?? "") + " — " + (string.IsNullOrEmpty(_request.kindLabel) ? "dynamic unit" : _request.kindLabel.ToLowerInvariant()) + ", isolated");
        }

        /// <summary>Places every part between two states (t = 0 the first, 1 the second): the ends of each bar, the section direction, the corners of each membrane.</summary>
        void Pose(StateInput a, StateInput b, float t)
        {
            var bi = 0;
            for (var i = 0; i < _parts.Count; i++)
            {
                var part = _parts[i];
                if (!part.IsSurface)
                {
                    var ba = a.bars[bi]; var bb = b.bars.Length > bi ? b.bars[bi] : ba; bi++;
                    var p0 = Vector3.Lerp(U3(ba.p0), U3(bb.p0), t);
                    var p1 = Vector3.Lerp(U3(ba.p1), U3(bb.p1), t);
                    var u = Vector3.Slerp(U3(ba.u).normalized, U3(bb.u).normalized, t);
                    var axis = p1 - p0;
                    var length = Mathf.Max(axis.magnitude, 0.001f);
                    var aDir = axis / length;
                    var vDir = Vector3.Cross(aDir, u);
                    if (vDir.sqrMagnitude < 1e-8f) vDir = Vector3.Cross(aDir, Vector3.up);
                    var sizeU = Mathf.Lerp(ba.sizeU, bb.sizeU, t); var sizeV = Mathf.Lerp(ba.sizeV, bb.sizeV, t);
                    var tr = part.Go.transform;
                    tr.position = (p0 + p1) * 0.5f;
                    if (part.Cylinder)
                    {
                        tr.rotation = Quaternion.FromToRotation(Vector3.up, aDir);
                        tr.localScale = new Vector3(sizeU, length * 0.5f, sizeV);          // a Unity cylinder is 1 across and 2 along its Y
                    }
                    else
                    {
                        tr.rotation = Quaternion.LookRotation(aDir, vDir.normalized);
                        tr.localScale = new Vector3(sizeU, sizeV, length);
                    }
                }
                else
                {
                    // the surfaces come after the bars in the list, in the same order as in every state
                    var si = i - CountBars();
                    var sa = a.surfaces[si]; var sb = b.surfaces.Length > si ? b.surfaces[si] : sa;
                    var v = new[]
                    {
                        Vector3.Lerp(U3(sa.a), U3(sb.a), t), Vector3.Lerp(U3(sa.b), U3(sb.b), t),
                        Vector3.Lerp(U3(sa.c), U3(sb.c), t), Vector3.Lerp(U3(sa.d), U3(sb.d), t),
                    };
                    part.Mesh.Clear();
                    var n = Vector3.Cross(v[1] - v[0], v[3] - v[0]).normalized;
                    part.Mesh.vertices = new[] { v[0], v[1], v[2], v[3], v[0], v[1], v[2], v[3] };
                    part.Mesh.normals = new[] { n, n, n, n, -n, -n, -n, -n };
                    part.Mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
                }
            }
        }

        int _barCount = -1;
        int CountBars()
        {
            if (_barCount < 0) { _barCount = 0; foreach (var p in _parts) if (!p.IsSurface) _barCount++; }
            return _barCount;
        }

        /// <summary>The sun between two states: its horizontal direction in the unit's frame and its height.</summary>
        void SetSun(StateInput a, StateInput b, float t)
        {
            if (_light == null) return;
            var el = Mathf.Max(6f, Mathf.Lerp(a.sunElevationDeg, b.sunElevationDeg, t)) * Mathf.Deg2Rad;
            var h = Vector2.Lerp(new Vector2(a.sunX, a.sunY), new Vector2(b.sunX, b.sunY), t);
            if (h.sqrMagnitude < 1e-6f) h = new Vector2(0f, 1f);
            h.Normalize();
            var towardSun = new Vector3(h.x * Mathf.Cos(el), Mathf.Sin(el), h.y * Mathf.Cos(el));
            _light.transform.rotation = Quaternion.LookRotation(-towardSun);
        }

        // -------------------------------------------------------------- the film

        static string Upper(string s) => (s ?? "").ToUpperInvariant();

        string Banner(StateInput s)
        {
            var sun = s.sunElevationDeg > 0.5f ? "   SUN " + s.sunElevationDeg.ToString("0") + " DEG HIGH" : "";
            return _request.kind == "fence" ? "FENCE " + Upper(s.label) : Upper(s.label) + sun;
        }

        /// <summary>What the unit is doing at this state, in a few words (the first line at the foot of the picture).</summary>
        string StateLine(StateInput s)
        {
            switch (_request.kind)
            {
                case "overhead": return "Louvres " + s.openDeg.ToString("0") + " deg open";
                case "slats": return "Slats tipped " + s.openDeg.ToString("0") + " deg";
                case "fins": return "Fins turned " + s.openDeg.ToString("0") + " deg";
                case "sail": return "Masts run to " + (s.openDeg * 100f).ToString("0") + "% of the analysed size";
                default: return "";
            }
        }

        /// <summary>Text broken into lines of at most `maxChars` characters at the spaces (TextMesh does not wrap).</summary>
        static string Wrap(string text, int maxChars)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Split('\n'))
            {
                var line = "";
                foreach (var word in para.Split(' '))
                {
                    if (line.Length > 0 && line.Length + 1 + word.Length > maxChars) { lines.Add(line); line = word; }
                    else line = line.Length == 0 ? word : line + " " + word;
                }
                lines.Add(line);
            }
            return string.Join("\n", lines);
        }

        void ShowMechanics(StateInput s)
        {
            switch (_request.kind)
            {
                case "sail":
                    _hud.SetFeed(new List<string> { StateLine(s), "Shade the sail keeps of what the analysis wanted: " + s.sunStoppedPercent.ToString("0") + "%" });
                    break;
                case "fence":
                    _hud.SetFeed(new List<string> { "Turned on only when it is needed: the roller lets the bottom bar rise along the rails" });
                    break;
                default:
                    _hud.SetFeed(new List<string>
                    {
                        StateLine(s),
                        "Direct sun stopped at this angle: " + s.sunStoppedPercent.ToString("0") + "%",
                        "Wind torque on one blade's pivot at the operating wind limit: " + s.windTorqueOperatingNm.ToString("0.00") + " N m",
                    });
                    break;
            }
        }

        string Headline()
        {
            switch (_request.kind)
            {
                case "slats": return "Kinetics: a slat screen tipping with the sun";
                case "fins": return "Kinetics: a fin screen turning with the sun";
                case "sail": return "Kinetics: a sail whose masts run on ground rails";
                case "fence": return "Kinetics: a roller fence, deployed only when it is needed";
                default: return "Kinetics: the louvre pergola opening and closing with the sun";
            }
        }

        List<string> IntroLines()
        {
            switch (_request.kind)
            {
                case "sail": return new List<string> { "The masts stand on carriages that slide on ground tracks: run out they spread the sail over the shadow's new place, run in they free the area for a garden; the size comes from the sun's own position, not a hand-set animation.", "In a storm the masts run in, telescope down and the fabric is slack." };
                case "fence": return new List<string> { "A curtain on a roller with guide rails in the Z axis: stored, half up, deployed. The bottom bar rides the rails; the motor is sized for its weight." };
                default: return new List<string> { "Every angle comes straight from LouvreActuationModel — the sun's own position at that hour, not a hand-set animation.", "Each blade is turned to keep the direct sun off the people behind it; one rod turns them all." };
            }
        }

        IEnumerator Run()
        {
            var titleLines = IntroLines();
            if (_request.mechanicsLines != null && _request.mechanicsLines.Length > 0)
            {
                titleLines.Add("");
                titleLines.AddRange(_request.mechanicsLines);
            }
            _hud.ShowCard(Wrap(Headline(), 36), Color.white, _request.pieceName ?? "", titleLines.ConvertAll(l => Wrap(l, 84)), 0.62f, 0.62f, 1.1f);
            foreach (var _ in Hold(TitleCardS, _request.states[0])) yield return null;
            _hud.HideCard();

            var n = _request.states.Length;
            for (var i = 0; i < n; i++)
            {
                var s = _request.states[i];
                if (i > 0) foreach (var _ in Move(_request.states[i - 1], s)) yield return null;
                foreach (var _ in Hold(HoldS, s)) yield return null;
            }
            // and back to where it started, to show the cycle
            if (n > 1) foreach (var _ in Move(_request.states[n - 1], _request.states[0])) yield return null;

            _hud.SetBanner("");
            _hud.ShowCard("Done", Color.white, _request.pieceName ?? "", new List<string>
            {
                Wrap("States shown: " + string.Join(", ", Array.ConvertAll(_request.states, s => s.label)), 52),
            });
            foreach (var _ in Hold(SummaryCardS, _request.states[0])) yield return null;

            FinishVideo();
            WriteReport(0);
        }

        IEnumerable<object> Move(StateInput from, StateInput to)
        {
            var frames = Mathf.Max(2, Mathf.RoundToInt(MoveS * _cfg.Fps));
            for (var i = 0; i < frames; i++)
            {
                var t = Mathf.SmoothStep(0f, 1f, i / (float)(frames - 1));
                Pose(from, to, t);
                SetSun(from, to, t);
                _hud.SetBanner(Banner(to));
                ShowMechanics(to);
                CaptureFrame();
                yield return null;
            }
        }

        IEnumerable<object> Hold(float seconds, StateInput s)
        {
            Pose(s, s, 0f);
            SetSun(s, s, 0f);
            _hud.SetBanner(Banner(s));
            ShowMechanics(s);
            var frames = Mathf.Max(1, Mathf.RoundToInt(seconds * _cfg.Fps));
            for (var i = 0; i < frames; i++) { CaptureFrame(); yield return null; }
        }

        // -------------------------------------------------------------- video / report plumbing (same shape as SunShadeRunner)

        void StartRecorder()
        {
#if UNITY_EDITOR
            try { _recorder = new VideoRecorder(_cam, _cfg.VideoPath, _cfg.Width, _cfg.Height, _cfg.Fps); }
            catch (Exception ex) { _results.videoError = "Couldn't start the video encoder: " + ex.Message; _recorder = null; }
#else
            _results.videoError = "Video recording needs the Unity Editor.";
#endif
        }

        void CaptureFrame()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            try { _recorder.CaptureFrame(); }
            catch (Exception ex) { _results.videoError = "Video encoding failed: " + ex.Message; DiscardVideo(); }
#endif
        }

        void FinishVideo()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var recorder = _recorder;
            _recorder = null;
            try { recorder.Dispose(); }
            catch (Exception ex) { _results.videoError = "Couldn't finalise the MP4: " + ex.Message; return; }
            if (recorder.FramesWritten == 0 || !File.Exists(recorder.Path))
            {
                _results.videoError = "The encoder produced no video.";
                try { if (File.Exists(recorder.Path)) File.Delete(recorder.Path); } catch { /* best effort */ }
                return;
            }
            _results.video.path = recorder.Path;
            _results.video.fps = recorder.Fps;
            _results.video.width = recorder.Width;
            _results.video.height = recorder.Height;
            _results.video.frames = recorder.FramesWritten;
            _results.video.durationS = recorder.FramesWritten / (float)recorder.Fps;
            _results.video.slowMotion = 1f;
#endif
        }

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

        IEnumerator Guard(IEnumerator inner)
        {
            while (true)
            {
                try { if (!inner.MoveNext()) yield break; }
                catch (Exception ex) { Fail(ex); yield break; }
                yield return inner.Current;
            }
        }

        void Fail(Exception ex)
        {
            Debug.LogError("[Kinetics] Failed: " + ex);
            _results.error = ex.Message;
            DiscardVideo();
            WriteReport(1);
        }

        void WriteReport(int exitCode)
        {
            if (_reportWritten) return;
            _reportWritten = true;
            FinishVideo();

            var dir = Path.Combine(Application.dataPath, "..", "Recordings");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "kinetics_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_results, true));
            Debug.Log("[Kinetics] Done. Report: " + path);

#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(exitCode);
            else if (_cam != null) { _cam.targetTexture = null; _cam.enabled = true; }
#endif
        }

        static string LayoutFileArg()
        {
            foreach (var arg in Environment.GetCommandLineArgs())
                if (arg.StartsWith("-layoutFile=")) return arg.Substring("-layoutFile=".Length);
            return null;
        }

        static Config ParseConfig()
        {
            var cfg = new Config { VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "kinetics.mp4")) };
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-noVideo") cfg.RecordVideo = false;
                else if (arg.StartsWith("-videoFile=")) cfg.VideoPath = Path.GetFullPath(arg.Substring("-videoFile=".Length));
                else if (arg.StartsWith("-videoWidth=")) cfg.Width = ParseInt(arg, "-videoWidth=", cfg.Width);
                else if (arg.StartsWith("-videoHeight=")) cfg.Height = ParseInt(arg, "-videoHeight=", cfg.Height);
                else if (arg.StartsWith("-videoFps=")) cfg.Fps = ParseInt(arg, "-videoFps=", cfg.Fps);
            }
            cfg.Width = Mathf.Clamp(cfg.Width, 320, 3840) & ~1;
            cfg.Height = Mathf.Clamp(cfg.Height, 240, 2160) & ~1;
            cfg.Fps = Mathf.Clamp(cfg.Fps, 10, 60);
            return cfg;
        }

        static int ParseInt(string arg, string prefix, int fallback) =>
            int.TryParse(arg.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
