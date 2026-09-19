using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Sportify.Simulation.Water;
using Sportify.Simulation.Wind;
using UnityEngine;

namespace Sportify.Simulation.Structure
{
    /// <summary>
    /// The static structural analysis: where the weight and the people will be on the roof, which bays of the structural grid are
    /// most loaded against the deck's capacity, and whether the load sits to one side. Reads the same layout export as the other
    /// analyses (the grid and columns come from the Revit model with the roof push), runs the load model (StructuralLoadCore.cs:
    /// plain arithmetic, no Unity), then films it: the grid, the permanent load, the imposed load, the people, the bays as blocks
    /// as tall as their load, the load's centre against the structure's, and the advice applied.
    ///
    /// What is drawn is the analysis's own load field and reports, not an illustration of them. Started by
    /// BatchRunner.RunStructuralAnalysis. Options (all optional):
    ///   -layoutFile=PATH  -videoFile=PATH  -noVideo  -videoWidth= -videoHeight= -videoFps=  -debugStills
    /// </summary>
    public class StructuralRunner : MonoBehaviour
    {
        [Serializable]
        public class ResultsFile
        {
            public string caseStudy = "";
            public string layoutSource = "";
            public string message = "";
            public StructureReport analysis = new StructureReport();
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
            public Vector3 Start;
            public LineRenderer Ghost;
            public TextMesh Name;
        }

        const float TitleCardS = 2.5f;
        const float GridS = 4.0f;
        const float DeadS = 5.0f;
        const float LiveS = 4.0f;
        const float PeopleS = 5.0f;
        const float BaysS = 7.0f;
        const float BalanceS = 7.0f;
        const float AdviceS = 8.0f;
        const float SummaryCardS = 6.0f;
        const float FixCardS = 8.0f;
        const float LayerHeight = 0.14f;

        static readonly Color Good = new Color(0.45f, 0.90f, 0.55f);
        static readonly Color Muted = new Color(0.66f, 0.70f, 0.76f);
        static readonly Color Amber = new Color(1.00f, 0.78f, 0.20f);
        static readonly Color Red = SimulationHud.CrossingText;
        static readonly Color GridBlue = new Color(0.45f, 0.65f, 1.0f, 0.9f);
        static readonly Color Orange = new Color(1.0f, 0.55f, 0.12f);

        readonly ResultsFile _results = new ResultsFile();
        readonly List<Piece> _pieces = new List<Piece>();
        readonly List<GameObject> _gridObjects = new List<GameObject>();
        readonly List<GameObject> _columnObjects = new List<GameObject>();
        readonly List<GameObject> _labels = new List<GameObject>();
        readonly List<BayTower> _towers = new List<BayTower>();
        readonly List<GameObject> _balanceObjects = new List<GameObject>();
        Config _cfg;
        GoldbeckPayload _payload;
        StructureInputs _inputs;
        StructureInputs _after;
        StructureReport _report;
        StructureReport _afterReport;
        LoadField _field, _afterField;
        Camera _cam;
        SimulationHud _hud;
        HeatLayer _heat;
        PeopleDots _people;
        GameObject _centreMarker, _loadMarker, _arrow, _splitLine;
        LineRenderer _arrowLine;
        bool _reportWritten;
        float _clock;

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
            if (!AnalysisMode.IsStructural) return;
            new GameObject("StructuralRunner").AddComponent<StructuralRunner>();
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

            _inputs = StructureLayoutAdapter.ToInputs(_payload);
            _report = StructureModel.Analyse(_inputs);
            _results.analysis = _report;
            _results.caseStudy = (LayoutLoader.IsRoofGardenSample ? "Goldbeck default - roof garden sample: " : "") + StructureModel.CaseStudy(_inputs, _report);

            if (_inputs.Items.Count == 0)
            {
                _report.ran = false;
                _results.message = "The layout has nothing that weighs on the roof yet (no sports field, activity, green-roof zone or tree).";
                Debug.LogWarning("[Structure] " + _results.message);
                return false;
            }

            _field = StructureModel.BuildField(_inputs);
            _after = ApplyAdvice(_inputs, _report, out var steps);
            _afterReport = StructureModel.Analyse(_after);
            _afterField = StructureModel.BuildField(_after);
            _adviceSteps = steps;

            Debug.Log("[Structure] " + _results.caseStudy + ". Bays over capacity: " + _report.summary.baysOver + " of " + _report.summary.baysChecked + ", busiest " +
                      Num(_report.summary.peakUtilisation * 100f, "0") + "%; load centre " + Num(_report.balance.totalEccentricityX * 100f, "+0.0;-0.0") + "% / " +
                      Num(_report.balance.totalEccentricityY * 100f, "+0.0;-0.0") + "% off (" + _report.balance.status + ").");

            if (_cfg.RecordVideo)
            {
                BuildScene();
                StartRecorder();
            }
            return true;
        }

