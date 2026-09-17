using System.Collections.Generic;
using UnityEngine;

namespace Sportify.Simulation
{
    public class ShotScenario
    {
        public int CourtIndex;
        public string Label;
        public Vector3 Origin;
        public Vector3 LaunchVelocity;

        public static List<ShotScenario> BuildDefaultBadmintonShots(List<CourtInstance> courts)
        {
            var shots = new List<ShotScenario>();
            const float contactHeight = 2.7f;

            foreach (var c in courts)
            {
                var halfLength = c.Size.x / 2f;
                var halfWidth = c.Size.z / 2f;

                AddShot(shots, c, "Clear-Long+X", new Vector3(c.Center.x - halfLength * 0.7f, contactHeight, c.Center.z), Vector3.right, 45f, 42f);
                AddShot(shots, c, "Clear-Long-X", new Vector3(c.Center.x + halfLength * 0.7f, contactHeight, c.Center.z), Vector3.left, 45f, 42f);
                AddShot(shots, c, "Smash", new Vector3(c.Center.x - halfLength * 0.3f, contactHeight, c.Center.z), Vector3.right, 70f, -15f);
                AddShot(shots, c, "Wide-Mishit", new Vector3(c.Center.x - halfLength * 0.7f, contactHeight, c.Center.z - halfWidth * 0.6f), new Vector3(0.85f, 0f, -0.53f).normalized, 40f, 38f);
            }

            return shots;
        }

        static void AddShot(List<ShotScenario> shots, CourtInstance court, string label, Vector3 origin, Vector3 horizontalDir, float speed, float launchAngleDeg)
        {
            var rad = launchAngleDeg * Mathf.Deg2Rad;
            var velocity = (horizontalDir.normalized * Mathf.Cos(rad) + Vector3.up * Mathf.Sin(rad)) * speed;

            shots.Add(new ShotScenario
            {
                CourtIndex = court.Index,
                Label = label,
                Origin = origin,
                LaunchVelocity = velocity,
            });
        }
    }
}
