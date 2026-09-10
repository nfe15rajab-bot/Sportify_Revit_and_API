using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Scoped to the two sub-deliverables buildable from geometry
    /// SportifyLayoutBuilder already creates, without any new input: a
    /// circulation diagram (isolates the "Combine" workset's circulation-
    /// path lines, already drawn by CreateCirculationPaths) and a 3D
    /// axonometric (the placeholder pieces are already real extruded
    /// solids — DirectShape boxes from FamilyPlacementBuilder — so this
    /// is a view/orientation setup, not new massing geometry). The bubble
    /// diagram isn't built yet — it needs new geometry (sized/labeled
    /// zone circles), not just a view of what's already placed.
    /// Re-running this command updates the same two named views rather
    /// than creating duplicates each time.
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

            if (!doc.IsWorkshared)
            {
                TaskDialog.Show(title,
                    "No Sportify layout has been imported into this project yet — run \"Import Configuration\" or turn on \"Auto Import\" first.");
                return Result.Succeeded;
            }

            var worksets = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset)
                .ToDictionary(w => w.Name, w => w.Id);

            if (!worksets.ContainsKey("Combine"))
            {
                TaskDialog.Show(title, "No \"Combine\" workset found — import a Sportify layout first.");
                return Result.Succeeded;
            }

            string circulationViewName, axoViewName;

            using (var t = new Transaction(doc, "Generate Sportify functional diagrams"))
            {
                t.Start();

                var circulationView = CreateOrReuseCirculationView(doc, worksets);
                var axoView = CreateOrReuseAxonometricView(doc, worksets);
                circulationViewName = circulationView.Name;
                axoViewName = axoView.Name;

                t.Commit();
            }

            TaskDialog.Show(title,
                $"Created/updated two views (see the Project Browser):\n" +
                $"- \"{circulationViewName}\" — floor plan, Combine workset only.\n" +
                $"- \"{axoViewName}\" — 3D isometric, all Sportify worksets.\n\n" +
                "Bubble diagram isn't built yet — it needs new zone geometry, not just a view of what's already placed.");

            return Result.Succeeded;
        }

        private static ViewPlan CreateOrReuseCirculationView(Document doc, Dictionary<string, WorksetId> worksets)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == CirculationViewName);

            ViewPlan view = existing ?? CreateFloorPlanView(doc);
            if (existing == null) view.Name = CirculationViewName;

            foreach (var (name, id) in worksets)
            {
                view.SetWorksetVisibility(id,
                    string.Equals(name, "Combine", StringComparison.OrdinalIgnoreCase) ? WorksetVisibility.Visible : WorksetVisibility.Hidden);
            }

            return view;
        }

        private static View3D CreateOrReuseAxonometricView(Document doc, Dictionary<string, WorksetId> worksets)
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

            foreach (var id in worksets.Values)
                view.SetWorksetVisibility(id, WorksetVisibility.Visible);

            return view;
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
