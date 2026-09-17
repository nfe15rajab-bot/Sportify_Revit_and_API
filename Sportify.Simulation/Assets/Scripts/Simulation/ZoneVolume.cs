using UnityEngine;

namespace Sportify.Simulation
{
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
            if (proj == null) return;
            if (Type == ZoneType.NeighborCourt && proj.OriginCourtIndex == OwnerCourtIndex) return;

            CollisionAnalysisRunner.Instance.OnZoneEntered(proj, this, other.transform.position);
        }
    }
}
