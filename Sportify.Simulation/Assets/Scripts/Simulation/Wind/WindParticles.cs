using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sportify.Simulation.Wind
{
    /// <summary>
    /// Wind lines drifting across the roof, faster where the roof speeds the wind up (edges and corners).
    /// A picture of the field the analysis used, moved by hand each video frame from a fixed seed, so
    /// the video is the same every run.
    /// </summary>
    public sealed class StreakField
    {
        const float Margin = 4f;          // how far upstream of the roof a line starts
        const float MinHeightM = 0.5f;
        const float MaxHeightM = 6.5f;
        const float LengthSeconds = 0.20f;

        struct Streak
        {
            public float X, Y, Height, Age, Life;
            public Vector2 Dir;
            public int DirIndex;
        }

        readonly Streak[] _streaks;
        readonly RibbonBatch _batch;
        readonly RoofField _field;
        readonly System.Random _rng = new System.Random(20260918);
        readonly float _baseSpeed;
        readonly float _surfaceY;
        bool _visible;

        public StreakField(RoofField field, int count, float baseSpeedMs, float surfaceY)
        {
            _field = field;
            _baseSpeed = baseSpeedMs;
            _surfaceY = surfaceY;
            _streaks = new Streak[count];

            var centre = LayoutSpace.ToWorld(field.Length * 0.5f, field.Width * 0.5f, 3f);
            _batch = new RibbonBatch("WindStreaks", count, 8, centre, new Vector3(field.Length + 40f, 30f, field.Width + 40f));
            for (var i = 0; i < count; i++) _batch.Hide(i);
            _batch.Apply();
        }

        float Rand() { return (float)_rng.NextDouble(); }

        /// <summary>Starts every line over on a new wind direction, scattered so the roof is already full of wind.</summary>
        public void Seed(int dirIndex)
        {
            var rad = (float)(WindModel.DirectionsDeg[dirIndex] * Math.PI / 180.0);
            var dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

            for (var i = 0; i < _streaks.Length; i++)
            {
                _streaks[i] = new Streak
                {
                    X = -Margin + Rand() * (_field.Length + 2f * Margin),
                    Y = -Margin + Rand() * (_field.Width + 2f * Margin),
                    Height = MinHeightM + Rand() * (MaxHeightM - MinHeightM),
                    Age = Rand() * 3f,
                    Life = 3.5f + Rand() * 2.5f,
                    Dir = dir,
                    DirIndex = dirIndex,
                };
            }
            _visible = true;
        }

        public void Hide()
        {
            _visible = false;
            for (var i = 0; i < _streaks.Length; i++) _batch.Hide(i);
            _batch.Apply();
        }

        public void Step(float dt)
        {
            if (!_visible) return;

            for (var i = 0; i < _streaks.Length; i++)
            {
                var s = _streaks[i];
                var speed = _baseSpeed * _field.SpeedFactorAt(s.DirIndex, s.X, s.Y);
                s.X += s.Dir.x * speed * dt;
                s.Y += s.Dir.y * speed * dt;
                s.Age += dt;

                var outside = s.X < -Margin - 1f || s.X > _field.Length + Margin + 1f ||
                              s.Y < -Margin - 1f || s.Y > _field.Width + Margin + 1f;
                if (outside || s.Age > s.Life) Respawn(ref s);
                _streaks[i] = s;
            }
        }

        /// <summary>Puts a line back upstream of the roof, at a random point along the windward side.</summary>
        void Respawn(ref Streak s)
        {
            var rx = Rand() * _field.Length;
            var ry = Rand() * _field.Width;

            // How far back along the wind from (rx, ry) until it leaves the roof rectangle.
            var back = float.MaxValue;
            if (s.Dir.x > 1e-4f) back = Mathf.Min(back, rx / s.Dir.x);
            else if (s.Dir.x < -1e-4f) back = Mathf.Min(back, (_field.Length - rx) / -s.Dir.x);
            if (s.Dir.y > 1e-4f) back = Mathf.Min(back, ry / s.Dir.y);
            else if (s.Dir.y < -1e-4f) back = Mathf.Min(back, (_field.Width - ry) / -s.Dir.y);
            if (back == float.MaxValue) back = 0f;

            var extra = 1f + Rand() * (Margin - 1f);
            s.X = rx - s.Dir.x * (back + extra);
            s.Y = ry - s.Dir.y * (back + extra);
            s.Height = MinHeightM + Rand() * (MaxHeightM - MinHeightM);
            s.Age = 0f;
            s.Life = 3.5f + Rand() * 2.5f;
        }

        public void Render()
        {
            if (!_visible) return;

            for (var i = 0; i < _streaks.Length; i++)
            {
                var s = _streaks[i];
                var factor = _field.SpeedFactorAt(s.DirIndex, s.X, s.Y);
                var speed = _baseSpeed * factor;
                var length = Mathf.Max(0.6f, speed * LengthSeconds);

                var head = LayoutSpace.ToWorld(s.X, s.Y, _surfaceY + s.Height);
                var tail = LayoutSpace.ToWorld(s.X - s.Dir.x * length, s.Y - s.Dir.y * length, _surfaceY + s.Height);

                var heat = Mathf.Clamp01((factor - 1f) / 0.53f);
                var color = Color.Lerp(new Color(0.82f, 0.93f, 1f), new Color(1f, 0.56f, 0.22f), heat);
                var fade = Mathf.Clamp01(s.Age / 0.5f) * Mathf.Clamp01((s.Life - s.Age) / 0.5f);
                color.a = (0.28f + 0.4f * heat) * fade;

                _batch.Set(i, tail, head, 0.11f, color);
            }
            _batch.Apply();
        }
    }

    /// <summary>Substrate lifting off the cells the analysis says would erode, and blowing away downwind.</summary>
    public sealed class DustField
    {
        public struct Source
        {
            public float X, Y;      // layout metres
            public int DirIndex;    // the direction the substrate blows toward
        }

        struct Grain
        {
            public float X, Y, Z, Vx, Vy, Vz, Age, Life;
            public bool Alive;
        }

        const float Gravity = 7f;

        readonly Grain[] _grains;
        readonly RibbonBatch _batch;
        readonly System.Random _rng = new System.Random(20260919);
        readonly float _surfaceY;
        readonly float _emitPerSecond;
        List<Source> _sources = new List<Source>();
        float _emitCarry;
        bool _visible;

        public DustField(RoofField field, int count, float emitPerSecond, float surfaceY)
        {
            _grains = new Grain[count];
            _emitPerSecond = emitPerSecond;
            _surfaceY = surfaceY;

            var centre = LayoutSpace.ToWorld(field.Length * 0.5f, field.Width * 0.5f, 3f);
            _batch = new RibbonBatch("WindDust", count, 9, centre, new Vector3(field.Length + 60f, 30f, field.Width + 60f));
            for (var i = 0; i < count; i++) _batch.Hide(i);
            _batch.Apply();
        }

        float Rand() { return (float)_rng.NextDouble(); }

        public void Start(List<Source> sources)
        {
            _sources = sources;
            _visible = sources.Count > 0;
            _emitCarry = 0f;
            for (var i = 0; i < _grains.Length; i++) _grains[i].Alive = false;
        }

        public void Stop()
        {
            _visible = false;
            for (var i = 0; i < _grains.Length; i++) { _grains[i].Alive = false; _batch.Hide(i); }
            _batch.Apply();
        }

        public void Step(float dt)
        {
            if (!_visible) return;

            _emitCarry += _emitPerSecond * dt;
            var emit = (int)_emitCarry;
            _emitCarry -= emit;

            for (var i = 0; i < _grains.Length; i++)
            {
                var g = _grains[i];
                if (!g.Alive)
                {
                    if (emit > 0 && _sources.Count > 0)
                    {
                        emit--;
                        g = Emit(_sources[(int)(Rand() * _sources.Count) % _sources.Count]);
                    }
                    _grains[i] = g;
                    continue;
                }

                g.Vz -= Gravity * dt;
                g.X += g.Vx * dt;
                g.Y += g.Vy * dt;
                g.Z += g.Vz * dt;
                g.Age += dt;
                if (g.Z < 0f || g.Age > g.Life) g.Alive = false;
                _grains[i] = g;
            }
        }

        Grain Emit(Source s)
        {
            var angle = (float)(WindModel.DirectionsDeg[s.DirIndex] * Math.PI / 180.0) + (Rand() - 0.5f) * 0.35f;
            var speed = 3.2f + Rand() * 2.4f;
            return new Grain
            {
                X = s.X + (Rand() - 0.5f) * 0.5f,
                Y = s.Y + (Rand() - 0.5f) * 0.5f,
                Z = 0.05f,
                Vx = Mathf.Cos(angle) * speed,
                Vy = Mathf.Sin(angle) * speed,
                Vz = 2.2f + Rand() * 2.4f,
                Age = 0f,
                Life = 1.6f,
                Alive = true,
            };
        }

        public void Render()
        {
            if (!_visible) return;

            for (var i = 0; i < _grains.Length; i++)
            {
                var g = _grains[i];
                if (!g.Alive) { _batch.Hide(i); continue; }

                var head = LayoutSpace.ToWorld(g.X, g.Y, _surfaceY + g.Z);
                var tail = LayoutSpace.ToWorld(g.X - g.Vx * 0.09f, g.Y - g.Vy * 0.09f, _surfaceY + Mathf.Max(0f, g.Z - g.Vz * 0.09f));

                var color = new Color(0.90f, 0.72f, 0.44f, 0.95f * Mathf.Clamp01((g.Life - g.Age) / 0.4f));
                _batch.Set(i, tail, head, 0.30f, color);
            }
            _batch.Apply();
        }
    }
}
