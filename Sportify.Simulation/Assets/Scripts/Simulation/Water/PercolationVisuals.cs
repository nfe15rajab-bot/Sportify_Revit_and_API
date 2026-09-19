using System;
using System.Collections.Generic;
using System.Linq;
using Sportify.Simulation.Wind;
using UnityEngine;

namespace Sportify.Simulation.Water
{
    /// <summary>
    /// One build-up drawn in section: its layers to scale, top to bottom, with the substrate cells coloured by how
    /// wet they are and the drainage layer filling from the bottom. It draws a SoilColumn, the same object the analysis
    /// steps, so what is filmed is exactly what was computed.
    /// </summary>
    public sealed class ColumnView
    {
        enum Kind { Vegetation, Substrate, Filter, Drainage, Protection, Barrier, Other }

        static readonly Color32 Dry = new Color32(196, 158, 108, 255);
        static readonly Color32 Wet = new Color32(28, 58, 132, 255);

        SoilColumn _column;
        readonly Texture2D _texture;
        readonly Color32[] _pixels;
        readonly Kind[] _kinds;        // per row, bottom first
        readonly int[] _cellOf;        // per row: substrate cell index, or -1
        readonly int[] _drainRow;      // per row: 0-based position in the drainage layer counted from its bottom, or -1
        readonly int _drainRows;
        readonly Transform _surface;
        readonly float _width, _x, _baseY, _height;
        readonly TextMesh _readout;

        public readonly float TopY;
        public readonly float X;
        public readonly float Width;
        public readonly float BottomY;

        public ColumnView(string title, AssemblyInput assembly, SoilColumn column, float x, float baseY, float targetHeightM, float width, SimulationHud hud)
        {
            _column = column;
            _x = x;
            _baseY = baseY;
            _width = width;
            X = x;
            Width = width;
            BottomY = baseY;

            // Rows of 10 mm, listed top to bottom first, then reversed for the texture (row 0 is the bottom).
            var top = new List<Kind>();
            var topCell = new List<int>();
            var topDrain = new List<int>();
            foreach (var layer in assembly.Layers)
            {
                var kind = KindOf(layer);
                var rows = Math.Max(1, (int)Math.Round(layer.ThicknessMm / PercolationModel.CellMm));
                for (var r = 0; r < rows; r++)
                {
                    top.Add(kind);
                    topCell.Add(-1);   // filled in below, once every row is known
                    topDrain.Add(kind == Kind.Drainage ? r : -1);
                }
            }

            // Cell indices for substrate rows: counted down through the substrate rows in order.
            var cell = 0;
            for (var i = 0; i < top.Count; i++) topCell[i] = top[i] == Kind.Substrate ? Math.Min(Math.Max(0, column.Cells - 1), cell++) : -1;

            var count = top.Count;
            _kinds = new Kind[count];
            _cellOf = new int[count];
            _drainRow = new int[count];
            var drainTotal = top.Count(k => k == Kind.Drainage);
            _drainRows = drainTotal;
            for (var i = 0; i < count; i++)
            {
                var from = count - 1 - i;      // texture row i (bottom first) is list index from
                _kinds[i] = top[from];
                _cellOf[i] = topCell[from];
                _drainRow[i] = top[from] == Kind.Drainage ? drainTotal - 1 - topDrain[from] : -1;   // 0 = the lowest drainage row
            }

            // Each column is drawn to its own scale (its layers in proportion), filling the same height: a 175 mm build-up
            // and a 970 mm tree bed would otherwise leave one of them too small to see the water in. The label gives the thickness.
            _height = targetHeightM;
            TopY = baseY + _height;

            _pixels = new Color32[count];
            _texture = new Texture2D(1, count, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };

            var frame = GameObject.CreatePrimitive(PrimitiveType.Quad);
            frame.name = "ColumnFrame";
            SceneBuilder.RemoveCollider(frame);
            frame.transform.position = new Vector3(x, baseY + _height * 0.5f, 0.05f);
            frame.transform.localScale = new Vector3(width + 0.08f, _height + 0.08f, 1f);
            frame.GetComponent<Renderer>().sharedMaterial = SceneBuilder.OverlayMaterial(new Color(0.9f, 0.92f, 0.95f, 0.9f));
            frame.GetComponent<Renderer>().sortingOrder = 1;

            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "ColumnLayers";
            SceneBuilder.RemoveCollider(quad);
            quad.transform.position = new Vector3(x, baseY + _height * 0.5f, 0f);
            quad.transform.localScale = new Vector3(width, _height, 1f);
            var material = SceneBuilder.OverlayMaterial(Color.white);
            material.mainTexture = _texture;
            quad.GetComponent<Renderer>().sharedMaterial = material;
            quad.GetComponent<Renderer>().sortingOrder = 2;

            // Water lying on the surface: a thin blue slab that grows with the surface store.
            var surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
            surface.name = "SurfaceWater";
            SceneBuilder.RemoveCollider(surface);
            surface.transform.position = new Vector3(x, TopY + 0.02f, 0f);
            surface.transform.localScale = new Vector3(width, 0.001f, 1f);
            surface.GetComponent<Renderer>().sharedMaterial = SceneBuilder.OverlayMaterial(new Color(0.35f, 0.65f, 1f, 0.95f));
            surface.GetComponent<Renderer>().sortingOrder = 3;
            _surface = surface.transform;

            var totalMm = assembly.Layers.Sum(l => l.ThicknessMm);
            hud.WorldLabel(title + "\n" + totalMm.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " mm build-up", new Vector3(x, baseY - 0.42f, 0f), 0.22f, Color.white);
            _readout = hud.WorldLabel("", new Vector3(x, baseY - 1.0f, 0f), 0.2f, new Color(0.85f, 0.9f, 1f));

            Refresh();
        }