        int _adviceSteps;

        /// <summary>The layout with every recommended move and lightening applied, for the "after" pictures.</summary>
        static StructureInputs ApplyAdvice(StructureInputs inputs, StructureReport report, out int steps)
        {
            var copy = inputs.CopyWithItems();
            steps = 0;
            foreach (var rec in report.recommendations)
            {
                if ((rec.kind != "move" && rec.kind != "lighten") || string.IsNullOrEmpty(rec.itemId)) continue;
                var item = copy.Items.Find(i => i.Id == rec.itemId);
                if (item == null) continue;
                if (rec.kind == "move")
                {
                    if (rec.axis == "x") item.X += rec.moveM; else item.Y += rec.moveM;
                }
                else item.DeadKnM2 = rec.newDeadKnM2;
                steps++;
            }
            return copy;
        }

        void BuildScene()
        {
            var roof = _payload.roof_context;
            SceneBuilder.BuildRoof(roof);
            _cam = SceneBuilder.BuildCamera(roof, (float)_cfg.Width / _cfg.Height);
            // A little further back than the shared view: grid names sit outside the roof edges and would run off the frame.
            var lookAt = LayoutSpace.ToWorld(roof.length_m * 0.5f, roof.width_m * 0.5f, 2.5f);
            _cam.transform.position = lookAt + (_cam.transform.position - lookAt) * 1.15f;
            _cam.farClipPlane *= 1.2f;
            SceneBuilder.BuildLight(Mathf.Sqrt(roof.length_m * roof.length_m + roof.width_m * roof.width_m));
            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height, "Structural load analysis", false);
            _hud.SetCaseStudy(_results.caseStudy);

            SceneBuilder.BuildEntryPoints(_payload.entry_points);
            BuildPieces();

