using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;
using UnityEngine;

namespace Sportify.Simulation.Dynamics
{
    /// <summary>
    /// The dynamic structural analysis, filmed as three scenarios one after the other: (1) the crowd through a day, (2) weather
    /// acting on the structure, (3) the deck's resonance with rhythmic crowd movement, ending on the frequency spectrum and the
    /// vibrating deck. Reads the same layout export as the other analyses, runs the model (DynamicLoadCore.cs: plain arithmetic, no
    /// Unity), then plays it: the crowd is a CrowdSim stepped in lockstep with the video, the rain a set of SoilColumns, the load cases
    /// and the response the model's own numbers.
    ///
    /// Started by BatchRunner.RunDynamicAnalysis. Options (all optional):
    ///   -layoutFile=PATH  -videoFile=PATH  -noVideo  -videoWidth= -videoHeight= -videoFps=  -debugStills
    /// </summary>
    public class DynamicRunner : MonoBehaviour
    {
        [Serializable]
        public class ResultsFile
        {
            public string caseStudy = "";
            public string layoutSource = "";
            public string message = "";
            public DynamicReport analysis = new DynamicReport();
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

        sealed class Piece
        {
            public LoadItem Item;
            public GameObject Body;
            public TextMesh Name;
        }

        const float TitleCardS = 3.0f;
        const float CrowdS = 18.0f, CrowdHoldS = 2.5f;
        const float RainS = 5.0f, SnowS = 4.0f, WindS = 5.0f, CaseS = 1.5f, CaseHoldS = 3.0f;
        const float FrequencyS = 4.0f, ForcingS = 5.5f, SweepS = 9.0f, FinalS = 7.0f;
        const float SummaryCardS = 8.0f, FixCardS = 9.0f;
        const float LayerHeight = 0.14f;
        const float VisualHz = 2.2f;                    // how fast the deck is drawn swinging: slowed right down

        static readonly Color Good = new Color(0.45f, 0.90f, 0.55f);
        static readonly Color Muted = new Color(0.66f, 0.70f, 0.76f);
        static readonly Color Amber = new Color(1.00f, 0.78f, 0.20f);
        static readonly Color Red = SimulationHud.CrossingText;
        static readonly Color PlotBack = new Color(0.03f, 0.04f, 0.07f, 0.92f);
        static readonly Color Ink = new Color(0.90f, 0.92f, 0.96f);

        readonly ResultsFile _results = new ResultsFile();
        readonly List<Piece> _pieces = new List<Piece>();
        readonly List<BayTower> _towers = new List<BayTower>();
        Config _cfg;
        GoldbeckPayload _payload;
        DynamicInputs _inputs;
        DynamicReport _report;
        DynamicRun _run;
        LoadField _field;
        Camera _cam;
        SimulationHud _hud;
        HeatLayer _heat;
        AgentPool _agents;
        VibratingDeck _deck;
        RibbonBatch _rain, _snow, _windLines;
        bool _reportWritten;
        float _clock;
        int[] _cellBay;

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
            if (!AnalysisMode.IsDynamic) return;
            new GameObject("DynamicRunner").AddComponent<DynamicRunner>();
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
            _inputs = DynamicLayoutAdapter.ToInputs(_payload);

            if (!_inputs.Structure.Items.Any(i => i.Persons > 0))
            {
                _report = new DynamicReport { ran = false };
                _results.analysis = _report;
                _results.message = "The layout has nowhere for people to be yet (no sports field, activity or accessible garden).";
                Debug.LogWarning("[Dynamic] " + _results.message);
                return false;
            }

            _report = DynamicModel.Analyse(_inputs, out _run);
            _results.analysis = _report;
            _field = _run.Field;
            _results.caseStudy = (LayoutLoader.IsRoofGardenSample ? "Goldbeck default - roof garden sample: " : "") + DynamicModel.CaseStudy(_inputs, _report);

            var c = _report.crowd;
            var r = _report.resonance;
            Debug.Log("[Dynamic] " + _results.caseStudy + ". Crowd peak " + Num(c.peakPersons, "0") + " at " + DynamicModel.Clock(c.peakAtHour) + "; worst case '" + _report.weather.worstCase + "' " +
                      Num(_report.weather.peakUtilisation * 100f, "0") + "% in " + _report.weather.worstBay + "; resonance " + Num(r.worstAccelerationG, "0.000") + " g in " + r.worstBay + " (" + r.baysExceeding + " bays exceed).");

            if (_cfg.RecordVideo)
            {
                BuildScene();
                StartRecorder();
            }
            return true;
        }

        void BuildScene()
        {
            var roof = _payload.roof_context;
            SceneBuilder.BuildRoof(roof);
            _cam = SceneBuilder.BuildCamera(roof, (float)_cfg.Width / _cfg.Height);
            var lookAt = LayoutSpace.ToWorld(roof.length_m * 0.5f, roof.width_m * 0.5f, 2.5f);
            _cam.transform.position = lookAt + (_cam.transform.position - lookAt) * 1.15f;
            _cam.farClipPlane *= 1.2f;
            SceneBuilder.BuildLight(Mathf.Sqrt(roof.length_m * roof.length_m + roof.width_m * roof.width_m));
            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height, "Dynamic structural analysis", false);
            _hud.SetCaseStudy(_results.caseStudy);
            _hud.SetPreliminary(AnalysisAssumptions.PreliminaryBanner(_report.assumptionUses));

            SceneBuilder.BuildEntryPoints(_payload.entry_points);
            BuildPieces();
            _heat = new HeatLayer("HeatDyn", 0f, 0f, roof.length_m, roof.width_m, _field.Nx, _field.Ny, LayerHeight, 3);
            _agents = new AgentPool();
            _deck = new VibratingDeck(_field, _run.Xs, _run.Ys, 0.7f);

            // which bay each cell belongs to (by its centre), for colouring cells by bay
            _cellBay = new int[_field.Nx * _field.Ny];
            for (var iy = 0; iy < _field.Ny; iy++)
                for (var ix = 0; ix < _field.Nx; ix++)
                    _cellBay[_field.Index(ix, iy)] = BayIndex((ix + 0.5) * _field.CellW, (iy + 0.5) * _field.CellH);

            foreach (var bay in _run.Static.bays)
            {
                var top = _inputs.Structure.Items.FirstOrDefault(i => i.Label == bay.topContributor);
                _towers.Add(new BayTower(bay, _hud, top != null ? PieceNames.Short(PieceNames.Compact(top), 22) : ""));
            }
            foreach (var t in _towers) t.SetActive(false);

            var centre = LayoutSpace.ToWorld(roof.length_m * 0.5f, roof.width_m * 0.5f, 6f);
            var big = new Vector3(roof.length_m + 40f, 30f, roof.width_m + 40f);
            _rain = new RibbonBatch("Rain", 700, 8, centre, big);
            _snow = new RibbonBatch("Snowfall", 500, 8, centre, big);
            _windLines = new RibbonBatch("WindLines", 260, 8, centre, big);
            HideStreaks(_rain); HideStreaks(_snow); HideStreaks(_windLines);
        }

        static void HideStreaks(RibbonBatch b)
        {
            for (var i = 0; i < b.Count; i++) b.Hide(i);
            b.Apply();
        }

        int BayIndex(double x, double y)
        {
            var nbx = _run.Xs.Length - 1;
            var c = 0;
            while (c < nbx - 1 && x >= _run.Xs[c + 1]) c++;
            var r = 0;
            while (r < _run.Ys.Length - 2 && y >= _run.Ys[r + 1]) r++;
            return r * nbx + c;
        }

