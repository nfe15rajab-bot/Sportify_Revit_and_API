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

namespace Sportify.Simulation.Sun
{
    /// <summary>
    /// The sun and shade analysis: the hours of direct sun every part of the roof gets, where it is too sunny for the people who stay on it or
    /// too shaded for the plants, and the shading equipment that would fix it. Reads the same layout export as the other analyses, runs the model
    /// (SunShadeCore.cs: plain arithmetic, no Unity), then films it: a summer day of shadows sweeping the roof, the sun hours of the three design
    /// days, the shade at midday against the target, the equipment going up piece by piece, and the roof after.
    ///
    /// What is drawn is the analysis's own sunlit fraction of every cell, from the same functions the numbers came from, not a rendering
    /// engine's shadows (the light's own shadows are off, so the two cannot disagree). Started by BatchRunner.RunSunAnalysis. Options (all optional):
    ///   -layoutFile=PATH  -videoFile=PATH  -noVideo  -videoWidth= -videoHeight= -videoFps=  -debugStills
    /// </summary>
    public class SunShadeRunner : MonoBehaviour
    {
        [Serializable]
        public class ResultsFile
        {
            public string caseStudy = "";
            public string layoutSource = "";
            public string message = "";
            public SunReport analysis = new SunReport();
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

        sealed class Visual
        {
            public EquipmentPiece Piece;
            public GameObject Root;
        }

        const float TitleCardS = 2.5f;
        const float DayS = 14.0f;
        const float MapS = 3.2f;
        const float MiddayS = 6.0f;
        const float PieceS = 3.6f;
        const float AfternoonS = 4.0f;
        const float AfterS = 4.0f;
        const float SummaryCardS = 6.5f;
        const float FixCardS = 9.0f;
        const float LayerHeight = 0.14f;

        static readonly Color Good = new Color(0.45f, 0.90f, 0.55f);
        static readonly Color Muted = new Color(0.66f, 0.70f, 0.76f);
        static readonly Color Amber = new Color(1.00f, 0.78f, 0.20f);
        static readonly Color Red = SimulationHud.CrossingText;
        static readonly Color PeopleColor = new Color(1.0f, 0.78f, 0.22f);
        static readonly Color GardenColor = new Color(0.45f, 0.92f, 0.55f);
        static readonly Color CourtColor = new Color(0.92f, 0.94f, 1.0f);
        static readonly Color ShadowBlue = new Color(0.04f, 0.09f, 0.30f);

        readonly ResultsFile _results = new ResultsFile();
        readonly List<Visual> _visuals = new List<Visual>();
        readonly List<GameObject> _zoneObjects = new List<GameObject>();
        readonly List<GameObject> _decor = new List<GameObject>();
        Config _cfg;
        GoldbeckPayload _payload;
        SunInputs _inputs;
        SunReport _report;
        SunGrid _grid;
        SunScene _before, _after;
        Camera _cam;
        SimulationHud _hud;
        HeatLayer _heat;
        GameObject _sunArrow, _sunBall;
        LineRenderer _sunLine;
        bool _reportWritten;
        double _lat;
        readonly List<Shape> _scratch = new List<Shape>();
        double[] _fractions;
        int[] _cellZone;                      // index into _report.zones of the zone a cell is judged in (people before garden before court), or -1

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
            if (!AnalysisMode.IsSun) return;
            new GameObject("SunShadeRunner").AddComponent<SunShadeRunner>();
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

            _inputs = SunLayoutAdapter.ToInputs(_payload);
            _report = SunModel.Analyse(_inputs);
            _results.analysis = _report;
            _results.caseStudy = (LayoutLoader.IsRoofGardenSample ? "Goldbeck default - roof garden sample: " : "") + SunModel.CaseStudy(_inputs, _report);

            if (_inputs.Structure.Items.Count == 0)
            {
                _report.ran = false;
                _results.message = "The layout has nothing on the roof yet (no sports field, activity, green-roof zone or tree).";
                Debug.LogWarning("[Sun] " + _results.message);
                return false;
            }

            _lat = _inputs.LatitudeDeg ?? SunModel.DefaultLatitudeDeg;
            _grid = SunModel.BuildGrid(_inputs);
            _before = SunModel.BuildScene(_inputs, null);
            _after = SunModel.BuildScene(_inputs, _report.equipment);
            _fractions = new double[_grid.Count];
            BuildCellZones();

            var s = _report.summary;
            Debug.Log("[Sun] " + _results.caseStudy + ". People zones too sunny: " + s.peopleZonesTooSunny + " of " + s.peopleZones + " (after: " + s.peopleZonesTooSunnyAfter +
                      "); gardens too shaded: " + s.gardenZonesTooShaded + " of " + s.gardenZones + "; " + s.pieces + " pieces, +" + Num(s.addedLoadKn, "0.#") + " kN.");

            if (_cfg.RecordVideo)
            {
                BuildScene();
                StartRecorder();
            }
            return true;
        }

        static Vector3 W(double x, double y, float h) { return LayoutSpace.ToWorld((float)x, (float)y, h); }

        LoadItem ItemOf(ZoneSun z)
        {
            return _inputs.Structure.Items.FirstOrDefault(i => i.Id == z.id || i.Id + "_seats" == z.id);
        }

        /// <summary>The zone's rectangle: the piece's, or for a spectators' band the court's grown by the margin.</summary>
        static void ZoneRect(ZoneSun z, LoadItem item, out double x0, out double y0, out double x1, out double y1)
        {
            var grow = z.kind == "spectators" ? SunModel.SpectatorMarginM : 0.0;
            x0 = item.X - grow; y0 = item.Y - grow; x1 = item.X + item.Width + grow; y1 = item.Y + item.Height + grow;
        }