        static Kind KindOf(LayerInput l)
        {
            switch ((l.Function ?? "").ToLowerInvariant())
            {
                case "vegetation": return Kind.Vegetation;
                case "substrate": return Kind.Substrate;
                case "filter": return Kind.Filter;
                case "drainage": return Kind.Drainage;
                case "protection": return Kind.Protection;
                case "root_barrier":
                case "waterproofing": return Kind.Barrier;
                default: return Kind.Other;
            }
        }

        /// <summary>Draws a different SoilColumn from now on (a new scenario starts from a fresh, wet column).</summary>
        public void Bind(SoilColumn column)
        {
            _column = column;
            Refresh();
        }

        /// <summary>How far a cell is from field capacity toward saturation, 0..1: the water the storm has added.</summary>
        float ExtraWater(int cell)
        {
            var span = _column.Spec.ThetaS - _column.Spec.ThetaFc;
            return span <= 0 ? 0f : Mathf.Clamp01((float)((_column.Theta[cell] - _column.Spec.ThetaFc) / span));
        }

        public void SetReadout(string text)
        {
            SimulationHud.SetLabelText(_readout, text);
        }

        /// <summary>Redraws the column from the state of its SoilColumn.</summary>
        public void Refresh()
        {
            var interception = _column.Spec.InterceptionMm > 0 ? Mathf.Clamp01((float)(_column.InterceptionMm / _column.Spec.InterceptionMm)) : 0f;
            var drainFill = (float)_column.DrainageFill;

            for (var i = 0; i < _kinds.Length; i++)
            {
                Color32 c;
                switch (_kinds[i])
                {
                    case Kind.Vegetation:
                        c = Color32.Lerp(new Color32(84, 150, 74, 255), new Color32(70, 130, 170, 255), interception * 0.6f);
                        break;
                    case Kind.Substrate:
                        c = _cellOf[i] >= 0 && _cellOf[i] < _column.Cells
                            ? Color32.Lerp(Dry, Wet, Mathf.Sqrt(ExtraWater(_cellOf[i])))
                            : Dry;
                        break;
                    case Kind.Filter: c = new Color32(214, 206, 178, 255); break;
                    case Kind.Drainage:
                        var filled = _drainRows > 0 && _drainRow[i] >= 0 && _drainRow[i] < drainFill * _drainRows;
                        c = filled ? new Color32(70, 130, 220, 255) : new Color32(168, 178, 196, 255);
                        break;
                    case Kind.Protection: c = new Color32(140, 140, 146, 255); break;
                    case Kind.Barrier: c = new Color32(52, 56, 64, 255); break;
                    default: c = new Color32(120, 120, 120, 255); break;
                }
                _pixels[i] = c;
            }
            _texture.SetPixels32(_pixels);
            _texture.Apply(false);

            var surface = Mathf.Clamp((float)_column.SurfaceMm, 0f, 4f);
            _surface.localScale = new Vector3(_width, Mathf.Max(0.001f, surface * 0.05f), 1f);
        }
    }

