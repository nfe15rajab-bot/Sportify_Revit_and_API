using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    // The Templates part of the BIM & Documentation panel: "Apply Sportify Template" (asks which language) and, in the Templates drop-down, the same in German or in English, and Hide and Show
    // of Revit's built-in templates. Like the other BIM commands: one transaction that is rolled back if anything fails, a TaskDialog that says what will happen (and asks, because these change a
    // lot at once) and what did happen, the same in the add-in's log, and no exception that reaches Revit.

    internal static class TemplateDialogs
    {
        /// <summary>Yes/No with No as the default. True for Yes.</summary>
        public static bool Confirm(string title, string instruction, string content)
        {
            var dialog = new TaskDialog(title) { MainInstruction = instruction, MainContent = content, CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No, DefaultButton = TaskDialogResult.No };
            return dialog.Show() == TaskDialogResult.Yes;
        }

        /// <summary>Which language: two links (German first, the default) and Cancel. Null when cancelled.</summary>
        public static TemplateLanguage? ChooseLanguage(string title, string instruction, string content)
        {
            var dialog = new TaskDialog(title) { MainInstruction = instruction, MainContent = content, CommonButtons = TaskDialogCommonButtons.Cancel };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Deutsch", "Leistungsphasen 1-4, Begriffe wie im deutschen BIM-Template (Dachaufsicht, AVA-Listen, DIN 277, DIN 276)");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "English", "The same structure with English names (Roof Plan, QTO lists); the DIN norms keep their numbers");
            dialog.DefaultButton = TaskDialogResult.CommandLink1;       // after the links exist: Revit throws otherwise
            return dialog.Show() switch { TaskDialogResult.CommandLink1 => TemplateLanguage.De, TaskDialogResult.CommandLink2 => TemplateLanguage.En, _ => null };
        }

        public static string Lines(IEnumerable<string> items, int max)
        {
            var list = items.ToList();
            return string.Join("\n", list.Take(max).Select(i => "• " + i)) + (list.Count > max ? $"\n… and {list.Count - max} more" : "");
        }
    }

    /// <summary>The work of the three Apply commands: they differ only in whether the language is asked or given.</summary>
    internal static class ApplyTemplate
    {
        public static Result Run(ExternalCommandData commandData, ref string message, TemplateLanguage? given)
        {
            const string title = "Sportify — Apply Sportify Template";
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                var found = SportifyElementScan.Find(doc);
                var set = SportifyTemplateSpec.For(given ?? TemplateLanguage.De);
                var content = found.IsEmpty
                    ? "This project holds no Sportify layout yet, so only the templates, filters, schedules and the list sheets are made now; run this again after an import for the views and the plan sheets."
                    : $"It also makes the views of the {found.Elements.Count} Sportify element(s) here (roof plan, pieces by kind, zones by build-up, circulation, axonometric), their sheets, and writes the DIN 277 / DIN 276 classes onto them.";
                var units = TemplateUnits.IsImperial(doc) ? $"\n\nThis project uses imperial units ({TemplateUnits.LengthUnitLabel(doc)}): they are converted to the German template's (meters, m², m³, degrees). The model itself does not move." : "";
                var body = $"Makes {set.ViewTemplates.Count} view templates (HOAI Leistungsphasen 1-4 and the Sportify views), their filters, {set.Schedules.Count} schedules (AVA/QTO, DIN 277, DIN 276) and {set.Sheets.Count} sheets on the German Plankopf. " +
                           content + units + "\n\nNothing of yours is changed or deleted; running it again updates what it made.";

                TemplateLanguage language;
                if (given == null)
                {
                    var chosen = TemplateDialogs.ChooseLanguage(title, $"Apply the Sportify template to \"{doc.Title}\" in which language?", body);
                    if (chosen == null) return Result.Cancelled;
                    language = chosen.Value;
                }
                else
                {
                    if (!TemplateDialogs.Confirm(title, $"Apply the Sportify template ({(given == TemplateLanguage.De ? "Deutsch" : "English")}) to \"{doc.Title}\"?", body)) return Result.Cancelled;
                    language = given.Value;
                }

                TemplateResult result;
                using (var t = new Transaction(doc, "Sportify: apply template"))
                {
                    t.Start();
                    result = SportifyTemplateBuilder.Apply(doc, language);
                    t.Commit();
                }

                var text = $"{result.Made.Count} item(s) made or updated:\n" + TemplateDialogs.Lines(result.Made, 30);
                if (result.Notes.Count > 0) text += "\n\nNote:\n" + TemplateDialogs.Lines(result.Notes, 12);
                TaskDialog.Show(title, text);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "the Sportify template could not be applied", ex, ref message);
            }
        }
    }

    /// <summary>Makes the Sportify templates in this project (view templates by Leistungsphase, filters, schedules, sheets on the German Plankopf, DIN 277 / DIN 276 classes), asking which language.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ApplySportifyTemplateCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => ApplyTemplate.Run(commandData, ref message, null);
    }

    /// <summary>The Sportify template with the German names (HOAI Leistungsphasen, AVA lists).</summary>
    [Transaction(TransactionMode.Manual)]
    public class ApplySportifyTemplateDeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => ApplyTemplate.Run(commandData, ref message, TemplateLanguage.De);
    }

    /// <summary>The Sportify template with English names.</summary>
    [Transaction(TransactionMode.Manual)]
    public class ApplySportifyTemplateEnCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) => ApplyTemplate.Run(commandData, ref message, TemplateLanguage.En);
    }

    /// <summary>Removes Revit's built-in view templates, views, sheets, schedules and filters (the German and the English template's) that nothing of the user's uses (they can be brought back).</summary>
    [Transaction(TransactionMode.Manual)]
    public class HideRevitTemplatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Hide Revit Templates";
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                var plan = BuiltInTemplates.PlanHide(doc);
                if (plan.Total == 0) { TaskDialog.Show(title, "Revit's built-in templates are not in this project (or your own work uses them all): nothing to hide."); return Result.Succeeded; }
                if (!TemplateDialogs.Confirm(title, $"Hide {plan.Total} of Revit's built-in items?",
                        $"Removes {plan.Describe()} that came with Revit's own templates and that nothing of yours uses. \"Show Revit Templates\" brings the view templates, filters and schedules back from the template file."))
                    return Result.Cancelled;

                var notes = new List<string>();
                BuiltInPlan done;
                using (var t = new Transaction(doc, "Sportify: hide Revit templates"))
                {
                    t.Start();
                    done = BuiltInTemplates.Hide(doc, notes);
                    t.Commit();
                }
                TaskDialog.Show(title, $"Hidden: {done.Describe()}." + (notes.Count > 0 ? "\n\nNote:\n" + TemplateDialogs.Lines(notes, 8) : ""));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "Revit's templates could not be hidden", ex, ref message);
            }
        }
    }

    /// <summary>Copies Revit's built-in view templates, filters and schedules back from the template the project came from (German BIM or English multi-discipline).</summary>
    [Transaction(TransactionMode.Manual)]
    public class ShowRevitTemplatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Show Revit Templates";
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(title, "Open a Revit project first."); return Result.Cancelled; }

                // the template the project came from: the language of its Sportify templates says it; if it has none, ask
                var templateNames = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name).ToList();
                var language = SportifyTemplateSpec.Detect(templateNames);
                if (language == null)
                {
                    language = TemplateDialogs.ChooseLanguage(title, "Which of Revit's templates should come back?", "Deutsch: the German BIM template (Architektur und Ingenieurbau). English: the multi-discipline template.");
                    if (language == null) return Result.Cancelled;
                }
                var set = BuiltInSet.For(language.Value);

                var (viewTemplates, filters, schedules) = BuiltInTemplates.PlanShow(doc, set);
                if (viewTemplates + filters + schedules == 0) { TaskDialog.Show(title, $"Revit's built-in view templates, filters and schedules ({set.Name} template) are all in this project already."); return Result.Succeeded; }
                if (!TemplateDialogs.Confirm(title, "Bring Revit's built-in templates back?",
                        $"Copies {viewTemplates} view templates, {filters} filters and {schedules} schedules out of Revit's {set.Name} template into this project (what is already here, by name, is kept as it is). \"Hide Revit Templates\" removes them again."))
                    return Result.Cancelled;

                var notes = new List<string>();
                int copied;
                using (var t = new Transaction(doc, "Sportify: show Revit templates"))
                {
                    t.Start();
                    copied = BuiltInTemplates.Show(doc, notes, set);
                    t.Commit();
                }
                TaskDialog.Show(title, $"{copied} element(s) copied from Revit's {set.Name} template." + (notes.Count > 0 ? "\n\nNote:\n" + TemplateDialogs.Lines(notes, 8) : ""));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(title, "Revit's templates could not be shown", ex, ref message);
            }
        }
    }
}