        /// <summary>Where a zone's label goes: people zones at their middle, gardens near their lower edge, so an activity on a garden does not hide its name.</summary>
        Vector3 LabelPos(ZoneSun z, LoadItem item, float height)
        {
            double x0, y0, x1, y1;
            ZoneRect(z, item, out x0, out y0, out x1, out y1);
            var y = z.kind == "garden" ? y0 + (y1 - y0) * 0.86 : (y0 + y1) * 0.5;
            return W((x0 + x1) * 0.5, y, height);
        }

        void BuildCellZones()
        {
            _cellZone = new int[_grid.Count];
            for (var c = 0; c < _cellZone.Length; c++) _cellZone[c] = -1;
            var courts = _inputs.Structure.Items.Where(i => i.Kind == LoadKind.Court).ToList();
            int Rank(string kind) { return kind == "people" || kind == "spectators" ? 3 : kind == "garden" ? 2 : 1; }
            for (var zi = 0; zi < _report.zones.Count; zi++)
            {
                var z = _report.zones[zi];
                var item = ItemOf(z);
                if (item == null) continue;
                double x0, y0, x1, y1;
                ZoneRect(z, item, out x0, out y0, out x1, out y1);
                for (var j = 0; j < _grid.Ny; j++)
                    for (var i = 0; i < _grid.Nx; i++)
                    {
                        var c = _grid.Index(i, j);
                        if (!_grid.Active[c]) continue;
                        double cx = _grid.CentreX(i), cy = _grid.CentreY(j);
                        if (cx < x0 || cx > x1 || cy < y0 || cy > y1) continue;
                        if (z.kind == "spectators" && courts.Any(k => cx >= k.X && cx <= k.X + k.Width && cy >= k.Y && cy <= k.Y + k.Height)) continue;
                        if (_cellZone[c] < 0 || Rank(z.kind) > Rank(_report.zones[_cellZone[c]].kind)) _cellZone[c] = zi;
                    }
            }
        }

        void BuildScene()
        {
            var roof = _payload.roof_context;
            SceneBuilder.BuildRoof(roof);
            if (_inputs.Outline.Count >= 3)
                _decor.Add(SceneBuilder.Line("RoofShape", _inputs.Outline.Select(p => W(p[0], p[1], 0.10f)).ToArray(), new Color(1f, 1f, 1f, 0.9f), 0.22f, true, true).gameObject);
            _cam = SceneBuilder.BuildCamera(roof, (float)_cfg.Width / _cfg.Height);
            var lookAt = LayoutSpace.ToWorld(roof.length_m * 0.5f, roof.width_m * 0.5f, 2.5f);
            _cam.transform.position = lookAt + (_cam.transform.position - lookAt) * 1.12f;
            _cam.farClipPlane *= 1.2f;
            SceneBuilder.BuildLight(Mathf.Sqrt(roof.length_m * roof.length_m + roof.width_m * roof.width_m));
            var light = UnityEngine.Object.FindFirstObjectByType<Light>();
            if (light != null) light.shadows = LightShadows.None;          // the shade drawn is the analysis's own, not the engine's
            QualitySettings.shadows = ShadowQuality.Disable;

            _hud = new SimulationHud(_cam, _cfg.Width, _cfg.Height, "Sun and shade analysis", false);
            _hud.SetCaseStudy(_results.caseStudy);
            _hud.SetPreliminary(AnalysisAssumptions.PreliminaryBanner(_report.assumptionUses));

            SceneBuilder.BuildEntryPoints(_payload.entry_points);
            BuildPieces();
            BuildObstacles();
            BuildTrees();

            _heat = new HeatLayer("HeatSun", 0f, 0f, roof.length_m, roof.width_m, _grid.Nx, _grid.Ny, LayerHeight, 3);
            BuildZoneOutlines();
            BuildEquipment();
            BuildSunMarker();
        }

        void BuildPieces()
        {
            foreach (var item in _inputs.Structure.Items)
            {
                if (item.Kind == LoadKind.Tree) continue;
                Color color;
                if (item.Kind == LoadKind.Court) color = SceneBuilder.SportColor((item.Label ?? "").Split(' ')[0]);
                else if (item.Kind == LoadKind.Activity) color = SceneBuilder.ActivityColor;
                else color = item.DeadKnM2 > 3.5 ? new Color(0.30f, 0.50f, 0.30f) : new Color(0.46f, 0.56f, 0.38f);
                var centre = W(item.X + item.Width * 0.5, item.Y + item.Height * 0.5, 0.05f);
                _decor.Add(SceneBuilder.Box("Piece_" + item.Id, centre, new Vector3((float)item.Width, 0.08f, (float)item.Height), SceneBuilder.LitMaterial(color, 0.10f)));
            }
        }

        void BuildObstacles()
        {
            var wallMaterial = SceneBuilder.LitMaterial(new Color(0.78f, 0.78f, 0.80f), 0.05f);
            foreach (var o in _inputs.Obstacles)
            {
                var len = Math.Sqrt((o.X1 - o.X0) * (o.X1 - o.X0) + (o.Y1 - o.Y0) * (o.Y1 - o.Y0));
                if (len < 0.05 || o.HeightM <= 0) continue;
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Wall";
                SceneBuilder.RemoveCollider(wall);
                wall.transform.position = W((o.X0 + o.X1) * 0.5, (o.Y0 + o.Y1) * 0.5, (float)o.HeightM * 0.5f);
                wall.transform.rotation = Quaternion.Euler(0f, (float)(Math.Atan2(o.Y1 - o.Y0, o.X1 - o.X0) * 180.0 / Math.PI), 0f);
                wall.transform.localScale = new Vector3((float)len, (float)o.HeightM, (float)Math.Max(o.ThicknessM, 0.12));
                wall.GetComponent<Renderer>().sharedMaterial = wallMaterial;
                _decor.Add(wall);
            }
        }

