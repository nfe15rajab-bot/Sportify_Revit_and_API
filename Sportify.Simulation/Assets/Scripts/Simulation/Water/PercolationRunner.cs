using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Sportify.Simulation.Wind;
using UnityEngine;

namespace Sportify.Simulation.Water
{
    /// <summary>
    /// The second garden analysis: what rain does inside a green roof. Reads the same layout export as the other
    /// analyses, runs the rainfall / percolation model (PercolationCore.cs: plain arithmetic, no Unity), then films it
    /// in section: rain falling on each build-up, water soaking down through its layers, the drainage layer filling,
    /// drops leaving, and the roof's outflow curve against the same rain on a bare roof.
    ///
    /// The columns drawn are the SoilColumn objects the analysis itself steps, so the picture is the computation, not an
    /// illustration of it. Started by BatchRunner.RunPercolationAnalysis. Options (all optional):
    ///   -layoutFile=PATH  -videoFile=PATH  -noVideo  -videoWidth= -videoHeight= -videoFps=  -debugStills
    /// </summary>
    public class PercolationRunner : MonoBehaviour
    {
        [Serializable]
        public class ResultsFile
        {
            public string caseStudy = "";
            public string layoutSource = "";
            public string message = "";
            public PercolationReport analysis = new PercolationReport();
            public CollisionAnalysisRunner.VideoInfo video = new CollisionAnalysisRunner.VideoInfo();
            public string videoError = "";
            public string error;
        }

        class Config
        {
            public bool RecordVideo = true;
            public string VideoPath;
            public int Width = 1920;
            public int Height = 1080;
            public int Fps = 30;
            public bool DebugStills;
        }

        sealed class Shown
        {
            public int ZoneIndex;
            public ZoneInput Zone;
            public ColumnSpec Spec;
            public SoilColumn Column;
            public ColumnView View;
            public float RunoffMmH;   // smoothed
        }

        const float TitleCardS = 2.5f;
        const float ScenarioVideoS = 8.0f;
        const float ScenarioHoldS = 1.6f;
        const float SummaryCardS = 7.0f;
        const float FixCardS = 8.0f;
        const int MaxColumns = 4;
        const float ColumnWidth = 2.0f;
        const float ColumnGap = 0.5f;
        const float ColumnHeight = 2.3f;
        const float StageTop = 3.6f;

        static readonly Color Good = new Color(0.45f, 0.90f, 0.55f);
        static readonly Color Muted = new Color(0.66f, 0.70f, 0.76f);
        static readonly Color Amber = new Color(1.00f, 0.78f, 0.20f);
        static readonly Color Red = SimulationHud.CrossingText;
        static readonly Color Rain = new Color(0.62f, 0.80f, 1.0f, 0.75f);
        static readonly Color Drip = new Color(0.40f, 0.62f, 1.0f, 0.95f);

        readonly ResultsFile _results = new ResultsFile();
        readonly List<Shown> _shown = new List<Shown>();
        Config _cfg;
        GoldbeckPayload _payload;
        WaterInputs _inputs;
        PercolationReport _report;
        Camera _cam;
        SimulationHud _hud;
        FallField _rain, _drips;
        Hydrograph _hydro;
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
            if (!AnalysisMode.IsPercolation) return;
            new GameObject("PercolationRunner").AddComponent<PercolationRunner>();
        }

