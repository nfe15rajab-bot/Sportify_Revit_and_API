using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Resolves the path to Revit's own "Metric Generic Model.rft" family
    /// template — what SportifyFamilyGenerator builds every auto-generated
    /// family from, so it works for any placement category without needing
    /// a hand-built specialty template. Probes the well-known install
    /// location first (confirmed present on Revit 2025's default install),
    /// then a couple of plausible variants, then — only if none exist —
    /// asks once via a file picker and caches whatever the user chose (or
    /// null if they cancelled) for the rest of the session, same idempotent
    /// cache shape as SportifySharedParameters.EnsureBound.
    /// </summary>
    internal static class GenericFamilyTemplateLocator
    {
        private static readonly string[] CandidatePaths =
        {
            @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English\Metric Generic Model.rft",
            @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English-Imperial\Metric Generic Model.rft",
            @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\English\Metric Generic Model.rft",
            @"C:\ProgramData\Autodesk\RVT 2024\Family Templates\English\Metric Generic Model.rft",
        };

        private static bool _attempted;
        private static string? _cachedPath;

        public static string? Resolve()
        {
            if (_attempted) return _cachedPath;
            _attempted = true;

            foreach (var path in CandidatePaths)
            {
                if (File.Exists(path))
                {
                    _cachedPath = path;
                    return _cachedPath;
                }
            }

            var fod = new FileOpenDialog("Revit Family Template (*.rft)|*.rft");
            fod.Title = "Locate Revit's \"Metric Generic Model.rft\" template (used to auto-build Sportify families) — not found at its usual install path";
            if (fod.Show() != ItemSelectionDialogResult.Confirmed) return null;

            try
            {
                _cachedPath = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
            }
            catch (System.Exception)
            {
                _cachedPath = null;
            }
            return _cachedPath;
        }
    }
}
