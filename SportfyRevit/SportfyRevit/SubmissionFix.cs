using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace SportfyRevit
{
    /// <summary>
    /// A one-time, unattended tidy-up of a project for submission (user, 2026-09-29): SPORTIFY_FIX_FILE=&lt;project.rvt&gt; and SPORTIFY_FIX_REPORT=&lt;report.txt&gt;
    /// at start-up. Off unless both are set. The launcher (Run-SubmissionFix.ps1) moves the original and its _backup folder aside first; this opens that
    /// copy (detached, worksets kept), and:
    ///   - clears the main model's Sportify content from every roof whose iterations live in design options (they showed through every option);
    ///   - names the design options by the iteration each is for: Option 1 planter, Option 2 quiet, Option 3 social;
    ///   - makes the Sportify elements visible in the Lageplan views and the axonometry (phase, phase filter, categories, worksets);
    ///   - sets the axonometry as the view the project opens in;
    /// then saves it back to the original path as a central model, writes what it did and found to the report, and exits Revit.
    /// The Revit API cannot make a design option active or move an element into one, so the options are NOT refilled here.
    /// </summary>
    internal static class SubmissionFix
    {
        static readonly string[] OptionNames = { "Option 1 planter", "Option 2 quiet", "Option 3 social" };

        static readonly BuiltInCategory[] SportifyCategories =
        {
            BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_Planting, BuiltInCategory.OST_Furniture,
            BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralFoundation, BuiltInCategory.OST_Walls, BuiltInCategory.OST_Doors,
        };

        internal static void Install(UIControlledApplication application)
        {
            var source = Environment.GetEnvironmentVariable("SPORTIFY_FIX_SOURCE");
            var file = Environment.GetEnvironmentVariable("SPORTIFY_FIX_FILE");
            var report = Environment.GetEnvironmentVariable("SPORTIFY_FIX_REPORT");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(report)) return;
            void OnIdle(object? sender, IdlingEventArgs e)
            {
                application.Idling -= OnIdle;
                if (sender is not UIApplication uiApp) return;
                var lines = new List<string>();
                try { Run(uiApp, source!, file!, lines); }
                catch (Exception ex) { lines.Add("FAILED: " + ex.Message); SportifyLog.Error("submission-fix", "failed", ex); }
                try { File.WriteAllText(report!, string.Join(Environment.NewLine, lines), Encoding.UTF8); } catch (Exception) { }
                try { uiApp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); } catch (Exception) { }
            }
            application.Idling += OnIdle;
        }

        static void Run(UIApplication uiApp, string source, string target, List<string> lines)
        {
            var info = BasicFileInfo.Extract(source);
            var options = new OpenOptions { Audit = false };
            if (info.IsWorkshared) options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
            var doc = uiApp.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), options);
            lines.Add($"opened {source} (workshared: {info.IsWorkshared}, detached with its worksets)");
            try
            {
                // SPORTIFY_FIX_STEPS: which steps run ("clear,rename,views,start"; all when unset). The first run renamed options that turned out
                // to be three sets of one option each, and a phase filter change hid elements in the axonometry: the second run is "start" only.
                var steps = (Environment.GetEnvironmentVariable("SPORTIFY_FIX_STEPS") ?? "clear,rename,views,start").Split(',').Select(x => x.Trim()).ToHashSet();
                Diagnose(doc, lines, "BEFORE");
                using (var t = new Transaction(doc, "Sportify: tidy up for submission"))
                {
                    t.Start();
                    if (steps.Contains("clear")) ClearMainModelUnderOptions(doc, lines);
                    if (steps.Contains("rename")) NameOptions(doc, lines);
                    doc.Regenerate();
                    var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate).ToList();
                    if (steps.Contains("views"))
                        foreach (var v in views.Where(v => v.Name.IndexOf("Lageplan", StringComparison.OrdinalIgnoreCase) >= 0 && v is ViewPlan))
                            ShowSportify(doc, v, lines);
                    var axo = steps.Contains("start") || steps.Contains("views") ? PickAxonometry(doc, views, lines) : null;
                    if (axo != null)
                    {
                        if (steps.Contains("views")) ShowSportify(doc, axo, lines);
                        var start = StartingViewSettings.GetStartingViewSettings(doc);
                        if (!steps.Contains("start")) { }
                        else if (start.IsAcceptableStartingView(axo.Id)) { start.ViewId = axo.Id; lines.Add($"STARTING VIEW: \"{axo.Name}\""); }
                        else lines.Add($"\"{axo.Name}\" cannot be the starting view");
                    }
                    t.Commit();
                }
                Diagnose(doc, lines, "AFTER");

                var save = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 5 };
                if (doc.IsWorkshared) save.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                doc.SaveAs(target, save);
                lines.Add($"SAVED to {target}" + (doc.IsWorkshared ? " as a central model" : ""));
            }
            finally
            {
                try { doc.Close(false); } catch (Exception) { }
            }
        }

        /// <summary>
        /// What the project holds, read only: every design option set and option with the Sportify elements inside it (by the import that made them), the
        /// main model's Sportify elements per roof, and for the Lageplan and axonometry views their phase, phase filter and how many Sportify elements they show.
        /// </summary>
        static void Diagnose(Document doc, List<string> lines, string when)
        {
            lines.Add($"---- {when} ----");
            var bySource = new Dictionary<ElementId, string>();
            foreach (var e in ImportLedger.ReadEntries(doc))
                foreach (var el in e.Elements) bySource[el.Id] = (string.IsNullOrEmpty(e.Source) ? "?" : e.Source) + " @" + e.RoofKey + " " + e.ImportedAtUtc.ToString("HH:mm") + "Z";
            string Summary(IEnumerable<Element> els) => string.Join(", ", els.GroupBy(x => bySource.TryGetValue(x.Id, out var s) ? s : "untracked").OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} from {g.Key}"));

            var sportify = new FilteredElementCollector(doc).WhereElementIsNotElementType().Where(x => RoofIdentity.IsSportifys(doc, x)).ToList();
            foreach (var o in new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>())
            {
                var set = doc.GetElement(o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId() ?? ElementId.InvalidElementId);
                var inside = sportify.Where(x => x.DesignOption?.Id == o.Id).ToList();
                lines.Add($"set \"{set?.Name}\" / option \"{o.Name}\" (id {o.Id.Value}{(o.IsPrimary ? ", primary" : "")}): {inside.Count} Sportify element(s){(inside.Count > 0 ? " = " + Summary(inside) : "")}");
            }
            var main = sportify.Where(x => x.DesignOption == null).ToList();
            lines.Add($"main model: {main.Count} Sportify element(s){(main.Count > 0 ? " = " + Summary(main) : "")}");
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate &&
                         (v.Name.IndexOf("Lageplan", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("Achsonometrie", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name == "Schemes_Spotify")))
            {
                var phase = doc.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name;
                var filter = doc.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId)?.Name;
                var shown = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().Count(x => RoofIdentity.IsSportifys(doc, x));
                lines.Add($"view \"{v.Name}\": phase {phase}, phase filter {filter}, shows {shown} Sportify element(s)");
            }
        }

        /// <summary>The main model's Sportify content on each roof whose iterations are in design options: it showed through every option.</summary>
        static void ClearMainModelUnderOptions(Document doc, List<string> lines)
        {
            var entries = ImportLedger.ReadEntries(doc);
            var optionEntries = entries.Where(e => e.RoofKey.StartsWith(RoofMatch.OptionKeyPrefix, StringComparison.Ordinal)).ToList();
            foreach (var e in optionEntries)
            {
                var option = doc.GetElement(new ElementId(long.Parse(e.RoofKey.Substring(RoofMatch.OptionKeyPrefix.Length))));
                var inOption = e.Elements.Where(x => x.DesignOption != null).ToList();
                lines.Add($"design option \"{option?.Name ?? e.RoofKey}\" holds {inOption.Count} Sportify element(s)" + (string.IsNullOrEmpty(e.Source) ? "" : $" ({e.Source})") + $", imported {e.ImportedAtUtc:u}");
            }
            if (optionEntries.Count == 0) { lines.Add("no design option holds a Sportify iteration: the main model is left as it is"); return; }

            var roofs = RoofIdentity.BuildingRoofs(doc).Where(r =>
            {
                var box = r.get_BoundingBox(null);
                return box != null && optionEntries.Any(e => RoofIdentity.MostlyOn(e.Elements, box));
            }).ToList();
            foreach (var roof in roofs)
            {
                var removed = ImportLedger.RemoveOnRoof(doc, roof.Id.Value.ToString(), roof, leftovers: true);
                lines.Add($"MAIN MODEL CLEARED on \"{roof.Name}\" (id {roof.Id.Value}): {removed} Sportify element(s) removed (the options keep theirs)");
            }
            if (roofs.Count == 0) lines.Add("no building roof found under the options' content: the main model is left as it is");
        }

        /// <summary>The design options of each set, in their order, renamed Option 1 planter / 2 quiet / 3 social (the API may refuse: then it is only reported).</summary>
        static void NameOptions(Document doc, List<string> lines)
        {
            var options = new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>().ToList();
            if (options.Count == 0) { lines.Add("the project has no design options"); return; }
            foreach (var set in options.GroupBy(o => o.get_Parameter(BuiltInParameter.OPTION_SET_ID)?.AsElementId()?.Value ?? -1))
            {
                var setName = doc.GetElement(new ElementId(set.Key))?.Name ?? "(set)";
                var ordered = set.OrderBy(o => NumberIn(o.Name)).ThenBy(o => o.Id.Value).ToList();
                lines.Add($"option set \"{setName}\": " + string.Join(", ", ordered.Select(o => $"\"{o.Name}\"{(o.IsPrimary ? " (primary)" : "")}")));
                for (var i = 0; i < ordered.Count && i < OptionNames.Length; i++)
                {
                    var o = ordered[i];
                    var before = o.Name;
                    try { o.Name = OptionNames[i]; lines.Add($"RENAMED \"{before}\" -> \"{o.Name}\""); }
                    catch (Exception ex)
                    {
                        var p = o.get_Parameter(BuiltInParameter.OPTION_NAME);
                        try { if (p != null && !p.IsReadOnly && p.Set(OptionNames[i])) { lines.Add($"RENAMED \"{before}\" -> \"{OptionNames[i]}\" (by its name parameter)"); continue; } } catch (Exception) { }
                        lines.Add($"could not rename \"{before}\": {ex.Message}");
                    }
                }
            }
        }

        static int NumberIn(string name)
        {
            var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : int.MaxValue;
        }

        /// <summary>The axonometry: Sportify's own ("S2 Achsonometrie"), else a 3D view named for an axonometry or isometry, else the first 3D view with Sportify in it.</summary>
        static View3D? PickAxonometry(Document doc, List<View> views, List<string> lines)
        {
            var threeD = views.OfType<View3D>().Where(v => !v.IsPerspective).ToList();
            lines.Add("3D views: " + string.Join(", ", threeD.Select(v => "\"" + v.Name + "\"")));
            string[] words = { "achsonometrie", "axonometr", "isometr", "axo" };
            foreach (var w in words)
            {
                var hit = threeD.FirstOrDefault(v => v.Name.ToLowerInvariant().Contains(w) && !v.Name.StartsWith("{"));
                if (hit != null) return hit;
            }
            return threeD.FirstOrDefault(v => !v.Name.StartsWith("{"));
        }

        /// <summary>Makes Sportify's elements visible in a view: its phase not before "Design and analysis", the phase filter showing everything, the categories and
        /// Sportify worksets on (on the view's template when it controls them). Reports how many Sportify elements the view showed before and after.</summary>
        static void ShowSportify(Document doc, View view, List<string> lines)
        {
            int Visible() => new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().Count(e => RoofIdentity.IsSportifys(doc, e));
            var before = Visible();
            var notes = new List<string>();
            var template = view.ViewTemplateId != ElementId.InvalidElementId ? doc.GetElement(view.ViewTemplateId) as View : null;
            View Owner(BuiltInParameter p) => template != null && template.GetTemplateParameterIds().Contains(new ElementId(p)) && !template.GetNonControlledTemplateParameterIds().Contains(new ElementId(p)) ? template : view;

            // phase: not before the one the imports are in
            var phases = doc.Phases.Cast<Phase>().ToList();
            var design = phases.FirstOrDefault(p => string.Equals(p.Name, SportifyPhases.DesignAndAnalysis, StringComparison.OrdinalIgnoreCase));
            var phaseParam = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (design != null && phaseParam != null && !phaseParam.IsReadOnly)
            {
                var current = phases.FindIndex(p => p.Id == phaseParam.AsElementId());
                if (current < phases.IndexOf(design)) { phaseParam.Set(design.Id); notes.Add($"phase {(current >= 0 ? phases[current].Name : "?")} -> {design.Name}"); }
            }
            // phase filter: show everything
            var showAll = new FilteredElementCollector(doc).OfClass(typeof(PhaseFilter)).Cast<PhaseFilter>()
                .FirstOrDefault(f => f.Name is "Show All" or "Alle anzeigen" or "Alles anzeigen");
            var filterOwner = Owner(BuiltInParameter.VIEW_PHASE_FILTER);
            var filterParam = filterOwner.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);
            if (showAll != null && filterParam != null && !filterParam.IsReadOnly && filterParam.AsElementId() != showAll.Id)
            { filterParam.Set(showAll.Id); notes.Add($"phase filter -> {showAll.Name}{(filterOwner == template ? " (on its template)" : "")}"); }
            // categories
            var catOwner = Owner(BuiltInParameter.VIS_GRAPHICS_MODEL);
            foreach (var bic in SportifyCategories)
            {
                var id = new ElementId(bic);
                try { if (catOwner.CanCategoryBeHidden(id) && catOwner.GetCategoryHidden(id)) { catOwner.SetCategoryHidden(id, false); notes.Add($"{bic.ToString().Replace("OST_", "")} shown{(catOwner == template ? " (on its template)" : "")}"); } }
                catch (Exception) { }
            }
            // Sportify worksets
            if (doc.IsWorkshared)
            {
                var wsOwner = Owner(BuiltInParameter.VIS_GRAPHICS_WORKSETS);
                foreach (var w in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).Where(w => w.Name.StartsWith("Sportify", StringComparison.OrdinalIgnoreCase)))
                {
                    try { if (wsOwner.GetWorksetVisibility(w.Id) == WorksetVisibility.Hidden) { wsOwner.SetWorksetVisibility(w.Id, WorksetVisibility.Visible); notes.Add($"workset \"{w.Name}\" shown"); } }
                    catch (Exception) { }
                }
            }
            doc.Regenerate();
            lines.Add($"VIEW \"{view.Name}\"{(template != null ? $" (template \"{template.Name}\")" : "")}: Sportify elements visible {before} -> {Visible()}" +
                      (notes.Count > 0 ? "; " + string.Join("; ", notes) : "; nothing needed changing"));
        }
    }
}
