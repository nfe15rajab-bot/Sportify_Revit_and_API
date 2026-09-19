using UnityEngine;

namespace Sportify.Simulation
{
    /// <summary>
    /// The one place layout coordinates become Unity world coordinates.
    ///
    /// A Sportify layout is a plan drawing: x runs left to right and y runs
    /// DOWN the page (web canvas convention). Unity's world is left-handed with
    /// y up, so mapping y_layout straight onto +z mirrors the plan when viewed
    /// from above. Mapping z = -y instead makes a top-down camera (up = +Z)
    /// show exactly the plan the designer drew, and puts the "top" edge of the
    /// roof on the far side of the video's 3/4 view.
    ///
    /// Everything reported back to Revit (violation x_m / y_m) is converted
    /// back with <see cref="ToLayout"/>, so the JSON stays in layout space.
    /// </summary>
    public static class LayoutSpace
    {
        public static Vector3 ToWorld(float x_m, float y_m, float height_m = 0f)
        {
            return new Vector3(x_m, height_m, -y_m);
        }

        public static Vector2 ToLayout(Vector3 world)
        {
            return new Vector2(world.x, -world.z);
        }
    }
}