        void BuildPieces()
        {
            foreach (var item in _inputs.Structure.Items)
            {
                var centre = LayoutSpace.ToWorld((float)(item.X + item.Width * 0.5), (float)(item.Y + item.Height * 0.5), 0.05f);
                GameObject body;
                if (item.Kind == LoadKind.Tree)
                {
                    body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    body.name = "Tree";
                    SceneBuilder.RemoveCollider(body);
                    body.transform.localScale = Vector3.one * Mathf.Max(0.8f, (float)item.Width * 0.45f);
                    body.transform.position = centre + Vector3.up * 0.5f;
                    body.GetComponent<Renderer>().sharedMaterial = SceneBuilder.LitMaterial(new Color(0.16f, 0.36f, 0.20f), 0.05f);
                }
                else
                {
                    Color color;
                    if (item.Kind == LoadKind.Court) color = SceneBuilder.SportColor((item.Label ?? "").Split(' ')[0]);
                    else if (item.Kind == LoadKind.Activity) color = SceneBuilder.ActivityColor;
                    else color = item.DeadKnM2 > 3.5 ? new Color(0.30f, 0.50f, 0.30f) : new Color(0.46f, 0.56f, 0.38f);
                    body = SceneBuilder.Box("Piece_" + item.Id, centre, new Vector3((float)item.Width, 0.08f, (float)item.Height), SceneBuilder.LitMaterial(color, 0.10f));
                }
                var name = PieceNames.Label(_hud, item, 0.7f);
                if (name != null) name.gameObject.SetActive(false);
                _pieces.Add(new Piece { Item = item, Body = body, Name = name });
            }
        }

        /// <summary>Shows or hides the name of every piece (its sport, activity or build-up), at the given height above the roof.</summary>
        void ShowNames(bool visible, float height = 0.7f)
        {
            foreach (var p in _pieces)
            {
                if (p.Name == null) continue;
                p.Name.gameObject.SetActive(visible);
                p.Name.transform.position = PieceNames.Position(p.Item, height);
            }
        }

        // the chart area: the empty band above the roof
        ScreenPlot NewPlot(float cx, float w)
        {
            return new ScreenPlot(_cam, _hud, _cfg.Height, cx, 268f, w, 205f, PlotBack);
        }

        // -------------------------------------------------------------- the run

