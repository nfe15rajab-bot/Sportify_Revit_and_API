using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Makes Sportify_DE.rte and Sportify_EN.rte: a new project from Revit's German BIM template (German) or its English multi-discipline template (English), with the Sportify templates applied
    /// (SportifyTemplateBuilder, without content: there is no model yet) and Revit's built-in ones hidden (BuiltInTemplates.Hide), saved as a template file. A project started from one has the
    /// Sportify view templates, filters, schedules and list sheets and nothing else to look at; "Show Revit templates" brings the built-in ones back. Run unattended with
    /// SPORTIFY_BUILD_TEMPLATE=&lt;folder&gt; (see SportfyRevitApp), which also checks the round trip and that an imperial project is converted.
    /// </summary>
    internal static class SportifyTemplateFile
    {
        public static string FileName(TemplateLanguage language) => language == TemplateLanguage.De ? "Sportify_DE.rte" : "Sportify_EN.rte";

        public static string Build(Application app, string outputPath, TemplateLanguage language)
        {
            var baseTemplate = TemplateSource.PathOf(app, BuiltInSet.For(language)) ?? throw new InvalidOperationException("Revit's " + BuiltInSet.For(language).Name + " template (" + BuiltInSet.For(language).TemplateFile + ") is not installed.");
            var doc = app.NewProjectDocument(baseTemplate);
            try
            {
                TemplateResult applied;
                BuiltInPlan hidden;
                var notes = new List<string>();
                using (var t = new Transaction(doc, "Sportify template"))
                {
                    t.Start();
                    applied = SportifyTemplateBuilder.Apply(doc, language, withContent: false);
                    hidden = BuiltInTemplates.Hide(doc, notes);
                    t.Commit();
                }
                SportifyLog.Info("templates", $"{FileName(language)}: {applied.Made.Count} item(s) made, hidden {hidden.Describe()}; {applied.Notes.Count + notes.Count} note(s)");
                foreach (var n in applied.Notes.Concat(notes)) SportifyLog.Info("templates", "  note: " + n);

                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                doc.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = true });
                SportifyLog.Info("templates", "template file written: " + outputPath);
            }
            finally { try { doc.Close(false); } catch (Exception) { /* already closed */ } }
            try { Verify(app, outputPath, language); }
            catch (Exception ex) { SportifyLog.Error("templates", "the round trip check of " + outputPath + " failed", ex); }
            return outputPath;
        }

        /// <summary>
        /// The round trip, on a real document: a new project from the template file shows what a user starts with; Show brings Revit's built-in view templates, filters and schedules back from the
        /// template; Hide removes them again. The counts of each step go to the log, so a template that does not hide, or a Show that brings nothing, is seen at once.
        /// </summary>
        public static void Verify(Application app, string templatePath, TemplateLanguage language)
        {
            var doc = app.NewProjectDocument(templatePath);
            try
            {
                string Counts()
                {
                    var c = (Dictionary<string, int>)TemplateInspector.Inspect(doc)["counts"]!;
                    return $"{c["viewTemplates"]} view templates, {c["views"]} views, {c["sheets"]} sheets, {c["schedules"]} schedules, {c["filters"]} filters";
                }
                var tag = "verify " + language + ": ";
                SportifyLog.Info("templates", tag + "a new project from the template starts with " + Counts());
                var notes = new List<string>();
                using (var t = new Transaction(doc, "Show Revit templates")) { t.Start(); var n = BuiltInTemplates.Show(doc, notes, BuiltInSet.For(language)); t.Commit(); SportifyLog.Info("templates", tag + "Show copied " + n + " element(s); now " + Counts()); }
                using (var t = new Transaction(doc, "Hide Revit templates")) { t.Start(); var done = BuiltInTemplates.Hide(doc, notes); t.Commit(); SportifyLog.Info("templates", tag + "Hide removed " + done.Describe() + "; now " + Counts()); }
                foreach (var n in notes.Take(10)) SportifyLog.Info("templates", "  " + tag + "note: " + n);
            }
            finally { try { doc.Close(false); } catch (Exception) { /* already closed */ } }
        }

        /// <summary>
        /// An imperial project (Revit's imperial default template) with the Sportify template applied: the length unit before and after, and the other units the conversion set, go to the log.
        /// </summary>
        public static void VerifyImperial(Application app, TemplateLanguage language)
        {
            var path = TemplateSource.ImperialTemplatePath(app);
            if (path == null) { SportifyLog.Warn("templates", "verify imperial: Revit's imperial template (Default_I_ENU.rte) is not installed"); return; }
            var doc = app.NewProjectDocument(path);
            try
            {
                string Units()
                {
                    var u = doc.GetUnits();
                    string L(ForgeTypeId spec) { try { return LabelUtils.GetLabelForUnit(u.GetFormatOptions(spec).GetUnitTypeId()); } catch (Exception) { return "?"; } }
                    return $"length {L(SpecTypeId.Length)}, area {L(SpecTypeId.Area)}, volume {L(SpecTypeId.Volume)}, angle {L(SpecTypeId.Angle)}";
                }
                var before = Units();
                var imperial = TemplateUnits.IsImperial(doc);
                TemplateResult result;
                using (var t = new Transaction(doc, "Sportify template on an imperial project")) { t.Start(); result = SportifyTemplateBuilder.Apply(doc, language, withContent: false); t.Commit(); }
                SportifyLog.Info("templates", $"verify imperial: project from Default_I_ENU.rte (imperial: {imperial}): {before}");
                SportifyLog.Info("templates", $"verify imperial: after the Sportify template ({language}, {result.Made.Count} item(s), {result.Notes.Count} note(s)): {Units()}; imperial now: {TemplateUnits.IsImperial(doc)}");
                foreach (var n in result.Notes.Take(10)) SportifyLog.Info("templates", "  verify imperial note: " + n);
            }
            finally { try { doc.Close(false); } catch (Exception) { /* already closed */ } }
        }
    }
}
