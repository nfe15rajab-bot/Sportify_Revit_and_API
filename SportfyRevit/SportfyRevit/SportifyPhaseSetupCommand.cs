using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Sets a project up for Sportify's phase system and its worksets. The phases are Existing (the building and the roof as they stand), Design and analysis (what an
    /// import brings) and Post analysis (the dynamic furniture Kinetics places from the analyses); the worksets are Existing Roof Base, Structure, Gardens, Sports,
    /// Furniture and Dynamic Furniture. Revit's API can neither CREATE a phase nor rename one, so this says which phases a project still needs and offers to open
    /// Manage &gt; Phasing for them; worksets it makes itself, after the usual question about worksharing.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class SetUpSportifyPhasesCommand : IExternalCommand
    {
        const string Title = "Sportify — Phases & Worksets Setup";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc == null) { TaskDialog.Show(Title, "Open a Revit project first."); return Result.Cancelled; }
                var report = new List<string>();
                var openPhasing = false;

                // ---- phases
                var s = SportifyPhases.Read(doc);
                report.Add("Phases in this project: " + string.Join(" → ", s.Phases) + ".");
                // Revit's API can neither create a phase nor rename one (found live: "This element does not support assignment of a user-specified name"): both are done once in
                // Manage > Phasing. Until then Sportify uses the project's phases by their order (first Existing, second Design and analysis, third Post analysis).
                if (s.ToRename.Count > 0 || s.Missing.Count > 0)
                {
                    var todo = new List<string>();
                    foreach (var r in s.ToRename) todo.Add("rename  " + r.Replace(" -> ", "  to  "));
                    foreach (var m in s.Missing) todo.Add("add     \"" + m + "\" after the last phase");
                    report.Add("In Manage > Phasing (Revit's API cannot do this): " + string.Join("; ", todo.Select(t => t.Replace("  ", " "))) + ". Until then Sportify uses your phases by their order" + (s.Missing.Count > 0 ? ", and what belongs in a missing phase stays in the phase it is created in" : "") + ".");
                    var open = new TaskDialog(Title)
                    {
                        MainInstruction = "Open Manage > Phasing to name the phases Sportify uses?",
                        MainContent = "Sportify's phases are Existing, Design and analysis, Post analysis (what an import brings goes to Design and analysis, the dynamic furniture to Post analysis). Revit's API can neither add a phase nor rename one, so it is done once by hand:\n\n  " + string.Join("\n  ", todo) + "\n\nIn the Phasing dialog: Project Phases > select a phase and type its name, or Insert After.",
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                    };
                    open.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the Phasing dialog", "It opens as soon as this dialog closes; run this again afterwards.");
                    open.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Not now", "Sportify keeps using the phases by their order.");
                    open.DefaultButton = TaskDialogResult.CommandLink2;
                    openPhasing = open.Show() == TaskDialogResult.CommandLink1;
                }
                else report.Add("Every Sportify phase is there.");

                // ---- worksets
                if (!doc.IsWorkshared && !doc.CanEnableWorksharing())
                {
                    report.Add("Worksets: Revit does not allow worksharing in this document (a template opened as itself, or a project that is not saved), so there are none to make.");
                }
                else
                {
                    if (!doc.IsWorkshared)
                    {
                        var ask = new TaskDialog(Title)
                        {
                            MainInstruction = "Turn worksharing on to make Sportify's worksets?",
                            MainContent = "This project is not workshared, so it has no worksets. Worksharing makes it a central-model project (it has to be saved as one) and cannot be turned off again.\n\n" +
                                          "The worksets are: " + string.Join(", ", SportifyWorksetSet.All) + ".",
                            CommonButtons = TaskDialogCommonButtons.Cancel,
                        };
                        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Turn worksharing on and make the worksets", "Cannot be undone.");
                        ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Leave the project as it is", "No worksets; Kinetics keeps its parts on the one workset.");
                        ask.DefaultButton = TaskDialogResult.CommandLink2;                 // after the links exist: Revit throws otherwise
                        if (ask.Show() == TaskDialogResult.CommandLink1)
                        {
                            doc.EnableWorksharing("Shared Levels and Grids", "Workset1");     // outside any transaction: Revit refuses it inside one
                            SportifyLog.Info("worksharing", "worksharing enabled by Phases & Worksets Setup, with the person's consent");
                        }
                    }
                    if (doc.IsWorkshared)
                    {
                        var before = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Select(w => w.Name).ToHashSet();
                        using var t = new Transaction(doc, "Sportify: make the worksets");
                        t.Start();
                        SportifyWorksetSet.Ensure(doc);
                        t.Commit();
                        var made = SportifyWorksetSet.All.Where(n => !before.Contains(n)).ToList();
                        report.Add("Worksets: " + (made.Count > 0 ? "made " + string.Join(", ", made) + (made.Count < SportifyWorksetSet.All.Length ? "; the others were there already" : "") : "all six were there already") + ".");
                    }
                    else report.Add("Worksets: none made (the project is not workshared).");
                }

                SportifyLog.Info("phases", string.Join(" | ", report));
                TaskDialog.Show(Title, string.Join("\n\n", report) + "\n\nBatch Assign Phasing (same pulldown) puts what an import already made into Design and analysis; Kinetics places its dynamic furniture in Post analysis by itself.");
                if (openPhasing)
                {
                    try { commandData.Application.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.Phases)); }
                    catch (Exception ex) { SportifyLog.Warn("phases", "the Phasing dialog could not be opened: " + ex.Message); }
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(Title, "the phases and worksets could not be set up", ex, ref message);
            }
        }
    }
}