        IEnumerator Run()
        {
            _hud.SetBanner("");
            _hud.SetCountsText("");
            _hud.SetFeed(new List<string>());
            _hud.SetLegend(new List<string>());

            _hud.ShowCard("Dynamic structural analysis", Color.white, _results.caseStudy, TitleLines());
            foreach (var _ in Hold(TitleCardS, "title")) yield return null;
            _hud.HideCard();

            ShowNames(true, 0.7f);
            foreach (var _ in SceneCrowds()) yield return null;
            foreach (var _ in SceneRain()) yield return null;
            foreach (var _ in SceneSnow()) yield return null;
            foreach (var _ in SceneWind()) yield return null;
            ShowNames(false);                       // the blocks and the frequency labels stand where the names would
            foreach (var _ in SceneCases()) yield return null;
            foreach (var _ in SceneFrequencies()) yield return null;
            ShowNames(true, 0.7f);
            foreach (var _ in SceneForcing()) yield return null;
            ShowNames(false);                       // the sweep names the pieces above the swinging deck
            foreach (var _ in SceneSweep()) yield return null;

            _cam.transform.position += Vector3.right * 1000f;   // the cards need a clear background: the roof is left behind, the HUD travels with the camera
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

        static string Banner(string scenario, string text) { return scenario + "   " + text; }

        void ClearStage()
        {
            _heat.Clear();
            _agents.Hide();
            _deck.SetActive(false);
            foreach (var t in _towers) t.SetActive(false);
            _hud.HideWindArrow();
        }

        // ================================================================ 1  CROWDS

        IEnumerable<object> SceneCrowds()
        {
            ClearStage();
            var c = _report.crowd;
            var sched = DaySchedule.Get(_inputs.Schedule);
            var sim = new CrowdSim(_inputs.Structure, sched);
            var plot = NewPlot(0f, 1560f);
            var maxPersons = Mathf.Max(10f, c.samples.Max(s => s.persons) * 1.15f);

            var line = plot.Line(new Color(1f, 0.85f, 0.35f), 3f);
            var pts = new List<Vector2>();
            foreach (var s in c.samples) pts.Add(new Vector2((s.hour - c.startHour) / (c.endHour - c.startHour), Mathf.Clamp01(s.persons / maxPersons) * 0.86f + 0.08f));
            var cursor = plot.Bar(new Color(1f, 1f, 1f, 0.8f));
            plot.Text("people on the roof", 0.11f, 0.93f, Ink, 21f);
            plot.Text(Num(maxPersons, "0"), 0.012f, 0.9f, Muted, 17f);
            for (var h = 6; h <= 22; h += 2) plot.Text(h.ToString("00") + ":00", (h - c.startHour) / (c.endHour - c.startHour), 0.03f, Muted, 17f);
            plot.Text(sched.Name, 0.86f, 0.93f, Muted, 19f);

            var totalSteps = (int)(CrowdSim.EndS - CrowdSim.StartS);
            var frames = Mathf.RoundToInt(CrowdS * _cfg.Fps);
            var done = 0;
            var dwellMax = 1.0;
            _hud.SetLegend(new List<string>
            {
                Tint("dots", new Color(0.55f, 0.90f, 1f)) + " walking in   " + Tint("dots", new Color(1f, 0.92f, 0.55f)) + " settled   " + Tint("dots", new Color(0.78f, 0.78f, 0.84f)) + " leaving",
                "The colour on the roof is where people have spent the day",
                Tint("The day is a generic schedule of the author's; people walk in straight lines", Muted),
            });

            for (var f = 0; f <= frames; f++)
            {
                var target = f >= frames ? totalSteps : Mathf.RoundToInt((f + 1) * (float)totalSteps / frames);
                if (f < frames)
                {
                    while (done < target) { sim.Step(1.0); done++; }
                }
                _clock += 1f / _cfg.Fps;
                _agents.Show(sim.Agents);

                if (f % 2 == 0 || f >= frames)
                {
                    dwellMax = Math.Max(1.0, sim.Dwell.Max());
                    _heat.Fill((ix, iy, cc) =>
                    {
                        var v = (float)(sim.Dwell[_field.Index(ix, iy)] / dwellMax);
                        return v < 0.02f ? new Color32(0, 0, 0, 0) : WithAlpha(LoadColors.Ramp(0.15f + v * 0.85f), 0.25f + 0.55f * v);
                    });
                }

                var hour = (float)sim.Hour;
                var x = Mathf.Clamp01((hour - c.startHour) / (c.endHour - c.startHour));
                var n = Mathf.Max(2, Mathf.CeilToInt(x * pts.Count));
                plot.SetLine(line, pts.Take(n).ToList());
                plot.SetQuad(cursor, x - 0.0012f, 0.02f, x + 0.0012f, 0.98f);

                var hi = Mathf.Clamp(sim.HourIndex, 0, DaySchedule.Hours - 1);
                _hud.SetBanner(Banner("1  CROWDS", DynamicModel.Clock(hour) + "   " + sched.PhaseAt(hi).ToUpperInvariant()));
                _hud.SetCountsText("ON THE ROOF " + Tint(sim.PersonsOnRoof.ToString(), Good) + "   " + Num(sim.PersonsOnRoof * (float)CrowdSim.PersonKn, "0") + " KN");
                var busiest = Enumerable.Range(0, sim.Destinations.Count).OrderByDescending(d => sim.Assigned(d)).Take(2).Where(d => sim.Assigned(d) > 0)
                    .Select(d => sim.Destinations[d].Label + ": " + sim.Assigned(d)).ToArray();
                _hud.SetFeed(new List<string> { busiest.Length == 0 ? "Nobody on the roof" : "Busiest now: " + string.Join(", ", busiest) });

                CaptureFrame();
                if (f == frames * 3 / 5) SaveStill("crowd");
                yield return null;
            }

            // the day is over: where people spent it
            _agents.Hide();
            _hud.SetBanner(Banner("1  CROWDS", "WHERE PEOPLE SPENT THE DAY"));
            _hud.SetCountsText("PEAK " + Tint(Num(c.peakPersons, "0"), Good) + " AT " + DynamicModel.Clock(c.peakAtHour));
            _hud.SetFeed(new List<string>
            {
                "The crowd never weighs more than " + Num(_report.summary.crowdSharePercent, "0.#") + "% of the load: about " + Num(c.peakCrowdKn, "0") + " kN at " + DynamicModel.Clock(c.peakAtHour),
                "Most crowded bay: " + c.busiestBay + ", " + Num(c.busiestBayPeakDensity, "0.00") + " people per m2",
            });
            foreach (var _ in Hold(CrowdHoldS, "crowd-day")) yield return null;
            plot.Destroy();
            _heat.Clear();
        }

        // ================================================================ 2  WEATHER

        IEnumerable<object> SceneRain()
        {
            ClearStage();
            var w = _report.weather;
            var zones = _inputs.Wind != null ? _inputs.Wind.Zones : new List<ZoneInput>();
            var cols = new List<SoilColumn>();
            var zoneOf = new int[_field.Nx * _field.Ny];
            var caps = new List<double>();
            var areas = new List<double>();
            for (var i = 0; i < zones.Count; i++)
            {
                var spec = PercolationModel.SpecFor(zones[i].Assembly);
                cols.Add(new SoilColumn(spec));
                caps.Add(Math.Max(1e-6, PercolationModel.StorageAt(spec, spec.ThetaS) - PercolationModel.StorageAt(spec, spec.ThetaFc)));
                areas.Add(zones[i].Width * zones[i].Height);
            }
            for (var iy = 0; iy < _field.Ny; iy++)
                for (var ix = 0; ix < _field.Nx; ix++)
                {
                    var x = (ix + 0.5) * _field.CellW; var y = (iy + 0.5) * _field.CellH;
                    zoneOf[_field.Index(ix, iy)] = -1;
                    for (var i = 0; i < zones.Count; i++)
                        if (x >= zones[i].X && x <= zones[i].X + zones[i].Width && y >= zones[i].Y && y <= zones[i].Y + zones[i].Height) zoneOf[_field.Index(ix, iy)] = i;
                }

            var plot = NewPlot(0f, 1560f);
            var series = w.rain.addedKnSeries;
            var maxKn = Mathf.Max(1f, series.Length > 0 ? series.Max() * 1.15f : 1f);
            var line = plot.Line(new Color(0.45f, 0.75f, 1f), 3f);
            var pts = new List<Vector2>();
            for (var i = 0; i < series.Length; i++) pts.Add(new Vector2(i / (float)(series.Length - 1), Mathf.Clamp01(series[i] / maxKn) * 0.86f + 0.08f));
            var cursor = plot.Bar(new Color(1f, 1f, 1f, 0.8f));
            plot.Text("water taken up by the build-ups (kN)", 0.19f, 0.93f, Ink, 21f);
            plot.Text(Num(maxKn, "0"), 0.012f, 0.9f, Muted, 17f);
            for (var m = 0; m <= 180; m += 30) plot.Text(m + " min", m / 180f, 0.03f, Muted, 17f);

            _hud.SetLegend(new List<string>
            {
                Tint("blue", new Color(0.3f, 0.5f, 0.95f)) + " = how full each green roof is, from field capacity to saturated",
                "Wetter build-ups weigh more: the load on the deck rises during the storm",
                Tint("A " + Num(w.rain.intensityMmH, "0") + " mm/h cloudburst for " + Num(w.rain.minutes, "0") + " min, on a roof that starts at field capacity", Muted),
            });

            var frames = Mathf.RoundToInt(RainS * _cfg.Fps);
            var totalSteps = (int)(DynamicModel.RainTotalMinutes * 60);
            var done = 0;
            var rng = new System.Random(17);
            var seeds = Enumerable.Range(0, _rain.Count).Select(i => new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble())).ToArray();

            for (var f = 0; f <= frames; f++)
            {
                // the storm itself (the first 12 minutes) gets 40% of the scene, the long drain-down the rest
                var tFrac = (f + 1) / (float)frames;
                var simMinutes = tFrac < 0.4f ? 12f * (tFrac / 0.4f) : 12f + (180f - 12f) * ((tFrac - 0.4f) / 0.6f);
                var target = f >= frames ? totalSteps : Mathf.RoundToInt(simMinutes * 60f);
                while (done < target)
                {
                    var rate = done < DynamicModel.RainMinutes * 60 ? DynamicModel.RainIntensityMmH / 3600.0 : 0.0;
                    foreach (var col in cols) col.Step(1.0, rate);
                    done++;
                }
                _clock += 1f / _cfg.Fps;

                double addedKn = 0;
                for (var i = 0; i < cols.Count; i++) addedKn += Math.Max(0, cols[i].StoredMm) * DynamicModel.Gravity / 1000.0 * areas[i];
                _heat.Fill((ix, iy, cc) =>
                {
                    var z = zoneOf[_field.Index(ix, iy)];
                    if (z < 0) return new Color32(0, 0, 0, 0);
                    var wet = Mathf.Clamp01((float)(Math.Max(0, cols[z].StoredMm) / caps[z]));
                    return (Color32)Color.Lerp(new Color(0.35f, 0.55f, 0.95f, 0.16f), new Color(0.04f, 0.12f, 0.75f, 0.9f), wet);
                });

                var raining = done < DynamicModel.RainMinutes * 60;
                var fall = _clock * 16f;
                var roof = _payload.roof_context;
                for (var i = 0; i < _rain.Count; i++)
                {
                    if (!raining) { _rain.Hide(i); continue; }
                    var s = seeds[i];
                    var yy = 13f - Mathf.Repeat(fall + s.z * 13f, 13f);
                    var p = new Vector3(s.x * roof.length_m, yy, -s.y * roof.width_m);
                    _rain.SetVertical(i, p, p - new Vector3(0f, 1.3f, 0f), 0.06f, new Color32(170, 205, 255, 200));
                }
                _rain.Apply();

                var minute = done / 60f;
                var x = Mathf.Clamp01(minute / 180f);
                plot.SetLine(line, pts.Take(Mathf.Max(2, Mathf.CeilToInt(x * pts.Count))).ToList());
                plot.SetQuad(cursor, x - 0.0012f, 0.02f, x + 0.0012f, 0.98f);
                _hud.SetBanner(Banner("2  WEATHER: RAIN", raining ? "CLOUDBURST " + Num(w.rain.intensityMmH, "0") + " MM/H" : "THE RAIN HAS STOPPED"));
                _hud.SetCountsText("WATER " + Tint("+" + Num((float)addedKn, "0") + " KN", Amber) + "   t = " + Num(minute, "0") + " MIN");
                _hud.SetFeed(new List<string> { "Build-ups: " + Num(w.rain.dryKn, "0") + " kN dry, " + Num(w.rain.fieldCapacityKn, "0") + " at field capacity, " + Num(w.rain.saturatedKn, "0") + " saturated (what the static analysis carries)" });
                CaptureFrame();
                if (f == frames / 3) SaveStill("rain");
                yield return null;
            }
            HideStreaks(_rain);
            plot.Destroy();
            _heat.Clear();
        }

