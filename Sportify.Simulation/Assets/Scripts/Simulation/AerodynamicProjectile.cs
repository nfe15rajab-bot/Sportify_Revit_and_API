using UnityEngine;

namespace Sportify.Simulation
{
    [RequireComponent(typeof(Rigidbody))]
    public class AerodynamicProjectile : MonoBehaviour
    {
        public float massKg = 0.005f;
        public float diameterM = 0.068f;
        public float dragCoefficient = 0.6f;
        public float airDensity = 1.225f;
        public float maxLifetimeS = 8f;

        public int OriginCourtIndex;
        public string ShotLabel;

        Rigidbody _rb;
        float _spawnTime;
        float _crossSectionArea;
        bool _expired;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _rb.mass = massKg;
            _rb.linearDamping = 0f;
            _rb.angularDamping = 0f;
            _crossSectionArea = Mathf.PI * (diameterM * 0.5f) * (diameterM * 0.5f);
        }

        void Start()
        {
            _spawnTime = Time.time;
        }

        void FixedUpdate()
        {
            var v = _rb.linearVelocity;
            var speed = v.magnitude;
            if (speed > 0.01f)
            {
                var dragMag = 0.5f * airDensity * dragCoefficient * _crossSectionArea * speed * speed;
                _rb.AddForce(-v.normalized * dragMag, ForceMode.Force);
            }

            if (!_expired && Time.time - _spawnTime > maxLifetimeS)
            {
                _expired = true;
                CollisionAnalysisRunner.Instance.OnProjectileExpired(this, transform.position);
            }
        }
    }
}