    /// <summary>Rain falling on the columns, and drops leaving them: many vertical streaks in one mesh.</summary>
    public sealed class FallField
    {
        readonly RibbonBatch _batch;
        readonly float[] _x, _y, _speed;
        readonly int[] _lane0;
        readonly float _top, _bottom, _length;
        readonly Color32 _color;
        readonly System.Random _rng;
        readonly Func<int, float> _floorAt;    // where a streak in lane i stops
        readonly int _count;
        readonly float[] _laneX;
        readonly float _laneWidth;
        float _active;

        public FallField(string name, int count, float[] laneX, float laneWidth, float top, float bottom, float length, float speed, Color color, int seed, Func<int, float> floorAt, int order)
        {
            _count = count;
            _laneX = laneX;
            _laneWidth = laneWidth;
            _top = top;
            _bottom = bottom;
            _length = length;
            _floorAt = floorAt;
            _rng = new System.Random(seed);
            _color = color;
            _x = new float[count];
            _y = new float[count];
            _speed = new float[count];
            _lane0 = new int[count];

            for (var i = 0; i < count; i++)
            {
                _lane0[i] = i % laneX.Length;
                _x[i] = laneX[_lane0[i]] + ((float)_rng.NextDouble() - 0.5f) * laneWidth;
                _y[i] = bottom + (float)_rng.NextDouble() * (top - bottom);
                _speed[i] = speed * (0.8f + 0.4f * (float)_rng.NextDouble());
            }

            _batch = new RibbonBatch(name, count, order, new Vector3(0f, 0f, 0f), new Vector3(60f, 40f, 4f));
            for (var i = 0; i < count; i++) _batch.Hide(i);
            _batch.Apply();
        }

        /// <summary>The share of the streaks in each lane that are falling, 0..1.</summary>
        public void SetActive(float fraction)
        {
            _active = Mathf.Clamp01(fraction);
        }

        public void SetLaneActive(int lane, float fraction)
        {
            _laneActive ??= new float[_laneX.Length];
            _laneActive[lane] = Mathf.Clamp01(fraction);
            _perLane = true;
        }

        float[] _laneActive;
        bool _perLane;

        public void Step(float dt)
        {
            for (var i = 0; i < _count; i++)
            {
                _y[i] -= _speed[i] * dt;
                var floor = _floorAt(_lane0[i]);
                if (_y[i] < floor)
                {
                    _y[i] = _top + (float)_rng.NextDouble() * 0.6f;
                    _x[i] = _laneX[_lane0[i]] + ((float)_rng.NextDouble() - 0.5f) * _laneWidth;
                }
            }
        }

        public void Render()
        {
            for (var i = 0; i < _count; i++)
            {
                var share = _perLane ? _laneActive[_lane0[i]] : _active;
                // Streaks are numbered around the lanes, so streak i is "on" when its rank among the lane's streaks is below the share.
                var rank = (i / _laneX.Length) / (float)Math.Max(1, _count / _laneX.Length);
                if (rank >= share || _y[i] < _floorAt(_lane0[i]))
                {
                    _batch.Hide(i);
                    continue;
                }
                var top = new Vector3(_x[i], _y[i], -0.05f);
                var bottom = new Vector3(_x[i], Mathf.Max(_floorAt(_lane0[i]), _y[i] - _length), -0.05f);
                _batch.SetVertical(i, top, bottom, 0.045f, _color);
            }
            _batch.Apply();
        }
    }

