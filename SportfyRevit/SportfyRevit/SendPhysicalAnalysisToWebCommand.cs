using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// One button that sends the physical analyses to the web app: wind and erosion, rain and soil percolation, static loads, dynamic analysis, sun and shade,
    /// run on the layout the web app pushed (Combine tab) and published to its Results tab in one go. No window asks anything first: what the designer already
    /// decided in this Revit session (or in the app's Structure inputs and Site conditions tabs) is used, and whatever is still unconfirmed shows up as PRELIMINARY in the results, as it does when the
    /// commands are run one by one. A summary says what was sent and offers to review the inputs (then it sends again).
    ///
    /// The ball trajectories and the videos are Unity's, and stay with their own commands in this panel (a recording made for exactly the numbers sent again stays
    /// with them: PhysicalAnalysisBatch). Nothing in the Revit model is touched.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SendPhysicalAnalysisToWebCommand : IExternalCommand
    {
        const string DialogTitle = "Sportify — Send Physical Analysis to the Web App";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var baseJson, out _);
            if (baseJson == null)
            {
                message = "No layout to analyse yet. Push or import a layout from the Sportify web app first (Combine tab).";
                return Result.Failed;
            }

            var review = false;
            while (true)
            {
                string layoutJson;
                if (review)
                {
                    // "Review the inputs": one window for every input of every analysis
                    var prepared = AnalysisAssumptionsDialog.Prepare(baseJson, AnalysisAssumptionsPatcher.AllAnalyses, DialogTitle, commandData.Application.MainWindowHandle, true);
                    if (prepared.Cancelled) return Result.Cancelled;
                    layoutJson = prepared.Json;
                }
                else layoutJson = AssumptionsSession.ApplyRemembered(baseJson);

                var sent = PhysicalAnalysisBatch.Run(layoutJson);
                var unconfirmed = AnalysisAssumptionsPatcher.AnyUnconfirmed(AnalysisAssumptionsPatcher.Read(layoutJson, AnalysisAssumptionsPatcher.AllAnalyses));

                var choice = ShowSummary(sent, unconfirmed);
                if (choice == Choice.Pdf)
                {
                    AnalysisMedia.ExportPdf(DialogTitle, layoutJson, null, AnalysisMedia.ProjectTitle(commandData));
                    return Result.Succeeded;
                }
                if (choice != Choice.Review) return sent.Any(s => s.Sent) ? Result.Succeeded : Result.Failed;
                review = true;
            }
        }

        enum Choice { Done, Pdf, Review }

        static Choice ShowSummary(List<SentAnalysis> sent, bool unconfirmed)
        {
            var ok = sent.Where(s => s.Sent).ToList();
            var body = new StringBuilder();
            foreach (var s in sent)
            {
                if (s.Sent)
                {
                    body.AppendLine($"  ✓ {s.Title}: {s.Headline}{(s.Preliminary ? " (PRELIMINARY)" : "")}");
                    if (s.VideoKept) body.AppendLine("      the earlier 3D video still fits these numbers and stays with them");
                    else if (s.VideoDropped) body.AppendLine("      an earlier 3D video showed other numbers and was taken off: run this analysis from its own button to film the new ones");
                }
                else body.AppendLine($"  ✗ {s.Title}: {s.Problem}");
            }
            body.AppendLine();
            body.AppendLine("Not included: the ball trajectories and the 3D videos need Unity; they have their own buttons in this panel. Without Unity, the charts of these results can be saved as a PDF.");
            if (ok.Count > 0)
                body.AppendLine("In the web app they are under the Results tab (Garden, Structure, Sun).");
            if (unconfirmed)
                body.AppendLine().AppendLine("Some of the values behind them are still unconfirmed (deck capacity, snow zone, orientation ...). They are marked PRELIMINARY in the web app until they are entered or accepted.");

            var dialog = new TaskDialog(DialogTitle)
            {
                MainInstruction = ok.Count == 0 ? "Nothing could be sent" : ok.Count == sent.Count ? $"All {ok.Count} physical analyses were sent to the web app" : $"{ok.Count} of {sent.Count} physical analyses were sent to the web app",
                MainContent = body.ToString(),
                CommonButtons = TaskDialogCommonButtons.Close,
                DefaultButton = TaskDialogResult.Close,
            };
            if (ok.Count > 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Also save the charts as a PDF", "No Unity needed: plans, bars and curves of every analysis just sent, in the Physical analysis folder of your Sportify folder.");
            if (unconfirmed && ok.Count > 0)
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Review the inputs, then send again", "One window for every value the analyses rest on: enter your own, or accept the built-in one knowingly.");
            var result = dialog.Show();
            return result == TaskDialogResult.CommandLink1 ? Choice.Pdf : result == TaskDialogResult.CommandLink2 ? Choice.Review : Choice.Done;
        }
    }
}
