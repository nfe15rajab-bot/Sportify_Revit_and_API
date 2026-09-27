using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// The "Getting started" button: the checklist of the user guide's walk-through (GettingStarted), with each step marked by what is true now and the next one offered as a link that
    /// does it. It opens when the person asks, never by itself, and a step that holds is remembered for the next session. A task dialog on purpose: it runs in Revit's API context
    /// (the project's import ledger can be read there), needs no window of its own, and shows again, freshly worked out, each time the button is clicked.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class GettingStartedCommand : IExternalCommand
    {
        const string Title = "Sportify — Getting started";
        const string Rule = "Decide in the web app (2D, quick estimates); build in Revit (the model, the full analyses, the documents). The pill at the top right of the web app says whether Revit is open. " +
                            "Everything Sportify makes is in your Sportify folder, and the user guide is in the Start menu under Sportify.";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                var uiApp = commandData.Application;
                var doc = uiApp.ActiveUIDocument?.Document;

                // "Start again" forgets what was remembered and shows the list once more; anything else ends it
                for (var round = 0; round < 3; round++)
                {
                    var rows = GettingStarted.Rows(Gather(doc), GettingStarted.Remembered());
                    GettingStarted.Remember(rows.Where(r => r.State == StepState.Done).Select(r => r.Step.Id));
                    var next = GettingStarted.NextRow(rows);
                    var canRestart = GettingStarted.Remembered().Count > 0;

                    var dialog = new TaskDialog(Title)
                    {
                        MainInstruction = GettingStarted.Headline(rows),
                        MainContent = GettingStarted.Checklist(rows) + "\n\n" + GettingStarted.Hint(rows),
                        ExpandedContent = Rule,
                        CommonButtons = TaskDialogCommonButtons.Close,
                        DefaultButton = TaskDialogResult.Close,
                    };
                    // the links are numbered from 1 without a gap: the step's own action first when it has one, then "start again"
                    var hasAction = next?.Step.Action != null;
                    if (hasAction)
                        dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, next!.Step.ActionText ?? next.Step.Title, "Does step " + (rows.ToList().IndexOf(next) + 1) + " for you.");
                    var restartLink = hasAction ? TaskDialogCommandLinkId.CommandLink2 : TaskDialogCommandLinkId.CommandLink1;
                    if (canRestart)
                        dialog.AddCommandLink(restartLink, "Start the list again", "Forgets which steps were done before, on this computer. The steps that hold now show as done again.");

                    var result = dialog.Show();
                    if (hasAction && result == TaskDialogResult.CommandLink1)
                    {
                        Run(uiApp, next!.Step.Action!, next.Step.ActionText ?? next.Step.Title);
                        return Result.Succeeded;
                    }
                    var restartResult = restartLink == TaskDialogCommandLinkId.CommandLink1 ? TaskDialogResult.CommandLink1 : TaskDialogResult.CommandLink2;
                    if (!canRestart || result != restartResult) return Result.Succeeded;
                    GettingStarted.Forget();
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("getting-started", "the checklist could not be shown: " + ex);
                message = "The checklist could not be shown: " + ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>What is true now. Only the import ledger needs the project; everything else the add-in knows without asking Revit.</summary>
        static GettingStartedFacts Gather(Document? doc)
        {
            var built = false;
            if (doc != null)
            {
                try { built = ImportLedger.ReadElements(doc).Count > 0; }
                catch (Exception ex) { SportifyLog.Warn("getting-started", "the import ledger could not be read: " + ex.Message); }
            }
            var analyses = RoofBoundaryServer.TryGetLatestAnalysisResults(out var results) && !string.IsNullOrWhiteSpace(results);
            return new GettingStartedFacts(RoofBoundaryServer.WebAppSeen, RoofBoundaryServer.CurrentPayload != null, RoofBoundaryServer.CurrentLayoutId != null, built, analyses, GettingStarted.DocumentsMade());
        }

        /// <summary>Does what a step's link says: opens the pane or the folder itself, and posts any other ribbon button's command (the same way SPORTIFY_RUN_COMMAND does). When Revit cannot take it, says which button to click.</summary>
        static void Run(UIApplication uiApp, string internalName, string text)
        {
            try
            {
                if (internalName == "OpenSportifyApp")
                {
                    var pane = uiApp.GetDockablePane(SportifyDockablePaneProvider.PaneId);
                    if (!pane.IsShown()) pane.Show();
                    return;
                }
                if (internalName == "OpenSportifyFolder")
                {
                    Process.Start(new ProcessStartInfo(SportifyWorkspace.EnsureCreated()) { UseShellExecute = true });
                    return;
                }
                var id = SportfyRevitApp.CommandIdFor(internalName);
                if (id != null && uiApp.CanPostCommand(id)) { uiApp.PostCommand(id); return; }
            }
            catch (Exception ex) { SportifyLog.Warn("getting-started", internalName + " could not be run from the checklist: " + ex.Message); }
            TaskDialog.Show(Title, "Revit could not start this from here. Click \"" + text + "\" on the Sportify tab of the ribbon.");
        }
    }
}
