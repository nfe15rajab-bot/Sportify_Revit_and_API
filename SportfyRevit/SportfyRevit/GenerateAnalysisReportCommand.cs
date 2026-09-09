using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// First real prototype of the report command, not the final version —
    /// proves the "a chart the web app generates ends up embedded in the
    /// Revit deliverable" pipeline end to end, before Sukriti's automated
    /// sync channel exists to carry it across automatically. Today: pick
    /// the PNG the web app's Analysis tab exported (e.g. the Sun Path
    /// chart's "Download Chart" button), place it on the active view.
    /// Once a real sync exists, this file-picker step is exactly what
    /// gets replaced by an automated fetch — the embedding logic below
    /// (ImageType + ImageInstance) stays the same either way, since that
    /// part is a Revit API question, not a sync question.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class GenerateAnalysisReportCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc.Document;

            var fod = new FileOpenDialog("Chart image (*.png)|*.png");
            fod.Title = "Pick a chart exported from the Sportify web app (Analysis tab → Download Chart)";
            if (fod.Show() != ItemSelectionDialogResult.Confirmed)
                return Result.Cancelled;

            string imagePath;
            try
            {
                imagePath = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
            }
            catch (Exception ex)
            {
                message = "Couldn't resolve the selected file: " + ex.Message;
                return Result.Failed;
            }

            using (var t = new Transaction(doc, "Generate Analysis Report (prototype)"))
            {
                t.Start();
                try
                {
                    var options = new ImageTypeOptions(imagePath, false, ImageTypeSource.Import);
                    var imageType = ImageType.Create(doc, options);

                    var placement = new ImagePlacementOptions(XYZ.Zero, BoxPlacement.Center);
                    ImageInstance.Create(doc, doc.ActiveView, imageType.Id, placement);

                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = "Couldn't place the chart image: " + ex.Message;
                    return Result.Failed;
                }
            }

            TaskDialog.Show("Sportify",
                "Placed the chart on the active view.\n\n" +
                "This is a prototype hand-off, not the final Deliverables report — " +
                "once the web app can push chart data directly (Sukriti's sync work), " +
                "this file-picker step goes away and the report assembles automatically.");

            return Result.Succeeded;
        }
    }
}