        IEnumerable<object> SceneSnow()
        {
            ClearStage();
            var w = _report.weather;
            var roof = _payload.roof_context;
            var deadFc = _report.weather.cases.First(c => c.key == "snow").totalKn - w.snow.roofKnM2 * roof.length_m * roof.width_m;
            var snowTotal = _report.weather.cases.First(c => c.key == "snow").totalKn;
            var plot = NewPlot(0f, 1560f);
            var line = plot.Line(new Color(0.9f, 0.95f, 1f), 3f);
            var lo = deadFc * 0.97f;
            var hi = snowTotal * 1.03f;
            plot.Text("load on the roof (kN)", 0.12f, 0.93f, Ink, 21f);
            plot.Text(Num(hi, "0"), 0.012f, 0.9f, Muted, 17f);
            plot.Text(Num(lo, "0"), 0.012f, 0.1f, Muted, 17f);
            plot.Text("snow builds up", 0.5f, 0.03f, Muted, 17f);
            var rng = new System.Random(23);
            var seeds = Enumerable.Range(0, _snow.Count).Select(i => new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble())).ToArray();
            _hud.SetLegend(new List<string>
            {
                "Snow on the whole roof: " + Num(w.snow.roofKnM2, "0.00") + " kN/m2 = " + Num(w.snow.shapeCoefficient, "0.0") + " x sk " + Num(w.snow.skKnM2, "0.00") + " kN/m2",
                Tint("DIN EN 1991-1-3/NA, zone " + w.snow.zone + (w.snow.zoneAssumed ? " (ASSUMED: set the site's snow zone)" : "") + " at " + Num(w.snow.altitudeM, "0") + " m" + (w.snow.altitudeAssumed ? " (assumed)" : ""), w.snow.zoneAssumed ? Amber : Muted),
                Tint("No drifting or sliding", Muted),
            });

