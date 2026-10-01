using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// A Sportify project always ends up with every workset open (user, 2026-10-01: "all the worksets should be open by default"). Which worksets open is
    /// decided by the file as it is opened: a local whose default is "Specify" asks every time, and the dialog's OK on its preselection kept most of them
    /// closed (the team's model showed the bare slabs: no building, no pieces). The Revit API cannot open a workset in an open project (only OpenOptions
    /// can, at opening), so when a workshared project with Sportify worksets opens with any of its worksets closed, it is closed and opened again at once
    /// with all of them, and the person is told. Once per file per Revit session: closing some again after that is the person's choice. A project changed
    /// before it could be closed (an import that ran first) is left open and the person is told where to open them (Collaborate > Worksets).
    /// </summary>
    internal static class WorksetsOnOpen
    {
        static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);
        static string? _path;
        static List<string> _closed = new();
        static int _step;
        static DateTime _since;

        internal static void Register(UIControlledApplication app)
        {
            app.ControlledApplication.DocumentOpened += OnOpened;
            app.Idling += OnIdling;
        }

        static void OnOpened(object? sender, DocumentOpenedEventArgs e)
        {
            try
            {
                var doc = e.Document;
                if (doc == null || doc.IsFamilyDocument || doc.IsLinked || !doc.IsWorkshared || doc.IsDetached || _path != null) return;
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path) || Done.Contains(path)) return;
                var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToList();
                if (!worksets.Any(w => w.Name.StartsWith("Sportify", StringComparison.Ordinal))) return;     // not a Sportify project: left as opened
                var closed = worksets.Where(w => !w.IsOpen).Select(w => w.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
                if (closed.Count == 0) return;
                Done.Add(path);
                _path = path; _closed = closed; _step = 0; _since = DateTime.UtcNow;
                SportifyLog.Info("worksets", $"\"{doc.Title}\" opened with {closed.Count} of {worksets.Count} workset(s) closed ({string.Join(", ", closed)}): it is opened again with all of them");
            }
            catch (Exception ex) { SportifyLog.Warn("worksets", "the worksets of the opened project could not be checked: " + ex.Message); }
        }

        static void OnIdling(object? sender, IdlingEventArgs e)
        {
            if (_path == null || sender is not UIApplication uiApp) return;
            e.SetRaiseWithoutDelay();
            try
            {
                var open = uiApp.Application.Documents.Cast<Document>().FirstOrDefault(d => !d.IsLinked && string.Equals(d.PathName, _path, StringComparison.OrdinalIgnoreCase));
                if (_step == 0)
                {
                    if (open == null) { _step = 2; return; }
                    if (!string.Equals(uiApp.ActiveUIDocument?.Document?.PathName, _path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (DateTime.UtcNow - _since > TimeSpan.FromSeconds(30)) GiveUp("it did not become the active project");
                        return;
                    }
                    if (open.IsModified) { GiveUp("it was changed before it could be closed"); return; }
                    uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Close));
                    _step = 1; _since = DateTime.UtcNow;
                    return;
                }
                if (_step == 1)
                {
                    if (open != null)
                    {
                        if (DateTime.UtcNow - _since > TimeSpan.FromSeconds(60)) GiveUp("it was not closed");
                        return;
                    }
                    _step = 2;
                }
                var path = _path;
                var closed = _closed;
                _path = null;
                var options = new OpenOptions();
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
                uiApp.OpenAndActivateDocument(ModelPathUtils.ConvertUserVisiblePathToModelPath(path), options, false);
                SportifyLog.Info("worksets", $"opened again with every workset: {path}");
                TaskDialog.Show("Sportify — Worksets",
                    $"The project opened with {closed.Count} workset(s) closed, so their elements were not loaded and no view could show them:\n\n{string.Join(", ", closed)}\n\n" +
                    "Sportify closed it and opened it again with every workset open. (Revit asks which worksets to open when the file's default is \"Specify\": choose All there to skip this.)");
            }
            catch (Exception ex)
            {
                GiveUp(ex.Message);
            }
        }

        static void GiveUp(string why)
        {
            var closed = _closed;
            _path = null;
            SportifyLog.Warn("worksets", "the project was not opened again with every workset: " + why);
            try
            {
                TaskDialog.Show("Sportify — Worksets",
                    $"{closed.Count} workset(s) of this project are closed, so their elements are not loaded and no view can show them:\n\n{string.Join(", ", closed)}\n\n" +
                    "Open them in Collaborate > Worksets (select them, then Open), or close the project and open it again with Worksets: All.");
            }
            catch (Exception) { /* told in the log */ }
        }
    }
}
