using System.Collections.Generic;
using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// A ball in flight: gravity (Unity physics) plus quadratic air drag,
    /// ending at first ground contact.
    ///
    /// The first version let the ball keep rolling after it landed. A sphere
    /// with no rolling resistance rolled along the roof for metres, wandered
    /// into the next court and was reported as a boundary crossing - every
    /// "violation" it found was that artefact. A shuttlecock (or any ball that
    /// has just come down from height) does not do that: it stops. So a shot
    /// now ends when it reaches the roof surface (or leaves the roof, or times
    /// out), and only its FLIGHT counts.
    ///
    /// The simulation runner drives this by hand (PreStep, Physics.Simulate,
    /// PostStep) at a fixed small step, so results don't depend on how fast
    /// the machine renders.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class AerodynamicProjectile : MonoBehaviour
    {
        const float AirDensity = 1.225f;
        const float MaxLifetimeS = 8f;

        public int OriginCourtIndex;
        public string ShotLabel;
        public int Slot;
        public BallProfile Ball;
        public bool Violated;

        public bool Finished { get; private set; }
        public string EndedBy { get; private set; }     // "landed" | "left-roof" | "timeout"
        public float EndTime { get; private set; }
        public Vector3 Origin { get; private set; }
        public Vector3 EndPosition { get; private set; }

        // Set when the flight ended by crossing the roof outline: which edge, and where.
        public string CrossedEdge { get; private set; }     // top | bottom | left | right
        public Vector3 CrossedEdgePoint { get; private set; }

        // One point per physics step: the trail in the video is drawn from this.
        public readonly List<Vector3> Path = new List<Vector3>(512);

        // First entry into each zone only: one crossing per shot per zone.
        public readonly HashSet<ZoneVolume> ZonesEntered = new HashSet<ZoneVolume>();

        Rigidbody _rb;
        Rect _roofXZ;
        float _launchTime;

        public void Launch(ShotScenario shot, Rect roofXZ, float simTime)
        {
            Ball = shot.Ball;
            OriginCourtIndex = shot.CourtIndex;
            ShotLabel = shot.Label;
            Slot = shot.Slot;
            Origin = shot.Origin;
            _roofXZ = roofXZ;
            _launchTime = simTime;

            transform.position = shot.Origin;
            transform.localScale = Vector3.one * Ball.DiameterM;

            _rb = GetComponent<Rigidbody>();
            _rb.mass = Ball.MassKg;
            _rb.useGravity = true;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0f;
            _rb.position = shot.Origin;
            _rb.linearVelocity = shot.LaunchVelocity;

            Path.Add(shot.Origin);
        }

        /// <summary>Applies this step's air drag. Call once before Physics.Simulate.</summary>
        public void PreStep()
        {
            if (Finished) return;

            var v = _rb.linearVelocity;
            var speed = v.magnitude;
            if (speed < 0.01f) return;

            var dragN = 0.5f * AirDensity * Ball.DragCoefficient * Ball.CrossSectionM2 * speed * speed;
            _rb.AddForce(-v / speed * dragN, ForceMode.Force);
        }

        /// <summary>
        /// Records the step and ends the flight if it is over: the ball came down
        /// on the roof, crossed the roof outline, or ran out of time. True the
        /// step it ends.
        ///
        /// Both events are located EXACTLY within the step (a 70 m/s smash covers
        /// 0.3 m per step) and the earlier one wins. The roof outline is tested
        /// on the ball's centre, geometrically, rather than by touching a wall
        /// volume: a wall makes the edge a ball is credited with depend on the
        /// order two corner triggers happen to fire, and counts a ball that only
        /// brushed the edge.
        /// </summary>
        public bool PostStep(float simTime, float dt)
        {
            if (Finished) return false;

            var previous = LastPathPoint;
            var p = _rb.position;
            var r = Ball.RadiusM;

            var tEdge = float.PositiveInfinity;
            string edge = null;
            // z = 0 is the plan's top edge, z = -width its bottom edge; x = 0 / x = length are left / right.
            ConsiderEdge(_roofXZ.xMin, previous.x, p.x, false, "left", ref tEdge, ref edge);
            ConsiderEdge(_roofXZ.xMax, previous.x, p.x, true, "right", ref tEdge, ref edge);
            ConsiderEdge(_roofXZ.yMin, previous.z, p.z, false, "bottom", ref tEdge, ref edge);
            ConsiderEdge(_roofXZ.yMax, previous.z, p.z, true, "top", ref tEdge, ref edge);

            var tGround = p.y <= r && _rb.linearVelocity.y <= 0f
                ? Mathf.Clamp01(Mathf.InverseLerp(previous.y, p.y, r))
                : float.PositiveInfinity;

            if (edge != null && tEdge <= tGround)
            {
                CrossedEdge = edge;
                CrossedEdgePoint = Vector3.Lerp(previous, p, tEdge);
                return Finish("left-roof", CrossedEdgePoint, simTime - dt * (1f - tEdge));
            }

            if (!float.IsPositiveInfinity(tGround))
            {
                var contact = Vector3.Lerp(previous, p, tGround);
                contact.y = r;
                return Finish("landed", contact, simTime - dt * (1f - tGround));
            }

            Path.Add(p);
            if (simTime - _launchTime > MaxLifetimeS) return Finish("timeout", p, simTime);
            return false;
        }

        // A ball that starts inside the roof and ends the step beyond `bound` crossed it at fraction t of the step.
        static void ConsiderEdge(float bound, float from, float to, bool isMax, string name, ref float tBest, ref string edgeBest)
        {
            var crossed = isMax ? to > bound : to < bound;
            if (!crossed) return;

            var t = Mathf.Approximately(from, to) ? 0f : Mathf.Clamp01((bound - from) / (to - from));
            if (t < tBest)
            {
                tBest = t;
                edgeBest = name;
            }
        }

        bool Finish(string how, Vector3 position, float time)
        {
            // Mark finished FIRST: moving the body below must not register as a zone entry.
            Finished = true;
            EndedBy = how;
            EndTime = time;
            EndPosition = position;

            if (Path.Count == 0 || Path[Path.Count - 1] != position) Path.Add(position);

            _rb.linearVelocity = Vector3.zero;
            _rb.angularVelocity = Vector3.zero;
            _rb.position = position;
            transform.position = position;
            _rb.isKinematic = true;
            return true;
        }

        public float FlightTimeS => (Finished ? EndTime : 0f) - _launchTime;

        /// <summary>Where the ball was at the end of the previous step (the launch point before the first).</summary>
        public Vector3 LastPathPoint => Path.Count > 0 ? Path[Path.Count - 1] : Origin;
    }
}
