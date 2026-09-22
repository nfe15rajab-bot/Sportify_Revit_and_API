using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Four deliverables, all from geometry SportifyLayoutBuilder already creates, without any new input: two real Revit views (a circulation floor plan — only what the
    /// import put on the "Combine" workset: the roof outline, setback, circulation-path lines and entry markers — and a 3D axonometric of everything Sportify built), and
    /// two SVG diagrams in the architectural style (FunctionalDiagramData/FunctionalDiagramSvg): a "spine" diagram — the placements as rounded rooms, coloured by category,
    /// with the real walkable route to each drawn as a thick red circulation spine and a red dot per entry — and a bubble (relationship) diagram, circles sized by real
    /// footprint area and connected by dotted lines, not to scale on purpose. Re-running this command updates the same named views and files rather than creating
    /// duplicates each time.
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

            // the bubble and spine diagrams: read-only over what the import built, no transaction needed
            var data = FunctionalDiagramData.Collect(doc);
            var spineSvg = FunctionalDiagramData.SpineSvg(data);
            var bubbleSvg = FunctionalDiagramData.BubbleSvg(data);

            // the two views as images, and the two SVG diagrams, in the workspace's Diagrams folder: the deliverable outside Revit
            string saved;
            try
            {
                var temp = Path.Combine(Path.GetTempPath(), "Sportify", "DiagramImages");
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                var (circulationImage, axoImage) = GenerateAnalysisReportCommand.KeepDiagrams(
                    GenerateAnalysisReportCommand.ExportViewImage(doc, circulationViewId, Path.Combine(temp, "circulation")),
                    GenerateAnalysisReportCommand.ExportViewImage(doc, axoViewId, Path.Combine(temp, "axonometric")));

                var dir = SportifyWorkspace.PathFor("diagrams");
                File.WriteAllText(Path.Combine(dir, "functional_spine.svg"), spineSvg);
                File.WriteAllText(Path.Combine(dir, "functional_bubble.svg"), bubbleSvg);
                saved = circulationImage != null || axoImage != null ? $"\n\nAll four saved to {dir}." : $"\n\nThe two diagrams saved to {dir}.";
            }
            catch (Exception ex) { saved = "\n\nThe diagrams could not be saved: " + ex.Message; }

            SportifyLog.Info("diagrams", $"views \"{circulationViewName}\" ({how}) and \"{axoViewName}\"; {data.Pieces.Count} piece(s), {data.Entries.Count} entr{(data.Entries.Count == 1 ? "y" : "ies")} in the spine/bubble diagrams");
            TaskDialog.Show(title,
                $"Created/updated:\n" +
                $"- \"{circulationViewName}\" — floor plan, {how}.\n" +
                $"- \"{axoViewName}\" — 3D isometric, everything Sportify built.\n" +
                $"- Circulation Spine — {data.Pieces.Count} piece(s) with the real route to {(data.Entries.Count == 0 ? "an entry (none found — every route is missing)" : data.Entries.Count == 1 ? "the entry" : data.Entries.Count + " entries")}.\n" +
                $"- Bubble Diagram — the same pieces by relationship, not to scale." + saved);

            return Result.Succeeded;
        }

        /// <summary>internal, not private: GenerateAnalysisReportCommand reuses this so the PDF report's diagram images always have a source view, creating one if needed rather than requiring this command to be run first. Call inside a transaction.</summary>
        internal static ViewPlan CreateOrReuseCirculationView(Document doc)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == CirculationViewName);

            ViewPlan view = existing ?? CreateFloorPlanView(doc);
            if (existing == null) view.Name = CirculationViewName;

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

            return view;
        }

        /// <summary>internal, not private: see CreateOrReuseCirculationView's note.</summary>
        internal static View3D CreateOrReuseAxonometricView(Document doc)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == AxonometricViewName);

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
                view.Name = AxonometricViewName;
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

        private static ViewPlan CreateFloorPlanView(Document doc)
        {
            var vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.FloorPlan);
            var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                .OrderBy(l => l.Elevation).First();
            return ViewPlan.Create(doc, vft.Id, level.Id);
        }
    }
}