        void Start()
        {
            try
            {
                if (!Prepare() || !Recording)
                {
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
            FinishVideo();
        }

        // -------------------------------------------------------------- setup

        bool Prepare()
        {
            _cfg = ParseConfig();
            Application.targetFrameRate = -1;
            QualitySettings.vSyncCount = 0;

            _payload = LayoutLoader.Load(null, LayoutLoader.RoofGardenSampleFileName);
            _results.layoutSource = LayoutLoader.LastPath ?? "";

            var windInputs = WindLayoutAdapter.ToInputs(_payload);
            _inputs = new WaterInputs { RoofLength = windInputs.RoofLength, RoofWidth = windInputs.RoofWidth, Zones = windInputs.Zones };
            _report = PercolationModel.Analyse(_inputs);
            _results.analysis = _report;
            _results.caseStudy = (LayoutLoader.IsRoofGardenSample ? "Goldbeck default - roof garden sample: " : "") +
                                 _inputs.Zones.Count + " green-roof zone" + (_inputs.Zones.Count == 1 ? "" : "s") + " on a " +
                                 Num((float)_inputs.RoofLength, "0.#") + " x " + Num((float)_inputs.RoofWidth, "0.#") + " m roof";

            if (_inputs.Zones.Count == 0)
            {
                _report.ran = false;
                _results.message = "The layout has no green-roof zones to analyse. Draw a green roof zone in the Combine tab first.";
                Debug.LogWarning("[Rain] " + _results.message);
                return false;
            }

            Debug.Log("[Rain] " + _results.caseStudy + ". Cloudburst: " + Num(_report.summary.cloudburstRetainedPercent, "0") + "% stays on the roof, peak " +
                      Num(_report.roof[2].peakFlowLps, "0.#") + " l/s (" + Num(_report.roof[2].referencePeakLps, "0.#") + " bare).");

            if (_cfg.RecordVideo)
            {
                if (_inputs.Zones.Any(z => PercolationModel.SpecFor(z.Assembly).HasSubstrate))
                {
                    BuildStage();
                    StartRecorder();
                }
                else
                {
                    _results.videoError = "Nothing to film: none of the zones has a substrate for water to soak into.";
                }
            }
            return true;
        }

        void BuildStage()
        {
            // Sections are drawn flat, so everything is unlit: no lights, one camera looking straight at the stage.
            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = SceneBuilder.BackgroundColor;
            _cam.fieldOfView = 32f;
            _cam.nearClipPlane = 0.5f;
            _cam.allowHDR = false;
            _cam.allowMSAA = true;
            camGo.transform.position = new Vector3(0f, 0.55f, -15f);
            camGo.transform.rotation = Quaternion.identity;
            _cam.enabled = false;

            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height, "Rain & percolation analysis", false);
            _hud.SetCaseStudy(_results.caseStudy);

            // One column per distinct build-up with a substrate, biggest zones first.
            var seen = new HashSet<string>();
            for (var i = 0; i < _inputs.Zones.Count && _shown.Count < MaxColumns; i++)
            {
                var z = _inputs.Zones[i];
                var spec = PercolationModel.SpecFor(z.Assembly);
                if (!spec.HasSubstrate || !seen.Add(z.Assembly.Key)) continue;
                _shown.Add(new Shown { ZoneIndex = i, Zone = z, Spec = spec });
            }
            _shown.Sort((a, b) => (b.Zone.Width * b.Zone.Height).CompareTo(a.Zone.Width * a.Zone.Height));

            var n = _shown.Count;
            var totalWidth = n * ColumnWidth + (n - 1) * ColumnGap;
            var laneX = new float[n];
            for (var i = 0; i < n; i++)
            {
                var s = _shown[i];
                laneX[i] = -totalWidth * 0.5f + ColumnWidth * 0.5f + i * (ColumnWidth + ColumnGap);
                s.Column = new SoilColumn(s.Spec);
                var name = s.Zone.Assembly.SystemName;
                if (string.IsNullOrEmpty(name)) name = s.Zone.Assembly.System;
                s.View = new ColumnView((i + 1) + "  " + Short(name, 16), s.Zone.Assembly, s.Column, laneX[i], 0f, ColumnHeight, ColumnWidth, _hud);
            }

            var tops = _shown.Select(s => s.View.TopY).ToArray();
            _rain = new FallField("Rain", 420, laneX, ColumnWidth * 0.95f, StageTop, 0f, 0.32f, 7.5f, Rain, 11, lane => tops[lane], 8);
            _drips = new FallField("Drips", 200, laneX, ColumnWidth * 0.6f, 0f, -0.22f, 0.1f, 1.6f, Drip, 12, lane => -0.22f, 9);
            _hydro = new Hydrograph(_hud, -6.3f, 6.3f, -2.28f, -1.5f, (float)PercolationModel.SeriesStepS);
        }

        // -------------------------------------------------------------- the run

        IEnumerator Run()
        {
            _hud.SetBanner("");
            _hud.SetCountsText("");
            SetLegend();

            _hud.ShowCard("Rain & percolation analysis", Color.white, _results.caseStudy, TitleLines());
            foreach (var _ in Hold(TitleCardS, "title")) yield return null;
            _hud.HideCard();

            for (var k = 0; k < _report.scenarios.Count; k++)
            {
                foreach (var _ in PlayScenario(k)) yield return null;
            }

            SavePoster();
            _cam.transform.position += Vector3.right * 1000f;   // the cards need a clear background: the stage is left behind, the HUD travels with the camera
            _hud.SetFeed(new List<string>());
            _hud.SetLegend(new List<string>());
            _hud.SetBanner("RESULT");
            _hud.SetCountsText("");
            ShowSummaryCard();
            foreach (var _ in Hold(SummaryCardS, "summary")) yield return null;

            _hud.SetBanner("WHAT TO CHANGE");
            ShowFixCard();
            foreach (var _ in Hold(FixCardS, "fix-list")) yield return null;

            FinishVideo();
            WriteReport(0);
        }

        IEnumerable<object> PlayScenario(int k)
        {
            var sc = _report.scenarios[k];
            var roof = _report.roof[k];
            var rainS = sc.durationMin * 60f;
            var totalS = rainS + (float)PercolationModel.TailMin * 60f;
            var frames = Mathf.RoundToInt(ScenarioVideoS * _cfg.Fps);
            var simPerFrame = totalS / frames;
            var rate = sc.intensityMmH / 3600.0;

            foreach (var s in _shown)
            {
                s.Column = new SoilColumn(s.Spec);
                s.View.Bind(s.Column);
                s.RunoffMmH = 0f;
            }
            _hydro.Begin(roof.referenceLps, roof.flowLps, totalS / 60f);

            var simDone = 0;
            for (var f = 0; f < frames; f++)
            {
                var target = Mathf.RoundToInt((f + 1) * simPerFrame);
                while (simDone < target)
                {
                    foreach (var s in _shown) s.Column.Step(PercolationModel.DtS, simDone < rainS ? rate : 0.0);
                    simDone++;
                }

                var raining = simDone < rainS;
                _rain.SetActive(raining ? 0.15f + 0.85f * Mathf.Clamp01(sc.intensityMmH / 108f) : 0f);
                for (var i = 0; i < _shown.Count; i++)
                {
                    var s = _shown[i];
                    var now = (float)(s.Column.LastRunoffMmS * 3600.0);
                    s.RunoffMmH = Mathf.Lerp(s.RunoffMmH, now, 0.25f);
                    var share = Mathf.Clamp01(s.RunoffMmH / Mathf.Max(1f, sc.intensityMmH));
                    _drips.SetLaneActive(i, s.RunoffMmH > 0.3f ? Mathf.Max(0.12f, share) : 0f);
                    s.View.Refresh();
                    s.View.SetReadout("runoff " + Num(s.RunoffMmH, "0") + " mm/h\n" + Num((float)s.Column.RunoffMm, "0.0") + " of " + Num((float)s.Column.RainMm, "0.0") + " mm");
                }

                var dt = 1f / _cfg.Fps;
                _rain.Step(dt);
                _drips.Step(dt);
                _rain.Render();
                _drips.Render();
                _hydro.Reveal(simDone / 60f);

                var clock = simDone / 60;
                _hud.SetBanner(sc.name.ToUpperInvariant() + " " + Num(sc.intensityMmH, "0") + " MM/H   |   t = " + clock + " min");
                var nowRoof = 0f;
                var idx = Mathf.Clamp(Mathf.FloorToInt(simDone / (float)PercolationModel.SeriesStepS), 0, roof.flowLps.Length - 1);
                nowRoof = roof.flowLps[idx];
                _hud.SetCountsText("OUTFLOW " + Tint(Num(nowRoof, "0.0"), Good) + " vs BARE " + Tint(Num(roof.referenceLps[idx], "0.0"), Red) + " l/s");
                _hud.SetFeed(new List<string> { raining ? "Raining" : "Rain has stopped: the roof drains down" });

                CaptureFrame();
                if (f == frames / 2) SaveStill("rain" + (k + 1));
                yield return null;
            }

            // The result of this scenario, held.
            var lines = new List<string>();
            for (var i = 0; i < _shown.Count && i < 5; i++)
            {
                var r = _report.zones[_shown[i].ZoneIndex].scenarios[k];
                var color = r.retainedPercent >= (float)PercolationModel.TargetRetentionPercent ? Good : (r.retainedPercent >= 30f ? Amber : Red);
                lines.Add(Tint(Num(r.retainedPercent, "0") + "%", color) + " kept   " + (i + 1) + "  " + Short(_shown[i].Zone.Assembly.SystemName ?? _shown[i].Zone.Assembly.System) +
                          (r.saturated ? "  (fills up)" : "") + (r.surfaceRunoffMm > 0.5f ? "  (surface runoff)" : ""));
            }
            _hud.SetFeed(lines);
            _hud.SetBanner(sc.name.ToUpperInvariant() + ": ROOF KEEPS " + Num(roof.retainedPercent, "0") + "%");
            _hud.SetCountsText("PEAK " + Tint(Num(roof.peakFlowLps, "0.0"), Good) + " vs BARE " + Tint(Num(roof.referencePeakLps, "0.0"), Red) + " l/s");
            foreach (var _ in Hold(ScenarioHoldS, null)) yield return null;
        }

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

        // -------------------------------------------------------------- cards

        void SetLegend()
        {
            _hud.SetLegend(new List<string>
            {
                "Each build-up drawn with its layers in proportion, top to bottom",
                SimulationHud.Tint("brown = holding its normal water, blue = extra water in transit (dark = full)", new Color(0.62f, 0.78f, 1f)),
                SimulationHud.Tint("drops under a column = runoff", Drip),
                SimulationHud.Tint("outflow curve: red = bare roof, green = this roof", Muted),
            });
        }

        List<string> TitleLines()
        {
            var lines = new List<string>
            {
                "What happens to rain on the garden roof:",
                "  - how much stays in the build-up?",
                "  - how much runs off, and how much later?",
                "  - does any build-up fill up?",
                "",
                "Three generic rain events, each on a wet roof:",
            };
            foreach (var sc in _report.scenarios) lines.Add("  " + sc.name + ":  " + sc.description);
            lines.Add("");
            lines.Add(SimulationHud.Tint("A screening model, not a hydrological design. Not the site's design rainfall.", Muted));
            return lines;
        }

        void ShowSummaryCard()
        {
            var s = _report.summary;
            var headline = s.zonesBelowTarget == 0
                ? "Every build-up meets the retention aim"
                : s.zonesBelowTarget + " of " + s.zonesChecked + " build-up" + (s.zonesChecked == 1 ? "" : "s") + " keep under " + Num((float)PercolationModel.TargetRetentionPercent, "0") + "% of a shower";

            var lines = new List<string> { "Share of the rain kept, against the same rain on a bare roof:", "" };
            for (var i = 0; i < _report.zones.Count; i++)
            {
                var z = _report.zones[i];
                if (z.substrateMm <= 0) { lines.Add(SimulationHud.Tint((i + 1) + "  " + Short(z.system) + ":  no substrate, keeps nothing", Muted)); continue; }
                var row = z.scenarios;
                lines.Add((i + 1) + "  " + Short(z.system) + " (" + Num(z.substrateMm, "0") + " mm)   " +
                          Cell(row[0].retainedPercent) + "  steady   " + Cell(row[1].retainedPercent) + "  shower   " + Cell(row[2].retainedPercent) + "  cloudburst");
            }
            var roof = _report.roof;
            lines.Add("");
            lines.Add("Whole roof:   " + Num(roof[0].retainedPercent, "0") + "% / " + Num(roof[1].retainedPercent, "0") + "% / " + Num(roof[2].retainedPercent, "0") + "%   kept");
            lines.Add("Cloudburst peak: " + Num(roof[2].peakFlowLps, "0") + " l/s, against " + Num(roof[2].referencePeakLps, "0") + " l/s with no green layers");
            _hud.ShowCard(headline, s.zonesBelowTarget == 0 ? Good : Amber, _results.caseStudy, lines);
        }

        string Cell(float percent)
        {
            var color = percent >= (float)PercolationModel.TargetRetentionPercent ? Good : (percent >= 30f ? Amber : Red);
            return SimulationHud.Tint(Num(percent, "0") + "%", color);
        }

        void ShowFixCard()
        {
            var lines = new List<string>();
            foreach (var rec in _report.recommendations)
                CardText.Bullet(lines, SimulationHud.Tint(Label(rec.kind), rec.kind == "fine" ? Good : Amber) + "  " + rec.text);
            _hud.ShowCard("What to change", Color.white, "Screening result: the results file lists every number and assumption", lines);
        }

        static string Label(string kind)
        {
            switch (kind)
            {
                case "more-storage": return "Retention";
                case "check-outlets": return "Drains";
                case "surface": return "Surface";
                default: return "OK";
            }
        }

        // -------------------------------------------------------------- video plumbing

        void StartRecorder()
        {
#if UNITY_EDITOR
            try
            {
                _recorder = new VideoRecorder(_cam, _cfg.VideoPath, _cfg.Width, _cfg.Height, _cfg.Fps);
                Debug.Log("[Rain] Recording " + _cfg.Width + "x" + _cfg.Height + " @ " + _cfg.Fps + " fps to " + _cfg.VideoPath);
            }
            catch (Exception ex)
            {
                _results.videoError = "Couldn't start the video encoder: " + ex.Message;
                Debug.LogError("[Rain] " + _results.videoError);
                _recorder = null;
            }
#else
            _results.videoError = "Video recording needs the Unity Editor.";
#endif
        }

        void CaptureFrame()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            try { _recorder.CaptureFrame(); }
            catch (Exception ex)
            {
                _results.videoError = "Video encoding failed: " + ex.Message;
                Debug.LogError("[Rain] " + _results.videoError);
                DiscardVideo();
            }
#endif
        }

