using UnityEngine;

namespace Sportify.Simulation
{
    // RoofEdge is not a volume (see AerodynamicProjectile); it stays a type so a
    // roof exit is reported alongside the other crossings in the same format.
    public enum ZoneType { NeighborCourt, CirculationSpace, RoofEdge }

    [RequireComponent(typeof(Collider))]
    public class ZoneVolume : MonoBehaviour
    {
        public ZoneType Type;
        public string Label;
        public int OwnerCourtIndex = -1;

        void OnTriggerEnter(Collider other)
        {
            var proj = other.GetComponent<AerodynamicProjectile>();
            if (proj == null || proj.Finished) return;
            if (Type == ZoneType.NeighborCourt && proj.OriginCourtIndex == OwnerCourtIndex) return;

            // One crossing per shot per zone: re-entering the same zone isn't a new finding.
            if (!proj.ZonesEntered.Add(this)) return;

            CollisionAnalysisRunner.Instance.OnZoneEntered(proj, this, other.transform.position);
        }
    }
}
