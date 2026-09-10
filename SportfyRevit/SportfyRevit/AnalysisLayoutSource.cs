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
        public static SportifyLayout? GetLayout(string analysisTitle)
        {
            string dialogTitle = "Sportify — " + analysisTitle;
            string? json;

            if (RoofBoundaryServer.TryGetLatestCombinedLayout(out var liveJson, out _) && liveJson != null)
            {
                json = liveJson;
            }
            else
            {
                var fod = new FileOpenDialog("Sportify layout JSON (*.json)|*.json");
                fod.Title = "No live data from the web app yet — select a Combine export instead";
                if (fod.Show() != ItemSelectionDialogResult.Confirmed)
                    return null;

                string path;
                try
                {
                    path = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
                }
                catch (Exception ex)
                {
                    TaskDialog.Show(dialogTitle, "Couldn't resolve the selected file: " + ex.Message);
                    return null;
                }

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
