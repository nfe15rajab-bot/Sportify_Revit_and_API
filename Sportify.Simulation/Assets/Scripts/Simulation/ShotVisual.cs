using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// Everything drawn for one shot: a glowing marker that follows the ball
    /// (the real ball is centimetres wide - invisible from a roof-wide camera),
    /// a trail behind it, and a dot where it lands. The trail switches to red
    /// at the moment the shot crosses a boundary.
    /// </summary>
    public class ShotVisual
    {
        // One colour per shot slot so "the smash" reads the same on every court.
        public static readonly Color[] SlotColors =
        {
            new Color(0.25f, 0.85f, 1.00f),   // cyan
            new Color(0.62f, 1.00f, 0.35f),   // lime
            new Color(1.00f, 0.45f, 0.90f),   // pink
            new Color(1.00f, 0.72f, 0.25f),   // amber
        };

        public static readonly Color CrossingColor = new Color(1.00f, 0.22f, 0.20f);

        const float TrailWidthM = 0.17f;

        public readonly AerodynamicProjectile Projectile;
        public Color BaseColor { get; }

        readonly Transform _marker;
        readonly float _markerSize;
        LineRenderer _trail;
        int _synced;
        bool _crossed;

        public ShotVisual(AerodynamicProjectile projectile)
        {
            Projectile = projectile;
            BaseColor = SlotColors[projectile.Slot % SlotColors.Length];

            // The physics sphere stays invisible; this one is only for the picture.
            var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "ShotMarker_" + projectile.name;
            SceneBuilder.RemoveCollider(marker);
            marker.GetComponent<Renderer>().sharedMaterial = SceneBuilder.GlowMaterial(BaseColor);
            _markerSize = projectile.Ball.VisualDiameterM;
            marker.transform.localScale = Vector3.one * _markerSize;
            marker.transform.position = projectile.Origin;
            _marker = marker.transform;

            _trail = NewTrail(projectile.name + "_trail", BaseColor, TrailWidthM, projectile.Origin);

            // A small ground dot under the launch point so the viewer sees where each shot came from.
            var origin = projectile.Origin;
            AddGroundDot("LaunchDot_" + projectile.name, new Vector3(origin.x, 0.09f, origin.z), BaseColor, 0.28f);
        }

        static LineRenderer NewTrail(string name, Color color, float width, Vector3 first)
        {
            var lr = SceneBuilder.Line(name, new[] { first }, color, width, false, false);
            return lr;
        }

        static void AddGroundDot(string name, Vector3 position, Color color, float radius)
        {
            var dot = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            dot.name = name;
            SceneBuilder.RemoveCollider(dot);
            dot.transform.position = position;
            dot.transform.localScale = new Vector3(radius * 2f, 0.01f, radius * 2f);
            dot.GetComponent<Renderer>().sharedMaterial = SceneBuilder.UnlitMaterial(color);
        }

        /// <summary>Appends the physics steps taken since the last call and moves the marker.</summary>
        public void Sync()
        {
            AppendNewPoints();
            _marker.position = Projectile.transform.position;
        }

        void AppendNewPoints()
        {
            var path = Projectile.Path;
            if (path.Count <= _synced) return;

            var start = _trail.positionCount;
            _trail.positionCount = start + (path.Count - _synced);
            for (var i = _synced; i < path.Count; i++)
                _trail.SetPosition(start + i - _synced, path[i]);
            _synced = path.Count;
        }

        /// <summary>Colours the rest of the flight red, starting exactly at the crossing point.</summary>
        public void MarkCrossing(Vector3 worldPosition)
        {
            if (_crossed) return;
            _crossed = true;

            AppendNewPoints();
            _trail.positionCount += 1;
            _trail.SetPosition(_trail.positionCount - 1, worldPosition);

            _trail = NewTrail(Projectile.name + "_trail_crossed", CrossingColor, TrailWidthM * 1.25f, worldPosition);
            _marker.GetComponent<Renderer>().sharedMaterial = SceneBuilder.GlowMaterial(CrossingColor);
        }

        /// <summary>The flight is over: park the marker, and mark the landing spot if it came down on the roof.</summary>
        public void Ended()
        {
            AppendNewPoints();
            var p = Projectile.EndPosition;
            _marker.position = p;
            _marker.localScale = Vector3.one * (_markerSize * 0.6f);

            // A shot that left over the roof edge has no landing spot on the roof.
            if (Projectile.EndedBy != "landed") return;

            AddGroundDot("LandingDot_" + Projectile.name, new Vector3(p.x, 0.10f, p.z),
                _crossed ? CrossingColor : BaseColor, 0.55f);
        }
    }
}
