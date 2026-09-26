using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// An unattended walk through the Kinetics panel, the way a person uses it: not the classes called by hand (that is KineticsSelfTest) but the ribbon's own commands, posted to Revit
    /// with PostCommand one after the other, in a project that is really open, on a REAL layout the web app exported (the newest sportify_combined_revit*.json in the Sportify folder's
    /// Layouts, or SPORTIFY_WALKTHROUGH_LAYOUT). Every dialog Revit would show is answered (the first command link: "yes, do it") and written down with what it said, the WPF dialog
    /// that asks for the kind is answered through a seam (KineticsPriorityDialog.AutoAnswer), and after every command what it did is read back from the model and from what the web
    /// app would be given. SPORTIFY_KINETICS_WALKTHROUGH=&lt;folder&gt; runs it once at startup, writes kinetics-walkthrough.json and kinetics-payload.json there and exits Revit
    /// (SPORTIFY_KINETICS_WALKTHROUGH_KEEP=1 leaves it open; SPORTIFY_KINETICS_WALKTHROUGH_SW=0 leaves the SOLIDWORKS step out). Off unless that variable is set.
    /// What it cannot do: press a button with a mouse (the commands are the same, the ribbon is not), record the Unity film (it drives the real Unity project) or run the Ball
    /// Trajectory analysis (Unity), so the roller fence is reported as "no fence to make", which is what a person would see.
    /// </summary>
    internal static class KineticsWalkthrough
    {
        sealed class Dialog { public string Step { get; set; } = ""; public string Id { get; set; } = ""; public string Text { get; set; } = ""; }

        sealed class Report
        {
            public string Layout { get; set; } = "";
            public List<string> Steps { get; set; } = new();
            public List<string> Failures { get; set; } = new();
            public List<Dialog> Dialogs { get; set; } = new();
            public List<string> Files { get; set; } = new();
            public bool Ok => Failures.Count == 0;
        }

        sealed class Step
        {
            public string Name = "";
            public string? Command;                          // the ribbon button's internal name in the Kinetics panel
            public Action<UIApplication>? Before;
            public Action<UIApplication>? After;
        }

        static readonly Report R = new();
        static readonly List<Step> Steps = new();
        static string _dir = "";
        static string _step = "start";
        static int _i = -1;
        static bool _posted, _exiting, _built;
        static DateTime _postedAt, _began;
        static ElementId _wallId = ElementId.InvalidElementId, _railingId = ElementId.InvalidElementId;
        static HashSet<long> _idsBefore = new();          // the kinetic parts that were in the project before the command (a re-placement replaces them: new ids, the same count)
        static KineticKind _kind = KineticKind.Overhead;
        static SailShapeChoice _shape = SailShapeChoice.Auto;

        internal static void Install(UIControlledApplication application)
        {
            var dir = Environment.GetEnvironmentVariable("SPORTIFY_KINETICS_WALKTHROUGH");
            if (string.IsNullOrWhiteSpace(dir)) return;
            _dir = dir!;
            _began = DateTime.UtcNow;
            application.DialogBoxShowing += OnDialog;
            // "Place one where I pick": the centre of the roof, the way a person might click it
            KineticsHosts.TestPick = (kind, type) =>
            {
                var (l, w) = SportifyLayoutBuilder.CurrentRoofSizeM;
                return KineticsHosts.PieceAt(KineticsPlan.ToWorld(l > 0 ? l / 2 : 5, w > 0 ? w / 2 : 5), kind, type);
            };
            KineticsPriorityDialog.AutoAnswer = _ => new KineticsPriorityDialog.Choice { Priority = "sun", Kind = _kind, SailShape = _shape };
            application.Idling += OnIdle;
        }

        static void OnDialog(object? sender, DialogBoxShowingEventArgs e)
        {
            var text = e is TaskDialogShowingEventArgs t ? t.Message : "";
            R.Dialogs.Add(new Dialog { Step = _step, Id = e.DialogId ?? "", Text = (text ?? "").Replace("\r", "").Trim() });
            try
            {
                // closing Revit: never save the scratch project; everywhere else the first command link is the "yes, do it" of every question Kinetics asks
                e.OverrideResult(_exiting ? (int)TaskDialogResult.No : e is TaskDialogShowingEventArgs ? (int)TaskDialogResult.CommandLink1 : 1);
            }
            catch (Exception ex) { R.Failures.Add("a dialog could not be answered (" + ex.Message + "): " + text); }
        }

        static string CommandId(string button) => "CustomCtrl_%CustomCtrl_%" + RibbonLayout.TabName + "%Kinetics%" + button;

        static string? FindLayout()
        {
            var env = Environment.GetEnvironmentVariable("SPORTIFY_WALKTHROUGH_LAYOUT");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            var folder = SportifyWorkspace.PathFor("layouts");
            return Directory.GetFiles(folder, "sportify_combined_revit*.json").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        static IEnumerable<FamilyInstance> KineticParts(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Where(f => f.Symbol?.Family?.Name?.StartsWith("SportifyKineticAdaptive") == true);

        static AnalysisResultPayload? Payload()
        {
            if (!RoofBoundaryServer.TryGetLatestAnalysisResults(out var json) || json == null) return null;
            try { return JsonSerializer.Deserialize<AnalysisResultPayload>(json); } catch (Exception) { return null; }
        }

        static void Fail(string what) { R.Failures.Add("[" + _step + "] " + what); }

        static IEnumerable<string> DialogsOfStep() => R.Dialogs.Where(d => d.Step == _step).Select(d => d.Text.Split('\n')[0]);

        // ------------------------------------------------------------------ the walk

        static void Build()
        {
            _built = true;

            Steps.Add(new Step
            {
                Name = "open a scratch project",
                Before = ui =>
                {
                    var app = ui.Application;
                    var template = app.DefaultProjectTemplate;
                    if (string.IsNullOrEmpty(template) || !File.Exists(template))
                        template = Directory.EnumerateFiles(@"C:\ProgramData\Autodesk\RVT " + app.VersionNumber + @"\Templates", "*.rte", SearchOption.AllDirectories).First();
                    var doc = app.NewProjectDocument(template);
                    var path = Path.Combine(_dir, "walkthrough.rvt");
                    doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                    doc.Close(false);
                    ui.OpenAndActivateDocument(path);
                },
                After = ui => { if (ui.ActiveUIDocument == null) Fail("no project is open"); else R.Steps.Add("open: " + ui.ActiveUIDocument.Document.Title); },
            });

            Steps.Add(new Step
            {
                Name = "the web app's layout arrives",
                Before = ui =>
                {
                    var file = FindLayout();
                    if (file == null) { Fail("there is no exported layout in the Sportify folder's Layouts (sportify_combined_revit*.json)"); return; }
                    var json = File.ReadAllText(file);
                    R.Layout = file;
                    RoofBoundaryServer.SetCombinedLayoutPayload(json);         // what the server does when the web app POSTs /combined-layout
                    var layout = JsonSerializer.Deserialize<SportifyLayout>(json);
                    R.Steps.Add("layout: " + Path.GetFileName(file) + " (" + json.Length + " bytes), roof " + (layout?.RoofContext == null ? "size unknown" : layout.RoofContext.LengthM.ToString("0.#") + " x " + layout.RoofContext.WidthM.ToString("0.#") + " m") + ", " + (layout?.Placements?.Count ?? 0) + " placements");
                },
            });

            Steps.Add(new Step
            {
                Name = "a wall and a railing to make screens on",
                Before = ui =>
                {
                    var doc = ui.ActiveUIDocument.Document;
                    using var t = new Transaction(doc, "Kinetics walkthrough: hosts");
                    t.Start();
                    var level = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().FirstOrDefault();
                    var wallType = new FilteredElementCollector(doc).OfClass(typeof(WallType)).FirstElementId();
                    if (level != null && wallType != ElementId.InvalidElementId)
                        _wallId = Wall.Create(doc, Line.CreateBound(new XYZ(0, 0, 0), new XYZ(8 / 0.3048, 0, 0)), wallType, level.Id, 3.0 / 0.3048, 0, false, false).Id;
                    var railingType = new FilteredElementCollector(doc).OfClass(typeof(RailingType)).FirstElementId();
                    if (level != null && railingType != ElementId.InvalidElementId)
                    {
                        try { var loop = new CurveLoop(); loop.Append(Line.CreateBound(new XYZ(0, 5 / 0.3048, 0), new XYZ(6 / 0.3048, 5 / 0.3048, 0))); _railingId = Railing.Create(doc, loop, railingType, level.Id).Id; }
                        catch (Exception ex) { R.Steps.Add("no railing could be made in the scratch project: " + ex.Message); }
                    }
                    t.Commit();
                    R.Steps.Add("hosts: wall " + _wallId.Value + ", railing " + _railingId.Value);
                },
            });

            Steps.Add(new Step
            {
                Name = "Generate Family",
                Command = "GenerateKineticFamily",
                After = ui =>
                {
                    var names = new FilteredElementCollector(ui.ActiveUIDocument.Document).OfClass(typeof(Family)).Cast<Family>().Select(f => f.Name).Where(n => n.StartsWith("SportifyKineticAdaptive")).ToList();
                    R.Steps.Add("families in the project: " + string.Join(", ", names));
                    if (names.Count < 2) Fail("the two adaptive families are not both in the project (" + names.Count + ")");
                },
            });

            Steps.Add(new Step
            {
                Name = "Choose Family",
                Command = "ChooseKineticFamily",
                After = ui =>
                {
                    var said = DialogsOfStep().ToList();
                    R.Steps.Add("Choose Family said: " + string.Join(" | ", said));
                    if (said.Count == 0) Fail("Choose Family showed nothing");
                },
            });

            void Import(string name, KineticKind kind, SailShapeChoice shape, Func<UIApplication, bool>? select, string expectPiece)
            {
                Steps.Add(new Step
                {
                    Name = name,
                    Command = "ImportKineticAdaptation",
                    Before = ui =>
                    {
                        _kind = kind; _shape = shape;
                        _idsBefore = KineticParts(ui.ActiveUIDocument.Document).Select(p => p.Id.Value).ToHashSet();
                        ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId>());
                        if (select != null && !select(ui)) Fail("nothing to select for this kind");
                    },
                    After = ui =>
                    {
                        var doc = ui.ActiveUIDocument.Document;
                        var all = KineticParts(doc).ToList();
                        var parts = all.Count(p => !_idsBefore.Contains(p.Id.Value));      // placed by this command
                        var payload = Payload()?.Kinetics;
                        var piece = payload?.Pieces?.FirstOrDefault(p => (p.Kind ?? "") == expectPiece);
                        R.Steps.Add(name + ": " + parts + " parts placed (" + all.Count + " in the project); the web app's payload has " + (payload?.Pieces?.Count ?? 0) + " piece(s)" + (piece == null ? ", none of kind " + expectPiece : ", " + expectPiece + ": " + (piece.EquipmentName ?? piece.EquipmentKey) + ", " + piece.BladeCount + " blades/masts")
                                    + "; dialogs: " + string.Join(" | ", DialogsOfStep()));
                        if (kind == KineticKind.Fence) return;                             // needs the Ball Trajectory analysis (Unity): "no fence to make" is the honest answer here
                        if (parts == 0) Fail(name + " placed nothing");
                        if (piece == null) Fail(name + " published no piece of kind " + expectPiece + " for the web app");
                    },
                });
            }
            Import("Import Analysis Adaptation: overhead louvre", KineticKind.Overhead, SailShapeChoice.Auto, null, "overhead");
            Import("Import Analysis Adaptation: sail (auto shape)", KineticKind.Sail, SailShapeChoice.Auto, null, "sail");
            Import("Import Analysis Adaptation: sail (triangle)", KineticKind.Sail, SailShapeChoice.Triangle, null, "sail");
            Import("Import Analysis Adaptation: slat screen on the wall", KineticKind.Slats, SailShapeChoice.Auto, ui => { if (_wallId == ElementId.InvalidElementId) return false; ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId> { _wallId }); return true; }, "slats");
            Import("Import Analysis Adaptation: fin screen on the railing", KineticKind.Fins, SailShapeChoice.Auto, ui => { if (_railingId == ElementId.InvalidElementId) return false; ui.ActiveUIDocument.Selection.SetElementIds(new List<ElementId> { _railingId }); return true; }, "fins");
            Import("Import Analysis Adaptation: roller fence", KineticKind.Fence, SailShapeChoice.Auto, null, "fence");

            if (Environment.GetEnvironmentVariable("SPORTIFY_KINETICS_WALKTHROUGH_SW") != "0")
            {
                Steps.Add(new Step
                {
                    Name = "Simulate (SOLIDWORKS)",
                    Command = "SimulateKinetics",
                    Before = ui => { _kind = KineticKind.Overhead; _shape = SailShapeChoice.Auto; },
                    After = ui =>
                    {
                        var payload = Payload()?.Kinetics;
                        var film = payload?.SimulationVideoPath;
                        R.Steps.Add("Simulate: film " + (film ?? "(none)") + "; dialogs: " + string.Join(" | ", DialogsOfStep()));
                        if (string.IsNullOrEmpty(film) || !File.Exists(film)) { Fail("the SOLIDWORKS film was not made or not published"); return; }
                        var size = new FileInfo(film).Length;
                        R.Files.Add(film + " (" + size + " bytes)");
                        if (size < 100_000) Fail("the SOLIDWORKS film is only " + size + " bytes");
                        if (File.GetLastWriteTimeUtc(film) < _began) Fail("the SOLIDWORKS film is older than this run");
                    },
                });
            }
        }

        // ------------------------------------------------------------------ driving it

        static void OnIdle(object? sender, IdlingEventArgs e)
        {
            if (sender is not UIApplication ui || _exiting) return;
            e.SetRaiseWithoutDelay();
            if (_posted && (DateTime.UtcNow - _postedAt).TotalSeconds < 3) return;      // a posted command has not started yet, or has only just finished
            try
            {
                if (!_built) Build();
                if (_i >= 0) { _step = Steps[_i].Name; Steps[_i].After?.Invoke(ui); _posted = false; }
                _i++;
                if (_i >= Steps.Count) { Finish(ui); return; }
                var st = Steps[_i];
                _step = st.Name;
                R.Steps.Add("== " + st.Name);
                st.Before?.Invoke(ui);
                if (st.Command != null)
                {
                    var id = RevitCommandId.LookupCommandId(CommandId(st.Command));
                    if (id == null) { Fail("the ribbon has no button " + st.Command); _posted = false; return; }
                    ui.PostCommand(id);
                    _postedAt = DateTime.UtcNow; _posted = true;
                }
            }
            catch (Exception ex) { Fail("the walk-through itself failed: " + ex); Finish(ui); }
        }

        static void Finish(UIApplication ui)
        {
            if (_exiting) return;
            _exiting = true;
            try
            {
                if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var json) && json != null)
                {
                    File.WriteAllText(Path.Combine(_dir, "kinetics-payload.json"), json);
                    R.Files.Add(Path.Combine(_dir, "kinetics-payload.json"));
                }
                R.Steps.Add("the whole walk took " + (DateTime.UtcNow - _began).TotalMinutes.ToString("0.0") + " min");
                File.WriteAllText(Path.Combine(_dir, "kinetics-walkthrough.json"), JsonSerializer.Serialize(R, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { SportifyLog.Warn("walkthrough", "the report could not be written: " + ex.Message); }
            SportifyLog.Info("walkthrough", "Kinetics walk-through " + (R.Ok ? "PASSED" : "FAILED (" + R.Failures.Count + ")") + ", report in " + _dir);
            KineticsPriorityDialog.AutoAnswer = null;
            if (Environment.GetEnvironmentVariable("SPORTIFY_KINETICS_WALKTHROUGH_KEEP") == "1") return;
            try { ui.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit)); }
            catch (Exception) { Process.GetCurrentProcess().Kill(); }
        }
    }
}
