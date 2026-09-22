using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Small Revit-view defaults every view or view template Sportify creates shares. Touches Revit types (unlike BimRules), so it is not compiled
    /// into the Revit-free Tools/AddinCheck; the checks there read this file's own source text instead.
    /// </summary>
    internal static class ViewDefaults
    {
        /// <summary>
        /// Turns a new view's crop region off. Revit gives a freshly created plan, 3D or section view a small default crop box (a few feet across,
        /// nowhere near a real roof) — left on, everything Sportify built is simply invisible until someone notices "the view is empty" and turns
        /// the crop off by hand. Called once, right after a view is made; an existing view a designer may have cropped on purpose is left alone.
        /// Best-effort: a view type that refuses the change (rare) is left as it is rather than failing the view.
        /// </summary>
        public static void DisableCrop(View view)
        {
            try
            {
                if (view.CropBoxActive) view.CropBoxActive = false;
                if (view.CropBoxVisible) view.CropBoxVisible = false;
            }
            catch (Exception) { /* a view type that does not allow it: left as it was */ }
        }
    }
}