        void BuildTrees()
        {
            var trunk = SceneBuilder.LitMaterial(new Color(0.30f, 0.20f, 0.12f), 0.05f);
            var crown = SceneBuilder.LitMaterial(new Color(0.16f, 0.40f, 0.20f), 0.05f);
            foreach (var p in _inputs.Wind.Plants)
            {
                if (p.HeightM < SunModel.MinShadingPlantM) continue;
                var r = (float)Math.Max(0.25, p.CrownM * 0.5);
                var zc = (float)Math.Max(r, p.HeightM - r);
                var t = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                t.name = "Trunk";
                SceneBuilder.RemoveCollider(t);
                t.transform.position = W(p.X, p.Y, zc * 0.5f);
                t.transform.localScale = new Vector3(0.25f, zc * 0.5f, 0.25f);
                t.GetComponent<Renderer>().sharedMaterial = trunk;
                _decor.Add(t);
                var c = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                c.name = "Crown";
                SceneBuilder.RemoveCollider(c);
                c.transform.position = W(p.X, p.Y, zc);
                c.transform.localScale = Vector3.one * r * 2f;
                c.GetComponent<Renderer>().sharedMaterial = crown;
                _decor.Add(c);
            }
        }

        void BuildZoneOutlines()
        {
            foreach (var z in _report.zones)
            {
                var item = _inputs.Structure.Items.FirstOrDefault(i => i.Id == z.id || i.Id + "_seats" == z.id);
                if (item == null) continue;
                var isBand = z.kind == "spectators";
                double x0 = item.X - (isBand ? SunModel.SpectatorMarginM : 0), y0 = item.Y - (isBand ? SunModel.SpectatorMarginM : 0);
                double x1 = item.X + item.Width + (isBand ? SunModel.SpectatorMarginM : 0), y1 = item.Y + item.Height + (isBand ? SunModel.SpectatorMarginM : 0);
                var color = z.kind == "garden" ? GardenColor : z.kind == "court" ? CourtColor : PeopleColor;
                var corners = new[] { W(x0, y0, 0.24f), W(x1, y0, 0.24f), W(x1, y1, 0.24f), W(x0, y1, 0.24f) };
                var line = SceneBuilder.Line("Zone_" + z.id, corners, color, isBand ? 0.10f : 0.16f, true, true).gameObject;
                line.SetActive(false);
                _zoneObjects.Add(line);
            }
        }

        void SetZonesVisible(bool visible)
        {
            foreach (var z in _zoneObjects) z.SetActive(visible);
        }

        void BuildEquipment()
        {
            var post = SceneBuilder.LitMaterial(new Color(0.85f, 0.85f, 0.88f), 0.2f);
            foreach (var p in _report.equipment)
            {
                var root = new GameObject("Equipment_" + p.key);
                Material plate = SceneBuilder.LitMaterial(p.key == "sail" ? new Color(0.92f, 0.86f, 0.62f) : p.key == "canopy" ? new Color(0.72f, 0.74f, 0.80f) : p.key == "pergola" ? new Color(0.62f, 0.44f, 0.28f) : new Color(0.90f, 0.40f, 0.34f), 0.1f);
                var cx = p.x + p.widthM * 0.5; var cy = p.y + p.depthM * 0.5;
                if (p.shape == "tree")
                {
                    var t = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    SceneBuilder.RemoveCollider(t);
                    t.transform.SetParent(root.transform, false);
                    t.transform.position = W(cx, cy, p.heightM * 0.3f);
                    t.transform.localScale = new Vector3(0.3f, p.heightM * 0.3f, 0.3f);
                    t.GetComponent<Renderer>().sharedMaterial = SceneBuilder.LitMaterial(new Color(0.30f, 0.20f, 0.12f), 0.05f);
                    var c = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    SceneBuilder.RemoveCollider(c);
                    c.transform.SetParent(root.transform, false);
                    var zc = Mathf.Max((float)SunModel.TreeCrownM * 0.5f, p.heightM - (float)SunModel.TreeCrownM * 0.5f);
                    c.transform.position = W(cx, cy, zc);
                    c.transform.localScale = Vector3.one * (float)SunModel.TreeCrownM;
                    c.GetComponent<Renderer>().sharedMaterial = SceneBuilder.LitMaterial(new Color(0.20f, 0.52f, 0.26f), 0.05f);
                }
                else if (p.shape == "disc")
                {
                    var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    SceneBuilder.RemoveCollider(pole);
                    pole.transform.SetParent(root.transform, false);
                    pole.transform.position = W(cx, cy, p.heightM * 0.5f);
                    pole.transform.localScale = new Vector3(0.12f, p.heightM * 0.5f, 0.12f);
                    pole.GetComponent<Renderer>().sharedMaterial = post;
                    var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    SceneBuilder.RemoveCollider(disc);
                    disc.transform.SetParent(root.transform, false);
                    disc.transform.position = W(cx, cy, p.heightM);
                    disc.transform.localScale = new Vector3(p.widthM, 0.05f, p.widthM);
                    disc.GetComponent<Renderer>().sharedMaterial = plate;
                }
                else
                {
                    var top = SceneBuilder.Box("Plate", W(cx, cy, p.heightM), new Vector3(p.widthM, 0.10f, p.depthM), plate, root.transform);
                    foreach (var corner in new[] { new[] { p.x + 0.15, p.y + 0.15 }, new[] { p.x + p.widthM - 0.15, p.y + 0.15 }, new[] { p.x + p.widthM - 0.15, p.y + p.depthM - 0.15 }, new[] { p.x + 0.15, p.y + p.depthM - 0.15 } })
                        SceneBuilder.Box("Post", W(corner[0], corner[1], p.heightM * 0.5f), new Vector3(0.14f, p.heightM, 0.14f), post, root.transform);
                }
                root.SetActive(false);
                _visuals.Add(new Visual { Piece = p, Root = root });
            }
        }