            // One layer over the whole roof, at the model's own resolution: what is coloured is the load field the numbers came from.
            _heat = new HeatLayer("HeatLoad", 0f, 0f, roof.length_m, roof.width_m, _field.Nx, _field.Ny, LayerHeight, 3);
            BuildGrid();
            BuildBays();
            BuildBalanceMarkers();
            _people = new PeopleDots(_field, _report.summary.expectedPersons, 320);
        }

        void BuildPieces()
        {
            foreach (var item in _inputs.Items)
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
                _pieces.Add(new Piece { Item = item, Body = body, Start = body.transform.position, Name = name });
            }
        }

        /// <summary>Shows or hides the name of every piece, at the given height above the roof.</summary>
        void ShowNames(bool visible, float height = 0.7f)
        {
            foreach (var p in _pieces)
            {
                if (p.Name == null) continue;
                p.Name.gameObject.SetActive(visible);
                p.Name.transform.position = PieceNames.Position(p.Item, height);
            }
        }

        void BuildGrid()
        {
            var l = _payload.roof_context.length_m;
            var w = _payload.roof_context.width_m;
            var lineColor = GridBlue;

            foreach (var g in _inputs.VerticalLines)
            {
                _gridObjects.Add(SceneBuilder.Line("Grid_" + g.Name, new[] { LayoutSpace.ToWorld((float)g.Position, 0f, 0.22f), LayoutSpace.ToWorld((float)g.Position, w, 0.22f) }, lineColor, 0.12f, false, true).gameObject);
                if (!string.IsNullOrEmpty(g.Name)) _labels.Add(_hud.WorldLabel(g.Name, LayoutSpace.ToWorld((float)g.Position, -1.4f, 0.3f), 1.1f, GridBlue).gameObject);
            }
            foreach (var g in _inputs.HorizontalLines)
            {
                _gridObjects.Add(SceneBuilder.Line("Grid_" + g.Name, new[] { LayoutSpace.ToWorld(0f, (float)g.Position, 0.22f), LayoutSpace.ToWorld(l, (float)g.Position, 0.22f) }, lineColor, 0.12f, false, true).gameObject);
                if (!string.IsNullOrEmpty(g.Name)) _labels.Add(_hud.WorldLabel(g.Name, LayoutSpace.ToWorld(-1.6f, (float)g.Position, 0.3f), 1.1f, GridBlue).gameObject);
            }
            if (_report.summary.gridAssumed)
            {
                // An assumed grid is drawn at the bay boundaries the analysis used, dashed by being thinner and greyer, and never named.
                foreach (var x in _report.verticalLinesM.Skip(1).Take(Mathf.Max(0, _report.verticalLinesM.Length - 2)))
                    _gridObjects.Add(SceneBuilder.Line("AssumedGridX", new[] { LayoutSpace.ToWorld(x, 0f, 0.22f), LayoutSpace.ToWorld(x, w, 0.22f) }, Muted, 0.08f, false, true).gameObject);
                foreach (var y in _report.horizontalLinesM.Skip(1).Take(Mathf.Max(0, _report.horizontalLinesM.Length - 2)))
                    _gridObjects.Add(SceneBuilder.Line("AssumedGridY", new[] { LayoutSpace.ToWorld(0f, y, 0.22f), LayoutSpace.ToWorld(l, y, 0.22f) }, Muted, 0.08f, false, true).gameObject);
            }

            var dark = SceneBuilder.LitMaterial(new Color(0.12f, 0.14f, 0.18f), 0.1f);
            foreach (var c in _report.columns)
            {
                var high = c.status == "high";
                var go = SceneBuilder.Box("Column", LayoutSpace.ToWorld(c.x, c.y, 0.35f), new Vector3(0.7f, 0.5f, 0.7f), high ? SceneBuilder.LitMaterial(Red, 0.1f) : dark);
                go.SetActive(false);
                _columnObjects.Add(go);
            }
            SetGridVisible(false);
        }

        void SetGridVisible(bool visible)
        {
            foreach (var g in _gridObjects) g.SetActive(visible);
            foreach (var g in _labels) g.SetActive(visible);
            foreach (var c in _columnObjects) c.SetActive(visible);
        }

        void BuildBays()
        {
            foreach (var bay in _report.bays)
            {
                var top = _inputs.Items.FirstOrDefault(i => i.Label == bay.topContributor);
                _towers.Add(new BayTower(bay, _hud, top != null ? PieceNames.Short(PieceNames.Compact(top), 22) : ""));
            }
            SetTowersVisible(false);
        }

        void SetTowersVisible(bool visible)
        {
            foreach (var t in _towers) t.SetActive(visible);
        }

        void BuildBalanceMarkers()
        {
            var w = _payload.roof_context.width_m;
            var b = _report.balance;

            _centreMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _centreMarker.name = "StructureCentre";
            SceneBuilder.RemoveCollider(_centreMarker);
            _centreMarker.transform.localScale = new Vector3(1.6f, 1.6f, 1.6f);
            _centreMarker.GetComponent<Renderer>().sharedMaterial = SceneBuilder.GlowMaterial(Color.white);
            _centreMarker.transform.position = LayoutSpace.ToWorld(b.centreX, b.centreY, 1.0f);

            _loadMarker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _loadMarker.name = "LoadCentre";
            SceneBuilder.RemoveCollider(_loadMarker);
            _loadMarker.transform.localScale = new Vector3(2.0f, 2.0f, 2.0f);
            _loadMarker.GetComponent<Renderer>().sharedMaterial = SceneBuilder.GlowMaterial(Orange);
            _loadMarker.transform.position = _centreMarker.transform.position;

            _splitLine = SceneBuilder.Line("Split", new[] { LayoutSpace.ToWorld(b.centreX, 0f, 0.6f), LayoutSpace.ToWorld(b.centreX, w, 0.6f) }, new Color(1f, 1f, 1f, 0.7f), 0.10f, false, true).gameObject;
            _arrowLine = SceneBuilder.Line("Offset", new[] { _centreMarker.transform.position, _centreMarker.transform.position }, Orange, 0.32f, false, false);
            _arrow = _arrowLine.gameObject;

            _balanceObjects.Add(_centreMarker);
            _balanceObjects.Add(_loadMarker);
            _balanceObjects.Add(_splitLine);
            _balanceObjects.Add(_arrow);
            SetBalanceVisible(false);
        }

        void SetBalanceVisible(bool visible)
        {
            foreach (var o in _balanceObjects) o.SetActive(visible);
        }

        Vector3 CentroidWorld(StructureReport r, float height)
        {
            return LayoutSpace.ToWorld(r.balance.totalCentroidX, r.balance.totalCentroidY, height);
        }

        void PlaceLoadMarker(Vector3 world)
        {
            _loadMarker.transform.position = world;
            var from = _centreMarker.transform.position;
            _arrowLine.SetPosition(0, from);
            _arrowLine.SetPosition(1, world);
        }

        // -------------------------------------------------------------- the run

        IEnumerator Run()
        {
            _hud.SetBanner("");
            _hud.SetCountsText("");
            _hud.SetFeed(new List<string>());
            _hud.SetLegend(new List<string>());

            _hud.ShowCard("Structural load analysis (static)", Color.white, _results.caseStudy, TitleLines());
            foreach (var _ in Hold(TitleCardS, "title")) yield return null;
            _hud.HideCard();

            foreach (var _ in SceneGrid()) yield return null;
            foreach (var _ in SceneLoad(true)) yield return null;
            foreach (var _ in SceneLoad(false)) yield return null;
            foreach (var _ in ScenePeople()) yield return null;
            foreach (var _ in SceneBays()) yield return null;
            foreach (var _ in SceneBalance()) yield return null;
            foreach (var _ in SceneAdvice()) yield return null;

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

        IEnumerable<object> SceneGrid()
        {
            _heat.Clear();
            SetGridVisible(true);
            ShowNames(true, 0.7f);
            var s = _report.summary;
            _hud.SetLegend(new List<string>
            {
                Tint("blue lines", GridBlue) + " = grid lines, " + Tint("dark squares", Muted) + " = columns",
                s.gridAssumed ? Tint("No grid in the layout: a regular 8.4 m grid is assumed (grey).", Amber) : "Pulled from the Revit model with the roof",
            });
            foreach (var frame in Play(GridS, t =>
            {
                _hud.SetBanner("STRUCTURE: " + _report.bays.Count + " BAYS" + (s.columnsChecked > 0 ? ", " + s.columnsChecked + " COLUMNS" : ""));
                _hud.SetFeed(new List<string> { "Each bay is checked against the deck capacity", "Columns take the load of the area nearest them" });
                var shown = Mathf.CeilToInt(t * _columnObjects.Count);
                for (var i = 0; i < _columnObjects.Count; i++) _columnObjects[i].SetActive(i < shown);
            }, "grid")) yield return frame;
        }

        IEnumerable<object> SceneLoad(bool permanent)
        {
            var cap = _report.summary.capacityKnM2;
            var cell = _field.CellW * _field.CellH;
            var values = permanent ? _field.Dead : _field.Live;
            var total = permanent ? _report.summary.deadKn : _report.summary.liveKn;

            _hud.SetLegend(new List<string>
            {
                Tint("load per m2, as a share of the deck capacity (" + Num(cap, "0.#") + " kN/m2" + (_report.summary.capacityAssumed ? ", a placeholder" : "") + "):", Muted),
                Tint("light", LoadColors.Ramp(0.1f)) + "  " + Tint("half", LoadColors.Ramp(0.5f)) + "  " + Tint("most", LoadColors.Ramp(0.8f)) + "  " + Tint("all of it", LoadColors.Ramp(1f)),
                permanent ? "Green roofs at their SATURATED weight, trees, floor build-ups, roof finishes" : "The code's imposed loads: courts 5.0 kN/m2, accessible gardens, roof",
            });

            var labels = new List<GameObject>();
            foreach (var p in _pieces)
            {
                if (p.Item.Kind == LoadKind.Tree) continue;
                var kn = permanent ? p.Item.DeadKnM2 : p.Item.LiveKnM2;
                if (!permanent && kn <= StructureModel.RoofLiveKnM2 + 1e-6) continue;
                var pos = LayoutSpace.ToWorld((float)(p.Item.X + p.Item.Width * 0.5), (float)(p.Item.Y + p.Item.Height * 0.5), 0.9f);
                var label = _hud.WorldLabel(PieceNames.Short(p.Item.Name, 26) + "\n" + (permanent ? "G " : "Q ") + Num((float)kn, "0.0") + " kN/m2", pos, 0.85f, Color.white).gameObject;
                label.SetActive(false);
                labels.Add(label);
            }

            ShowNames(false);
            var seconds = permanent ? DeadS : LiveS;
            foreach (var frame in Play(seconds, t =>
            {
                var grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2.2f));
                _heat.Fill((ix, iy, c) => WithAlpha(LoadColors.Ramp((float)(values[_field.Index(ix, iy)] / cell / cap) * grow), 0.80f));
                foreach (var l in labels) l.SetActive(t > 0.5f);
                _hud.SetBanner(permanent ? "PERMANENT LOAD (G)   " + Num(total, "0") + " kN" : "IMPOSED LOAD (Q)   " + Num(total, "0") + " kN");
                _hud.SetFeed(new List<string> { permanent ? "What the roof carries every day" : "What the code says each use must be designed for" });
            }, permanent ? "permanent" : "imposed")) yield return frame;

            foreach (var l in labels) UnityEngine.Object.Destroy(l);
        }

        IEnumerable<object> ScenePeople()
        {
            var cell = _field.CellW * _field.CellH;
            var cap = _report.summary.capacityKnM2;
            var s = _report.summary;
            ShowNames(true, 1.6f);
            _hud.SetLegend(new List<string>
            {
                Tint("dots", new Color(1f, 0.92f, 0.55f)) + " = the people expected: players, spectators, visitors, arrivals at the entries",
                Tint("Where activity concentrates, not what the imposed load is made of", Muted),
            });
            foreach (var frame in Play(PeopleS, t =>
            {
                var fade = 1f - 0.7f * Mathf.Clamp01(t * 3f);
                _heat.Fill((ix, iy, c) => WithAlpha(LoadColors.Ramp((float)((_field.Dead[_field.Index(ix, iy)] + _field.Live[_field.Index(ix, iy)]) / cell / cap)), 0.80f * fade));
                _people.Animate(_clock, Mathf.Clamp01(t * 1.6f));
                _hud.SetBanner("WHERE PEOPLE WILL BE   about " + Num(s.expectedPersons, "0"));
                _hud.SetFeed(new List<string> { Num(s.busiestBaysSharePercent, "0") + "% of them are in the busiest fifth of the bays" });
            }, "people")) yield return frame;

            _people.SetActive(false);
        }

        IEnumerable<object> SceneBays()
        {
            var s = _report.summary;
            _heat.Clear();
            ShowNames(false);
            SetTowersVisible(true);
            _hud.SetLegend(new List<string>
            {
                "Block height = load against the deck capacity of " + Num(s.capacityKnM2, "0.#") + " kN/m2" + (s.capacityAssumed ? Tint(" (a PLACEHOLDER: enter the engineer's figure)", Amber) : ""),
                Tint("green", LoadColors.Status("ok")) + " under 80%   " + Tint("amber", LoadColors.Status("marginal")) + " 80-100%   " + Tint("red", LoadColors.Status("over")) + " over the capacity",
                Tint("Red squares = columns taking more than " + Num((float)StructureModel.ColumnHighFactor, "0.#") + "x the average", Red),
            });

            foreach (var frame in Play(BaysS, t =>
            {
                var grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 1.8f));
                foreach (var tower in _towers) tower.SetGrow(grow, _clock);
                _hud.SetBanner("BAYS: " + s.baysOver + " OVER CAPACITY, " + s.baysMarginal + " MARGINAL   most loaded " + s.worstBay + " at " + Num(s.peakUtilisation * 100f, "0") + "%");
                _hud.SetFeed(new List<string> { "Each block's height and its label: the bay's load as a share of the deck capacity" });
                _hud.SetCountsText("BAYS OVER " + Tint(s.baysOver.ToString(), s.baysOver > 0 ? Red : Good) + "   COLUMNS HIGH " + Tint(s.columnsHigh.ToString(), s.columnsHigh > 0 ? Amber : Good));
            }, "bays")) yield return frame;

            SavePoster();
            SetTowersVisible(false);
            foreach (var t in _towers) t.SetGrow(0f);
        }

        IEnumerable<object> SceneBalance()
        {
            var b = _report.balance;
            var cell = _field.CellW * _field.CellH;
            var cap = _report.summary.capacityKnM2;
            SetBalanceVisible(true);
            ShowNames(true, 2.4f);
            _hud.SetLegend(new List<string>
            {
                Tint("white ball", Color.white) + " = centre of the structure (" + b.centreBasis + ")",
                Tint("orange ball", Orange) + " = centre of the total load (permanent + imposed)",
                Tint("A load centre far from the structure's centre loads one side of the roof and its columns more", Muted),
            });

            var target = CentroidWorld(_report, 1.0f);
            foreach (var frame in Play(BalanceS, t =>
            {
                _heat.Fill((ix, iy, c) => WithAlpha(LoadColors.Ramp((float)((_field.Dead[_field.Index(ix, iy)] + _field.Live[_field.Index(ix, iy)]) / cell / cap)), 0.55f));
                PlaceLoadMarker(Vector3.Lerp(_centreMarker.transform.position, target, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2f))));
                var offset = "off by " + Num(Mathf.Abs(b.totalEccentricityX) * 100f, "0.0") + "% of the length, " + Num(Mathf.Abs(b.totalEccentricityY) * 100f, "0.0") + "% of the width";
                _hud.SetBanner("BALANCE: " + b.status.ToUpperInvariant() + (string.IsNullOrEmpty(b.heavySide) ? "" : " (" + b.heavySide.ToUpperInvariant() + ")"));
                _hud.SetFeed(new List<string> { "The load's centre is " + offset });
                _hud.SetCountsText("L " + Tint(Num(b.leftSharePercent, "0") + "%", b.leftSharePercent > b.rightSharePercent ? Amber : Muted) +
                                   "  R " + Tint(Num(b.rightSharePercent, "0") + "%", b.rightSharePercent > b.leftSharePercent ? Amber : Muted) +
                                   "  T " + Num(b.topSharePercent, "0") + "%  B " + Num(b.bottomSharePercent, "0") + "%");
            }, "balance")) yield return frame;
        }

        IEnumerable<object> SceneAdvice()
        {
            var b = _report.balance;
            var a = _afterReport.balance;
            var cell = _field.CellW * _field.CellH;
            var cap = _report.summary.capacityKnM2;
            var moved = _pieces.Where(p => Math.Abs(_after.Items[_pieces.IndexOf(p)].X - p.Item.X) + Math.Abs(_after.Items[_pieces.IndexOf(p)].Y - p.Item.Y) > 1e-6).ToList();
            var lightened = _pieces.Where(p => Math.Abs(_after.Items[_pieces.IndexOf(p)].DeadKnM2 - p.Item.DeadKnM2) > 1e-6).ToList();

            if (_adviceSteps == 0)
            {
                _hud.SetLegend(new List<string> { "The load is within " + Num((float)StructureModel.EccentricityMarginal * 100f, "0") + "% of the structure's centre: nothing has to move" });
                foreach (var frame in Play(3.0f, t =>
                {
                    _hud.SetBanner("NOTHING TO REBALANCE");
                    _hud.SetFeed(new List<string> { "See the list of what to check on the next card" });
                }, "advice")) yield return frame;
                SetBalanceVisible(false);
                _heat.Clear();
                yield break;
            }

            ShowNames(true, 0.7f);
            var ghosts = new List<GameObject>();
            foreach (var p in moved)
            {
                var it = p.Item;
                var line = SceneBuilder.Line("Ghost_" + it.Id, new[]
                {
                    LayoutSpace.ToWorld((float)it.X, (float)it.Y, 0.3f), LayoutSpace.ToWorld((float)(it.X + it.Width), (float)it.Y, 0.3f),
                    LayoutSpace.ToWorld((float)(it.X + it.Width), (float)(it.Y + it.Height), 0.3f), LayoutSpace.ToWorld((float)it.X, (float)(it.Y + it.Height), 0.3f),
                }, Amber, 0.16f, true, true);
                ghosts.Add(line.gameObject);
            }
            var labels = new List<GameObject>();
            foreach (var p in lightened)
            {
                var i = _pieces.IndexOf(p);
                var pos = LayoutSpace.ToWorld((float)(p.Item.X + p.Item.Width * 0.5), (float)(p.Item.Y + p.Item.Height * 0.5), 2.2f);
                labels.Add(_hud.WorldLabel(Num((float)p.Item.DeadKnM2, "0.0") + " > " + Num((float)_after.Items[i].DeadKnM2, "0.0") + " kN/m2", pos, 1.1f, Amber).gameObject);
            }

            _hud.SetLegend(new List<string>
            {
                Tint("amber outline", Amber) + " = where a piece stands now; it slides to where the advice puts it",
                "Colours: total load as a share of the deck capacity, before > after",
            });

            var from = CentroidWorld(_report, 1.0f);
            var to = CentroidWorld(_afterReport, 1.0f);
            foreach (var frame in Play(AdviceS, t =>
            {
                var k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((t - 0.15f) / 0.6f));
                foreach (var p in moved)
                {
                    var after = _after.Items[_pieces.IndexOf(p)];
                    var target = LayoutSpace.ToWorld((float)(after.X + after.Width * 0.5), (float)(after.Y + after.Height * 0.5), 0.05f);
                    p.Body.transform.position = Vector3.Lerp(p.Start, target, k);
                    if (p.Name != null) p.Name.transform.position = p.Body.transform.position + Vector3.up * 0.7f;
                }
                _heat.Fill((ix, iy, c) =>
                {
                    var i = _field.Index(ix, iy);
                    var before = (float)((_field.Dead[i] + _field.Live[i]) / cell / cap);
                    var afterShare = (float)((_afterField.Dead[i] + _afterField.Live[i]) / cell / cap);
                    return WithAlpha(LoadColors.Ramp(Mathf.Lerp(before, afterShare, k)), 0.6f);
                });
                PlaceLoadMarker(Vector3.Lerp(from, to, k));
                _hud.SetBanner("AFTER THE ADVICE: LOAD CENTRE " + Num(Mathf.Max(Mathf.Abs(a.totalEccentricityX), Mathf.Abs(a.totalEccentricityY)) * 100f, "0.0") + "% OFF (WAS " +
                               Num(Mathf.Max(Mathf.Abs(b.totalEccentricityX), Mathf.Abs(b.totalEccentricityY)) * 100f, "0.0") + "%)");
                var feed = new List<string>();
                foreach (var rec in _report.recommendations.Where(r => (r.kind == "move" || r.kind == "lighten") && !string.IsNullOrEmpty(r.itemId)).Take(3))
                    feed.Add((rec.kind == "move" ? "Move " + rec.target + " " + Num(Mathf.Abs(rec.moveM), "0.0") + " m " + Direction(rec) : "Lighten " + rec.target + " to " + Num(rec.newDeadKnM2, "0.0") + " kN/m2"));
                _hud.SetFeed(feed);
                _hud.SetCountsText("BUSIEST BAY " + Tint(Num(_afterReport.summary.peakUtilisation * 100f, "0") + "%", _afterReport.summary.baysOver > 0 ? Red : Good) +
                                   " (WAS " + Num(_report.summary.peakUtilisation * 100f, "0") + "%)");
            }, "advice")) yield return frame;

            // Put the roof back as it was for anything drawn afterwards.
            foreach (var g in ghosts) UnityEngine.Object.Destroy(g);
            foreach (var l in labels) UnityEngine.Object.Destroy(l);
            foreach (var p in moved) p.Body.transform.position = p.Start;
            ShowNames(false);
            SetBalanceVisible(false);
            _heat.Clear();
        }

        static string Direction(StructureRecommendation rec)
        {
            if (rec.axis == "x") return rec.moveM < 0 ? "left" : "right";
            return rec.moveM < 0 ? "toward the top" : "toward the bottom";
        }

        static Color32 WithAlpha(Color c, float alpha)
        {
            return new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
        }

        /// <summary>Plays a scene: calls onFrame(t) with t from 0 to 1 once per video frame, capturing each.</summary>
        IEnumerable<object> Play(float seconds, Action<float> onFrame, string stillName)
        {
            var frames = Mathf.Max(2, Mathf.RoundToInt(seconds * _cfg.Fps));
            for (var i = 0; i < frames; i++)
            {
                _clock += 1f / _cfg.Fps;
                onFrame(i / (float)(frames - 1));
                CaptureFrame();
                if (stillName != null && i == frames * 3 / 4) SaveStill(stillName);
                yield return null;
            }
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

        List<string> TitleLines()
        {
            return new List<string>
            {
                "What the roof structure has to carry, and where:",
                "  - the weight of the garden build-ups, trees, courts and floors",
                "  - the imposed loads of use, and the people expected",
                "  - which bays of the grid are most loaded against the deck",
                "  - whether the load sits to one side, and what would balance it",
                "",
                "Static loads only. Crowds in motion, wind, rain and snow are the dynamic analysis.",
                SimulationHud.Tint("A screening model, not a structural verification.", Muted),
            };
        }

        void ShowSummaryCard()
        {
            var s = _report.summary;
            var b = _report.balance;
            var headline = s.baysOver > 0
                ? s.baysOver + " of " + s.baysChecked + " bays are over the deck capacity"
                : (b.status != "balanced" ? "The load sits to one side of the roof" : "No bay is over capacity and the load is balanced");
            var lines = new List<string>
            {
                "Load on the roof:   " + Num(s.deadKn, "0") + " kN permanent + " + Num(s.liveKn, "0") + " kN imposed = " + Num(s.totalKn, "0") + " kN   (" + Num(s.meanKnM2, "0.0") + " kN/m2 on average)",
                "Deck capacity:   " + Num(s.capacityKnM2, "0.#") + " kN/m2" + (s.capacityAssumed ? Tint("   a PLACEHOLDER: enter the structural engineer's figure", Amber) : ""),
                "Most loaded:   " + s.worstBay + " at " + Cell(s.peakUtilisation),
                "Bays:   " + Tint(s.baysOver + " over", s.baysOver > 0 ? Red : Good) + ",  " + Tint(s.baysMarginal + " marginal", s.baysMarginal > 0 ? Amber : Good) + ",  of " + s.baysChecked,
                "Columns:   " + s.columnsHigh + " of " + s.columnsChecked + " take more than " + Num((float)StructureModel.ColumnHighFactor, "0.#") + "x the average",
                "People:   about " + Num(s.expectedPersons, "0") + ", " + Num(s.busiestBaysSharePercent, "0") + "% in the busiest fifth of the bays",
                "Balance:   " + Tint(b.status, b.status == "balanced" ? Good : (b.status == "marginal" ? Amber : Red)) + ",  load centre " + Num(Mathf.Abs(b.totalEccentricityX) * 100f, "0.0") + "% (length) / " +
                    Num(Mathf.Abs(b.totalEccentricityY) * 100f, "0.0") + "% (width) off",
            };
            _hud.ShowCard(headline, s.baysOver == 0 && b.status == "balanced" ? Good : Amber, _results.caseStudy, lines);
        }

        string Cell(float utilisation)
        {
            var color = utilisation > 1f ? Red : (utilisation > (float)StructureModel.MarginalFrom ? Amber : Good);
            return SimulationHud.Tint(Num(utilisation * 100f, "0") + "%", color);
        }

        void ShowFixCard()
        {
            // The card is short: one line per finding. The full sentences, and every number, are in the results file and the PDF report.
            var lines = new List<string>();
            var s = _report.summary;

            var over = _report.bays.Where(b => b.status == "over").OrderByDescending(b => b.utilisation).ToList();
            if (over.Count > 0)
            {
                CardText.Bullet(lines, Tint("Capacity", Red) + "  " + string.Join(", ", over.Take(4).Select(b => b.label + " " + Num(b.utilisation * 100f, "0") + "%").ToArray()) +
                                (over.Count > 4 ? ", ..." : "") + (string.IsNullOrEmpty(over[0].topContributor) ? "" : ", mostly under " + over[0].topContributor) + ".");
            }

            var steps = _report.recommendations.Where(r => (r.kind == "move" || r.kind == "lighten") && !string.IsNullOrEmpty(r.itemId)).Take(3).ToList();
            foreach (var rec in steps)
                CardText.Bullet(lines, Tint(Label(rec.kind), Amber) + "  " + ShortStep(rec));
            if (_report.recommendations.Any(r => r.kind == "move" && string.IsNullOrEmpty(r.itemId)))
                CardText.Bullet(lines, Tint("Balance", Amber) + "  No single move or lightening of one piece puts it right: shift several pieces toward the light side.");
            else if (steps.Count > 0 && _afterReport.balance.status != "balanced")
                CardText.Bullet(lines, Tint("Balance", Amber) + "  Still " + _afterReport.balance.status + " after these steps: shift more load toward the light side.");

            var crowd = _report.recommendations.FirstOrDefault(r => r.kind == "concentration");
            if (crowd != null)
                CardText.Bullet(lines, Tint("People", Amber) + "  " + Num(s.busiestBaysSharePercent, "0") + "% of the expected " + Num(s.expectedPersons, "0") + " are in the busiest fifth of the bays: keep heavy loads out of them.");

            if (s.columnsHigh > 0)
            {
                var worst = _report.columns.OrderByDescending(c => c.ratioToMean).First();
                CardText.Bullet(lines, Tint("Columns", Amber) + "  " + s.columnsHigh + " take more than " + Num((float)StructureModel.ColumnHighFactor, "0.#") + "x the average; " + worst.label + " takes " +
                                Num(worst.loadKn, "0") + " kN (" + Num(worst.ratioToMean, "0.0") + "x).");
            }

            if (s.capacityAssumed)
                CardText.Bullet(lines, Tint("Capacity", Muted) + "  " + Num(s.capacityKnM2, "0.#") + " kN/m2 is a placeholder: enter the structural engineer's figure in the Combine tab.");
            if (s.gridAssumed)
                CardText.Bullet(lines, Tint("Grid", Muted) + "  No structural grid in the layout: a regular 8.4 m grid was assumed. Push the roof from Revit for the real one.");
            if (s.baysOver == 0 && _report.balance.status == "balanced")
                CardText.Bullet(lines, Tint("OK", Good) + "  No bay is over its capacity and the load is within " + Num((float)StructureModel.EccentricityMarginal * 100f, "0") + "% of the structure's centre.");

            _hud.ShowCard("What to change", Color.white, "Screening result: the results file lists every number and assumption", lines);
        }

        static string ShortStep(StructureRecommendation rec)
        {
            var tail = ": load centre " + Num(Mathf.Abs(rec.eccentricityAfter) * 100f, "0.0") + "% off, busiest bay " + Num(rec.peakUtilisationAfter * 100f, "0") + "%.";
            return rec.kind == "move"
                ? rec.target + " " + Num(Mathf.Abs(rec.moveM), "0.0") + " m " + Direction(rec) + tail
                : rec.target + " down to " + Num(rec.newDeadKnM2, "0.0") + " kN/m2 (" + Num(rec.newDeadKnM2 * 1000f / 9.81f, "0") + " kg/m2)" + tail;
        }

        static string Label(string kind)
        {
            switch (kind)
            {
                case "over-capacity": return "Capacity";
                case "vulnerable": return "Loaded";
                case "move": return "Move";
                case "lighten": return "Lighten";
                case "concentration": return "People";
                case "grid": return "Grid";
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
                Debug.Log("[Structure] Recording " + _cfg.Width + "x" + _cfg.Height + " @ " + _cfg.Fps + " fps to " + _cfg.VideoPath);
            }
            catch (Exception ex)
            {
                _results.videoError = "Couldn't start the video encoder: " + ex.Message;
                Debug.LogError("[Structure] " + _results.videoError);
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
                Debug.LogError("[Structure] " + _results.videoError);
                DiscardVideo();
            }
#endif
        }

        void SaveStill(string name)
        {
#if UNITY_EDITOR
            if (_recorder == null || !_cfg.DebugStills) return;
            var stillPath = Path.ChangeExtension(_cfg.VideoPath, null) + "_" + name + ".png";
            try { _recorder.SaveStill(stillPath); } catch (Exception ex) { Debug.LogWarning("[Structure] Couldn't save " + stillPath + ": " + ex.Message); }
#endif
        }

        void SavePoster()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var posterPath = Path.ChangeExtension(_cfg.VideoPath, ".png");
            try { _recorder.SaveStill(posterPath); _results.video.posterPath = posterPath; }
            catch (Exception ex) { Debug.LogWarning("[Structure] Couldn't save the poster image: " + ex.Message); }
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
                Debug.LogError("[Structure] " + _results.videoError);
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
            Debug.Log("[Structure] Video: " + recorder.Path + " (" + recorder.FramesWritten + " frames, " + Num(_results.video.durationS, "0.0") + " s)");
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
            Debug.LogError("[Structure] Failed: " + ex);
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
            var path = Path.Combine(dir, "structure_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_results, true));
            Debug.Log("[Structure] Done. Report: " + path);

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
            var cfg = new Config { VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "structural_loads.mp4")) };
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
