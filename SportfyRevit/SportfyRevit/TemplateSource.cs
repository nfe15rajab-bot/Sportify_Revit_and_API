using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Revit's own German BIM template as a source: where it is, and copying things out of it into a project (the Sportify title blocks come from it, and "Show Revit templates" brings the
    /// built-in view templates, filters and schedules back from it). The template is opened read only and closed again without saving; nothing in it is ever changed.
    /// </summary>
    internal static class TemplateSource
    {
        /// <summary>The template of a built-in set (Templates\German\BIM_Architektur_und_Ingenieurbau.rte, Templates\English\Default-Multi-Discipline_Metric.rte) in this Revit's ProgramData folder, or null if it is not installed (another language pack, a trimmed install).</summary>
        public static string? PathOf(Application app, BuiltInSet set)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.VersionNumber, "Templates", set.TemplateFile);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Revit's German BIM template: the source of the Plankopf title blocks and of the units.</summary>
        public static string? GermanTemplatePath(Application app) => PathOf(app, BuiltInSet.German);

        /// <summary>Revit's default imperial template, for the check that an imperial project is converted.</summary>
        public static string? ImperialTemplatePath(Application app)
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.VersionNumber, "Templates", "Default_I_ENU.rte");
            return File.Exists(path) ? path : null;
        }

        /// <summary>Copies the elements chosen by <paramref name="choose"/> out of the German template into <paramref name="target"/>. Call inside a transaction of the target. Returns how many were copied.</summary>
        public static int CopyFromGerman(Document target, Func<Document, ICollection<ElementId>> choose, List<string> notes) => CopyFrom(target, BuiltInSet.German, choose, notes);

        /// <summary>Copies the elements chosen by <paramref name="choose"/> out of a built-in set's template into <paramref name="target"/>. Call inside a transaction of the target. Returns how many were copied.</summary>
        public static int CopyFrom(Document target, BuiltInSet set, Func<Document, ICollection<ElementId>> choose, List<string> notes)
        {
            var path = PathOf(target.Application, set);
            if (path == null) { notes.Add("Revit's " + set.Name + " template (" + set.TemplateFile + ") is not installed on this machine."); return 0; }

            Document? source = null;
            try
            {
                source = target.Application.OpenDocumentFile(path);
                var ids = choose(source);
                if (ids.Count == 0) return 0;
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new UseDestinationTypes());
                var copied = ElementTransformUtils.CopyElements(source, ids, target, Transform.Identity, options);
                return copied?.Count ?? 0;
            }
            catch (Exception ex)
            {
                notes.Add("Copying from Revit's " + set.Name + " template failed: " + ex.Message.Split('\n')[0]);
                SportifyLog.Warn("templates", "copy from the " + set.Name + " template failed: " + ex.Message);
                return 0;
            }
            finally { try { source?.Close(false); } catch (Exception) { /* already closed */ } }
        }

        /// <summary>When a type of the same name is already in the project, the project's is kept (nothing of the user's is replaced by the template's).</summary>
        private sealed class UseDestinationTypes : IDuplicateTypeNamesHandler
        {
            public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) => DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
