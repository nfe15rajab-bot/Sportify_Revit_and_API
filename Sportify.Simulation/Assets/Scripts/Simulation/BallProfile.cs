using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// Physical description of the ball a sport is played with. The simulation
    /// only needs mass, size and a drag coefficient: the flight is gravity plus
    /// quadratic air drag, ending at first ground contact.
    ///
    /// Drag coefficients are deliberately on the LOW side for the round balls
    /// (less drag means a longer flight), so a shot that stays inside its court
    /// here stays inside it in practice too. The shuttlecock figures are the
    /// original ones from the first version of this simulation.
    /// </summary>
    public class BallProfile
    {
        public string Name;
        public float MassKg;
        public float DiameterM;
        public float DragCoefficient;

        // Size of the glowing marker drawn in the video. The real ball is far
        // too small to see from a roof-wide camera (a shuttle is 6.8 cm).
        public float VisualDiameterM = 0.42f;

        public float RadiusM => DiameterM * 0.5f;
        public float CrossSectionM2 => Mathf.PI * RadiusM * RadiusM;

        public static readonly BallProfile Shuttlecock = new BallProfile
        {
            Name = "Shuttlecock", MassKg = 0.005f, DiameterM = 0.068f, DragCoefficient = 0.60f,
        };

        public static readonly BallProfile Basketball = new BallProfile
        {
            Name = "Basketball (size 7)", MassKg = 0.62f, DiameterM = 0.239f, DragCoefficient = 0.47f,
        };

        public static readonly BallProfile Handball = new BallProfile
        {
            Name = "Handball (size 3)", MassKg = 0.425f, DiameterM = 0.19f, DragCoefficient = 0.47f,
        };

        public static readonly BallProfile Volleyball = new BallProfile
        {
            Name = "Volleyball", MassKg = 0.27f, DiameterM = 0.21f, DragCoefficient = 0.45f,
        };

        public static readonly BallProfile FutsalBall = new BallProfile
        {
            Name = "Indoor football (futsal)", MassKg = 0.42f, DiameterM = 0.20f, DragCoefficient = 0.25f,
        };

        // Multi-sport halls and any sport the table doesn't know about.
        public static readonly BallProfile GenericBall = new BallProfile
        {
            Name = "Generic ball", MassKg = 0.45f, DiameterM = 0.21f, DragCoefficient = 0.47f,
        };
    }
}