    /// <summary>
    /// The roof's outflow over time: the same rain on the roof with no green layers (red) and on this roof (green),
    /// drawn up to the moment shown, from the analysis's own hydrographs.
    /// </summary>
    public sealed class Hydrograph
    {
        readonly LineRenderer _bare, _garden;
        readonly float _x0, _x1, _y0, _y1;
        float[] _bareSeries, _gardenSeries;
        float _maxLps;
        float _totalMin;
        readonly float _stepMin;
        readonly TextMesh _scaleLabel;

        public Hydrograph(SimulationHud hud, float x0, float x1, float y0, float y1, float stepS)
        {
            _x0 = x0; _x1 = x1; _y0 = y0; _y1 = y1;
            _stepMin = stepS / 60f;

            var panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
            panel.name = "HydrographPanel";
            SceneBuilder.RemoveCollider(panel);
            panel.transform.position = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, 0.1f);
            panel.transform.localScale = new Vector3(x1 - x0, y1 - y0, 1f);
            panel.GetComponent<Renderer>().sharedMaterial = SceneBuilder.OverlayMaterial(new Color(0.03f, 0.04f, 0.07f, 0.92f));
            panel.GetComponent<Renderer>().sortingOrder = 1;

            SceneBuilder.Line("HydrographAxis", new[] { new Vector3(x0 + 0.1f, y0 + 0.08f, 0f), new Vector3(x1 - 0.1f, y0 + 0.08f, 0f) },
                new Color(0.7f, 0.74f, 0.8f, 0.9f), 0.02f, false, false);

            _bare = Curve("HydrographBare", new Color(1f, 0.42f, 0.4f));
            _garden = Curve("HydrographGarden", new Color(0.45f, 0.92f, 0.55f));

            hud.WorldLabel("roof outflow", new Vector3(x0 + 0.95f, y1 - 0.2f, 0f), 0.26f, new Color(0.85f, 0.88f, 0.94f));
            _scaleLabel = hud.WorldLabel("", new Vector3(x1 - 1.3f, y1 - 0.2f, 0f), 0.26f, new Color(0.85f, 0.88f, 0.94f));
        }

        static LineRenderer Curve(string name, Color color)
        {
            var lr = SceneBuilder.Line(name, new[] { Vector3.zero, Vector3.zero }, color, 0.06f, false, false);
            lr.sortingOrder = 6;
            lr.positionCount = 0;
            return lr;
        }

        public void Begin(float[] bare, float[] garden, float totalMin)
        {
            _bareSeries = bare;
            _gardenSeries = garden;
            _totalMin = Mathf.Max(1f, totalMin);
            _maxLps = 0.5f;
            foreach (var v in bare) _maxLps = Mathf.Max(_maxLps, v);
            foreach (var v in garden) _maxLps = Mathf.Max(_maxLps, v);
            _maxLps *= 1.1f;
            _bare.positionCount = 0;
            _garden.positionCount = 0;
            SimulationHud.SetLabelText(_scaleLabel, "peak scale " + _maxLps.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " l/s");
        }

        Vector3 Point(int i, float value)
        {
            var t = Mathf.Clamp01(i * _stepMin / _totalMin);
            return new Vector3(Mathf.Lerp(_x0 + 0.2f, _x1 - 0.2f, t), Mathf.Lerp(_y0 + 0.1f, _y1 - 0.4f, Mathf.Clamp01(value / _maxLps)), -0.02f);
        }

        /// <summary>Draws both curves up to the given simulated minute.</summary>
        public void Reveal(float simMin)
        {
            if (_bareSeries == null) return;
            var n = Mathf.Clamp(Mathf.FloorToInt(simMin / _stepMin), 0, Mathf.Min(_bareSeries.Length, _gardenSeries.Length) - 1);
            Fill(_bare, _bareSeries, n);
            Fill(_garden, _gardenSeries, n);
        }

        void Fill(LineRenderer lr, float[] series, int n)
        {
            lr.positionCount = n + 1;
            for (var i = 0; i <= n; i++) lr.SetPosition(i, Point(i, series[i]));
        }
    }
}