        void BuildSunMarker()
        {
            _sunBall = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sunBall.name = "Sun";
            SceneBuilder.RemoveCollider(_sunBall);
            _sunBall.transform.localScale = Vector3.one * 2.4f;
            _sunBall.GetComponent<Renderer>().sharedMaterial = SceneBuilder.GlowMaterial(new Color(1f, 0.92f, 0.35f));
            _sunLine = SceneBuilder.Line("SunRay", new[] { Vector3.zero, Vector3.one }, new Color(1f, 0.92f, 0.35f, 0.9f), 0.10f, false, false);
            _sunArrow = _sunLine.gameObject;
            _sunBall.SetActive(false);
            _sunArrow.SetActive(false);
        }

        /// <summary>Puts the sun's marker where the sun is: out past the roof edge on the sun's side, as high as its elevation says (at a fixed 30 m).</summary>
        void PlaceSun(SunPosition sun, bool visible)
        {
            _sunBall.SetActive(visible && sun.Up);
            _sunArrow.SetActive(visible && sun.Up);
            if (!(visible && sun.Up)) return;
            double tx, ty;
            SunModel.TowardSun(sun, _before.NorthDeg, out tx, out ty);
            var roof = _payload.roof_context;
            var centre = W(roof.length_m * 0.5f, roof.width_m * 0.5f, 0.5f);
            var dir = new Vector3((float)tx, 0f, -(float)ty).normalized;
            var reach = roof.width_m * 0.85f;
            var ground = centre + dir * reach;
            var pos = ground + Vector3.up * Mathf.Clamp((float)Math.Tan(sun.ElevationDeg * SunModel.Deg) * reach * 0.35f, 2f, 14f);
            _sunBall.transform.position = pos;
            _sunLine.SetPosition(0, centre);
            _sunLine.SetPosition(1, pos);
        }

        // -------------------------------------------------------------- the maps

        void Fractions(SunScene scene, SunPosition sun, double treeTau)
        {
            SunModel.FillFractions(_grid, scene, sun, treeTau, _fractions, _scratch);
        }

        /// <summary>The sun hours of every cell on a design day, for a scene.</summary>
        double[] SunHoursMap(SunScene scene, int day)
        {
            var hours = new double[_grid.Count];
            var f = new double[_grid.Count];
            var tau = SunModel.TreeTau(SunModel.DayKeys[day]);
            foreach (var t in SunModel.DayTimes(_lat, SunModel.DayOfYear[day]))
            {
                SunModel.FillFractions(_grid, scene, SunModel.SunAt(_lat, SunModel.DayOfYear[day], t), tau, f, _scratch);
                for (var c = 0; c < hours.Length; c++) hours[c] += f[c] * SunModel.StepH;
            }
            return hours;
        }

        /// <summary>The mean shade of every cell over the heat window of the summer day.</summary>
        double[] MiddayShadeMap(SunScene scene)
        {
            var shade = new double[_grid.Count];
            var f = new double[_grid.Count];
            var n = 0;
            foreach (var t in SunModel.DayTimes(_lat, SunModel.DayOfYear[0]))
            {
                if (!SunModel.InWindow(t)) continue;
                SunModel.FillFractions(_grid, scene, SunModel.SunAt(_lat, SunModel.DayOfYear[0], t), SunModel.TreeTau("jun"), f, _scratch);
                for (var c = 0; c < shade.Length; c++) shade[c] += 1.0 - f[c];
                n++;
            }
            if (n > 0) for (var c = 0; c < shade.Length; c++) shade[c] /= n;
            return shade;
        }

        static Color32 WithAlpha(Color c, float alpha)
        {
            return new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
        }

        /// <summary>Shadow over the sunlit roof: clear where the sun reaches, dark blue where it does not.</summary>
        Color32 ShadowColour(int ix, int iy)
        {
            var c = _grid.Index(ix, iy);
            if (!_grid.Active[c]) return new Color32(0, 0, 0, 0);
            var f = (float)_fractions[c];
            return f >= 0.999f ? WithAlpha(new Color(1f, 0.95f, 0.6f), 0.10f) : WithAlpha(ShadowBlue, 0.30f + 0.55f * (1f - f));
        }

        static Color SunRamp(float h, float max)
        {
            var r = Mathf.Clamp01(h / Mathf.Max(0.1f, max));
            var dark = new Color(0.10f, 0.16f, 0.46f); var mid = new Color(0.97f, 0.86f, 0.28f); var hot = new Color(1.0f, 0.32f, 0.10f);
            return r < 0.5f ? Color.Lerp(dark, mid, r * 2f) : Color.Lerp(mid, hot, (r - 0.5f) * 2f);
        }

        static Color ShadeRamp(float shadePercent, float target)
        {
            var r = shadePercent / Mathf.Max(1f, target);
            return r >= 1f ? new Color(0.30f, 0.80f, 0.50f) : Color.Lerp(new Color(1.0f, 0.34f, 0.18f), new Color(1.0f, 0.82f, 0.25f), Mathf.Clamp01(r));
        }

