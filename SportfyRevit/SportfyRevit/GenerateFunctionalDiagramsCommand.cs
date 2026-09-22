using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Scoped to the two sub-deliverables buildable from geometry
    /// SportifyLayoutBuilder already creates, without any new input: a
    /// circulation diagram (only what the import put on the "Combine" workset:
    /// the roof outline, setback, circulation-path lines and entry markers) and
    /// a 3D axonometric (the placeholder pieces are already real extruded
    /// solids — DirectShape boxes from FamilyPlacementBuilder — so this
    /// is a view/orientation setup, not new massing geometry). The bubble
    /// diagram isn't built yet — it needs new geometry (sized/labeled
    /// zone circles), not just a view of what's already placed.
    /// Re-running this command updates the same two named views rather
    /// than creating duplicates each time.
    ///
    /// It works with or without worksets. In a workshared project the circulation
    /// view hides every workset but Combine, as it always did. In a project without
    /// worksharing (the import asks first, and "import without worksets" is a
    /// legitimate answer) the same is done element by element: what the import
    /// created and would have put on any other workset is hidden in that view. It used
    /// to refuse such a project with "No Sportify layout has been imported", which was
    /// wrong: the layout was there, it was the worksets that were not.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class GenerateFunctionalDiagramsCommand : IExternalCommand
    {
        private const string CirculationViewName = "Sportify - Circulation Diagram";
        private const string AxonometricViewName = "Sportify - Massing Axonometric";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            const string title = "Sportify — Generate Functional Diagrams";
            var doc = commandData.Application.ActiveUIDocument.Document;

            // What decides whether there is anything to draw is what the import created (its ledger), not whether the project has worksets.
            if (SportifyElementScan.Find(doc).IsEmpty)
            {
                TaskDialog.Show(title,
                    "This project holds nothing from Sportify yet — run \"Import Configuration\" or turn on \"Auto Import\" and push a layout first.");
                return Result.Succeeded;
            }

            string circulationViewName, axoViewName;
            ElementId circulationViewId, axoViewId;
            string how;

            using (var t = new Transaction(doc, "Generate Sportify functional diagrams"))
            {
                t.Start();

                var circulationView = CreateOrReuseCirculationView(doc);
                var axoView = CreateOrReuseAxonometricView(doc);
                how = doc.IsWorkshared && new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Any(w => w.Name == BimRules.CombineWorkset)
                    ? "Combine workset only"
                    : "the circulation paths, roof outline and entries only (the project has no worksets)";
                circulationViewName = circulationView.Name;
                axoViewName = axoView.Name;
                circulationViewId = circulationView.Id;
                axoViewId = axoView.Id;

                t.Commit();
            }

            // the two views as images in the workspace's Diagrams folder: the deliverable outside Revit
            string saved;
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "Sportify", "DiagramImages");
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                var (circulationImage, axoImage) = GenerateAnalysisReportCommand.KeepDiagrams(
                    GenerateAnalysisReportCommand.ExportViewImage(doc, circulationViewId, Path.Combine(temp, "circulation")),
                    GenerateAnalysisReportCommand.ExportViewImage(doc, axoViewId, Path.Combine(temp, "axonometric")));
                saved = circulationImage != null || axoImage != null ? $"\n\nImages saved to {SportifyWorkspace.PathFor("diagrams")}." : "";
            }
            catch (Exception ex) { saved = "\n\nThe views could not be exported as images: " + ex.Message; }

            SportifyLog.Info("diagrams", $"views \"{circulationViewName}\" ({how}) and \"{axoViewName}\"");
            TaskDialog.Show(title,
                $"Created/updated two views (see the Project Browser):\n" +
                $"- \"{circulationViewName}\" — floor plan, {how}.\n" +
                $"- \"{axoViewName}\" — 3D isometric, everything Sportify built.\n\n" +
                "Bubble diagram isn't built yet — it needs new zone geometry, not just a view of what's already placed." + saved);

            return Result.Succeeded;
        }

        /// <summary>internal, not private: GenerateAnalysisReportCommand reuses this so the PDF report's diagram images always have a source view, creating one if needed rather than requiring this command to be run first. Call inside a transaction.</summary>
        internal static ViewPlan CreateOrReuseCirculationView(Document doc, string? name = null, bool configure = true, ElementId? levelId = null)
        {
            name ??= CirculationViewName;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);

            ViewPlan view = existing ?? CreateFloorPlanView(doc, levelId);
            if (existing == null) view.Name = name;
            if (configure) ConfigureCirculationView(doc, view);
            return view;
        }

        /// <summary>What makes the circulation view the circulation view: only Combine visible (by workset, or element by element without worksets). Separate so that a view template can be applied first and this after.</summary>
        internal static void ConfigureCirculationView(Document doc, ViewPlan view)
        {

            var worksets = doc.IsWorkshared
                ? new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Name, w => w.Id)
                : new Dictionary<string, WorksetId>();

            if (worksets.ContainsKey(BimRules.CombineWorkset))
            {
                foreach (var (name, id) in worksets)
                    view.SetWorksetVisibility(id, string.Equals(name, BimRules.CombineWorkset, StringComparison.OrdinalIgnoreCase) ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
            }
            else
            {
                HideAllButCombine(doc, view);
            }
        }

        /// <summary>internal, not private: see CreateOrReuseCirculationView's note.</summary>
        internal static View3D CreateOrReuseAxonometricView(Document doc, string? name = null)
        {
            name ??= AxonometricViewName;
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == name);

            View3D view;
            if (existing != null)
            {
                view = existing;
            }
            else
            {
                var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
                view = View3D.CreateIsometric(doc, vft.Id);
                view.Name = name;
                ViewDefaults.DisableCrop(view);
            }

            view.DetailLevel = ViewDetailLevel.Fine;
            view.DisplayStyle = DisplayStyle.ShadingWithEdges;

            if (doc.IsWorkshared)
                foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    view.SetWorksetVisibility(w.Id, WorksetVisibility.Visible);

            return view;
        }

        /// <summary>
        /// Without worksets: hides, in the view, what the import created and would have put on Sports or Gardens (courts, furniture, planting, ground floors), so that what is left is
        /// what it puts on Combine. The same rule as the worksets' (BimRules.WorksetFor).
        /// </summary>
        private static void HideAllButCombine(Document doc, View view)
        {
            var toHide = new List<ElementId>();
            foreach (var el in SportifyElementScan.Find(doc).Elements)
            {
                if (BimRules.WorksetFor(SportifyElementScan.KindOf(el), SportifyElementScan.CategoryOf(el), SportifyElementScan.IsPlanting(el)) == BimRules.CombineWorkset) continue;
                if (el.CanBeHidden(view) && !el.IsHidden(view)) toHide.Add(el.Id);
            }
            if (toHide.Count > 0) view.HideElements(toHide);
        }

        private static ViewPlan CreateFloorPlanView(Document doc, ElementId? levelId = null)
        {
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.FloorPlan);
            var level = levelId ?? new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).First().Id;
            var view = ViewPlan.Create(doc, vft.Id, level);
            ViewDefaults.DisableCrop(view);
            return view;
        }
    }
}
