using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Finds Revit's "Generic Model" family template, whatever language this Revit is in, and never stops an import to ask about it.
    ///
    /// What every generator needs is a template whose category is Generic Models. It used to probe four English paths and, when none existed,
    /// open a file dialog in the middle of the import transaction (Auto Import included). On a German, French, Spanish... Revit the template
    /// has another name in another folder, so generation could not work there at all, and cancelling the dialog switched it off for the session.
    ///
    /// Now: (1) the choice remembered from an earlier session (%APPDATA%\Sportify\family-template.txt), (2) a search of the template folders
    /// Revit itself reports (Application.FamilyTemplatePath, the RVT year's "Family Templates" folder, the old English paths), candidates
    /// ranked by file name in any language (FamilyTemplateRanking) and CONFIRMED by opening them and reading the family category, which is
    /// what does not depend on language, (3) only when a person is at the machine (a manual import) and nothing was found: one dialog, before
    /// the import starts, whose answer is remembered. All of it happens in FamilyPreparation, before any transaction; the generators only
    /// call Resolve(), which returns what was found and never shows anything.
    /// </summary>
    internal static class GenericFamilyTemplateLocator
    {
        private static readonly string[] OldEnglishPaths =
        {
            @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English\Metric Generic Model.rft",
            @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English-Imperial\Metric Generic Model.rft",
            @"C:\ProgramData\Autodesk\RVT 2026\Family Templates\English\Metric Generic Model.rft",
            @"C:\ProgramData\Autodesk\RVT 2024\Family Templates\English\Metric Generic Model.rft",
        };

        private static bool _searched;
        private static bool _dialogDeclined;
        private static string? _path;

        /// <summary>Why there is no template, in words a person can act on; null when one was found.</summary>
        internal static string? FailureReason { get; private set; } = "the search for Revit's Generic Model family template has not run";

        /// <summary>The template the generators build from, or null. Shows nothing, searches nothing: <see cref="Prepare"/> did.</summary>
        public static string? Resolve() => _path != null && File.Exists(_path) ? _path : null;

        private static string SavedChoiceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "family-template.txt");

        /// <summary>
        /// Finds the template (once per session unless it was lost) and returns its path, or null with <see cref="FailureReason"/>.
        /// Call it before the import transaction opens.
        /// </summary>
        public static string? Prepare(Application app, bool allowDialog)
        {
            if (Resolve() != null) return _path;

            if (!_searched)
            {
                _searched = true;
                _path = FromSavedChoice(app) ?? FromSearch(app);
                if (_path != null) Remember(_path);
            }
            if (_path == null && allowDialog && !_dialogDeclined) _path = FromDialog(app);

            FailureReason = _path != null ? null :
                "Revit's Generic Model family template was not found in this Revit's template folders" +
                (allowDialog ? (_dialogDeclined ? " and no template was chosen" : "") : " (choose one with a manual Import Configuration once and it is remembered)");
            SportifyLog.Info("family-template", _path != null ? "template: " + _path : "no template: " + FailureReason);
            return _path;
        }

        private static string? FromSavedChoice(Application app)
        {
            try
            {
                if (!File.Exists(SavedChoiceFile)) return null;
                var saved = File.ReadAllText(SavedChoiceFile).Trim();
                return File.Exists(saved) && IsGenericModelTemplate(app, saved) ? saved : null;
            }
            catch (Exception ex) { SportifyLog.Warn("family-template", "the remembered template could not be used: " + ex.Message); return null; }
        }

        private static string? FromSearch(Application app)
        {
            var roots = new List<string>();
            void AddRoot(string? dir) { if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) && !roots.Contains(dir, StringComparer.OrdinalIgnoreCase)) roots.Add(dir); }
            try { AddRoot(app.FamilyTemplatePath); } catch (Exception) { /* not every Revit reports it */ }
            try { if (!string.IsNullOrWhiteSpace(app.FamilyTemplatePath)) AddRoot(Path.GetDirectoryName(app.FamilyTemplatePath.TrimEnd('\\', '/'))); } catch (Exception) { }
            AddRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "RVT " + app.VersionNumber, "Family Templates"));

            var files = new List<string>();
            foreach (var root in roots)
            {
                try { files.AddRange(Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories)); }
                catch (Exception ex) { SportifyLog.Warn("family-template", "could not list " + root + ": " + ex.Message); }
            }
            files.AddRange(OldEnglishPaths.Where(File.Exists));

            var ranked = FamilyTemplateRanking.Rank(files.Distinct(StringComparer.OrdinalIgnoreCase));
            SportifyLog.Info("family-template", $"{files.Count} template file(s) in {roots.Count} folder(s), {ranked.Count} look like the Generic Model template");
            // Only the best few are opened: the file name only suggests, the category confirms.
            foreach (var candidate in ranked.Take(6))
                if (IsGenericModelTemplate(app, candidate)) return candidate;
            return null;
        }

        private static string? FromDialog(Application app)
        {
            try
            {
                var ask = new TaskDialog("Sportify")
                {
                    MainInstruction = "Sportify needs Revit's Generic Model family template",
                    MainContent = "It builds the families for the pieces of a layout from it, and could not find it in this Revit's template folders. " +
                                  "Choose the file (a .rft called Generic Model in your language, for example \"Metric Generic Model.rft\" or \"Allgemeines Modell.rft\"). " +
                                  "Your choice is remembered. Without it, pieces become placeholder boxes.",
                    CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                    DefaultButton = TaskDialogResult.Ok,
                };
                if (ask.Show() != TaskDialogResult.Ok) { _dialogDeclined = true; return null; }

                var fod = new FileOpenDialog("Revit Family Template (*.rft)|*.rft") { Title = "Locate Revit's Generic Model family template" };
                if (fod.Show() != ItemSelectionDialogResult.Confirmed) { _dialogDeclined = true; return null; }
                var chosen = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
                if (IsGenericModelTemplate(app, chosen)) { Remember(chosen); return chosen; }

                TaskDialog.Show("Sportify", "That file is not a Generic Model template (its category is not Generic Models), so it cannot be used.");
                _dialogDeclined = true;
                return null;
            }
            catch (Exception ex)
            {
                SportifyLog.Error("family-template", "the template dialog failed", ex);
                _dialogDeclined = true;
                return null;
            }
        }

        /// <summary>The category is what makes a template the Generic Model one, in every language.</summary>
        private static bool IsGenericModelTemplate(Application app, string path)
        {
            Document? familyDoc = null;
            try
            {
                familyDoc = app.NewFamilyDocument(path);
                var category = familyDoc.OwnerFamily?.FamilyCategory;
                return category != null && category.Id.IntegerValue == (int)BuiltInCategory.OST_GenericModel;
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("family-template", "cannot use " + path + ": " + ex.Message);
                return false;
            }
            finally
            {
                try { familyDoc?.Close(false); } catch (Exception) { /* nothing to save */ }
            }
        }

        private static void Remember(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavedChoiceFile)!);
                File.WriteAllText(SavedChoiceFile, path);
            }
            catch (Exception ex) { SportifyLog.Warn("family-template", "could not remember the template: " + ex.Message); }
        }
    }
}