        void PaintHours(double[] hours, float max)
        {
            _heat.Fill((ix, iy, c) =>
            {
                var i = _grid.Index(ix, iy);
                return _grid.Active[i] ? WithAlpha(SunRamp((float)hours[i], max), 0.78f) : new Color32(0, 0, 0, 0);
            });
        }

        void PaintShade(double[] shade)
        {
            var target = _report.summary.shadeTargetPercent;
            _heat.Fill((ix, iy, c) =>
            {
                var i = _grid.Index(ix, iy);
                return _grid.Active[i] ? WithAlpha(ShadeRamp((float)(100.0 * shade[i]), target), 0.72f) : new Color32(0, 0, 0, 0);
            });
        }

        // -------------------------------------------------------------- the run

        IEnumerator Run()
        {
            _hud.SetBanner("");
            _hud.SetCountsText("");
            _hud.SetFeed(new List<string>());
            _hud.SetLegend(new List<string>());

            _hud.ShowCard("Sun and shade analysis", Color.white, _results.caseStudy, TitleLines());
            foreach (var _ in Hold(TitleCardS, "title")) yield return null;
            _hud.HideCard();

            foreach (var _ in SceneDay()) yield return null;
            foreach (var _ in SceneHours()) yield return null;
            foreach (var _ in SceneMidday(_before, false)) yield return null;
            if (_visuals.Count > 0)
            {
                foreach (var _ in SceneEquipment()) yield return null;
                foreach (var _ in SceneAfternoon()) yield return null;
                foreach (var _ in SceneMidday(_after, true)) yield return null;
                foreach (var _ in SceneAfterHours()) yield return null;
            }

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

        string Clock(double t)
        {
            var h = (int)Math.Floor(t); var m = (int)Math.Floor((t - h) * 60.0);
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture);
        }

        IEnumerable<object> SceneDay()
        {
            SetZonesVisible(false);
            var times = SunModel.DayTimes(_lat, SunModel.DayOfYear[0]);
            if (times.Count == 0) yield break;
            double t0 = times[0] - SunModel.StepH * 0.5, t1 = times[times.Count - 1] + SunModel.StepH * 0.5;
            var tau = SunModel.TreeTau("jun");
            _hud.SetLegend(new List<string>
            {
                Tint("dark blue", new Color(0.45f, 0.55f, 1f)) + " = in the shadow of a wall or a tree; everything else is in direct sun",
                "Trees let " + Num((float)(SunModel.TreeTauSummer * 100), "0") + "% of the sun through in summer; walls none. Solar time: 12:00 = sun due south.",
            });
            foreach (var frame in Play(DayS, t =>
            {
                var tt = t0 + (t1 - t0) * t;
                var sun = SunModel.SunAt(_lat, SunModel.DayOfYear[0], tt);
                PlaceSun(sun, true);
                if (!sun.Up) { _heat.Clear(); }
                else
                {
                    Fractions(_before, sun, tau);
                    _heat.Fill((ix, iy, c) => ShadowColour(ix, iy));
                }
                _hud.SetBanner("21 JUNE   " + Clock(tt) + " SOLAR TIME   SUN " + Num((float)sun.ElevationDeg, "0") + " DEG HIGH");
                _hud.SetFeed(new List<string> { "A summer day on the roof: the shadows of the walls and trees sweep round", "Every frame is the analysis's own sunlit fraction of the cells" });
            }, "day")) yield return frame;
            PlaceSun(new SunPosition(), false);
        }

