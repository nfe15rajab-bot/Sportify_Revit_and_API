using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Sportify.Simulation.Wind
{
    /// <summary>
    /// The garden analysis: what a storm does to a green roof. Reads the same layout export as the ball
    /// simulation, runs the wind and erosion model (WindAnalysisCore.cs - plain arithmetic, no Unity),
    /// then films the result: the wind coming from eight directions across the roof, the build-ups that
    /// would lift, the substrate that would blow away, and what to change.
    ///
    /// The numbers come from the model, never from the render: the video is a picture of them, drawn
    /// frame by frame from a fixed seed, so it is the same every run and the analysis would be identical
    /// without it (the Revit add-in computes the same numbers without Unity at all).
    ///
    /// Started by BatchRunner.RunWindAnalysis. Command-line options (all optional):
    ///   -layoutFile=PATH   Sportify Combine export to analyse (default: bundled roof-garden sample)
    ///   -videoFile=PATH    where to write the MP4 (default: Recordings/wind_erosion.mp4)
    ///   -noVideo           numbers only
    ///   -videoWidth= -videoHeight= -videoFps=   default 1920 x 1080 @ 30
    ///   -debugStills       also save PNGs of each section of the video
    ///   -windZone=1..4     German wind zone (default 2)      -terrain=0|I|II|III|IV   (default III)
    ///   -roofHeight=M      roof height above ground, if the layout does not carry it
    /// </summary>
    public class WindAnalysisRunner : MonoBehaviour
    {
        // -------------------------------------------------------------- results file (JsonUtility)

        [Serializable]
        public class ResultsFile
        {
            public string caseStudy = "";
            public string layoutSource = "";
            public string message = "";
            public WindReport analysis = new WindReport();
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
            public string WindZone;
            public string Terrain;
            public float RoofHeightM;
        }

        // -------------------------------------------------------------- timeline (seconds of video)

        const float TitleCardS = 2.5f;
        const float DirectionS = 2.6f;
        const float UpliftS = 3.5f;
        const float ErosionBareS = 3.5f;
        const float ErosionClosedS = 3.0f;
        const float PosterHoldS = 1.0f;
        const float SummaryCardS = 6.0f;
        const float FixSceneS = 4.0f;
        const float FixCardS = 8.0f;

        const int StreakCount = 1500;
        const float StreakSpeedMs = 15f;     // how fast a line moves in the video where the wind is undisturbed
        const float IdleSpeedUp = 0.45f;     // gentle sway between sections
        const int FeedLines = 5;

        static readonly Color Good = new Color(0.45f, 0.90f, 0.55f);
        static readonly Color Muted = new Color(0.66f, 0.70f, 0.76f);
        static readonly Color Amber = new Color(1.00f, 0.78f, 0.20f);
        static readonly Color Orange = new Color(0.98f, 0.60f, 0.15f);
        static readonly Color Red = SimulationHud.CrossingText;

        // -------------------------------------------------------------- state

        readonly ResultsFile _results = new ResultsFile();
        Config _cfg;
        GoldbeckPayload _payload;
        WindInputs _inputs;
        WindSite _site;
        WindReport _report;
        RoofField _roofField;
        HeatLayer _roofLayer;
        readonly List<ZoneField> _zoneFields = new List<ZoneField>();
        readonly List<HeatLayer> _zoneLayers = new List<HeatLayer>();
        readonly List<float> _zoneTops = new List<float>();
        readonly List<PlantVisual> _plants = new List<PlantVisual>();
        StreakField _streaks;
        DustField _dust;
        Camera _cam;
        SimulationHud _hud;

        float _time;
        int _dir = -1;                 // wind direction on show, or -1
        bool _windOn;
        bool _showLoad;
        Vector3 _windVec = Vector3.right;
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
            if (!AnalysisMode.IsWind) return;
            new GameObject("WindAnalysisRunner").AddComponent<WindAnalysisRunner>();
        }

        void Start()
        {
            // Everything funnels into WriteReport (success, nothing to analyse, or an exception) so a
            // headless -executeMethod run always exits instead of leaving Unity for the caller to time out on.
            try
            {
                if (!Prepare())
                {
                    WriteReport(0);
                    return;
                }
                if (!Recording)
                {
                    // Nothing to film (-noVideo, or the encoder would not start): the numbers are already done.
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

            _inputs = WindLayoutAdapter.ToInputs(_payload);
            if (!string.IsNullOrEmpty(_cfg.WindZone)) _inputs.WindZone = _cfg.WindZone;
            if (!string.IsNullOrEmpty(_cfg.Terrain)) _inputs.TerrainCategory = _cfg.Terrain;
            if (_cfg.RoofHeightM > 0f) _inputs.RoofElevationM = _cfg.RoofHeightM;

            _site = WindModel.ResolveSite(_inputs);
            _report = WindModel.Analyse(_inputs);
            _results.analysis = _report;
            _results.caseStudy = LayoutLoader.IsRoofGardenSample
                ? "Goldbeck default - roof garden sample: " + WindModel.CaseStudy(_inputs)
                : WindModel.CaseStudy(_inputs);

            if (_inputs.Zones.Count == 0 && _inputs.Plants.Count == 0)
            {
                _report.ran = false;
                _results.message = "The layout has no green-roof zones or plants to analyse. Draw a green roof zone in the Combine tab first.";
                Debug.LogWarning("[Wind] " + _results.message);
                return false;
            }

            Debug.Log("[Wind] " + _results.caseStudy + ". Peak pressure at the roof " + Num(_report.site.peakPressureAtRoofPa, "0") +
                      " Pa; " + _report.summary.plantsFailing + " tree(s) fail, " + _report.summary.zonesUpliftFlagged + " zone(s) lift.");

            BuildScene();
            if (_cfg.RecordVideo) StartRecorder();
            return true;
        }

        void BuildScene()
        {
            var roof = _payload.roof_context;
            _roofField = new RoofField(_inputs, _site);

            SceneBuilder.BuildRoof(roof);

            var aspect = (float)_cfg.Width / _cfg.Height;
            _cam = SceneBuilder.BuildCamera(roof, aspect);
            SceneBuilder.BuildLight(Mathf.Sqrt(roof.length_m * roof.length_m + roof.width_m * roof.width_m));
            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height, "Wind & erosion analysis", false);
            _hud.SetCaseStudy(_results.caseStudy);

            BuildCourts();
            SceneBuilder.BuildContext(LayoutLoader.ExtractContext(_payload).Where(p => p.Category != "garden").ToList());
            SceneBuilder.BuildEntryPoints(_payload.entry_points);

            // The roof-wide layer sits just above the roof and courts; each bed gets its own on its top face.
            _roofLayer = new HeatLayer("HeatRoof", 0f, 0f, roof.length_m, roof.width_m, _roofField.Nx, _roofField.Ny, 0.07f, 3);

            for (var i = 0; i < _inputs.Zones.Count; i++) BuildZone(i, _inputs.Zones[i]);

            for (var i = 0; i < _inputs.Plants.Count; i++)
            {
                var data = _report.plants[i];
                var zone = _inputs.Zones.FirstOrDefault(z => z.Id == data.zoneId);
                var baseY = zone != null ? _zoneTops[_inputs.Zones.IndexOf(zone)] : 0f;
                _plants.Add(new PlantVisual(data, baseY, data.status != "not-checked"));
            }

            var surface = 0f;
            _streaks = new StreakField(_roofField, StreakCount, StreakSpeedMs, surface);
            _dust = new DustField(_roofField, 700, 260f, surface);
        }

        void BuildCourts()
        {
            foreach (var c in LayoutLoader.ExtractCourts(_payload))
            {
                var centre = c.CenterLayout;
                SceneBuilder.Box("Court_" + c.Index, LayoutSpace.ToWorld(centre.x, centre.y, 0.02f),
                    new Vector3(c.ExtentX, 0.04f, c.ExtentY), SceneBuilder.LitMaterial(SceneBuilder.SportColor(c.Sport), 0.15f));
                SceneBuilder.Line("CourtLines_" + c.Index, new[]
                {
                    LayoutSpace.ToWorld(c.XMin, c.YMin, 0.055f), LayoutSpace.ToWorld(c.XMax, c.YMin, 0.055f),
                    LayoutSpace.ToWorld(c.XMax, c.YMax, 0.055f), LayoutSpace.ToWorld(c.XMin, c.YMax, 0.055f),
                }, new Color(1f, 1f, 1f, 0.95f), 0.10f, true, true);
                _hud.WorldLabel(c.Sport, LayoutSpace.ToWorld(centre.x, centre.y, 0.5f), 0.9f, new Color(1f, 1f, 1f, 0.75f));
            }
        }

        void BuildZone(int index, ZoneInput zone)
        {
            var field = new ZoneField(zone, _inputs, _site);
            _zoneFields.Add(field);

            var top = Mathf.Clamp((float)WindLayoutAdapter.TotalThicknessM(zone.Assembly), 0.06f, 1.4f);
            _zoneTops.Add(top);

            Color color;
            switch (field.Cover)
            {
                case CoverKind.Paved: color = new Color(0.62f, 0.62f, 0.60f); break;
                case CoverKind.CoarseGravel: color = new Color(0.72f, 0.68f, 0.60f); break;
                default:
                    color = string.Equals(zone.Assembly != null ? zone.Assembly.Category : "", "intensive", StringComparison.OrdinalIgnoreCase)
                        ? new Color(0.30f, 0.50f, 0.30f)
                        : new Color(0.46f, 0.56f, 0.38f);
                    break;
            }

            var cx = (float)(zone.X + zone.Width * 0.5);
            var cy = (float)(zone.Y + zone.Height * 0.5);
            SceneBuilder.Box("Zone_" + (index + 1), LayoutSpace.ToWorld(cx, cy, top * 0.5f),
                new Vector3((float)zone.Width, top, (float)zone.Height), SceneBuilder.LitMaterial(color, 0.04f));

            var x0 = (float)zone.X; var x1 = (float)(zone.X + zone.Width);
            var y0 = (float)zone.Y; var y1 = (float)(zone.Y + zone.Height);
            SceneBuilder.Line("ZoneOutline_" + (index + 1), new[]
            {
                LayoutSpace.ToWorld(x0, y0, top + 0.02f), LayoutSpace.ToWorld(x1, y0, top + 0.02f),
                LayoutSpace.ToWorld(x1, y1, top + 0.02f), LayoutSpace.ToWorld(x0, y1, top + 0.02f),
            }, new Color(0.10f, 0.14f, 0.10f, 0.9f), 0.14f, true, true);

            _zoneLayers.Add(new HeatLayer("Heat_" + (index + 1), x0, y0, (float)zone.Width, (float)zone.Height,
                field.Nx, field.Ny, top + 0.03f, 3));

            var system = zone.Assembly != null ? (!string.IsNullOrEmpty(zone.Assembly.SystemName) ? zone.Assembly.SystemName : zone.Assembly.System) : "";
            var labelX = Mathf.Clamp(cx, 6f, _payload.roof_context.length_m - 6f);   // narrow beds at the roof edge would run off the picture
            _hud.WorldLabel((index + 1) + "  " + Short(system), LayoutSpace.ToWorld(labelX, y1 - 1.2f, top + 0.6f), 0.85f, Color.white);
        }

        // -------------------------------------------------------------- the run

        IEnumerator Run()
        {
            _hud.SetBanner("");
            _hud.SetCountsText("");
            ShowCalm();

            // Title card over the untouched garden.
            if (Recording)
            {
                _hud.ShowCard("Wind & erosion analysis", Color.white, _results.caseStudy, TitleLines());
                foreach (var _ in Play(TitleCardS, "title")) yield return null;
                _hud.HideCard();
            }

            // The wind from each of the eight directions in turn.
            for (var d = 0; d < WindModel.DirectionsDeg.Length; d++)
            {
                BeginDirection(d);
                foreach (var _ in Play(DirectionS, d == 2 ? "sweep" : null)) yield return null;
            }

            // Which build-ups would lift.
            EndWind();
            ShowUpliftMap();
            foreach (var _ in Play(UpliftS, "uplift")) yield return null;

            // Which surfaces would erode: bare while the planting grows, then closed.
            ShowErosionMap(true);
            foreach (var _ in Play(ErosionBareS, "erosion-bare")) yield return null;
            ShowErosionMap(false);
            foreach (var _ in Play(ErosionClosedS, "erosion-closed")) yield return null;
            _dust.Stop();

            // The verdict, over the calm garden coloured by how each tree fared.
            ShowVerdictScene();
            foreach (var _ in Play(PosterHoldS, null)) yield return null;
            if (Recording) SavePoster();

            if (Recording)
            {
                ShowSummaryCard();
                foreach (var _ in Play(SummaryCardS, "summary")) yield return null;

                _hud.HideCard();
                ShowFixScene();
                foreach (var _ in Play(FixSceneS, "fixes")) yield return null;

                ShowFixCard();
                foreach (var _ in Play(FixCardS, "fix-list")) yield return null;
            }

            FinishVideo();
            WriteReport(0);
        }

        /// <summary>Plays the current picture for the given number of seconds of video, animating it each frame.</summary>
        IEnumerable<object> Play(float seconds, string stillName)
        {
            var frames = Mathf.Max(1, Mathf.RoundToInt(seconds * _cfg.Fps));
            var dt = 1f / _cfg.Fps;
            for (var i = 0; i < frames; i++)
            {
                Animate(dt);
                CaptureFrame();
                if (stillName != null && i == frames / 2) SaveStill(stillName);
                yield return null;
            }
        }

        void Animate(float dt)
        {
            _time += dt;
            _streaks.Step(dt);
            _dust.Step(dt);

            for (var i = 0; i < _plants.Count; i++)
            {
                var p = _plants[i];
                var d = _dir >= 0 ? _dir : 0;
                var speedFactor = _windOn && _dir >= 0 ? _roofField.SpeedFactorAt(_dir, p.Data.xM, p.Data.yM) : IdleSpeedUp;
                var load = _showLoad && _dir >= 0 && p.Data.utilisationByDirection != null && d < p.Data.utilisationByDirection.Length
                    ? p.Data.utilisationByDirection[d]
                    : 0f;
                p.Update(_time, _windVec, speedFactor, load, _showLoad);
            }

            _streaks.Render();
            _dust.Render();
        }

        // -------------------------------------------------------------- sections

        void ClearMaps()
        {
            _roofLayer.Clear();
            foreach (var layer in _zoneLayers) layer.Clear();
        }

        void ShowCalm()
        {
            ClearMaps();
            _windOn = false;
            _showLoad = false;
            _dir = -1;
            _streaks.Hide();
            _dust.Stop();
            _hud.HideWindArrow();
            _hud.SetFeed(new List<string>());
            _hud.SetLegend(new List<string>());
        }

        void EndWind()
        {
            _windOn = false;
            _showLoad = false;
            _streaks.Hide();
            _hud.HideWindArrow();
            _hud.SetFeed(new List<string>());
        }

        void BeginDirection(int d)
        {
            _dir = d;
            _windOn = true;
            _showLoad = true;

            var angle = WindModel.DirectionsDeg[d];
            var rad = (float)(angle * Math.PI / 180.0);
            _windVec = new Vector3(Mathf.Cos(rad), 0f, -Mathf.Sin(rad));   // layout y runs down the plan, world z the other way

            var report = _report.directions[d];
            // With the orientation known the wind is named by compass ("from the south-west"), else by the plan ("from the top-left").
            var named = string.IsNullOrEmpty(report.compassLabel) ? report.label : report.compassLabel;
            _hud.SetBanner("WIND " + named.ToUpperInvariant() + "   |   " + (d + 1) + " / " + WindModel.DirectionsDeg.Length);
            _hud.SetCountsText("TREES AT RISK " + Tint(report.plantsFailing.ToString(), report.plantsFailing > 0 ? Red : Good) +
                               "   MARGINAL " + Tint(report.plantsMarginal.ToString(), report.plantsMarginal > 0 ? Amber : Good));
            _hud.ShowWindArrow((float)angle);

            // Roof-wide zone map plus each bed's own, all from the same classifier.
            _roofLayer.Fill((ix, iy, c) => ZoneMapColor(_roofField.ZoneAtCell(d, ix, iy)));
            for (var i = 0; i < _zoneLayers.Count; i++)
            {
                _zoneLayers[i].Fill((ix, iy, c) => ZoneMapColor(
                    WindModel.Classify(c.x, c.y, angle, _inputs.RoofLength, _inputs.RoofWidth, _site.RoofElevation)));
            }

            _streaks.Seed(d);

            var legend = new List<string>
            {
                "Roof wind zones (EN 1991-1-4), wind speeds up at the edges:",
                SimulationHud.Tint("F  corners - strongest", Red),
                SimulationHud.Tint("G  edges", Orange),
                SimulationHud.Tint("H  outer band", Amber),
                "Ring under a tree: green / amber / red = load against its limit",
            };
            if (report.isPrevailing)
                legend.Add(SimulationHud.Tint("Prevailing direction (generic for Germany, not site data)", Amber));
            _hud.SetLegend(legend);

            var lines = new List<string>();
            var loaded = _report.plants
                .Where(p => p.status != "not-checked" && p.utilisationByDirection != null && p.utilisationByDirection[d] > 0.8f)
                .OrderByDescending(p => p.utilisationByDirection[d])
                .Take(FeedLines)
                .ToList();
            foreach (var p in loaded)
            {
                var u = p.utilisationByDirection[d];
                lines.Add(SimulationHud.Tint(Num(u, "0.00") + "x", PlantVisual.StatusColor(u)) + "  " + p.species +
                          "  (" + Num(p.xM, "0.#") + ", " + Num(p.yM, "0.#") + ")" + (u > 1f ? "  would be blown over" : ""));
            }
            if (lines.Count == 0) lines.Add(SimulationHud.Tint("No tree is above 80% of its limit from this side", Good));
            _hud.SetFeed(lines);
        }

        void ShowUpliftMap()
        {
            _windOn = false;
            _showLoad = false;
            _roofLayer.Clear();
            _hud.HideWindArrow();

            var flagged = _report.zones.Count(z => z.upliftStatus == "fails");
            _hud.SetBanner("BUILD-UP UPLIFT   |   WORST WIND DIRECTION");
            _hud.SetCountsText("ZONES LIFTING " + Tint(flagged.ToString(), flagged > 0 ? Red : Good) + " / " + _report.zones.Count);

            for (var i = 0; i < _zoneLayers.Count; i++)
            {
                var f = _zoneFields[i];
                _zoneLayers[i].Fill((ix, iy, c) => UpliftColor(f.Known, f.Uplift[iy * f.Nx + ix]));
            }

            _hud.SetLegend(new List<string>
            {
                "Wind suction on the roof against the weight of the build-up:",
                SimulationHud.Tint("red - would lift (over 1.0x)", Red),
                SimulationHud.Tint("amber - 0.8x to 1.0x", Amber),
                SimulationHud.Tint("green - holds", Good),
            });

            var lines = new List<string>();
            foreach (var z in _report.zones.OrderByDescending(z => z.upliftUtilisationMax).Take(FeedLines))
            {
                if (z.upliftStatus == "unknown") { lines.Add(z.label + "  no build-up data"); continue; }
                var color = z.upliftStatus == "fails" ? Red : (z.upliftStatus == "marginal" ? Amber : Good);
                lines.Add(SimulationHud.Tint(Num(z.upliftUtilisationMax, "0.00") + "x", color) + "  " + z.label + "  " + Short(z.system) +
                          "  " + Num(z.dryWeightKgM2, "0") + " kg/m2");
            }
            _hud.SetFeed(lines);
        }

        void ShowErosionMap(bool bare)
        {
            _windOn = false;
            _showLoad = false;
            _roofLayer.Clear();
            _hud.HideWindArrow();

            var reference = bare ? (float)WindModel.StrongBreezeMs : (float)WindModel.GaleMs;
            _hud.SetBanner(bare ? "EROSION   |   BARE SUBSTRATE, BEAUFORT 6" : "EROSION   |   CLOSED PLANTING, BEAUFORT 8");

            var sources = new List<DustField.Source>();
            var affected = 0f;
            var planted = 0f;

            for (var i = 0; i < _zoneLayers.Count; i++)
            {
                var f = _zoneFields[i];
                var layer = _zoneLayers[i];
                var onsets = bare ? f.BareOnset : f.EstablishedOnset;
                layer.Fill((ix, iy, c) => ErosionColor(f.Paved, onsets[iy * f.Nx + ix], reference));

                if (f.Paved) continue;
                var cellArea = (float)(f.Zone.Width * f.Zone.Height) / (f.Nx * f.Ny);
                planted += f.Nx * f.Ny * cellArea;
                for (var iy = 0; iy < f.Ny; iy++)
                {
                    for (var ix = 0; ix < f.Nx; ix++)
                    {
                        if (onsets[iy * f.Nx + ix] >= reference) continue;
                        affected += cellArea;
                        var c = layer.CellCentre(ix, iy);
                        sources.Add(new DustField.Source { X = c.x, Y = c.y, DirIndex = f.BareWorstDirection[iy * f.Nx + ix] });
                    }
                }
            }

            _dust.Start(sources);
            _hud.SetCountsText("ERODING " + Tint(Num(planted > 0 ? 100f * affected / planted : 0f, "0") + "%", affected > 0 ? Red : Good) + " OF PLANTED AREA");

            _hud.SetLegend(new List<string>
            {
                "Wind speed (10 m) at which the substrate starts to move, worst direction:",
                SimulationHud.Tint("red - moves below " + Num(reference * 0.75f, "0.#") + " m/s", Red),
                SimulationHud.Tint("orange - moves below " + Num(reference, "0.#") + " m/s", Orange),
                SimulationHud.Tint("green - holds", Good),
                SimulationHud.Tint("tan flecks = substrate blown off", Amber),
            });

            var lines = new List<string>();
            if (bare)
            {
                lines.Add("Bare substrate, before the planting closes:");
                lines.Add("starts to move at " + Tint(Num(_report.summary.lowestBareOnsetMs, "0.#") + " m/s", Red) + " at the most exposed point");
                lines.Add(Num(_report.summary.percentPlantedAreaBareErodes, "0") + "% of the planted area moves in a strong breeze");
            }
            else
            {
                lines.Add("Closed planting doubles the wind needed:");
                lines.Add(Num(_report.summary.percentPlantedAreaErosionFlagged, "0.#") + "% of the planted area still moves in a gale");
                lines.Add(affected > 0 ? "mostly the corner strips" : "nothing moves");
            }
            _hud.SetFeed(lines);
        }

        void ShowVerdictScene()
        {
            ClearMaps();
            _dust.Stop();
            _streaks.Hide();
            _windOn = false;
            _showLoad = false;
            _hud.HideWindArrow();
            _hud.SetFeed(new List<string>());
            _hud.SetLegend(new List<string>
            {
                "Ring under a tree, worst wind direction:",
                SimulationHud.Tint("green - holds", Good),
                SimulationHud.Tint("amber - 80% to 100% of its limit", Amber),
                SimulationHud.Tint("red - would be blown over", Red),
            });
            _hud.SetBanner("RESULT");

            var s = _report.summary;
            _hud.SetCountsText("TREES FAILING " + Tint(s.plantsFailing.ToString(), s.plantsFailing > 0 ? Red : Good) + " / " + s.plantsChecked +
                               "   ZONES LIFTING " + Tint(s.zonesUpliftFlagged.ToString(), s.zonesUpliftFlagged > 0 ? Red : Good));
            foreach (var p in _plants) p.ShowWorstStatus();
        }

        void ShowFixScene()
        {
            ClearMaps();
            _hud.SetBanner("WHAT TO CHANGE");
            _hud.SetLegend(new List<string>
            {
                SimulationHud.Tint("Grey = gravel ballast / erosion strip in the edge band", new Color(0.85f, 0.83f, 0.78f)),
                SimulationHud.Tint("Red ring = tree needs anchorage, more substrate or a move", Red),
            });

            for (var i = 0; i < _zoneLayers.Count; i++)
            {
                var f = _zoneFields[i];
                var zoneResult = _report.zones[i];
                var ballast = zoneResult.upliftStatus == "fails";
                _zoneLayers[i].Fill((ix, iy, c) =>
                {
                    var k = iy * f.Nx + ix;
                    var lifts = ballast && f.Uplift[k] > 1f;
                    var erodes = !f.Paved && f.EstablishedOnset[k] < WindModel.GaleMs;
                    return lifts || erodes ? new Color32(214, 208, 194, 235) : new Color32(0, 0, 0, 0);
                });

                if (ballast)
                {
                    var mid = new Vector2((float)(f.Zone.X + f.Zone.Width * 0.5), (float)(f.Zone.Y + f.Zone.Height * 0.5));
                    _hud.WorldLabel("+" + Num(zoneResult.requiredBallastMm, "0") + " mm gravel at the edges",
                        LayoutSpace.ToWorld(mid.x, mid.y, _zoneTops[i] + 1.0f), 0.85f, Color.white);
                }
            }

            foreach (var p in _plants)
            {
                if (p.Data.status != "fails") continue;
                var text = p.Data.species + "\n" + Num(p.Data.utilisation, "0.0") + "x its limit";
                _hud.WorldLabel(text, p.CrownTop + new Vector3(0f, 1.3f, 0f), 0.9f, Color.white);
            }

            _hud.SetFeed(new List<string>());
        }

        // -------------------------------------------------------------- cards

        List<string> TitleLines()
        {
            var site = _report.site;
            var lines = new List<string>
            {
                "Wind zone " + site.windZone + " (" + Num(site.basicWindSpeedMs, "0.#") + " m/s): " + site.windZoneSource,
                "Terrain " + site.terrainCategory + ", roof " + Num(site.roofElevationM, "0.#") + " m above ground: " + site.roofHeightSource,
                "",
                "What a storm does to the garden:",
                "  - can a tree be blown over?",
                "  - does the build-up lift off the roof?",
                "  - does the substrate blow away?",
                "",
                SimulationHud.Tint("Eight wind directions, one after the other. A screening model, not a structural design.", Muted),
            };
            return lines;
        }

        void ShowSummaryCard()
        {
            var s = _report.summary;
            var problems = s.plantsFailing + s.zonesUpliftFlagged;
            var headline = problems == 0 ? "No wind problems found" : problems + " wind problem" + (problems == 1 ? "" : "s") + " found";

            var lines = new List<string>();
            var site = _report.site;
            lines.Add("Peak wind pressure at the roof " + Num(site.peakPressureAtRoofPa, "0") + " Pa (" + Num(site.peakSpeedAtRoofMs, "0") + " m/s gusts)");
            lines.Add("");

            if (s.plantsChecked > 0)
            {
                var tint = s.plantsFailing > 0 ? Red : (s.plantsMarginal > 0 ? Amber : Good);
                lines.Add(SimulationHud.Tint("Trees:  " + s.plantsFailing + " of " + s.plantsChecked + " would be blown over, " + s.plantsMarginal + " marginal", tint));
                foreach (var p in _report.plants.Where(p => p.status == "fails").OrderByDescending(p => p.utilisation).Take(3))
                    lines.Add("      " + p.species + " - " + Num(p.utilisation, "0.0") + "x its limit, " + p.worstDirectionLabel);
            }
            else
            {
                lines.Add(SimulationHud.Tint("Trees:  none in the layout", Muted));
            }

            if (site.hasNorth && site.prevailingDirectionIndex >= 0)
            {
                var prevailing = _report.directions[site.prevailingDirectionIndex];
                lines.Add(SimulationHud.Tint("Prevailing wind (" + prevailing.compassLabel + "):  " + prevailing.plantsFailing + " trees at risk, " +
                                             prevailing.zonesUpliftFlagged + " zones lift", Muted));
            }

            var upliftTint = s.zonesUpliftFlagged > 0 ? Red : Good;
            lines.Add(SimulationHud.Tint("Uplift:  " + s.zonesUpliftFlagged + " of " + s.zonesChecked + " zones need ballast (" +
                                         Num(s.percentPlantedAreaUpliftFlagged, "0.#") + "% of the planted area)", upliftTint));

            var lowestClosed = _report.zones.Where(z => z.erosionRisk != "n/a").Select(z => z.erosionOnsetEstablishedMs).DefaultIfEmpty(0f).Min();
            lines.Add(SimulationHud.Tint("Erosion:  bare substrate moves from " + Num(s.lowestBareOnsetMs, "0.#") + " m/s; closed planting holds to " +
                                         Num(lowestClosed, "0.#") + " m/s", s.lowestBareOnsetMs < WindModel.StrongBreezeMs ? Amber : Good));

            lines.Add("");
            lines.Add(SimulationHud.Tint("Loads and speeds are estimates: see the assumptions in the results file.", Muted));
            _hud.ShowCard(headline, problems == 0 ? Good : Red, _results.caseStudy, lines);
        }

        void ShowFixCard()
        {
            var lines = new List<string>();

            foreach (var p in _report.plants.Where(p => p.status == "fails").OrderByDescending(p => p.utilisation))
            {
                var fix = new List<string>();
                if (p.requiredAnchorageKNm > 0.05f) fix.Add("anchorage for " + Num(p.requiredAnchorageKNm, "0") + " kNm");
                fix.Add(Num(p.requiredSubstrateMm, "0") + " mm of substrate");
                if (p.canMoveToFix) fix.Add("move to (" + Num(p.betterXM, "0.#") + ", " + Num(p.betterYM, "0.#") + ")");
                Bullet(lines, SimulationHud.Tint("Tree", Red) + "  " + p.species + " at (" + Num(p.xM, "0.#") + ", " + Num(p.yM, "0.#") + "): " +
                              string.Join(" or ", fix.ToArray()));
            }

            foreach (var z in _report.zones)
            {
                if (z.upliftStatus == "fails")
                {
                    Bullet(lines, SimulationHud.Tint("Uplift", Red) + "  " + z.label + ": " + Num(z.upliftFlaggedAreaPercent, "0") + "% of it lifts - add " +
                                  Num(z.requiredBallastMm, "0") + " mm of gravel in the " + Num(z.flaggedDepthM, "0.#") + " m band along the " + z.worstEdge + " edge");
                }
            }

            foreach (var z in _report.zones.Where(z => z.erosionRisk == "high"))
                Bullet(lines, SimulationHud.Tint("Erosion", Amber) + "  " + z.label + ": keep the " + Num(z.erosionBandDepthM, "0.#") + " m strip along the " + z.erosionEdge + " edge in coarse gravel");

            if (_report.summary.percentPlantedAreaBareErodes > 0)
                Bullet(lines, SimulationHud.Tint("Erosion", Amber) + "  until the planting closes, use pre-grown mats or an erosion mat");

            if (_report.summary.plantsMarginal > 0)
                Bullet(lines, SimulationHud.Tint("Watch", Amber) + "  " + _report.summary.plantsMarginal + " marginal tree" + (_report.summary.plantsMarginal == 1 ? "" : "s") + " (80% to 100% of the limit)");

            if (lines.Count == 0) lines.Add(SimulationHud.Tint("Nothing to change: every check passed.", Good));

            _hud.ShowCard("What to change", Color.white, "Screening result: the results file lists every number and assumption", lines);
        }

        /// <summary>Adds a bullet, wrapped to the card's width; long lines continue indented.</summary>
        static void Bullet(List<string> lines, string text)
        {
            const int width = 66;   // characters of visible text per line at the card's font size
            var words = text.Split(' ');
            var line = new StringBuilder("- ");
            var visible = 2;
            var tagOpen = false;

            foreach (var word in words)
            {
                var visibleLength = VisibleLength(word);
                if (visible + visibleLength + 1 > width && visible > 2 && !tagOpen)
                {
                    lines.Add(line.ToString());
                    line = new StringBuilder("    ");
                    visible = 4;
                }
                if (line.Length > 0 && line[line.Length - 1] != ' ') { line.Append(' '); visible++; }
                line.Append(word);
                visible += visibleLength;
                tagOpen = word.Contains("<color") && !word.Contains("</color>");
                if (word.Contains("</color>")) tagOpen = false;
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

        // -------------------------------------------------------------- colours

        static Color32 ZoneMapColor(RoofZone z)
        {
            switch (z)
            {
                case RoofZone.F: return new Color32(242, 64, 51, 175);
                case RoofZone.G: return new Color32(250, 153, 38, 150);
                case RoofZone.H: return new Color32(250, 217, 77, 95);
                default: return new Color32(0, 0, 0, 0);
            }
        }

        static Color32 UpliftColor(bool known, float utilisation)
        {
            if (!known) return new Color32(150, 150, 150, 110);
            if (utilisation > 1f) return new Color32(235, 51, 51, 205);
            if (utilisation > (float)WindModel.MarginalFrom) return new Color32(250, 191, 51, 175);
            return new Color32(77, 191, 102, 100);
        }

        static Color32 ErosionColor(bool paved, float onsetMs, float reference)
        {
            if (paved) return new Color32(0, 0, 0, 0);
            if (onsetMs < reference * 0.75f) return new Color32(235, 51, 51, 205);
            if (onsetMs < reference) return new Color32(250, 153, 38, 185);
            return new Color32(77, 191, 102, 90);
        }

        // -------------------------------------------------------------- video plumbing

        void StartRecorder()
        {
#if UNITY_EDITOR
            try
            {
                _recorder = new VideoRecorder(_cam, _cfg.VideoPath, _cfg.Width, _cfg.Height, _cfg.Fps);
                Debug.Log("[Wind] Recording " + _cfg.Width + "x" + _cfg.Height + " @ " + _cfg.Fps + " fps to " + _cfg.VideoPath);
            }
            catch (Exception ex)
            {
                _results.videoError = "Couldn't start the video encoder: " + ex.Message;
                Debug.LogError("[Wind] " + _results.videoError);
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
            try
            {
                _recorder.CaptureFrame();
            }
            catch (Exception ex)
            {
                _results.videoError = "Video encoding failed: " + ex.Message;
                Debug.LogError("[Wind] " + _results.videoError);
                DiscardVideo();
            }
#endif
        }

        void SaveStill(string name)
        {
#if UNITY_EDITOR
            if (_recorder == null || !_cfg.DebugStills) return;
            var stillPath = Path.ChangeExtension(_cfg.VideoPath, null) + "_" + name + ".png";
            try { _recorder.SaveStill(stillPath); } catch (Exception ex) { Debug.LogWarning("[Wind] Couldn't save " + stillPath + ": " + ex.Message); }
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
                _results.video.posterPath = posterPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Wind] Couldn't save the poster image: " + ex.Message);
            }
#endif
        }

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
                _results.videoError = "Couldn't finalise the MP4: " + ex.Message;
                Debug.LogError("[Wind] " + _results.videoError);
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
            Debug.Log("[Wind] Video: " + recorder.Path + " (" + recorder.FramesWritten + " frames, " + Num(_results.video.durationS, "0.0") + " s)");
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

        // -------------------------------------------------------------- finishing

        /// <summary>Lets a failure inside the coroutine reach Fail(): C# can't yield inside a try/catch.</summary>
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
            Debug.LogError("[Wind] Failed: " + ex);
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
            var path = Path.Combine(dir, "wind_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_results, true));
            Debug.Log("[Wind] Done. Report: " + path);

#if UNITY_EDITOR
            if (Application.isBatchMode)
            {
                UnityEditor.EditorApplication.Exit(exitCode);
            }
            else if (_cam != null)
            {
                _cam.targetTexture = null;
                _cam.enabled = true;
            }
#endif
        }

        // -------------------------------------------------------------- helpers

        static string Tint(string text, Color color)
        {
            return SimulationHud.Tint(text, color);
        }

        static string Num(float value, string format)
        {
            return value.ToString(format, CultureInfo.InvariantCulture);
        }

        /// <summary>"ZinCo Sloped Sedum" is too long for a label on the roof: keep the last words.</summary>
        static string Short(string system)
        {
            if (string.IsNullOrEmpty(system)) return "";
            var words = system.Split(' ');
            if (system.Length <= 24) return system;
            var text = new StringBuilder();
            foreach (var word in words)
            {
                if (text.Length + word.Length + 1 > 22) break;
                if (text.Length > 0) text.Append(' ');
                text.Append(word);
            }
            return text.Append("...").ToString();
        }

        static Config ParseConfig()
        {
            var cfg = new Config
            {
                VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "wind_erosion.mp4")),
            };

            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (arg == "-noVideo") cfg.RecordVideo = false;
                else if (arg == "-debugStills") cfg.DebugStills = true;
                else if (arg.StartsWith("-videoFile=")) cfg.VideoPath = Path.GetFullPath(arg.Substring("-videoFile=".Length));
                else if (arg.StartsWith("-videoWidth=")) cfg.Width = ParseInt(arg, "-videoWidth=", cfg.Width);
                else if (arg.StartsWith("-videoHeight=")) cfg.Height = ParseInt(arg, "-videoHeight=", cfg.Height);
                else if (arg.StartsWith("-videoFps=")) cfg.Fps = ParseInt(arg, "-videoFps=", cfg.Fps);
                else if (arg.StartsWith("-windZone=")) cfg.WindZone = arg.Substring("-windZone=".Length);
                else if (arg.StartsWith("-terrain=")) cfg.Terrain = arg.Substring("-terrain=".Length);
                else if (arg.StartsWith("-roofHeight="))
                    float.TryParse(arg.Substring("-roofHeight=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out cfg.RoofHeightM);
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
