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
            // SPORTIFY_FIX_SYNC=1: `source` is a person's LOCAL copy; it is opened as it is (not detached) and the changes go to the central by a
            // Synchronize with Central, so the central keeps its identity and every local made from it stays valid. Otherwise: detached, saved as below.
            var sync = Environment.GetEnvironmentVariable("SPORTIFY_FIX_SYNC") == "1" && info.IsWorkshared && info.IsLocal;
            // SPORTIFY_FIX_SAVELOCAL=1: the same local opened as it is, its changes saved in the local only (Save, no Synchronize with Central)
            var saveLocal = !sync && Environment.GetEnvironmentVariable("SPORTIFY_FIX_SAVELOCAL") == "1" && info.IsWorkshared && info.IsLocal;
            if (sync || saveLocal) options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));
            else if (info.IsWorkshared)
            {
                options.DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets;
                options.SetOpenWorksetsConfiguration(new WorksetConfiguration(WorksetConfigurationOption.OpenAllWorksets));   // every element loaded, so the report counts them all
            }
            var doc = uiApp.Application.OpenDocumentFile(ModelPathUtils.ConvertUserVisiblePathToModelPath(source), options);
            lines.Add($"opened {source} (workshared: {info.IsWorkshared}, " + (sync ? "a local, to be synchronized with its central)" : saveLocal ? "a local, to be saved as a local only)" : "detached with its worksets)"));
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
                if (steps.Contains("sheets")) ArrangeSheets(doc, lines);
                if (steps.Contains("team")) ApplyTeam(doc, Environment.GetEnvironmentVariable("SPORTIFY_FIX_TEAM"), lines);
                if (steps.Contains("info")) ApplyInfo(doc, Environment.GetEnvironmentVariable("SPORTIFY_FIX_INFO"), lines);
                if (steps.Contains("worksets")) ShowAllWorksets(doc, lines);
                if (steps.Contains("phases")) DesignViewsOnNewestPhase(doc, lines);
                if (steps.Contains("strays")) RemoveStrays(doc, lines);
                if (steps.Contains("posts")) KineticPostRepair.Run(doc, lines);
                if (steps.Contains("memory")) KeepMemory(doc, lines);
                var exportTo = Environment.GetEnvironmentVariable("SPORTIFY_FIX_EXPORT");
                if (steps.Contains("titleblock-inspect")) TitleBlockFix.Inspect(doc, exportTo, lines);
                if (steps.Contains("titleblock"))
                    TitleBlockFix.Apply(doc, Environment.GetEnvironmentVariable("SPORTIFY_FIX_TB_EDITS"), Environment.GetEnvironmentVariable("SPORTIFY_FIX_TB_COPY"), exportTo, lines);
                Diagnose(doc, lines, "AFTER");
                if (Environment.GetEnvironmentVariable("SPORTIFY_FIX_EXPORT") is string export && export.Length > 0) ExportSheets(doc, export, lines);
                if (Environment.GetEnvironmentVariable("SPORTIFY_FIX_NOSAVE") == "1") { lines.Add("NOT SAVED (dry run)"); return; }
                if (saveLocal)
                {
                    doc.Save(new SaveOptions { Compact = false });
                    lines.Add("SAVED the local only (not synchronized with " + ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()) + ")");
                    return;
                }
                if (sync)
                {
                    var swc = new SynchronizeWithCentralOptions { Comment = "Sportify: the plan set in the team's order, the project team", SaveLocalBefore = false, SaveLocalAfter = true };
                    swc.SetRelinquishOptions(new RelinquishOptions(true));
                    doc.SynchronizeWithCentral(new TransactWithCentralOptions(), swc);
                    lines.Add("SYNCHRONIZED with " + ModelPathUtils.ConvertModelPathToUserVisiblePath(doc.GetWorksharingCentralModelPath()) + ", the local saved");
                    return;
                }

                var save = new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 5 };
                // found 2026-09-30: left at the API's default the central asked which worksets to open, and a new local opened with most of them closed (the
                // building and the pieces looked gone). Everyone opening it now gets every workset.
                if (doc.IsWorkshared) save.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true, OpenWorksetsDefault = SimpleWorksetConfiguration.AllWorksets });
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
            // every recorded import, where its elements stand: a copy built at the wrong height (a layout whose height named no roof) shows its undersides
            // far from every roof's top (found 2026-09-30: the team's export "(6)" at 0 m, under the building, recorded as roof "0")
            var roofsHere = RoofIdentity.BuildingRoofs(doc);
            foreach (var e in ImportLedger.ReadEntries(doc))
            {
                var zs = e.Elements.Select(x => x.get_BoundingBox(null)).Where(b => b != null).Select(b => UnitUtils.ConvertFromInternalUnits(b!.Min.Z, UnitTypeId.Meters)).ToList();
                var on = roofsHere.FirstOrDefault(r => RoofIdentity.MostlyOn(e.Elements, r.get_BoundingBox(null)));
                lines.Add($"ledger @{e.RoofKey} {e.ImportedAtUtc:MM-dd HH:mm}Z \"{e.Source}\": {e.Elements.Count} element(s), undersides " +
                          (zs.Count > 0 ? $"{zs.Min():0.##}..{zs.Max():0.##} m" : "-") + ", standing on " + (on == null ? "no roof or floor of the building" : $"\"{on.Name}\" (id {on.Id.Value})"));
            }
            foreach (var g in main.Where(x => x.LookupParameter("Sportify_QualityKey")?.AsString() == "ACTIVITY_PING_PONG").GroupBy(x => x.LookupParameter("Sportify_RoofId")?.AsString() ?? ""))
                lines.Add($"ping pong elements on roof {g.Key}: {g.Count()}");
            // the kinetic units (Kinetics > Import Analysis Adaptation): each unit's parts by role, so a unit missing its posts shows (2026-09-30: pergola columns gone)
            var iterationIds = IterationLedger.ReadUniqueIds(doc);
            foreach (var g in sportify.Where(x => x.LookupParameter("Sportify_Category")?.AsString() == SportifyKineticFamilyBuilder.KineticsCategoryValue)
                         .GroupBy(x => (Unit: x.LookupParameter("Sportify_Variant")?.AsString() ?? "?", Roof: x.LookupParameter("Sportify_RoofId")?.AsString() ?? "",
                                        Where: x.DesignOption != null ? "option " + x.DesignOption.Name : iterationIds.Contains(x.UniqueId) ? "iteration" : "main"))
                         .OrderBy(g => g.Key.Where).ThenBy(g => g.Key.Unit))
            {
                var zs = g.Select(x => x.get_BoundingBox(null)).Where(b => b != null).Select(b => UnitUtils.ConvertFromInternalUnits(b!.Min.Z, UnitTypeId.Meters)).ToList();
                var ws = g.Select(x => doc.GetWorksetTable().GetWorkset(x.WorksetId)?.Name).Distinct();
                var boxes = g.Select(x => x.get_BoundingBox(null)).Where(b => b != null).ToList();
                double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
                var plan = boxes.Count > 0 ? $"; plan x {M(boxes.Min(b => b!.Min.X)):0.#}..{M(boxes.Max(b => b!.Max.X)):0.#}, y {M(boxes.Min(b => b!.Min.Y)):0.#}..{M(boxes.Max(b => b!.Max.Y)):0.#} m (model)" : "";
                lines.Add($"kinetic unit \"{g.Key.Unit}\" ({g.Key.Where}, roof {g.Key.Roof}): " + string.Join(", ", g.GroupBy(x => x.LookupParameter("Sportify_TypeId")?.AsString() ?? "?").OrderBy(r => r.Key).Select(r => $"{r.Count()} {r.Key}")) +
                          $"; undersides {(zs.Count > 0 ? $"{zs.Min():0.##}..{zs.Max():0.##} m" : "-")}{plan}; worksets {string.Join("/", ws)}");
            }
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate &&
                         (v.Name.IndexOf("Lageplan", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("Achsonometrie", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name == "Schemes_Spotify")))
            {
                var phase = doc.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name;
                var filter = doc.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId)?.Name;
                var shown = new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().Count(x => RoofIdentity.IsSportifys(doc, x));
                lines.Add($"view \"{v.Name}\": phase {phase}, phase filter {filter}, shows {shown} Sportify element(s)");
            }
            lines.Add("phases: " + string.Join(", ", doc.Phases.Cast<Phase>().Select(p => p.Name)));
            // every 3D view: what of the newest import it shows, and what hides the rest (user, 2026-10-01: "in 3d this is empty" while the plan shows the pieces)
            var newest = ImportLedger.ReadEntries(doc).Where(e => !e.RoofKey.StartsWith("option:", StringComparison.Ordinal)).OrderByDescending(e => e.ImportedAtUtc).FirstOrDefault();
            if (newest != null)
            {
                var pieces = newest.Elements.Where(e => e is FamilyInstance && e.IsValidObject).ToList();
                // each piece's family and whether it has any 3D solid (a family of plan lines only shows in plan and vanishes in 3D)
                static double Volume(GeometryElement? g)
                {
                    double v = 0;
                    if (g == null) return 0;
                    foreach (var o in g)
                    {
                        if (o is Solid so) v += Math.Abs(so.Volume);
                        else if (o is GeometryInstance gi) v += Volume(gi.GetInstanceGeometry());
                    }
                    return v;
                }
                foreach (var grp in pieces.Cast<FamilyInstance>().GroupBy(fi => fi.Symbol?.FamilyName ?? "?"))
                {
                    var vol = grp.Sum(fi => { try { return Volume(fi.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine })); } catch (Exception) { return 0; } });
                    var label = grp.First().LookupParameter("Sportify_Label")?.AsString() ?? "";
                    lines.Add($"piece family \"{grp.Key}\" ({grp.Count()}x{(label.Length > 0 ? ", " + label : "")}): 3D solids {UnitUtils.ConvertFromInternalUnits(vol, UnitTypeId.CubicMeters):0.###} m3" + (vol <= 1e-9 ? "  <- NO 3D GEOMETRY" : ""));
                }
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate))
                {
                    var visible = new HashSet<ElementId>(new FilteredElementCollector(doc, v.Id).WhereElementIsNotElementType().ToElementIds());
                    var hidden = pieces.Where(e => !visible.Contains(e.Id)).ToList();
                    var phase = doc.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE)?.AsElementId() ?? ElementId.InvalidElementId)?.Name;
                    var filter = doc.GetElement(v.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER)?.AsElementId() ?? ElementId.InvalidElementId)?.Name;
                    var why = new List<string>();
                    foreach (var g in hidden.GroupBy(e => e.WorksetId))
                    {
                        var ws = doc.GetWorksetTable().GetWorkset(g.Key);
                        try { if (doc.IsWorkshared && !v.IsWorksetVisible(g.Key)) why.Add($"{g.Count()} on workset \"{ws?.Name}\" hidden in the view"); } catch (Exception) { }
                    }
                    if (hidden.Count > 0 && v.IsSectionBoxActive) why.Add("a section box is on");
                    if (hidden.Count > 0 && v.ViewTemplateId != ElementId.InvalidElementId) why.Add("template \"" + doc.GetElement(v.ViewTemplateId)?.Name + "\"");
                    lines.Add($"3D view \"{v.Name}\": phase {phase}, filter {filter}; shows {pieces.Count - hidden.Count} of the newest import's {pieces.Count} pieces" + (why.Count > 0 ? " (" + string.Join("; ", why) + ")" : ""));
                }
            }
            var pinfo = doc.ProjectInformation;
            foreach (var bip in new[] { BuiltInParameter.PROJECT_NAME, BuiltInParameter.PROJECT_NUMBER, BuiltInParameter.CLIENT_NAME, BuiltInParameter.PROJECT_ADDRESS, BuiltInParameter.PROJECT_BUILDING_NAME,
                                        BuiltInParameter.PROJECT_AUTHOR, BuiltInParameter.PROJECT_ORGANIZATION_NAME, BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION })
                lines.Add($"project information {bip}: \"{pinfo.get_Parameter(bip)?.AsString()}\"");
            foreach (var n in new[] { "Verfasser eMail", "Verfasser Firma 1", "Verfasser Strasse", "Verfasser PLZ Ort", "Verfasser Telefon", "Verfasser URL",
                                      "Bauherr", "Kundenadresse", "Kundenort", "Kundentelefon", "Kundenzusatz", "Baustellenstraße", "Baustellenort" })
                if (pinfo.LookupParameter(n) is Parameter gp) lines.Add($"project information \"{n}\": \"{gp.AsString()}\"");
            foreach (var sh in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().OrderBy(x => x.SheetNumber, StringComparer.Ordinal))
                lines.Add($"people {sh.SheetNumber} \"{sh.Name}\": designed \"{sh.get_Parameter(BuiltInParameter.SHEET_DESIGNED_BY)?.AsString()}\", drawn \"{sh.get_Parameter(BuiltInParameter.SHEET_DRAWN_BY)?.AsString()}\", checked \"{sh.get_Parameter(BuiltInParameter.SHEET_CHECKED_BY)?.AsString()}\", approved \"{sh.get_Parameter(BuiltInParameter.SHEET_APPROVED_BY)?.AsString()}\"; group \"{sh.LookupParameter("Projektbrowser Plangliederung")?.AsString()}\"");
            // which iteration each 3D / plan view shows: the "Sportify Iteration N" worksets visible in it (none = only the main model)
            var groups = IterationWorksets.Find(doc);
            if (groups.Count > 0)
                foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && (v is View3D || v is ViewPlan)))
                {
                    var shown = groups.Where(g => g.Worksets.Any(w => { try { return v.IsWorksetVisible(w.Id); } catch (Exception) { return false; } })).Select(g => g.Index).ToList();
                    if (shown.Count != 1 || v.Name.IndexOf("iteration", StringComparison.OrdinalIgnoreCase) >= 0 || v.Name.IndexOf("interation", StringComparison.OrdinalIgnoreCase) >= 0)
                        lines.Add($"iterations in view \"{v.Name}\": {(shown.Count == 0 ? "none" : string.Join(" + ", shown))}{(shown.Count > 1 ? "  <- more than one: they stand on top of each other" : "")}");
                }
            if (doc.IsWorkshared)
                foreach (var ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset))
                    lines.Add($"workset \"{ws.Name}\": open {ws.IsOpen}, visible by default {ws.IsVisibleByDefault}, elements {new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(ws.Id)).WhereElementIsNotElementType().GetElementCount()}");
            lines.Add("view templates: " + string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate && (v.Name.StartsWith("S") || v.Name == "Diagrams")).Select(v => v.Name).OrderBy(n => n)));
            lines.Add("Sportify views: " + string.Join(", ", new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => !v.IsTemplate && v.Name.StartsWith("Sportify - ")).Select(v => v.Name).OrderBy(n => n)));
            var vps = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToList();
            var lists = new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(x => !x.IsTitleblockRevisionSchedule).ToList();
            foreach (var sh in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                         .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.SheetNumber, @"^S[1-4]-") || x.SheetNumber.StartsWith("SPORT") || x.LookupParameter("Projektbrowser Plangliederung")?.AsString() == "Sportify")
                         .OrderBy(x => x.SheetNumber, StringComparer.Ordinal))
            {
                var tb = new FilteredElementCollector(doc, sh.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().OfType<FamilyInstance>().FirstOrDefault();
                var bb = tb?.get_BoundingBox(sh);
                var size = bb == null ? "" : $" {(bb.Max.X - bb.Min.X) * 304.8:0} x {(bb.Max.Y - bb.Min.Y) * 304.8:0} mm";
                lines.Add($"sheet {sh.SheetNumber} \"{sh.Name}\": {(tb == null ? "no title block" : tb.Symbol.FamilyName + " : " + tb.Symbol.Name)}{size}; views: "
                          + string.Join(" + ", vps.Where(v => v.SheetId == sh.Id).Select(v => "\"" + doc.GetElement(v.ViewId)?.Name + "\""))
                          + "; lists: " + string.Join(" + ", lists.Where(x => x.OwnerViewId == sh.Id).Select(x => "\"" + doc.GetElement(x.ScheduleId)?.Name + "\"")));
            }
        }

        /// <summary>The plan set arranged the way Apply Sportify Template and every drawing of the diagrams now do (SportifySheets). With SPORTIFY_FIX_LAYOUT (a layout the
        /// web app exported) the diagrams and the tag view are drawn from it first, as an analysis would; without it, the tag view is drawn and the sheets arranged.</summary>
        static void ArrangeSheets(Document doc, List<string> lines)
        {
            var layoutPath = Environment.GetEnvironmentVariable("SPORTIFY_FIX_LAYOUT");
            if (!string.IsNullOrWhiteSpace(layoutPath) && File.Exists(layoutPath))
            {
                var layout = System.Text.Json.JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(layoutPath));
                SportifyDiagramViews.Refresh(doc, layout);
                lines.Add($"diagrams, tag view and sheets drawn from {layoutPath} ({layout?.Placements?.Count ?? 0} piece(s))");
                return;
            }
            using var t = new Transaction(doc, "Sportify: plan set");
            t.Start();
            var template = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().FirstOrDefault(v => v.IsTemplate && v.Name == SportifyDiagramViews.TemplateName);
            var tags = SportifyTagView.Draw(doc, template);
            lines.Add(tags == null ? "no tag view (no Tag_Sportify)" : $"tag view \"{tags.Name}\" drawn");
            var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name).ToList();
            var language = SportifyTemplateSpec.Detect(names) ?? TemplateLanguage.De;
            var made = new List<string>(); var notes = new List<string>();
            SportifySheets.Arrange(doc, SportifyTemplateSpec.For(language), null, null, made, notes);
            t.Commit();
            lines.Add($"plan set arranged ({language}):");
            lines.AddRange(made.Select(m => "  " + m));
            lines.AddRange(notes.Select(n => "  NOTE " + n));
        }

        /// <summary>
        /// Project Information by parameter name (SPORTIFY_FIX_INFO, a JSON object: {"Verfasser Strasse": "Emilienstraße 45", "Kundentelefon": ""}), a text parameter
        /// each; and every sheet's issue date written the German way: a date the template left as "09/27/26" (US) becomes "27.09.2026", the same day.
        /// </summary>
        static void ApplyInfo(Document doc, string? path, List<string> lines)
        {
            using var t = new Transaction(doc, "Sportify: project information");
            t.Start();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                var values = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new Dictionary<string, string>();
                foreach (var (name, value) in values)
                {
                    var p = doc.ProjectInformation.LookupParameter(name);
                    if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) { lines.Add($"info: \"{name}\" is not a text parameter of Project Information"); continue; }
                    var was = p.AsString();
                    if (was != value) { p.Set(value); lines.Add($"info: \"{name}\": \"{was}\" -> \"{value}\""); }
                }
            }
            int dates = 0;
            foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                var p = sheet.get_Parameter(BuiltInParameter.SHEET_ISSUE_DATE);
                var m = p == null || p.IsReadOnly ? null : System.Text.RegularExpressions.Regex.Match(p.AsString() ?? "", @"^(\d{1,2})/(\d{1,2})/(\d{2}|\d{4})$");
                if (m == null || !m.Success) continue;
                int month = int.Parse(m.Groups[1].Value), day = int.Parse(m.Groups[2].Value), year = int.Parse(m.Groups[3].Value);
                if (year < 100) year += 2000;
                if (month < 1 || month > 12 || day < 1 || day > 31) continue;
                p!.Set($"{day:00}.{month:00}.{year}");
                dates++;
            }
            if (dates > 0) lines.Add($"info: {dates} sheet issue date(s) written as dd.mm.yyyy");
            t.Commit();
        }

        /// <summary>Every workset visible, each iteration view on its iteration (WorksetRepair, the same as the ribbon's Show All Worksets).</summary>
        static void ShowAllWorksets(Document doc, List<string> lines) => WorksetRepair.ShowAll(doc, lines);

        /// <summary>
        /// The views on the plan set's design sheets (S2-02 roof new, S2-03 axonometric, S2-04..06 the diagrams, S2-07 a person's extra sheet) show the
        /// newest phase (Post analysis, else Design and analysis, else the project's last): an import puts its pieces in Design and analysis, so a view
        /// left in Existing showed none of them (found live 2026-09-30, "S2 Achsonometrie"). S2-01, the roof as it stands, keeps Existing. A view in the
        /// newest phase still shows everything built in the earlier ones.
        /// </summary>
        static void DesignViewsOnNewestPhase(Document doc, List<string> lines)
        {
            var phases = doc.Phases.Cast<Phase>().ToList();
            if (phases.Count < 2) { lines.Add("phases: the project has one phase"); return; }
            var status = SportifyPhases.Read(doc);
            var newest = status.PostAnalysis ?? status.DesignAndAnalysis ?? phases[phases.Count - 1];
            var design = new[] { "S2-02", "S2-03", "S2-04", "S2-05", "S2-06", "S2-07" };
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(x => design.Contains(x.SheetNumber)).ToList();
            using var t = new Transaction(doc, "Sportify: design views on the newest phase");
            t.Start();
            foreach (var sheet in sheets)
                foreach (var id in sheet.GetAllPlacedViews())
                {
                    if (doc.GetElement(id) is not View v || v.IsTemplate) continue;
                    var p = v.get_Parameter(BuiltInParameter.VIEW_PHASE);
                    if (p == null || p.IsReadOnly) { lines.Add($"phases: \"{v.Name}\" has no phase to set"); continue; }
                    var was = doc.GetElement(p.AsElementId())?.Name ?? "?";
                    if (p.AsElementId() == newest.Id) continue;
                    try { p.Set(newest.Id); lines.Add($"phases: \"{v.Name}\" (sheet {sheet.SheetNumber}) {was} -> {newest.Name}"); }
                    catch (Exception ex) { lines.Add($"phases: \"{v.Name}\" could not be set: {ex.Message}"); }
                }
            t.Commit();
        }

        /// <summary>
        /// Takes away what imports recorded under a key that names no roof ("0") stood below SPORTIFY_FIX_STRAYS_BELOW_M (metres, every underside): a layout
        /// whose height named no roof, built under the building (2026-09-30, the team's export "(6)" at 0 m). Run only on purpose, after the report
        /// ("ledger @0 …") showed them; nothing happens without the height. Never touches a design option or a roof's own imports.
        /// </summary>
        static void RemoveStrays(Document doc, List<string> lines)
        {
            if (!double.TryParse(Environment.GetEnvironmentVariable("SPORTIFY_FIX_STRAYS_BELOW_M"), System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var belowM))
            { lines.Add("strays: SPORTIFY_FIX_STRAYS_BELOW_M is not set, nothing removed"); return; }
            var belowFt = UnitUtils.ConvertToInternalUnits(belowM, UnitTypeId.Meters);
            // by the undersides, as the report shows them (a sketch or a line recorded with a piece has no box of its own)
            var strays = ImportLedger.ReadEntries(doc).Where(e => RoofMatch.IsUnnamedRoofKey(e.RoofKey) && e.Elements.All(x => x.DesignOption == null)
                && e.Elements.Select(x => x.get_BoundingBox(null)).Where(b => b != null).ToList() is var boxes && boxes.Count > 0 && boxes.All(b => b!.Min.Z < belowFt)).ToList();
            if (strays.Count == 0) { lines.Add($"strays: no import under an unnamed roof stands below {belowM} m"); return; }
            using var t = new Transaction(doc, "Sportify: remove imports built below the roof");
            t.Start();
            foreach (var e in strays)
            {
                var ids = e.Elements.Select(x => x.Id).Where(id => doc.GetElement(id) != null).ToList();
                var removed = 0;
                try { removed = doc.Delete(ids).Count; doc.Delete(e.Storage.Id); }
                catch (Exception ex) { lines.Add($"strays: the import of {e.ImportedAtUtc:MM-dd HH:mm}Z could not be removed: {ex.Message}"); continue; }
                lines.Add($"strays: removed the import of {e.ImportedAtUtc:MM-dd HH:mm}Z \"{e.Source}\" under roof \"{e.RoofKey}\": {ids.Count} element(s) ({removed} with what depended on them)");
            }
            t.Commit();
        }

        /// <summary>
        /// Writes into the project what this computer remembers of it and the project itself does not hold yet, so it reaches the central with the next
        /// synchronization and everyone who opens it (user, 2026-10-01: the professor should not have to push again): every push kept on this computer
        /// (ProjectMemoryFiles), and the layout of SPORTIFY_FIX_LAYOUT_SEED for roof SPORTIFY_FIX_LAYOUT_ROOF when the project has none for that roof.
        /// </summary>
        static void KeepMemory(Document doc, List<string> lines)
        {
            var inProject = PushedRoofStore.Read(doc);
            foreach (var f in ProjectMemoryFiles.Read(doc, "push"))
            {
                if (!long.TryParse(f.RoofKey, out var id) || inProject.Any(p => p.RoofId == id)) continue;
                PushedRoofStore.Write(doc, id, f.Json);
                lines.Add($"memory: the push of roof {id} ({f.WrittenUtc.ToLocalTime():dd.MM.yyyy HH:mm}) written into the project");
            }
            var seed = Environment.GetEnvironmentVariable("SPORTIFY_FIX_LAYOUT_SEED");
            var roof = Environment.GetEnvironmentVariable("SPORTIFY_FIX_LAYOUT_ROOF") ?? "";
            if (string.IsNullOrEmpty(seed) || !File.Exists(seed)) return;
            if (ImportedLayoutStore.Read(doc, roof) is ImportedLayoutStore.Stored have && have.RoofKey == roof) { lines.Add($"memory: the project already holds a layout for roof {roof}"); return; }
            using var t = new Transaction(doc, "Sportify: remember the layout");
            t.Start();
            ImportedLayoutStore.Write(doc, roof, File.ReadAllText(seed));
            t.Commit();
            lines.Add($"memory: the layout {Path.GetFileName(seed)} written into the project for roof {roof}");
        }

        sealed record TeamMember(string Name, string? Email);
        sealed record Team(string Name, List<TeamMember> Members);
        sealed record TeamFile(List<Team> Teams);

        /// <summary>
        /// The project team in the model (user, 2026-09-30), from SPORTIFY_FIX_TEAM (a local JSON: {"teams":[{"name","members":[{"name","email"}]}]}; kept out
        /// of the public repository): Project Information's Author (every team and its names) and Organization (TH OWL); a legend "S Projektteam" / "S Project
        /// Team" with every name and e-mail, placed at the top right of every Sportify sheet (a legend is the one view that can be on many sheets); "Designed By"
        /// on the Sportify sheets: the computational team, which designed the plan set. Drawn / Checked / Approved By are left as they are (reported).
        /// Nothing here pretends anyone worked in the file: no username, workset owner or history is touched.
        /// </summary>
        static void ApplyTeam(Document doc, string? path, List<string> lines)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { lines.Add("team: no SPORTIFY_FIX_TEAM file"); return; }
            var team = System.Text.Json.JsonSerializer.Deserialize<TeamFile>(File.ReadAllText(path), new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (team?.Teams == null || team.Teams.Count == 0) { lines.Add("team: the file lists no team"); return; }
            var names = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate).Select(v => v.Name).ToList();
            var german = (SportifyTemplateSpec.Detect(names) ?? TemplateLanguage.De) == TemplateLanguage.De;

            using var t = new Transaction(doc, "Sportify: project team");
            t.Start();
            var info = doc.ProjectInformation;
            void Set(BuiltInParameter bip, string value)
            {
                var p = info.get_Parameter(bip);
                if (p == null || p.IsReadOnly) { lines.Add($"team: Project Information {bip} cannot be set"); return; }
                var was = p.AsString();
                // a value a person typed is theirs (found 2026-09-30: the team had filled Author and Organization themselves); only an empty field is filled
                if (!string.IsNullOrWhiteSpace(was)) { lines.Add($"team: Project Information {bip} kept as typed: \"{was}\""); return; }
                p.Set(value); lines.Add($"team: Project Information {bip}: \"{value}\"");
            }
            Set(BuiltInParameter.PROJECT_AUTHOR, string.Join("; ", team.Teams.Select(x => x.Name + ": " + string.Join(", ", x.Members.Select(m => m.Name)))));
            Set(BuiltInParameter.PROJECT_ORGANIZATION_NAME, "TH OWL - Digital Tools and Methods 2");
            Set(BuiltInParameter.PROJECT_ORGANIZATION_DESCRIPTION, string.Join(" and ", team.Teams.Select(x => x.Name)) + ", with GOLDBECK");

            var legend = TeamLegend(doc, team, german, lines);
            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.SheetNumber, @"^S[1-4]-\d\d$") || x.SheetNumber.StartsWith("SPORT")).ToList();
            var designer = team.Teams[0].Name;
            foreach (var sheet in sheets)
            {
                var d = sheet.get_Parameter(BuiltInParameter.SHEET_DESIGNED_BY);
                if (d != null && !d.IsReadOnly && d.AsString() != designer) d.Set(designer);
                if (legend == null) continue;
                var onIt = new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)).Cast<Viewport>().Any(vp => vp.ViewId == legend.Id);
                if (onIt || !Viewport.CanAddViewToSheet(doc, sheet.Id, legend.Id)) continue;
                var frame = SportifySheets.Frame(doc, sheet);
                var placed = Viewport.Create(doc, sheet.Id, legend.Id, new XYZ(frame.Max.X - 110 / 304.8, frame.Max.Y - 60 / 304.8, 0));
                doc.Regenerate();
                var box = placed.GetBoxOutline();                                   // its top right corner 15 mm inside the frame's
                placed.SetBoxCenter(placed.GetBoxCenter() + new XYZ(frame.Max.X - 15 / 304.8 - box.MaximumPoint.X, frame.Max.Y - 15 / 304.8 - box.MaximumPoint.Y, 0));
            }
            t.Commit();
            lines.Add($"team: \"{legend?.Name}\" on {sheets.Count} Sportify sheet(s); Designed By = \"{designer}\" on them");
        }

        /// <summary>The team legend: the project's own of that name (its text replaced), else a copy of any legend emptied (Revit's API cannot make a legend from nothing).</summary>
        static View? TeamLegend(Document doc, TeamFile team, bool german, List<string> lines)
        {
            var name = german ? "S Projektteam" : "S Project Team";
            var legends = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.ViewType == ViewType.Legend && !v.IsTemplate).ToList();
            var legend = legends.FirstOrDefault(v => v.Name == name);
            if (legend == null)
            {
                var source = legends.FirstOrDefault();
                if (source == null) { lines.Add("team: the project has no legend to copy, so no team legend"); return null; }
                legend = (View)doc.GetElement(source.Duplicate(ViewDuplicateOption.Duplicate));
                legend.Name = name;
            }
            // only what is drawn in it (found in the dry run: deleting everything the view "owned" took the view with it)
            var own = new FilteredElementCollector(doc, legend.Id).WhereElementIsNotElementType()
                .Where(e => e.Id != legend.Id && e.OwnerViewId == legend.Id && (e is TextNote || e is CurveElement || e is FilledRegion
                            || e.Category?.Id.Value == (long)BuiltInCategory.OST_LegendComponents)).Select(e => e.Id).ToList();
            if (own.Count > 0) doc.Delete(own);
            var text = new StringBuilder(german ? "PROJEKTTEAM" : "PROJECT TEAM");
            foreach (var tm in team.Teams)
            {
                text.Append("\r\r").Append(tm.Name.ToUpperInvariant());
                foreach (var m in tm.Members) text.Append("\r").Append(m.Name).Append(string.IsNullOrWhiteSpace(m.Email) ? "" : "   " + m.Email);
            }
            var typeId = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).FirstElementId();
            TextNote.Create(doc, legend.Id, XYZ.Zero, text.ToString(), typeId);
            SportifyTemplateBuilder.SetBrowserGrouping(legend);
            lines.Add($"team: legend \"{name}\" with {team.Teams.Sum(x => x.Members.Count)} name(s)");
            return legend;
        }

        /// <summary>Every Sportify sheet as a PNG in `folder`, to look at without opening Revit.</summary>
        static void ExportSheets(Document doc, string folder, List<string> lines)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var ids = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.SheetNumber, @"^S[1-4]-") || x.SheetNumber.StartsWith("SPORT")).Select(x => x.Id).ToList();
                ids.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>().Where(v => !v.IsTemplate && (v.Name.StartsWith(SportifyTagView.Title) || v.Name.StartsWith("Sportify - Zoning"))).Select(v => v.Id));
                var opts = new ImageExportOptions
                {
                    ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(folder, "sheet"), HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150, ZoomType = ZoomFitType.FitToPage, PixelSize = 2400,
                };
                opts.SetViewsAndSheets(ids);
                doc.ExportImage(opts);
                lines.Add($"exported {ids.Count} sheet(s)/view(s) to {folder}");
            }
            catch (Exception ex) { lines.Add("export failed: " + ex.Message); }
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