            var frames = Mathf.RoundToInt(SnowS * _cfg.Fps);
            for (var f = 0; f <= frames; f++)
            {
                var t = f / (float)frames;
                var k = Mathf.SmoothStep(0f, 1f, t);
                _clock += 1f / _cfg.Fps;
                _heat.Fill((ix, iy, cc) => new Color(0.96f, 0.98f, 1f, 0.9f * k));
                var fall = _clock * 3.2f;
                for (var i = 0; i < _snow.Count; i++)
                {
                    var s = seeds[i];
                    var yy = 11f - Mathf.Repeat(fall + s.z * 11f, 11f);
                    var p = new Vector3(s.x * roof.length_m + 0.5f * Mathf.Sin(_clock + s.y * 6f), yy, -s.y * roof.width_m);
                    _snow.SetVertical(i, p, p - new Vector3(0f, 0.28f, 0f), 0.16f, new Color32(255, 255, 255, 230));
                }
                _snow.Apply();

                var pts = new List<Vector2> { new Vector2(0f, Mathf.InverseLerp(lo, hi, deadFc) * 0.86f + 0.07f), new Vector2(Mathf.Max(0.001f, t), Mathf.InverseLerp(lo, hi, Mathf.Lerp(deadFc, snowTotal, k)) * 0.86f + 0.07f) };
                plot.SetLine(line, pts);
                _hud.SetBanner(Banner("2  WEATHER: SNOW", Num(w.snow.roofKnM2 * k, "0.00") + " KN/M2 ON THE ROOF"));
                _hud.SetCountsText("ROOF LOAD " + Tint(Num(Mathf.Lerp(deadFc, snowTotal, k), "0"), Amber) + " KN");
                _hud.SetFeed(new List<string> { "The whole roof carries it, gardens and courts alike" });
                CaptureFrame();
                if (f == frames * 3 / 4) SaveStill("snow");
                yield return null;
            }
            HideStreaks(_snow);
            plot.Destroy();
            _heat.Clear();
        }

        IEnumerable<object> SceneWind()
        {
            ClearStage();
            var w = _report.weather;
            var roof = _payload.roof_context;
            var site = WindModel.ResolveSite(_inputs.Wind ?? new WindInputs { RoofLength = roof.length_m, RoofWidth = roof.width_m });
            var dir = w.wind.worstTowardAngleDeg;
            var cells = DynamicModel.WindCells(_inputs, site, dir, _field);
            var cellArea = _field.CellW * _field.CellH;
            var maxSuction = Math.Min(-1e-9, cells.Min(v => v) / cellArea);   // the most negative, per m2
            var rad = dir * Mathf.Deg2Rad;
            var along = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));
            var side = new Vector3(-along.z, 0f, along.x);
            var rng = new System.Random(31);
            var seeds = Enumerable.Range(0, _windLines.Count).Select(i => new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble())).ToArray();
            var centre = LayoutSpace.ToWorld(roof.length_m * 0.5f, roof.width_m * 0.5f, 1.2f);
            var reach = Mathf.Sqrt(roof.length_m * roof.length_m + roof.width_m * roof.width_m) * 0.62f;

            _hud.SetLegend(new List<string>
            {
                Tint("blue", new Color(0.35f, 0.8f, 1f)) + " = suction lifting the roof: the edges and corners most",
                "Zones F, G, H, I of EN 1991-1-4 for a flat roof, at the direction that lifts a bay most",
                Tint("Wind zone " + w.wind.zone + ", peak pressure " + Num(w.wind.peakPressurePa, "0") + " Pa at " + Num(w.wind.roofHeightM, "0.#") + " m", Muted),
            });
            _hud.ShowWindArrow(dir);

            var frames = Mathf.RoundToInt(WindS * _cfg.Fps);
            for (var f = 0; f <= frames; f++)
            {
                var t = f / (float)frames;
                var k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2f));
                _clock += 1f / _cfg.Fps;
                _heat.Fill((ix, iy, cc) =>
                {
                    var s = Mathf.Clamp01((float)((cells[_field.Index(ix, iy)] / cellArea) / maxSuction));
                    return (Color32)Color.Lerp(new Color(0.9f, 0.95f, 1f, 0f), new Color(0.2f, 0.7f, 1f, 0.9f), s * k);
                });
                for (var i = 0; i < _windLines.Count; i++)
                {
                    var s = seeds[i];
                    var travel = Mathf.Repeat(_clock * 14f + s.z * 2f * reach, 2f * reach) - reach;
                    var p = centre + along * travel + side * ((s.x - 0.5f) * 2f * reach * 0.7f) + Vector3.up * (0.3f + s.y * 1.6f);
                    _windLines.Set(i, p - along * 2.2f, p, 0.1f, new Color32(210, 235, 255, 200));
                }
                _windLines.Apply();
                _hud.SetBanner(Banner("2  WEATHER: WIND", "DESIGN GALE " + WindModel.DirectionLabel(dir).ToUpperInvariant()));
                _hud.SetCountsText("EDGE SUCTION " + Tint(Num(w.wind.maxSuctionKnM2, "0.00"), Amber) + " KN/M2   MEAN " + Num(w.wind.meanSuctionKnM2, "0.00"));
                _hud.SetFeed(new List<string> { "Suction lifts the roof and takes weight off the deck; the build-ups need enough weight to stay put" });
                CaptureFrame();
                if (f == frames * 3 / 4) SaveStill("wind");
                yield return null;
            }
            HideStreaks(_windLines);
            _hud.HideWindArrow();
            _heat.Clear();
        }

        IEnumerable<object> SceneCases()
        {
            ClearStage();
            var w = _report.weather;
            var nb = _towers.Count;
            foreach (var t in _towers) t.SetActive(true);
            var current = new float[nb];
            var order = new[] { "busy", "rain", "snow", "event" };
            var cap = _run.Static.summary.capacityKnM2;
            _hud.SetLegend(new List<string>
            {
                "Block height = the bay's load against the deck capacity of " + Num(cap, "0.#") + " kN/m2" + (_inputs.Structure.CapacityKnM2.HasValue ? "" : Tint(" (a PLACEHOLDER)", Amber)),
                Tint("green", LoadColors.Status("ok")) + " under 80%   " + Tint("amber", LoadColors.Status("marginal")) + " 80-100%   " + Tint("red", LoadColors.Status("over")) + " over the capacity",
                Tint("Characteristic combinations, no partial safety factors", Muted),
            });

            foreach (var key in order)
            {
                var lc = w.cases.First(c => c.key == key);
                var from = (float[])current.Clone();
                var frames = Mathf.RoundToInt(CaseS * _cfg.Fps);
                for (var f = 0; f <= frames; f++)
                {
                    var k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(f / (frames * 0.45f)));
                    _clock += 1f / _cfg.Fps;
                    for (var b = 0; b < nb; b++)
                    {
                        current[b] = Mathf.Lerp(from[b], lc.bayUtilisation[b], k);
                        _towers[b].SetUtilisation(current[b]);
                        _towers[b].SetGrow(1f, _clock);
                    }
                    _hud.SetBanner(Banner("2  WEATHER", lc.name.ToUpperInvariant()));
                    _hud.SetCountsText("BUSIEST " + lc.worstBay + " " + Tint(Num(lc.peakUtilisation * 100f, "0") + "%", lc.peakUtilisation > 1f ? Red : Good) + "   OVER " + lc.baysOver);
                    _hud.SetFeed(new List<string> { lc.description });
                    CaptureFrame();
                    if (key == "snow" && f == frames - 1) SaveStill("case-snow");
                    yield return null;
                }
            }

            // the governing case of every bay
            var govFrames = Mathf.RoundToInt(CaseHoldS * _cfg.Fps);
            var gFrom = (float[])current.Clone();
            for (var f = 0; f <= govFrames; f++)
            {
                var k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(f / (govFrames * 0.4f)));
                _clock += 1f / _cfg.Fps;
                for (var b = 0; b < nb; b++)
                {
                    current[b] = Mathf.Lerp(gFrom[b], w.governing[b].utilisation, k);
                    _towers[b].SetUtilisation(current[b]);
                    _towers[b].SetGrow(1f, _clock);
                }
                _hud.SetBanner(Banner("2  WEATHER", "GOVERNING CASE OF EACH BAY"));
                _hud.SetCountsText(Tint(w.worstCase.ToUpperInvariant(), Amber) + " " + Num(w.peakUtilisation * 100f, "0") + "% IN " + w.worstBay.ToUpperInvariant());
                _hud.SetFeed(new List<string> { "Static design (saturated build-ups + code imposed loads): " + Num(_run.Static.summary.peakUtilisation * 100f, "0") + "% in " + _run.Static.summary.worstBay });
                CaptureFrame();
                if (f == govFrames * 3 / 4) SaveStill("governing");
                yield return null;
            }
            foreach (var t in _towers) { t.SetGrow(0f); t.SetActive(false); }
        }

        // ================================================================ 3  RESONANCE

        static Color FrequencyColour(float hz, float brightness = 1f)
        {
            return Spectrum.At((hz - 2f) / 10f, brightness);
        }

        IEnumerable<object> SceneFrequencies()
        {
            ClearStage();
            var r = _report.resonance;
            var labels = new List<GameObject>();
            for (var b = 0; b < r.bays.Count; b++)
            {
                var bay = _run.Static.bays[b];
                labels.Add(_hud.WorldLabel(Num(r.bays[b].frequencyHz, "0.0") + " Hz", LayoutSpace.ToWorld((bay.x0 + bay.x1) * 0.5f, (bay.y0 + bay.y1) * 0.5f, 1.0f), 1.15f, Color.white).gameObject);
                labels[b].SetActive(false);
            }
            var plot = NewPlot(0f, 1560f);
            var span = plot.Text("natural frequency of each bay: a strip along its long span, deck depth = span / " + Num((float)DynamicModel.SpanToDepth, "0") + ", concrete, the load's mass", 0.5f, 0.86f, Ink, 21f);
            for (var f = 2; f <= 12; f++)
            {
                var x = (f - 2f) / 10f * 0.9f + 0.05f;
                var q = plot.Bar(FrequencyColour(f));
                plot.SetQuad(q, x - 0.045f, 0.34f, x + 0.045f, 0.55f);
                plot.Text(f + " Hz", x, 0.24f, Ink, 19f);
            }
            plot.Text("low", 0.03f, 0.45f, Muted, 17f);
            plot.Text("high", 0.97f, 0.45f, Muted, 17f);
            foreach (var b in r.bays)
            {
                var x = (b.frequencyHz - 2f) / 10f * 0.9f + 0.05f;
                var m = plot.Bar(Color.white);
                plot.SetQuad(m, x - 0.003f, 0.30f, x + 0.003f, 0.62f);
            }

            _hud.SetLegend(new List<string>
            {
                "Each bay coloured by its natural frequency, red (low) to violet (high)",
                r.estimated ? Tint("ESTIMATED from the spans: good to about 25%. Enter the engineer's first natural frequency in the Site tab", Amber) : "The engineer's figure",
                Tint("Heavier and longer means lower: the tree bed's bays are the lowest", Muted),
            });

            var frames = Mathf.RoundToInt(FrequencyS * _cfg.Fps);
            for (var f = 0; f <= frames; f++)
            {
                var k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(f / (frames * 0.35f)));
                _clock += 1f / _cfg.Fps;
                _heat.Fill((ix, iy, cc) => WithAlpha(FrequencyColour(r.bays[_cellBay[_field.Index(ix, iy)]].frequencyHz), 0.62f * k));
                foreach (var l in labels) l.SetActive(k > 0.6f);
                _hud.SetBanner(Banner("3  RESONANCE", "NATURAL FREQUENCY OF EACH BAY"));
                _hud.SetCountsText("FROM " + Tint(Num(r.lowestFrequencyHz, "0.0"), FrequencyColour(r.lowestFrequencyHz)) + " TO " + Tint(Num(r.highestFrequencyHz, "0.0"), FrequencyColour(r.highestFrequencyHz)) + " HZ");
                _hud.SetFeed(new List<string> { "A deck vibrates most when a crowd's rhythm meets its natural frequency" });
                CaptureFrame();
                if (f == frames * 3 / 4) SaveStill("frequencies");
                yield return null;
            }
            foreach (var l in labels) UnityEngine.Object.Destroy(l);
            plot.Destroy();
        }

        IEnumerable<object> SceneForcing()
        {
            var r = _report.resonance;
            var plot = NewPlot(0f, 1560f);
            const float fMin = 1f, fMax = 12.5f;
            Func<float, float> X = hz => 0.07f + (hz - fMin) / (fMax - fMin) * 0.91f;
            plot.Text("what a crowd's rhythm drives: its harmonics (bar thickness = strength)", 0.36f, 0.94f, Ink, 21f);
            var rowColours = new[] { new Color(0.35f, 0.85f, 0.5f), new Color(1f, 0.6f, 0.2f), new Color(0.9f, 0.3f, 0.8f) };
            var bars = new List<GameObject>();
            var barLayout = new List<float[]>();
            for (var a = 0; a < r.activities.Count; a++)
            {
                var act = r.activities[a];
                var y = 0.72f - a * 0.22f;
                plot.Text(act.name, 0.035f, y, rowColours[a % 3], 19f);
                for (var k = 1; k <= act.alpha.Length; k++)
                {
                    if (act.alpha[k - 1] < 0.05f) continue;
                    var th = Mathf.Clamp01(act.alpha[k - 1] / 1.9f) * 0.17f + 0.02f;
                    var q = plot.Bar(rowColours[a % 3]);
                    bars.Add(q);
                    barLayout.Add(new[] { X(k * act.fpLowHz), y - th * 0.5f, X(k * act.fpHighHz), y + th * 0.5f, a });
                    plot.Text(k == 1 ? "1st" : k + "", (X(k * act.fpLowHz) + X(k * act.fpHighHz)) * 0.5f, y + 0.115f, Muted, 15f);
                }
            }
            for (var f = 2; f <= 12; f += 2) plot.Text(f + " Hz", X(f), 0.03f, Muted, 17f);

            var lo = r.lowestFrequencyHz * (r.estimated ? (float)DynamicModel.BandLow : 1f);
            var hi = r.highestFrequencyHz * (r.estimated ? (float)DynamicModel.BandHigh : 1f);
            var deck = plot.Bar(new Color(1f, 1f, 1f, 0.22f));
            plot.SetQuad(deck, X(lo), 0.08f, X(hi), 0.9f);
            plot.Text("the deck", (X(lo) + X(hi)) * 0.5f, 0.14f, Color.white, 19f);

            _hud.SetLegend(new List<string>
            {
                "A rhythm of fp Hz also drives 2 fp, 3 fp, 4 fp: the bars show them",
                "Where a bar crosses the white band, the rhythm meets the deck",
                Tint("Jumping has stronger harmonics, and the crowd moves together", Muted),
            });

            var frames = Mathf.RoundToInt(ForcingS * _cfg.Fps);
            for (var f = 0; f <= frames; f++)
            {
                var t = f / (float)frames;
                _clock += 1f / _cfg.Fps;
                for (var i = 0; i < bars.Count; i++)
                {
                    var b = barLayout[i];
                    var row = (int)b[4];
                    var appear = Mathf.Clamp01(t * 3f - row * 0.6f);
                    if (appear <= 0f) { bars[i].SetActive(false); continue; }
                    plot.SetQuad(bars[i], b[0], b[1], b[0] + (b[2] - b[0]) * appear, b[3]);
                }
                _hud.SetBanner(Banner("3  RESONANCE", "WHAT THE CROWD DRIVES"));
                _hud.SetCountsText("DECK " + Tint(Num(lo, "0.0") + " TO " + Num(hi, "0.0") + " HZ", Color.white));
                _hud.SetFeed(new List<string> { "Walking is gentle; court play and a jumping crowd drive strong harmonics" });
                CaptureFrame();
                if (f == frames * 4 / 5) SaveStill("forcing");
                yield return null;
            }
            plot.Destroy();
        }

        IEnumerable<object> SceneSweep()
        {
            var r = _report.resonance;
            _heat.Clear();
            if (r.sweepG.Length == 0 || string.IsNullOrEmpty(r.worstBay))
            {
                _hud.SetBanner(Banner("3  RESONANCE", "NO RHYTHMIC LOAD ON THIS ROOF"));
                foreach (var _ in Hold(3f, null)) yield return null;
                yield break;
            }

            var act = r.activities.First(a => a.name == r.sweepActivity);
            var nb = _run.Static.bays.Count;
            var limit = act.limitG;
            var force = new double[nb]; var mass = new double[nb]; var fn = new double[nb];
            for (var b = 0; b < nb; b++)
            {
                var bay = r.bays[b];
                var resp = bay.activities.First(x => x.activity == act.name);
                force[b] = resp.participants > 0 ? resp.forcePa : 0;
                mass[b] = bay.massKgM2 + resp.participants * 90.0 / bay.areaM2;
                fn[b] = r.estimated ? bay.frequencyHz * Math.Sqrt(bay.massKgM2 / mass[b]) : bay.frequencyHz;
            }
            var worstIndex = r.bays.FindIndex(x => x.label == r.worstBay);

            var left = NewPlot(-395f, 750f);
            var right = NewPlot(395f, 750f);

            // left: the spectrum, the worst bay's response against the deck's frequency, every column its colour of the spectrum
            var spec = r.spectrum.First(s => s.activity == r.sweepActivity);
            var cols = new List<GameObject>();
            const float sMin = 2f, sMax = 12f;
            left.Text("resonance spectrum: " + r.worstBay + " against the deck's frequency (root scale)", 0.5f, 0.95f, Ink, 19f);
            var specMax = Mathf.Max(spec.accelerationG.Max(), limit * 1.5f);
            Func<float, float> Root = g => Mathf.Sqrt(Mathf.Clamp01(g / specMax));
            for (var i = 0; i < spec.accelerationG.Length; i++)
            {
                var q = left.Bar(FrequencyColour(sMin + i * 0.1f));
                cols.Add(q);
            }
            var limitBar = left.Bar(new Color(1f, 1f, 1f, 0.85f));
            var limitY = Root(limit) * 0.72f + 0.08f;
            left.SetQuad(limitBar, 0.05f, limitY - 0.004f, 0.985f, limitY + 0.004f);
            left.Text("limit " + Num(limit, "0.00") + " g", 0.9f, limitY + 0.06f, Color.white, 15f);
            var fnMark = left.Bar(new Color(1f, 1f, 1f, 0.9f));
            var fnX = 0.05f + Mathf.Clamp01(((float)fn[worstIndex] - sMin) / (sMax - sMin)) * 0.935f;
            left.SetQuad(fnMark, fnX - 0.003f, 0.06f, fnX + 0.003f, 0.9f);
            left.Text("this deck " + Num((float)fn[worstIndex], "0.0") + " Hz", fnX, 0.02f, Color.white, 16f);
            for (var f = 2; f <= 12; f += 2) left.Text(f + "", 0.05f + (f - sMin) / (sMax - sMin) * 0.935f, 0.955f - 0.9f, Muted, 14f);

            // right: the response as the crowd's rhythm sweeps its range
            right.Text(r.sweepActivity.ToLowerInvariant() + ": " + r.worstBay + " as the rhythm sweeps", 0.5f, 0.93f, Ink, 19f);
            var curve = right.Line(new Color(1f, 0.85f, 0.3f), 3f);
            var curveCursor = right.Bar(new Color(1f, 1f, 1f, 0.85f));
            var limitBar2 = right.Bar(new Color(1f, 1f, 1f, 0.85f));
            var gMax = Mathf.Max(2f * limit, r.sweepG.Max() * 1.1f);
            right.SetQuad(limitBar2, 0.05f, limit / gMax * 0.82f + 0.08f - 0.004f, 0.985f, limit / gMax * 0.82f + 0.08f + 0.004f);
            right.Text("limit", 0.95f, limit / gMax * 0.82f + 0.13f, Color.white, 15f);
            for (var f = 0; f <= 4; f++) right.Text(Num(act.fpLowHz + f / 4f * (act.fpHighHz - act.fpLowHz), "0.0"), 0.05f + f / 4f * 0.935f, 0.04f, Muted, 14f);
            right.Text("rhythm (Hz)", 0.5f, 0.0f + 0.13f, Muted, 14f);
            var sweepPts = new List<Vector2>();
            for (var i = 0; i < r.sweepG.Length; i++) sweepPts.Add(new Vector2(0.05f + i / (float)(r.sweepG.Length - 1) * 0.935f, Mathf.Clamp01(r.sweepG[i] / gMax) * 0.82f + 0.08f));

            _deck.SetActive(true);

            // where things stand, drawn above the swinging deck: white = the courts and play areas where the jumping crowd is, green = gardens (walkers only)
            var markers = new List<GameObject>();
            foreach (var it in _inputs.Structure.Items)
            {
                if (it.Kind == LoadKind.Tree) continue;
                var host = (it.Kind == LoadKind.Court || it.Kind == LoadKind.Activity) && it.Persons > 0;
                var h = 2.0f;
                var x0 = (float)it.X; var x1 = (float)(it.X + it.Width); var y0 = (float)it.Y; var y1 = (float)(it.Y + it.Height);
                var line = SceneBuilder.Line("Outline_" + it.Id, new[]
                {
                    LayoutSpace.ToWorld(x0, y0, h), LayoutSpace.ToWorld(x1, y0, h), LayoutSpace.ToWorld(x1, y1, h), LayoutSpace.ToWorld(x0, y1, h),
                }, host ? new Color(1f, 1f, 1f, 0.95f) : new Color(0.55f, 0.95f, 0.55f, 0.75f), host ? 0.22f : 0.12f, true, true);
                markers.Add(line.gameObject);
                markers.Add(_hud.WorldLabel(PieceNames.Short(it.Name, 26), LayoutSpace.ToWorld((float)(it.X + it.Width * 0.5), (float)(it.Y + it.Height * 0.5), 3.4f), 0.85f, host ? Color.white : new Color(0.7f, 0.95f, 0.7f)).gameObject);
            }
            var amp = new float[nb];
            var colour = new Color[nb];
            var swingClock = 0f;
            var bestIndex = 0;
            for (var i = 1; i < r.sweepG.Length; i++) if (r.sweepG[i] > r.sweepG[bestIndex]) bestIndex = i;
            var fpWorst = act.fpLowHz + bestIndex * (float)DynamicModel.FpStepHz;

            _hud.SetLegend(new List<string>
            {
                "The deck swings slowed down and exaggerated; colour = acceleration against the comfort limit (" + Num(limit, "0.00") + " g)",
                Tint("blue", LoadColors.Ramp(0.05f)) + " calm   " + Tint("green", LoadColors.Ramp(0.5f)) + " noticeable   " + Tint("yellow", LoadColors.Ramp(0.8f)) + " near the limit   " + Tint("red", LoadColors.Ramp(1f)) + " over it",
                Tint("White = the courts (the jumping crowd); green = gardens (walkers only)", Muted),
            });

            var totalFrames = Mathf.RoundToInt((SweepS + FinalS) * _cfg.Fps);
            var sweepFrames = Mathf.RoundToInt(SweepS * _cfg.Fps);
            var labels = new List<GameObject>();
            for (var f = 0; f <= totalFrames; f++)
            {
                var sweeping = f < sweepFrames;
                var t = sweeping ? f / (float)sweepFrames : 1f;
                var fp = sweeping ? Mathf.Lerp(act.fpLowHz, act.fpHighHz, t) : fpWorst;
                _clock += 1f / _cfg.Fps;
                swingClock += VisualHz / _cfg.Fps;

                var worstRatio = 0f;
                for (var b = 0; b < nb; b++)
                {
                    var g = force[b] > 0 ? (float)DynamicModel.AccelerationG(act.alpha, force[b], mass[b], fn[b], fp, DynamicModel.Damping) : 0f;
                    var ratio = g / limit;
                    worstRatio = Mathf.Max(worstRatio, ratio);
                    amp[b] = Mathf.Clamp(ratio * 0.22f, 0f, 0.95f);
                    colour[b] = force[b] > 0 ? LoadColors.Ramp(Mathf.Clamp(ratio, 0.02f, 1.6f)) : new Color(0.30f, 0.38f, 0.52f);
                }
                _deck.Update(amp, colour, Mathf.Sin(swingClock * Mathf.PI * 2f));

                // spectrum columns rise at the start
                var grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(f / (float)(_cfg.Fps * 2)));
                for (var i = 0; i < cols.Count; i++)
                {
                    var x0 = 0.05f + i / (float)(cols.Count) * 0.935f;
                    var x1 = 0.05f + (i + 1) / (float)(cols.Count) * 0.935f;
                    var h = Root(spec.accelerationG[i]) * 0.72f * grow;
                    left.SetQuad(cols[i], x0, 0.08f, x1, 0.08f + Mathf.Max(0.004f, h));
                }
                var upto = sweeping ? Mathf.Max(2, Mathf.CeilToInt(t * sweepPts.Count)) : sweepPts.Count;
                right.SetLine(curve, sweepPts.Take(upto).ToList());
                var cx = 0.05f + Mathf.InverseLerp(act.fpLowHz, act.fpHighHz, fp) * 0.935f;
                right.SetQuad(curveCursor, cx - 0.003f, 0.06f, cx + 0.003f, 0.92f);

                _hud.SetBanner(Banner("3  RESONANCE", sweeping ? "THE CROWD'S RHYTHM SWEEPS " + Num(act.fpLowHz, "0.0") + " TO " + Num(act.fpHighHz, "0.0") + " HZ" : "AT THE WORST RHYTHM"));
                _hud.SetCountsText("RHYTHM " + Tint(Num(fp, "0.0") + " HZ", Color.white) + "   WORST " + Tint(Num(worstRatio * limit, "0.00") + " G", worstRatio > 1f ? Red : Good) + " / " + Num(limit, "0.00"));
                _hud.SetFeed(new List<string>
                {
                    "Harmonic " + r.worstHarmonic + " of the rhythm meets the deck around " + Num((float)fn[worstIndex], "0.0") + " Hz: " + r.worstBay + " swings the most",
                    r.estimated ? "The deck's frequency is an estimate (about +/- 25%)" : "",
                });

                if (!sweeping && labels.Count == 0)
                {
                    for (var b = 0; b < nb; b++)
                    {
                        if (force[b] <= 0) continue;
                        var bay = _run.Static.bays[b];
                        var g = (float)DynamicModel.AccelerationG(act.alpha, force[b], mass[b], fn[b], fp, DynamicModel.Damping);
                        if (g / limit < 0.3f) continue;
                        labels.Add(_hud.WorldLabel(Num(g, "0.00") + " g", LayoutSpace.ToWorld((bay.x0 + bay.x1) * 0.5f, (bay.y0 + bay.y1) * 0.5f, 2.2f), 1.1f, Color.white).gameObject);
                    }
                }

                CaptureFrame();
                if (f == sweepFrames / 2) SaveStill("sweep");
                if (f == totalFrames - 20) { SaveStill("spectrum-vibration"); SavePoster(); }
                if (f == totalFrames - 13) SaveStill("vibration-half-cycle-later");
                yield return null;
            }

            foreach (var l in labels) UnityEngine.Object.Destroy(l);
            foreach (var m in markers) UnityEngine.Object.Destroy(m);
            left.Destroy();
            right.Destroy();
            _deck.SetActive(false);
        }

        static Color32 WithAlpha(Color c, float alpha)
        {
            return new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
        }

        /// <summary>Plays a scene: calls onFrame(t) with t from 0 to 1 once per video frame, capturing each.</summary>
        IEnumerable<object> Hold(float seconds, string stillName)
        {
            var frames = Mathf.Max(1, Mathf.RoundToInt(seconds * _cfg.Fps));
            for (var i = 0; i < frames; i++)
            {
                _clock += 1f / _cfg.Fps;
                CaptureFrame();
                if (stillName != null && i == frames / 2) SaveStill(stillName);
                yield return null;
            }
        }

        // -------------------------------------------------------------- cards

        List<string> TitleLines()
        {
            return new List<string>
            {
                "Three scenarios, one after the other:",
                "",
                "  1  Crowds:     how people move over the roof through a day",
                "  2  Weather:    what a cloudburst, snow and wind do to the load",
                "  3  Resonance:  whether a rhythmic crowd shakes the deck",
                "",
                "It ends on the frequency spectrum and the vibrating deck.",
                SimulationHud.Tint("A screening model, not a structural verification or a vibration design.", Muted),
            };
        }

        void ShowSummaryCard()
        {
            var c = _report.crowd;
            var w = _report.weather;
            var r = _report.resonance;
            var s = _report.summary;
            var headline = r.baysExceeding > 0 || s.baysOverCapacity > 0
                ? (r.baysExceeding > 0 ? "Resonance: " + r.baysExceeding + " of " + r.bays.Count + " bays exceed the " + (r.estimated ? "estimated " : "") + "comfort limit" : s.baysOverCapacity + " bays exceed the " + (s.capacityAssumed ? "ASSUMED " : "") + "capacity in some weather")
                : "Nothing exceeds its limit";
            var lines = new List<string>
            {
                "1  Crowds:   busiest at " + DynamicModel.Clock(c.peakAtHour) + ", about " + Num(c.peakPersons, "0") + " people (" + Num(c.peakCrowdKn, "0") + " kN, " + Num(s.crowdSharePercent, "0.#") + "% of the load)",
                "             most crowded " + c.busiestBay + ", " + Num(c.busiestBayPeakDensity, "0.00") + " people per m2",
                "2  Weather:  governing '" + w.worstCase + "', " + w.worstBay + " at " + Cell(w.peakUtilisation),
                "             snow zone " + w.snow.zone + (w.snow.zoneAssumed ? Tint(" (assumed)", Amber) : "") + ", sk " + Num(w.snow.skKnM2, "0.00") + "; a cloudburst adds " + Num(w.rain.peakAddedKn, "0") + " kN",
                "3  Resonance:  deck " + Num(r.lowestFrequencyHz, "0.0") + " to " + Num(r.highestFrequencyHz, "0.0") + " Hz" + (r.estimated ? Tint(" (estimated)", Amber) : ""),
                "             worst " + r.worstBay + ", " + (r.worstActivity ?? "").ToLowerInvariant() + ": " + Tint(Num(r.worstAccelerationG, "0.00") + " g", r.worstRatio > 1f ? Red : Good) + " against " + Num(r.worstLimitG, "0.00") + " g",
            };
            _hud.ShowCard(headline, r.worstRatio > 1f || s.baysOverCapacity > 0 ? Amber : Good, _results.caseStudy, lines);
        }

        string Cell(float utilisation)
        {
            var color = utilisation > 1f ? Red : (utilisation > (float)StructureModel.MarginalFrom ? Amber : Good);
            return SimulationHud.Tint(Num(utilisation * 100f, "0") + "%", color);
        }

        void ShowFixCard()
        {
            var lines = new List<string>();
            var w = _report.weather;
            var r = _report.resonance;
            var s = _report.summary;
            var over = w.governing.Where(g => g.utilisation > 1f).OrderByDescending(g => g.utilisation).ToList();
            if (over.Count > 0)
                CardText.Bullet(lines, Tint("Weather", Red) + "  " + string.Join(", ", over.Take(4).Select(g => g.label + " " + Num(g.utilisation * 100f, "0") + "% (" + g.governingCase.ToLowerInvariant() + ")").ToArray()) + (over.Count > 4 ? ", ..." : "") + ".");
            else
                CardText.Bullet(lines, Tint("Weather", Good) + "  No bay exceeds the capacity in any case.");
            if (r.worstRatio > 1f)
            {
                var a = r.activities.First(x => x.name == r.worstActivity);
                var kMax = 1;
                for (var k = 1; k <= a.alpha.Length; k++) if (a.alpha[k - 1] >= 0.25f) kMax = k;
                CardText.Bullet(lines, Tint("Resonance", Red) + "  " + r.worstBay + " reaches " + Num(r.worstAccelerationG, "0.00") + " g under " + r.worstActivity.ToLowerInvariant() + " (limit " + Num(r.worstLimitG, "0.00") + "). Only a deck above " +
                                Num(a.fpHighHz * kMax, "0.0") + " Hz clears the crowd's harmonics; else more damping, or no rhythmic events on these bays.");
            }
            else CardText.Bullet(lines, Tint("Resonance", Good) + "  No bay exceeds its comfort limit.");
            CardText.Bullet(lines, Tint("Crowds", Amber) + "  The people are " + Num(s.crowdSharePercent, "0.#") + "% of the load: the build-ups decide the balance, not the crowd.");
            if (s.preliminary) CardText.Bullet(lines, Tint("PRELIMINARY", Amber) + "  Not confirmed: " + AnalysisAssumptions.PreliminaryNames(_report.assumptionUses) + ". Enter your own values or accept the built-in ones (the Site tab, or the window Revit opens before the analysis).");
            else if (!string.IsNullOrEmpty(s.acceptedNote)) CardText.Bullet(lines, Tint("Inputs", Muted) + "  " + s.acceptedNote + ".");
            _hud.ShowCard("What to change", Color.white, "Screening result: the results file lists every number and assumption", lines);
        }

        // -------------------------------------------------------------- video plumbing

        void StartRecorder()
        {
#if UNITY_EDITOR
            try
            {
                _recorder = new VideoRecorder(_cam, _cfg.VideoPath, _cfg.Width, _cfg.Height, _cfg.Fps);
                Debug.Log("[Dynamic] Recording " + _cfg.Width + "x" + _cfg.Height + " @ " + _cfg.Fps + " fps to " + _cfg.VideoPath);
            }
            catch (Exception ex)
            {
                _results.videoError = "Couldn't start the video encoder: " + ex.Message;
                Debug.LogError("[Dynamic] " + _results.videoError);
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
                Debug.LogError("[Dynamic] " + _results.videoError);
                DiscardVideo();
            }
#endif
        }

        void SaveStill(string name)
        {
#if UNITY_EDITOR
            if (_recorder == null || !_cfg.DebugStills) return;
            var stillPath = Path.ChangeExtension(_cfg.VideoPath, null) + "_" + name + ".png";
            try { _recorder.SaveStill(stillPath); } catch (Exception ex) { Debug.LogWarning("[Dynamic] Couldn't save " + stillPath + ": " + ex.Message); }
#endif
        }

        void SavePoster()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var posterPath = Path.ChangeExtension(_cfg.VideoPath, ".png");
            try { _recorder.SaveStill(posterPath); _results.video.posterPath = posterPath; }
            catch (Exception ex) { Debug.LogWarning("[Dynamic] Couldn't save the poster image: " + ex.Message); }
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
                Debug.LogError("[Dynamic] " + _results.videoError);
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
            Debug.Log("[Dynamic] Video: " + recorder.Path + " (" + recorder.FramesWritten + " frames, " + Num(_results.video.durationS, "0.0") + " s)");
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
            Debug.LogError("[Dynamic] Failed: " + ex);
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
            var path = Path.Combine(dir, "dynamic_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_results, true));
            Debug.Log("[Dynamic] Done. Report: " + path);

#if UNITY_EDITOR
            if (Application.isBatchMode) UnityEditor.EditorApplication.Exit(exitCode);
            else if (_cam != null) { _cam.targetTexture = null; _cam.enabled = true; }
#endif
        }

        // -------------------------------------------------------------- helpers

        static string Tint(string text, Color color) { return SimulationHud.Tint(text, color); }
        static string Num(float value, string format) { return value.ToString(format, CultureInfo.InvariantCulture); }

        static Config ParseConfig()
        {
            var cfg = new Config { VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "dynamic_analysis.mp4")) };
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
}