        void SaveStill(string name)
        {
#if UNITY_EDITOR
            if (_recorder == null || !_cfg.DebugStills) return;
            var stillPath = Path.ChangeExtension(_cfg.VideoPath, null) + "_" + name + ".png";
            try { _recorder.SaveStill(stillPath); } catch (Exception ex) { Debug.LogWarning("[Rain] Couldn't save " + stillPath + ": " + ex.Message); }
#endif
        }

        void SavePoster()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var posterPath = Path.ChangeExtension(_cfg.VideoPath, ".png");
            try { _recorder.SaveStill(posterPath); _results.video.posterPath = posterPath; }
            catch (Exception ex) { Debug.LogWarning("[Rain] Couldn't save the poster image: " + ex.Message); }
#endif
        }

        void FinishVideo()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var recorder = _recorder;
            _recorder = null;
            try { recorder.Dispose(); }
            catch (Exception ex)
            {
                _results.videoError = "Couldn't finalise the MP4: " + ex.Message;
                Debug.LogError("[Rain] " + _results.videoError);
                return;
            }

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
            Debug.Log("[Rain] Video: " + recorder.Path + " (" + recorder.FramesWritten + " frames, " + Num(_results.video.durationS, "0.0") + " s)");
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
            Debug.LogError("[Rain] Failed: " + ex);
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
            var path = Path.Combine(dir, "percolation_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_results, true));
            Debug.Log("[Rain] Done. Report: " + path);

#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(exitCode);
            else if (_cam != null) { _cam.targetTexture = null; _cam.enabled = true; }
#endif
        }

        // -------------------------------------------------------------- helpers

        static string Tint(string text, Color color) { return SimulationHud.Tint(text, color); }
        static string Num(float value, string format) { return value.ToString(format, CultureInfo.InvariantCulture); }

        static string Short(string name, int max = 22)
        {
            if (string.IsNullOrEmpty(name)) return "";
            return name.Length <= max ? name : name.Substring(0, max - 2).TrimEnd() + "...";
        }

        static Config ParseConfig()
        {
            var cfg = new Config { VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "rain_percolation.mp4")) };
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-noVideo") cfg.RecordVideo = false;
                else if (arg == "-debugStills") cfg.DebugStills = true;
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

        static int ParseInt(string arg, string prefix, int fallback)
        {
            return int.TryParse(arg.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        }
    }

    /// <summary>Text laid out for the video's cards.</summary>
    public static class CardText
    {
        /// <summary>Adds a bullet wrapped to the card's width; long lines continue indented. Colour tags don't count as text.</summary>
        public static void Bullet(List<string> lines, string text)
        {
            const int width = 66;
            var line = new StringBuilder("- ");
            var visible = 2;
            var open = false;

            foreach (var word in text.Split(' '))
            {
                var length = VisibleLength(word);
                if (visible + length + 1 > width && visible > 2 && !open)
                {
                    lines.Add(line.ToString());
                    line = new StringBuilder("    ");
                    visible = 4;
                }
                if (line.Length > 0 && line[line.Length - 1] != ' ') { line.Append(' '); visible++; }
                line.Append(word);
                visible += length;
                if (word.Contains("<color") && !word.Contains("</color>")) open = true;
                if (word.Contains("</color>")) open = false;
            }
            lines.Add(line.ToString());
        }

        static int VisibleLength(string word)
        {
            var n = 0;
            var inTag = false;
            foreach (var ch in word)
            {
                if (ch == '<') inTag = true;
                else if (ch == '>') inTag = false;
                else if (!inTag) n++;
            }
            return n;
        }
    }
}
