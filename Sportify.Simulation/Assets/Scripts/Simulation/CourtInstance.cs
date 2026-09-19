using UnityEngine;

namespace Sportify.Simulation
{
    public class CourtInstance
    {
        public int Index;
        public string Sport;
        public string Norm;
        public float RunoffM;
        public float MinHeightM;

        // Informational only. The exported bounding_box is already the
        // POST-rotation footprint (the web app swaps width/height for a 90
        // degree court), so applying this a second time would turn every
        // rotated court the wrong way round.
        public float RotationDeg;

        // Footprint in layout coordinates (metres, y down).
        public float XMin, XMax, YMin, YMax;

        public float ExtentX => XMax - XMin;
        public float ExtentY => YMax - YMin;
        public bool LongAxisIsX => ExtentX >= ExtentY;

        public Vector2 CenterLayout => new Vector2((XMin + XMax) * 0.5f, (YMin + YMax) * 0.5f);
        public Vector3 Center => LayoutSpace.ToWorld(CenterLayout.x, CenterLayout.y);

        // x extent, clear height, y extent (the world-z extent).
        public Vector3 Size => new Vector3(ExtentX, MinHeightM, ExtentY);

        public string DisplayName => "Court " + Index + " (" + Sport + ")";
    }
}
