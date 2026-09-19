using System.Collections.Generic;
using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// One stray shot to simulate: where it starts, how fast it leaves, which
    /// ball it is. Scenarios are written in the COURT's own frame (along its
    /// long axis / across it) and only converted to world space at the end, so
    /// a court the designer rotated is shot along its real long axis.
    ///
    /// Every sport gets the same four-shot shape as the original badminton
    /// set: a long shot toward each end, one hard flat shot, and one shot that
    /// leaves at an angle to the long axis (a mishit toward the side).
    ///
    /// The BADMINTON set is the original scenario set, unchanged. The other
    /// sports' speeds are representative "hard but plausible" recreational to
    /// club-level figures (handball throws ~22-28 m/s, futsal kicks ~24-30
    /// m/s, basketball passes ~16-21 m/s, volleyball serves/spikes ~22-28 m/s)
    /// and are meant to be tuned: they are plain numbers in the tables below.
    ///
    /// Two shot sets are built from the same tables:
    ///  - BuildFor: the four scenarios per court, exactly as written. These are
    ///    the shots drawn in the video.
    ///  - BuildSweepFor: a fan around every scenario (directions, speeds, launch
    ///    angles). Far more shots, never drawn; they give the roof-exit
    ///    percentage and the fence proposal a spread to be computed over.
    /// </summary>
    public class ShotScenario
    {
        public int CourtIndex;
        public string Label;
        public int Slot;            // 0-3 within its court; drives the trail colour in the video
        public BallProfile Ball;
        public Vector3 Origin;
        public Vector3 LaunchVelocity;

        // The fan. Every scenario is flown at each combination of these.
        public static readonly float[] SweepAzimuthOffsetsDeg = { -60f, -45f, -30f, -15f, 0f, 15f, 30f, 45f, 60f };
        public static readonly float[] SweepSpeedFactors = { 0.6f, 0.8f, 1.0f };
        public static readonly float[] SweepElevationOffsetsDeg = { -8f, 0f, 8f };

        public static string SweepDescription =>
            SweepAzimuthOffsetsDeg.Length + " directions (+/-60 deg around each scenario), " +
            SweepSpeedFactors.Length + " speeds (60-100% of the scenario's), " +
            SweepElevationOffsetsDeg.Length + " launch angles (+/-8 deg) per shot type";

        struct Spec
        {
            public string Label;      // "{a}" becomes the signed long axis, e.g. "+X"
            public float Along;       // launch point, -1..+1 of half the court length
            public float Across;      // launch point, -1..+1 of half the court width
            public int Direction;     // +1 / -1 along the long axis
            public float AzimuthDeg;  // turn away from the long axis; + is toward +across
            public float SpeedMs;
            public float AngleDeg;    // launch elevation, negative = downward
            public float HeightM;     // launch height above the roof
        }

        struct CourtFrame
        {
            public Vector2 Along, Across, Centre;
            public float HalfLength, HalfWidth;
            public string AxisName;
        }

        static Spec S(string label, float along, float across, int direction, float azimuthDeg,
                      float speedMs, float angleDeg, float heightM)
        {
            return new Spec
            {
                Label = label, Along = along, Across = across, Direction = direction,
                AzimuthDeg = azimuthDeg, SpeedMs = speedMs, AngleDeg = angleDeg, HeightM = heightM,
            };
        }

        // --- the original badminton scenarios (Wide-Mishit: direction (0.85, -0.53) => -31.94 deg) ---
        static readonly Spec[] BadmintonShots =
        {
            S("Clear-Long{a}", -0.7f, 0f, +1, 0f, 42f, 45f, 2.7f),
            S("Clear-Long{a}", +0.7f, 0f, -1, 0f, 42f, 45f, 2.7f),
            S("Smash", -0.3f, 0f, +1, 0f, 70f, -15f, 2.7f),
            S("Wide-Mishit", -0.7f, -0.6f, +1, -31.94f, 40f, 38f, 2.7f),
        };

        static readonly Spec[] BasketballShots =
        {
            S("Long-Pass{a}", -0.7f, 0f, +1, 0f, 16f, 30f, 2.2f),
            S("Long-Pass{a}", +0.7f, 0f, -1, 0f, 16f, 30f, 2.2f),
            S("Hard-Pass", -0.3f, 0f, +1, 0f, 21f, 10f, 2.2f),
            S("Wide-Mishit", -0.7f, -0.6f, +1, -32f, 14f, 40f, 2.2f),
        };

        static readonly Spec[] HandballShots =
        {
            S("Long-Throw{a}", -0.7f, 0f, +1, 0f, 24f, 35f, 2.4f),
            S("Long-Throw{a}", +0.7f, 0f, -1, 0f, 24f, 35f, 2.4f),
            S("Jump-Shot", -0.3f, 0f, +1, 0f, 28f, 12f, 2.6f),
            S("Wide-Mishit", -0.7f, -0.6f, +1, -32f, 22f, 30f, 2.4f),
        };

        static readonly Spec[] VolleyballShots =
        {
            S("Serve{a}", -0.9f, 0f, +1, 0f, 22f, 22f, 2.6f),
            S("Serve{a}", +0.9f, 0f, -1, 0f, 22f, 22f, 2.6f),
            S("Spike", -0.15f, 0f, +1, 0f, 28f, -8f, 3.0f),
            S("Wide-Mishit", -0.5f, -0.6f, +1, -40f, 16f, 50f, 2.6f),
        };

        static readonly Spec[] FutsalShots =
        {
            S("Clearance{a}", -0.7f, 0f, +1, 0f, 26f, 32f, 0.3f),
            S("Clearance{a}", +0.7f, 0f, -1, 0f, 26f, 32f, 0.3f),
            S("Driven-Shot", -0.3f, 0f, +1, 0f, 30f, 8f, 0.3f),
            S("Wide-Shot", -0.7f, -0.6f, +1, -30f, 24f, 20f, 0.3f),
        };

        static readonly Spec[] GenericShots =
        {
            S("Long-Throw{a}", -0.7f, 0f, +1, 0f, 18f, 35f, 2.2f),
            S("Long-Throw{a}", +0.7f, 0f, -1, 0f, 18f, 35f, 2.2f),
            S("Flat-Drive", -0.3f, 0f, +1, 0f, 24f, 10f, 2.2f),
            S("Wide-Mishit", -0.7f, -0.6f, +1, -32f, 16f, 40f, 2.2f),
        };

        static void Lookup(string sport, out BallProfile ball, out Spec[] specs)
        {
            switch ((sport ?? "").Trim().ToLowerInvariant())
            {
                case "badminton": ball = BallProfile.Shuttlecock; specs = BadmintonShots; break;
                case "basketball": ball = BallProfile.Basketball; specs = BasketballShots; break;
                case "handball": ball = BallProfile.Handball; specs = HandballShots; break;
                case "volleyball": ball = BallProfile.Volleyball; specs = VolleyballShots; break;
                case "football": ball = BallProfile.FutsalBall; specs = FutsalShots; break;
                default: ball = BallProfile.GenericBall; specs = GenericShots; break;
            }
        }

        /// <summary>The four scenarios per court, exactly as written: what the video shows.</summary>
        public static List<ShotScenario> BuildFor(List<CourtInstance> courts)
        {
            return Build(courts, false);
        }

        /// <summary>A fan around every scenario: what the roof-exit percentage is computed over.</summary>
        public static List<ShotScenario> BuildSweepFor(List<CourtInstance> courts)
        {
            return Build(courts, true);
        }

        static List<ShotScenario> Build(List<CourtInstance> courts, bool sweep)
        {
            var shots = new List<ShotScenario>();
            foreach (var court in courts)
                AddCourtShots(shots, court, sweep);
            return shots;
        }

        static void AddCourtShots(List<ShotScenario> shots, CourtInstance court, bool sweep)
        {
            Lookup(court.Sport, out var ball, out var specs);

            // Court frame in LAYOUT coordinates (x right, y down).
            var frame = new CourtFrame
            {
                Along = court.LongAxisIsX ? new Vector2(1f, 0f) : new Vector2(0f, 1f),
                Across = court.LongAxisIsX ? new Vector2(0f, 1f) : new Vector2(1f, 0f),
                HalfLength = (court.LongAxisIsX ? court.ExtentX : court.ExtentY) * 0.5f,
                HalfWidth = (court.LongAxisIsX ? court.ExtentY : court.ExtentX) * 0.5f,
                AxisName = court.LongAxisIsX ? "X" : "Y",
                Centre = court.CenterLayout,
            };

            for (var i = 0; i < specs.Length; i++)
            {
                var s = specs[i];

                if (!sweep)
                {
                    shots.Add(Make(court, ball, frame, s, i, s.AzimuthDeg, s.SpeedMs, s.AngleDeg));
                    continue;
                }

                AddFan(shots, court, ball, frame, s, i, mirrored: false);

                // A shot that veers to one side is just as likely to veer to the other.
                // Fanning only the side the scenario happens to be written for would
                // put fences on one edge of the roof and leave its mirror image bare.
                if (s.Across != 0f || s.AzimuthDeg != 0f)
                    AddFan(shots, court, ball, frame, s, i, mirrored: true);
            }
        }

        static void AddFan(List<ShotScenario> shots, CourtInstance court, BallProfile ball, CourtFrame frame,
                           Spec s, int slot, bool mirrored)
        {
            var sign = mirrored ? -1f : 1f;
            var spec = s;
            spec.Across = s.Across * sign;

            foreach (var azimuth in SweepAzimuthOffsetsDeg)
                foreach (var speedFactor in SweepSpeedFactors)
                    foreach (var elevation in SweepElevationOffsetsDeg)
                        shots.Add(Make(court, ball, frame, spec, slot,
                            sign * (s.AzimuthDeg + azimuth), s.SpeedMs * speedFactor, s.AngleDeg + elevation));
        }

        static ShotScenario Make(CourtInstance court, BallProfile ball, CourtFrame frame, Spec s, int slot,
                                 float azimuthDeg, float speedMs, float angleDeg)
        {
            var originLayout = frame.Centre + frame.Along * (s.Along * frame.HalfLength)
                                            + frame.Across * (s.Across * frame.HalfWidth);

            var az = azimuthDeg * Mathf.Deg2Rad;
            var dirLayout = (frame.Along * (s.Direction * Mathf.Cos(az)) + frame.Across * Mathf.Sin(az)).normalized;

            // Layout (x, y-down) -> world (x, z = -y).
            var horizontal = new Vector3(dirLayout.x, 0f, -dirLayout.y);

            var el = angleDeg * Mathf.Deg2Rad;
            var velocity = (horizontal * Mathf.Cos(el) + Vector3.up * Mathf.Sin(el)) * speedMs;

            return new ShotScenario
            {
                CourtIndex = court.Index,
                Label = s.Label.Replace("{a}", (s.Direction > 0 ? "+" : "-") + frame.AxisName),
                Slot = slot,
                Ball = ball,
                Origin = LayoutSpace.ToWorld(originLayout.x, originLayout.y, s.HeightM),
                LaunchVelocity = velocity,
            };
        }
    }
}
