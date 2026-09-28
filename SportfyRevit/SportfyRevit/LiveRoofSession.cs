using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// Opens a detached copy of a real project as the ACTIVE document (unlike IfcWorksetsSelfTest, which opens a
    /// copy in the background, checks it and closes it again) and pushes one of its roofs to Sportify for real —
    /// RoofBoundaryServer.SetPayload, exactly what the "Push to Sportify" ribbon button does, so the web app's
    /// Combine tab sees real structure and entries on its next poll. The original file is never opened and never
    /// touched: a copy is made first, and (if the source is workshared) opened detached from its central model.
    ///
    /// SPORTIFY_OPEN_AND_PUSH_FILE=&lt;project.rvt&gt; and SPORTIFY_OPEN_AND_PUSH_ROOF_IFCTAG=&lt;IfcTag&gt; do this once
    /// at start-up. Revit is left open with the copy as the active document afterward — a real, interactive session,
    /// not a background check — so the ribbon and SPORTIFY_RUN_COMMAND both work on it normally from that point on.
    /// Off unless both variables are set.
    /// </summary>
    internal static class LiveRoofSession
    {
        internal static void Install(UIControlledApplication application)
        {
            var file = Environment.GetEnvironmentVariable("SPORTIFY_OPEN_AND_PUSH_FILE");
            var tag = Environment.GetEnvironmentVariable("SPORTIFY_OPEN_AND_PUSH_ROOF_IFCTAG");
            if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(tag)) return;
            void OnIdle(object? sender, IdlingEventArgs e)
            {
                application.Idling -= OnIdle;
                if (sender is not UIApplication uiApp) return;
                try { Run(uiApp, file!, tag!); }
                catch (Exception ex) { SportifyLog.Error("livesession", "SPORTIFY_OPEN_AND_PUSH_FILE failed", ex); }
            }
            application.Idling += OnIdle;
        }

        static void Run(UIApplication uiApp, string file, string tag)
        {
            if (!File.Exists(file)) { SportifyLog.Warn("livesession", "the project does not exist: " + file); return; }
            var dir = Path.Combine(Path.GetTempPath(), "sportify-live-session");
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, "live-session-copy.rvt");
            File.Copy(file, copy, true);
            SportifyLog.Info("livesession", "copied " + file + " to " + copy);

            var info = BasicFileInfo.Extract(copy);
            var options = new OpenOptions { Audit = false };
            if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;

            UIDocument uidoc;
            try { uidoc = uiApp.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(copy), options, false); }
            catch (Exception ex) { SportifyLog.Error("livesession", "the copy could not be opened", ex); return; }
            var doc = uidoc.Document;
            SportifyLog.Info("livesession", "opened the copy as the active document (workshared: " + doc.IsWorkshared + ")");

            var roof = new HashSet<ElementId>();
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                if (el.LookupParameter("IfcTag")?.AsString() == tag) roof.Add(el.Id);
            if (roof.Count == 0) { SportifyLog.Warn("livesession", "no element found with IfcTag \"" + tag + "\": nothing pushed"); return; }

            var roofEl = doc.GetElement(roof.First());
            var built = PushRoofCommandBase.Build(doc, roofEl, RoofPushScope.All, roof, out var failure);
            if (built == null) { SportifyLog.Warn("livesession", "the roof could not be pushed: " + failure); return; }
            var merged = RoofPushMerge.Merge(RoofBoundaryServer.CurrentPayload, built.Json, RoofPushScope.All, out _);
            RoofBoundaryServer.SetPayload(merged);
            SportifyLog.Info("livesession", "pushed the roof (IfcTag " + tag + ") live: " + built.LengthM + " x " + built.WidthM + " m — the web app's Combine tab will see it on its next poll");
        }
    }
}
