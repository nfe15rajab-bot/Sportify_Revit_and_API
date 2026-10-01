using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Shared "get me the current layout" step for every read-only Analysis
    /// command — same live-then-file-picker fallback SetSunAndLocationCommand
    /// already uses, so Analysis works whether or not a live sync exists yet.
    /// Every failure path shows its own explanation and returns null;
    /// callers just bail out (Result.Cancelled) when this does.
    /// </summary>
    internal static class AnalysisLayoutSource
    {
        public static SportifyLayout? GetLayout(string analysisTitle, Document? doc = null)
        {
            string dialogTitle = "Sportify — " + analysisTitle;
            string? json;

            // the web app's, else what was last imported into this project (ProjectLayout); a file only when the project has none: the newest export in the
            // Layouts folder is not necessarily this roof's (2026-10-01: the High Roof garden's was offered for the Low Roof)
            if (ProjectLayout.TryGet(doc, out var liveJson, out _) && liveJson != null)
            {
                json = liveJson;
            }
            else
            {
                var path = LayoutFilePicker.Pick(LayoutFilePicker.CurrentRevitWindow(), analysisTitle, "No live data from the web app yet — select a Combine export instead");
                if (path == null)
                    return null;

                try
                {
                    json = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(dialogTitle, "Couldn't read that JSON file: " + ex.Message);
                    return null;
                }
            }

            try
            {
                return JsonSerializer.Deserialize<SportifyLayout>(json);
            }
            catch (Exception ex)
            {
                TaskDialog.Show(dialogTitle, "Couldn't parse that Combine export: " + ex.Message);
                return null;
            }
        }
    }
}
