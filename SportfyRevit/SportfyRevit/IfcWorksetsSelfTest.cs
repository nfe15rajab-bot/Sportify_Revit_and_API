using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// An unattended check of "IFC Worksets by Class" on a real model, for what nothing outside Revit can prove: that the IFC import's parameters are read as the rules expect, that the elements really
    /// move, that a second run has nothing left to move, and that nothing of Sportify's is touched. SPORTIFY_IFC_WORKSETS_SELFTEST=&lt;folder&gt; and SPORTIFY_IFC_WORKSETS_FILE=&lt;project.rvt&gt; run it once at
    /// start-up: the project is COPIED into the folder and the copy is opened (detached from its central model) and closed again without saving, so the original is never opened. It writes
    /// ifc-worksets-selftest.json there and exits Revit (SPORTIFY_IFC_WORKSETS_SELFTEST_KEEP=1 leaves it open). SPORTIFY_IFC_WORKSETS_ROOF_IFCTAG=&lt;IfcTag&gt; names the roof element to try the roof
    /// workset on. Off unless the two variables are set.
    /// </summary>
    internal static class IfcWorksetsSelfTest
    {
        sealed class Report
        {
            public List<string> Steps { get; set; } = new();
            public List<string> Failures { get; set; } = new();
            public Dictionary<string, int> ByClass { get; set; } = new();
            public Dictionary<string, int> ByWorkset { get; set; } = new();
            public Dictionary<string, int> Moved { get; set; } = new();
            public Dictionary<string, int> Phases { get; set; } = new();
            public Dictionary<string, int> WorksetsBefore { get; set; } = new();
            public Dictionary<string, int> WorksetsAfter { get; set; } = new();
            public int IfcElements { get; set; }
            public int WithoutClass { get; set; }
            public Dictionary<string, int> Unclassified { get; set; } = new();
            public int SkippedSportify { get; set; }
            public int RoofElements { get; set; }
            public int SecondRunToMove { get; set; } = -1;
            public double Seconds { get; set; }
            public bool Ok => Failures.Count == 0;
        }

        internal static void Install(UIControlledApplication application)
        {
            var dir = Environment.GetEnvironmentVariable("SPORTIFY_IFC_WORKSETS_SELFTEST");
            var file = Environment.GetEnvironmentVariable("SPORTIFY_IFC_WORKSETS_FILE");
            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(file)) return;
            void OnIdle(object? sender, IdlingEventArgs e)
            {
                application.Idling -= OnIdle;
                if (sender is not UIApplication uiApp) return;
                var report = new Report();
                var clock = Stopwatch.StartNew();
                try { Run(uiApp, dir!, file!, report); }
                catch (Exception ex) { report.Failures.Add("the self-test itself failed: " + ex); }
                report.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
                try
                {
                    Directory.CreateDirectory(dir!);
                    File.WriteAllText(Path.Combine(dir!, "ifc-worksets-selftest.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { SportifyLog.Warn("selftest", "the report could not be written: " + ex.Message); }
                SportifyLog.Info("selftest", "IFC worksets self-test " + (report.Ok ? "PASSED" : "FAILED (" + report.Failures.Count + ")") + ", report in " + dir);
                if (Environment.GetEnvironmentVariable("SPORTIFY_IFC_WORKSETS_SELFTEST_KEEP") != "1")
                {
                    try { uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); }
                    catch (Exception) { Process.GetCurrentProcess().Kill(); }
                }
            }
            application.Idling += OnIdle;
        }

        static Dictionary<string, int> WorksetCounts(Document doc)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var names = new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToDictionary(w => w.Id.IntegerValue, w => w.Name);
            foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                if (el.WorksetId == WorksetId.InvalidWorksetId) continue;
                var n = names.TryGetValue(el.WorksetId.IntegerValue, out var nm) ? nm : "?";
                counts[n] = counts.GetValueOrDefault(n) + 1;
            }
            return counts;
        }

        static void Run(UIApplication uiApp, string dir, string file, Report r)
        {
            Directory.CreateDirectory(dir);
            if (!File.Exists(file)) { r.Failures.Add("the project does not exist: " + file); return; }
            var copy = Path.Combine(dir, "ifc-worksets-copy.rvt");
            File.Copy(file, copy, true);
            r.Steps.Add("copied the project to " + copy);

            var info = BasicFileInfo.Extract(copy);
            var options = new OpenOptions { Audit = false };
            if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            Document doc;
            try { doc = uiApp.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(copy), options); }
            catch (Exception ex) { r.Failures.Add("the copy could not be opened: " + ex.Message); return; }
            r.Steps.Add("opened the copy (workshared: " + doc.IsWorkshared + ")");
            try
            {
                if (!doc.IsWorkshared) { r.Failures.Add("the project is not workshared: nothing to test"); return; }
                var table = IfcWorksetRules.Defaults;

                var sportifyBefore = SportifyElementScan.Find(doc).Elements.ToDictionary(e => e.Id.Value, e => IfcWorksetService.WorksetNameOf(doc, e));
                var total = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
                r.WorksetsBefore = WorksetCounts(doc);

                var roof = new HashSet<ElementId>();
                var tag = Environment.GetEnvironmentVariable("SPORTIFY_IFC_WORKSETS_ROOF_IFCTAG");
                if (!string.IsNullOrWhiteSpace(tag))
                    foreach (var el in new FilteredElementCollector(doc).WhereElementIsNotElementType())
                        if (el.LookupParameter("IfcTag")?.AsString() == tag) roof.Add(el.Id);
                r.Steps.Add("roof elements named by IfcTag \"" + tag + "\": " + roof.Count);

                var plan = IfcWorksetService.Plan(doc, table, roof);
                r.IfcElements = plan.Items.Count;
                r.WithoutClass = plan.WithoutClass;
                r.Unclassified = new Dictionary<string, int>(plan.Unclassified);
                r.SkippedSportify = plan.SkippedSportify;
                r.RoofElements = plan.RoofCount;
                r.ByClass = new Dictionary<string, int>(plan.ByClass);
                r.ByWorkset = new Dictionary<string, int>(plan.ByWorkset);
                r.Phases = new Dictionary<string, int>(plan.Phases);
                r.Steps.Add("planned: " + plan.Items.Count + " IFC element(s), " + plan.ToMove + " to move");
                if (plan.Items.Count == 0) r.Failures.Add("no IFC element found in the project");
                if (!string.IsNullOrWhiteSpace(tag) && plan.RoofCount != roof.Count) r.Failures.Add("the roof elements (" + roof.Count + ") were not all found among the IFC elements (" + plan.RoofCount + ")");

                var result = IfcWorksetService.Apply(doc, plan);
                r.Moved = new Dictionary<string, int>(result.Moved);
                r.Steps.Add("applied: moved " + result.Moved.Values.Sum() + ", made " + string.Join(", ", result.Made) + ", already " + result.Already + ", owned by others " + result.OwnedByOthers + ", refused " + result.Refused);
                if (result.Refused > 0) r.Failures.Add(result.Refused + " element(s) could not be moved");
                if (result.Moved.Values.Sum() + result.Already + result.OwnedByOthers + result.Refused != plan.Items.Count) r.Failures.Add("the moved, already, owned and refused do not add up to the elements planned");

                var wrong = plan.Items.Count(i => IfcWorksetService.WorksetNameOf(doc, i.Element) != i.Workset && !i.Workset.Equals(IfcWorksetService.WorksetNameOf(doc, i.Element), StringComparison.OrdinalIgnoreCase));
                if (wrong > 0) r.Failures.Add(wrong + " element(s) are not on the workset the plan gave them");
                r.WorksetsAfter = WorksetCounts(doc);
                // what has no IFC class stays where it is: the workset of the levels and the IFC's grid axes must hold as many as before
                const string shared = "Shared Levels and Grids";
                if (r.WorksetsBefore.GetValueOrDefault(shared) != r.WorksetsAfter.GetValueOrDefault(shared))
                    r.Failures.Add("the workset \"" + shared + "\" held " + r.WorksetsBefore.GetValueOrDefault(shared) + " element(s) before and " + r.WorksetsAfter.GetValueOrDefault(shared) + " after: elements with no IFC class must stay where they are");

                var sportifyAfter = SportifyElementScan.Find(doc).Elements.ToDictionary(e => e.Id.Value, e => IfcWorksetService.WorksetNameOf(doc, e));
                var touched = sportifyBefore.Count(kv => !sportifyAfter.TryGetValue(kv.Key, out var w) || w != kv.Value);
                if (touched > 0) r.Failures.Add(touched + " Sportify element(s) changed workset");
                r.Steps.Add("Sportify elements: " + sportifyBefore.Count + " before, " + touched + " changed");

                var again = IfcWorksetService.Plan(doc, table, roof);
                r.SecondRunToMove = again.ToMove;
                if (again.ToMove != 0) r.Failures.Add("a second run would still move " + again.ToMove + " element(s)");
                if (again.Items.Count != plan.Items.Count) r.Failures.Add("a second run finds " + again.Items.Count + " IFC element(s) instead of " + plan.Items.Count);
                var totalAfter = new FilteredElementCollector(doc).WhereElementIsNotElementType().GetElementCount();
                if (totalAfter != total) r.Failures.Add("the number of elements changed from " + total + " to " + totalAfter);
            }
            finally
            {
                try { doc.Close(false); r.Steps.Add("closed the copy without saving"); }
                catch (Exception ex) { r.Failures.Add("the copy could not be closed: " + ex.Message); }
            }
        }
    }
}
