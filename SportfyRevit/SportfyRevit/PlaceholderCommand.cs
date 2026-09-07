using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Shared "not built yet" dialog for ribbon commands that exist only to
    /// stake out the Sportify ribbon's full planned shape (App & Data Import
    /// / Analysis / Data Export & Deliverables) before each analysis/export
    /// feature actually gets designed and implemented.
    /// </summary>
    internal static class PlaceholderCommand
    {
        public static Result Show(string title, string detail)
        {
            TaskDialog.Show("Sportify — " + title, $"{title} isn't implemented yet.\n\n{detail}");
            return Result.Succeeded;
        }
    }
}
