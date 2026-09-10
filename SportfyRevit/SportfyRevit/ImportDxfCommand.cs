using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Imports a DXF export (the web app's Sport tab "Export DXF" button —
    /// sportController.js's buildDXFEntities/serialiseDXF) as reference
    /// geometry: real field markings/dimensions to trace or check family
    /// placement against, before a parametric family exists for every
    /// sport/variant. Revit treats DXF as the same CAD-link format as DWG
    /// (DWGImportOptions/Document.Import handle both) — Unit is forced to
    /// Meter here because serialiseDXF writes plain AC1009 (R12) DXF with
    /// no $INSUNIT header variable, so nothing in the file itself tells
    /// Revit what scale the numbers are in; FIELDS' own dimensions
    /// (data.js) are already in meters, which is what gets written.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportDxfCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Import DXF";
            var doc = commandData.Application.ActiveUIDocument.Document;

            var fod = new FileOpenDialog("DXF file (*.dxf)|*.dxf");
            fod.Title = "Pick a DXF export from the Sportify web app (Sport tab -> Export DXF)";
            if (fod.Show() != ItemSelectionDialogResult.Confirmed)
                return Result.Cancelled;

            string path;
            try
            {
                path = ModelPathUtils.ConvertModelPathToUserVisiblePath(fod.GetSelectedModelPath());
            }
            catch (Exception ex)
            {
                TaskDialog.Show(title, "Couldn't resolve the selected file: " + ex.Message);
                return Result.Failed;
            }

            var options = new DWGImportOptions
            {
                Unit = ImportUnit.Meter,
                Placement = ImportPlacement.Origin,
                ColorMode = ImportColorMode.Preserved,
                OrientToView = true,
            };

            using (var t = new Transaction(doc, "Import Sportify DXF"))
            {
                t.Start();
                try
                {
                    bool imported = doc.Import(path, options, doc.ActiveView, out _);
                    if (!imported)
                    {
                        t.RollBack();
                        TaskDialog.Show(title, "Revit couldn't import that file — check it's a valid DXF export from the Sport tab.");
                        return Result.Failed;
                    }
                    t.Commit();
                }
                catch (Exception ex)
                {
                    t.RollBack();
                    message = "Couldn't import the DXF file: " + ex.Message;
                    return Result.Failed;
                }
            }

            TaskDialog.Show(title,
                "Imported as reference geometry (import instance) on the active view, placed at the origin in meters.\n\n" +
                "Use it to trace real family geometry against, or to sanity-check placement/scale before a parametric family exists for this sport/variant.");

            return Result.Succeeded;
        }
    }
}