        IEnumerable<object> SceneHours()
        {
            SetZonesVisible(true);
            for (var d = 0; d < SunModel.DayKeys.Length; d++)
            {
                var hours = SunHoursMap(_before, d);
                var info = _report.days[d];
                var labels = new List<GameObject>();
                foreach (var z in _report.zones.Where(z => z.kind != "court"))
                {
                    var item = ItemOf(z);
                    if (item == null) continue;
                    var h = d == 0 ? z.sunHoursJune : d == 1 ? z.sunHoursMarch : z.sunHoursDecember;
                    var label = _hud.WorldLabel(PieceNames.Short(z.label, 22) + "\n" + Num(h, "0.#") + " h", LabelPos(z, item, 1.0f), 0.85f, Color.white).gameObject;
                    label.SetActive(false);
                    labels.Add(label);
                }
                var max = Mathf.Max(6f, Mathf.Ceil(_report.days[0].sunsetH - _report.days[0].sunriseH));
                _hud.SetLegend(new List<string>
                {
                    Tint("direct sun hours through the day:", Muted),
                    Tint("0 h", SunRamp(0, max)) + "  " + Tint(Num(max * 0.5f, "0") + " h", SunRamp(max * 0.5f, max)) + "  " + Tint(Num(max, "0") + " h", SunRamp(max, max)),
                    "Sun " + Num(info.sunriseH, "0.#") + " to " + Num(info.sunsetH, "0.#") + " h solar time, " + Num(info.noonElevationDeg, "0") + " deg high at noon",
                });
                foreach (var frame in Play(MapS, t =>
                {
                    var grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2.5f));
                    _heat.Fill((ix, iy, c) =>
                    {
                        var i = _grid.Index(ix, iy);
                        return _grid.Active[i] ? WithAlpha(SunRamp((float)hours[i] * grow, max), 0.78f) : new Color32(0, 0, 0, 0);
                    });
                    foreach (var l in labels) l.SetActive(t > 0.5f);
                    _hud.SetBanner("SUN HOURS " + info.name.ToUpperInvariant() + ": MEAN " + Num(info.roofMeanSunHours, "0.#") + " H");
                    _hud.SetFeed(new List<string> { d == 0 ? "The longest day: where the roof is hottest and the gardens are happiest" : d == 1 ? "Spring and autumn: half the sun of June" : "Midwinter: what the low sun and the walls leave" });
                }, "sun-hours-" + SunModel.DayKeys[d])) yield return frame;
                foreach (var l in labels) UnityEngine.Object.Destroy(l);
            }
        }

        IEnumerable<object> SceneMidday(SunScene scene, bool after)
        {
            SetZonesVisible(true);
            var shade = MiddayShadeMap(scene);
            var s = _report.summary;
            var labels = new List<GameObject>();
            foreach (var z in _report.zones.Where(z => z.kind != "court"))
            {
                var item = ItemOf(z);
                if (item == null) continue;
                string text; Color color;
                if (z.kind == "garden")
                {
                    var hrs = after ? z.afterSunHoursJune : z.sunHoursJune; var st = after ? z.afterStatus : z.status;
                    text = PieceNames.Short(z.label, 22) + "\nsun " + Num(hrs, "0.#") + " h (needs " + Num(s.gardenMinSunHours, "0.#") + ")"; color = st == "too-shaded" ? Amber : Good;
                }
                else
                {
                    var sh = after ? z.afterPeakShadePercent : z.peakShadePercent; var st = after ? z.afterStatus : z.status;
                    text = PieceNames.Short(z.label, 22) + "\nshade " + Num(sh, "0") + "% (wants " + Num(s.shadeTargetPercent, "0") + "%)"; color = st == "too-sunny" ? Red : Good;
                }
                var label = _hud.WorldLabel(text, LabelPos(z, item, 1.2f), 0.85f, color).gameObject;
                label.SetActive(false);
                labels.Add(label);
            }
            _hud.SetLegend(new List<string>
            {
                Tint("people zones: average shade between 11 and 16 h on 21 June, against the target of " + Num(s.shadeTargetPercent, "0") + "%:", Muted),
                Tint("none", ShadeRamp(0, s.shadeTargetPercent)) + "  " + Tint("some", ShadeRamp(s.shadeTargetPercent * 0.5f, s.shadeTargetPercent)) + "  " + Tint("enough", ShadeRamp(s.shadeTargetPercent, s.shadeTargetPercent)),
                "Painted: the people zones (red to green), gardens that get their sun (green) or not (amber), courts (white, never covered)",
            });
            foreach (var frame in Play(MiddayS, t =>
            {
                var grow = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 2.5f));
                _heat.Fill((ix, iy, c) =>
                {
                    var i = _grid.Index(ix, iy);
                    var zi = _grid.Active[i] ? _cellZone[i] : -1;
                    if (zi < 0) return new Color32(0, 0, 0, 0);
                    var z = _report.zones[zi];
                    if (z.kind == "people" || z.kind == "spectators") return WithAlpha(ShadeRamp((float)(100.0 * shade[i] * grow), s.shadeTargetPercent), 0.78f);
                    if (z.kind == "garden") return WithAlpha((after ? z.afterStatus : z.status) == "too-shaded" ? Amber : Good, 0.32f * grow);
                    return WithAlpha(CourtColor, 0.20f * grow);
                });
                foreach (var l in labels) l.SetActive(t > 0.4f);
                _hud.SetBanner((after ? "WITH EQUIPMENT: " : "") + "MIDDAY SHADE, " + (after ? s.peopleZonesTooSunnyAfter : s.peopleZonesTooSunny) + " OF " + s.peopleZones + " TOO SUNNY");
                _hud.SetFeed(new List<string> { "The people zones need shade when the sun is highest; the gardens need their sun", "The white courts are never covered" });
            }, after ? "midday-after" : "midday")) yield return frame;
            foreach (var l in labels) UnityEngine.Object.Destroy(l);
        }

        IEnumerable<object> SceneEquipment()
        {
            SetZonesVisible(true);
            var afternoon = SunModel.SunAt(_lat, SunModel.DayOfYear[0], 13.75);
            var tau = SunModel.TreeTau("jun");
            var partial = SunModel.BuildScene(_inputs, null);
            PlaceSun(afternoon, true);
            for (var i = 0; i < _visuals.Count; i++)
            {
                var v = _visuals[i];
                var p = v.Piece;
                v.Root.SetActive(true);
                partial.Equipment.Add(p);
                var label = _hud.WorldLabel(p.name + "\n" + (p.shape == "disc" ? Num(p.widthM, "0.#") + " m across" : Num(p.widthM, "0.#") + " x " + Num(p.depthM, "0.#") + " m") + ", " + Num(p.heightM, "0.#") + " m high",
                    W(p.x + p.widthM * 0.5, p.y + p.depthM * 0.5, p.heightM + 1.4f), 0.9f, Color.white).gameObject;
                var idx = i;
                foreach (var frame in Play(PieceS, t =>
                {
                    var rise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t * 3f));
                    v.Root.transform.localScale = new Vector3(1f, Mathf.Max(0.02f, rise), 1f);
                    v.Root.transform.position = new Vector3(0f, 0f, 0f);
                    Fractions(partial, afternoon, tau);
                    _heat.Fill((ix, iy, c) => ShadowColour(ix, iy));
                    _hud.SetBanner("EQUIPMENT " + (idx + 1) + " OF " + _visuals.Count + "   13:45 SOLAR TIME");
                    _hud.SetFeed(new List<string>
                    {
                        p.name + " for " + p.zoneLabel + ": shade at midday " + Num(p.shadeBeforePercent, "0") + "% -> " + Num(p.shadeAfterPercent, "0") + "%",
                        "+" + Num(p.addedLoadKn, "0.#") + " kN on the deck" + (p.windUpliftKn > 0.05f ? ", wind up to " + Num(p.windUpliftKn, "0.#") + " kN on its anchors" : ""),
                    });
                }, "piece-" + (i + 1))) yield return frame;
                UnityEngine.Object.Destroy(label);
            }
            PlaceSun(new SunPosition(), false);
        }

        IEnumerable<object> SceneAfternoon()
        {
            SetZonesVisible(false);
            var times = SunModel.DayTimes(_lat, SunModel.DayOfYear[0]).Where(SunModel.InWindow).ToList();
            if (times.Count == 0) yield break;
            double t0 = times[0] - SunModel.StepH * 0.5, t1 = times[times.Count - 1] + SunModel.StepH * 0.5;
            var tau = SunModel.TreeTau("jun");
            foreach (var frame in Play(AfternoonS, t =>
            {
                var tt = t0 + (t1 - t0) * t;
                var sun = SunModel.SunAt(_lat, SunModel.DayOfYear[0], tt);
                PlaceSun(sun, true);
                Fractions(_after, sun, tau);
                _heat.Fill((ix, iy, c) => ShadowColour(ix, iy));
                _hud.SetBanner("21 JUNE   " + Clock(tt) + " SOLAR TIME   WITH EQUIPMENT");
                _hud.SetFeed(new List<string> { "The heat window, 11:00 to 16:00, with the equipment up", "Each piece's shadow moves across the zone it serves" });
            }, "afternoon")) yield return frame;
            PlaceSun(new SunPosition(), false);
        }

        IEnumerable<object> SceneAfterHours()
        {
            SetZonesVisible(true);
            var hours = SunHoursMap(_after, 0);
            var info = _report.days[0];
            var max = Mathf.Max(6f, Mathf.Ceil(info.sunsetH - info.sunriseH));
            var s = _report.summary;
            _hud.SetLegend(new List<string>
            {
                Tint("direct sun hours on 21 June, with the equipment:", Muted),
                Tint("0 h", SunRamp(0, max)) + "  " + Tint(Num(max * 0.5f, "0") + " h", SunRamp(max * 0.5f, max)) + "  " + Tint(Num(max, "0") + " h", SunRamp(max, max)),
                "Gardens under their " + Num(s.gardenMinSunHours, "0.#") + " h need: " + s.gardenZonesTooShaded + " before, " + s.gardenZonesTooShadedAfter + " after",
            });
            foreach (var frame in Play(AfterS, t =>
            {
                PaintHours(hours, max);
                _hud.SetBanner("SUN HOURS 21 JUNE WITH EQUIPMENT: MEAN " + Num(info.roofMeanSunHoursAfter, "0.#") + " H");
                _hud.SetFeed(new List<string> { "The shade goes where people stay; the gardens keep their sun" });
            }, "sun-hours-after")) yield return frame;
        }

        // -------------------------------------------------------------- cards

        List<string> TitleLines()
        {
            var s = _report.summary;
            return new List<string>
            {
                "How much direct sun each part of the roof gets, and what to do about it:",
                "  - the sun on 21 June, 21 March and 21 December (latitude " + Num(s.latitudeDeg, "0.#") + " N" + (s.latitudeAssumed ? ", assumed" : "") + ")",
                "  - the shadows of walls and trees, hour by hour",
                "  - which play areas are too sunny at midday, which gardens too shaded",
                "  - the shading equipment that would fix it, and what it weighs on the deck",
                "",
                "Direct sun only, in solar time, without neighbouring buildings.",
                SimulationHud.Tint("A screening model, not a daylight or thermal design.", Muted),
            };
        }

        void ShowSummaryCard()
        {
            var s = _report.summary;
            var headline = s.peopleZonesTooSunny > 0
                ? s.peopleZonesTooSunny + " of " + s.peopleZones + " people zones are too sunny at midday"
                : s.gardenZonesTooShaded > 0 ? s.gardenZonesTooShaded + " of " + s.gardenZones + " gardens get too little sun" : "The sun and shade balance holds";
            var lines = new List<string>
            {
                "Latitude:   " + Num(s.latitudeDeg, "0.#") + " N" + (s.latitudeAssumed ? Tint("   assumed: set the site", Amber) : "") + ",  roof turned " + Num(s.northDeg, "0") + " deg" + (s.northAssumed ? Tint("   assumed", Amber) : ""),
            };
            foreach (var d in _report.days)
                lines.Add(d.name + ":   sun " + Num(d.sunriseH, "0.#") + "-" + Num(d.sunsetH, "0.#") + " h,  " + Num(d.noonElevationDeg, "0") + " deg at noon,  roof mean " + Num(d.roofMeanSunHours, "0.#") + " h" + (_report.equipment.Count > 0 ? " -> " + Num(d.roofMeanSunHoursAfter, "0.#") + " h" : ""));
            lines.Add("People zones:   " + Tint(s.peopleZonesTooSunny + " too sunny", s.peopleZonesTooSunny > 0 ? Red : Good) + (s.pieces > 0 ? " -> " + Tint(s.peopleZonesTooSunnyAfter + " with the equipment", s.peopleZonesTooSunnyAfter > 0 ? Amber : Good) : "") + ",  of " + s.peopleZones + "  (target " + Num(s.shadeTargetPercent, "0") + "% shade at midday)");
            lines.Add("Gardens:   " + Tint(s.gardenZonesTooShaded + " too shaded", s.gardenZonesTooShaded > 0 ? Amber : Good) + (s.pieces > 0 ? " -> " + s.gardenZonesTooShadedAfter + " with the equipment" : "") + ",  of " + s.gardenZones + "  (need " + Num(s.gardenMinSunHours, "0.#") + " h of sun)");
            if (s.pieces > 0)
                lines.Add("Equipment:   " + s.pieces + " pieces,  +" + Num(s.addedLoadKn, "0.#") + " kN;  the deck's busiest bay " + Num(_report.structure.peakUtilisationBefore * 100f, "0") + "% -> " + Num(_report.structure.peakUtilisationAfter * 100f, "0") + "%");
            _hud.ShowCard(headline, s.peopleZonesTooSunny > 0 || s.gardenZonesTooShaded > 0 ? Amber : Good, _results.caseStudy, lines);
        }

        /// <summary>A zone's name without its parenthetical, so a card line stays on one row ("Badminton (standard) spectators" becomes "Badminton spectators").</summary>
        static string PlainZone(string label)
        {
            return System.Text.RegularExpressions.Regex.Replace(label ?? "", @"\s*\([^)]*\)", "");
        }

        void ShowFixCard()
        {
            var lines = new List<string>();
            var s = _report.summary;
            foreach (var p in _report.equipment)
                CardText.Bullet(lines, Tint(p.name, Amber) + " " + (p.shape == "disc" ? Num(p.widthM, "0.#") + " m across" : Num(p.widthM, "0.#") + " x " + Num(p.depthM, "0.#") + " m") + " at (" + Num(p.x + p.widthM * 0.5f, "0") + ", " + Num(p.y + p.depthM * 0.5f, "0") + "): " +
                                PlainZone(p.zoneLabel) + " " + Num(p.shadeBeforePercent, "0") + " -> " + Num(p.shadeAfterPercent, "0") + "%");
            if (_report.equipment.Count > 0)
            {
                var wind = _report.equipment.Max(p => p.windUpliftKn);
                CardText.Bullet(lines, Tint("Deck", Muted) + "  +" + Num(s.addedLoadKn, "0.#") + " kN in all, busiest bay " + Num(_report.structure.peakUtilisationBefore * 100f, "0") + "% -> " + Num(_report.structure.peakUtilisationAfter * 100f, "0") + "%" +
                                (wind > 0.05f ? "; wind uplift up to " + Num(wind, "0") + " kN a piece (anchors: structural engineer)." : "."));
            }
            foreach (var r in _report.recommendations.Where(r => r.kind == "no-option" || r.kind == "too-shaded" || r.kind == "fine"))
                CardText.Bullet(lines, Tint(r.kind == "fine" ? "OK" : "Note", r.kind == "fine" ? Good : Muted) + "  " + r.text);
            if (s.preliminary) CardText.Bullet(lines, Tint("PRELIMINARY", Amber) + "  Not confirmed: " + AnalysisAssumptions.PreliminaryNames(_report.assumptionUses) + ".");
            else if (!string.IsNullOrEmpty(s.acceptedNote)) CardText.Bullet(lines, Tint("Inputs", Muted) + "  " + s.acceptedNote + ".");
            _hud.ShowCard("What to change", Color.white, "Screening result: the results file lists every number and assumption", lines);
        }

        // -------------------------------------------------------------- playing

        float _clock;

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

        // -------------------------------------------------------------- video plumbing

        void StartRecorder()
        {
#if UNITY_EDITOR
            try
            {
                _recorder = new VideoRecorder(_cam, _cfg.VideoPath, _cfg.Width, _cfg.Height, _cfg.Fps);
                Debug.Log("[Sun] Recording " + _cfg.Width + "x" + _cfg.Height + " @ " + _cfg.Fps + " fps to " + _cfg.VideoPath);
            }
            catch (Exception ex)
            {
                _results.videoError = "Couldn't start the video encoder: " + ex.Message;
                Debug.LogError("[Sun] " + _results.videoError);
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
                Debug.LogError("[Sun] " + _results.videoError);
                DiscardVideo();
            }
#endif
        }

        void SaveStill(string name)
        {
#if UNITY_EDITOR
            if (_recorder == null || !_cfg.DebugStills) return;
            var stillPath = Path.ChangeExtension(_cfg.VideoPath, null) + "_" + name + ".png";
            try { _recorder.SaveStill(stillPath); } catch (Exception ex) { Debug.LogWarning("[Sun] Couldn't save " + stillPath + ": " + ex.Message); }
#endif
        }

        void SavePoster()
        {
#if UNITY_EDITOR
            if (_recorder == null) return;
            var posterPath = Path.ChangeExtension(_cfg.VideoPath, ".png");
            try { _recorder.SaveStill(posterPath); _results.video.posterPath = posterPath; }
            catch (Exception ex) { Debug.LogWarning("[Sun] Couldn't save the poster image: " + ex.Message); }
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
                Debug.LogError("[Sun] " + _results.videoError);
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
            Debug.Log("[Sun] Video: " + recorder.Path + " (" + recorder.FramesWritten + " frames, " + Num(_results.video.durationS, "0.0") + " s)");
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
            Debug.LogError("[Sun] Failed: " + ex);
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
            var path = Path.Combine(dir, "sun_results.json");
            File.WriteAllText(path, JsonUtility.ToJson(_results, true));
            Debug.Log("[Sun] Done. Report: " + path);

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
            var cfg = new Config { VideoPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings", "sun_shade.mp4")) };
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
