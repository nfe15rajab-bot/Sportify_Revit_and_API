using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Sportify.Simulation.Sun;

namespace SportfyRevit
{
    /// <summary>
    /// Shared ground for the Kinetics commands: the priority and kind dialog, reading the published results the units answer (the sun and shade result's pergolas and sails,
    /// the ball analysis's fences, the wind pressure), finding each kind's hosts, working the mechanics out, and putting the units in the project as adaptive components on
    /// their worksets and in the Post analysis phase.
    /// </summary>
    internal static class KineticsShared
    {
        internal const string Title = "Sportify — Kinetics";
        private static string _lastPriority = "sun";
        private static KineticKind _lastKind = KineticKind.Overhead;

        internal sealed class Context
        {
            public string Priority = "sun";
            public KineticKind Kind;
            public SunAndShadingResultDto? Sun;
            public List<KineticHost> Hosts = new();
            public KineticEnvironment Env = new();
            /// <summary>True when Kinetics ran the Sun &amp; Shade Analysis itself, for lack of a current one: the dialogs then say so.</summary>
            public bool RanSunAnalysis;
            public string SunAnalysisMode = "fixed";
            public List<string> Notes = new();
            public SailShapeChoice SailShape = SailShapeChoice.Auto;
        }

        /// <summary>The line a dialog starts with when Kinetics had to run the analysis itself; empty otherwise.</summary>
        internal static string RanNote(Context ctx) => ctx.RanSunAnalysis
            ? "There was no Sun & Shade result for this layout, so Kinetics ran the analysis first (allowing only " +
              (ctx.SunAnalysisMode == "light" ? "the light pieces: sails and parasols" : "fixed structures: pergolas, canopies") + ").\n\n"
            : "";

        static AnalysisResultPayload? ReadPayload()
        {
            if (!RoofBoundaryServer.TryGetLatestAnalysisResults(out var json) || json == null) return null;
            try { return JsonSerializer.Deserialize<AnalysisResultPayload>(json); }
            catch (Exception ex) { SportifyLog.Warn("kinetics", "the published results could not be read: " + ex.Message); return null; }
        }

        /// <summary>A sun and shade result exists for the layout on screen: a section of another layout, or one with no stamp, is as good as none.</summary>
        static bool HasCurrentSun(AnalysisResultPayload? payload)
        {
            if (payload?.SunAndShading == null) return false;
            var layoutId = RoofBoundaryServer.LayoutIdForPublishing;
            if (layoutId == null) return true;
            return payload.Sections.TryGetValue("sun_and_shading", out var info) && info.LayoutId == layoutId;
        }

        /// <summary>
        /// The roof's plan frame and elevation from the layout the web app last pushed, so every unit stands where the analysis put it, also in a Revit
        /// session that never imported it. Returns the deserialized layout itself (null if there is none, or it could not be read) so the caller can
        /// also read any kinetic elements the designer placed by hand in it (KineticsHosts.FromPlacements) — one read serves both.
        /// </summary>
        static SportifyLayout? AdoptLayoutFrame()
        {
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var baseJson, out _);
            if (baseJson == null) return null;
            try
            {
                var layout = JsonSerializer.Deserialize<SportifyLayout>(baseJson);
                if (layout != null) SportifyLayoutBuilder.AdoptRoofFrame(layout);
                return layout;
            }
            catch (Exception ex) { SportifyLog.Warn("kinetics", "the roof frame of the last layout could not be read: " + ex.Message); return null; }
        }

        /// <summary>
        /// Runs the Sun &amp; Shade Analysis on the layout the web app last pushed, in-process and without a window, with the inputs remembered this session and the shading
        /// equipment restricted to <paramref name="equipment"/> ("fixed": pergolas and canopies; "light": sails and parasols). Publishes the result like the ribbon command does.
        /// False, with the reason in <paramref name="problem"/>, when it cannot.
        /// </summary>
        static bool RunSunAnalysis(string equipment, out string problem)
        {
            problem = "";
            RoofBoundaryServer.TryGetLatestCombinedLayout(out var baseJson, out _);
            if (baseJson == null)
            {
                problem = "There is no layout to analyse yet. Push or import one from the Sportify web app (Combine tab) first.";
                return false;
            }
            try
            {
                var layout = JsonSerializer.Deserialize<SportifyLayout>(AssumptionsSession.ApplyRemembered(baseJson)) ?? throw new InvalidOperationException("The layout was empty.");
                var inputs = SunLayoutAdapter.ToInputs(layout);
                if (inputs.Structure.Items.Count == 0)
                {
                    problem = "The layout has nothing on the roof yet (no sports field, activity, green-roof zone or tree), so there is nothing to shade. Place something in the Combine tab first.";
                    return false;
                }
                inputs.Equipment = equipment;
                var report = SunModel.Analyse(inputs);
                AnalysisResultPublisher.PublishSunAndShading(AnalyzeSunShadeCommand.BuildPublishedResult(report, SunModel.CaseStudy(inputs, report), null));
                SportifyLog.Info("kinetics", "ran the Sun & Shade Analysis for Kinetics (" + equipment + " only): " + report.equipment.Count + " piece(s) recommended");
                return true;
            }
            catch (Exception ex)
            {
                SportifyLog.Warn("kinetics", "the Sun & Shade Analysis could not be run for Kinetics: " + ex);
                problem = "The Sun & Shade Analysis could not be run: " + ex.Message;
                return false;
            }
        }

        /// <summary>The priority dropdown alone: the chosen key, or null when cancelled.</summary>
        internal static string? AskPriority(IntPtr owner)
        {
            var choice = Ask(owner, askKind: false);
            return choice?.Priority;
        }

        static KineticsPriorityDialog.Choice? Ask(IntPtr owner, bool askKind)
        {
            var choice = KineticsPriorityDialog.Ask(Title, owner, _lastPriority, _lastKind, askKind);
            if (choice == null) return null;
            _lastPriority = choice.Priority;
            if (askKind) _lastKind = choice.Kind;

            if (askKind)
            {
                var kindInfo = KineticKinds.Get(choice.Kind);
                if (!kindInfo.Built)
                {
                    TaskDialog.Show(Title, kindInfo.Label + " has no dynamic mechanism built yet — it can be placed in the web app's Kinetics tab for spatial planning, but Import Analysis Adaptation has nothing to actuate it with. Pick a built kind (overhead louvre, slat/fin screen, sail, roller fence), or come back once this one has one.");
                    return null;
                }
            }
            return choice;
        }

        static List<SunEquipmentDto> Pieces(SunAndShadingResultDto? sun, KineticKind kind)
        {
            var all = sun?.Equipment ?? new List<SunEquipmentDto>();
            return kind == KineticKind.Sail ? all.Where(e => e.Key == "sail").ToList() : all.Where(e => e.Key == "pergola" || e.Key == "canopy").ToList();
        }

        /// <summary>The wind pressure the units are checked against: the sun and shade result's peak pressure at the roof, else the wind analysis's, else a stated placeholder.</summary>
        static void SetEnvironment(Context ctx, AnalysisResultPayload? payload)
        {
            var sun = ctx.Sun;
            if (sun != null)
            {
                ctx.Env.LatitudeDeg = sun.LatitudeDeg; ctx.Env.NorthDeg = sun.NorthDeg;
                if (sun.ShadeTargetPercent > 0) ctx.Env.ShadeTargetPercent = sun.ShadeTargetPercent;
            }
            if (sun != null && sun.PeakWindPressurePa > 0) { ctx.Env.PressurePa = sun.PeakWindPressurePa; ctx.Env.PressureNote = "the Sun & Shade result's peak wind pressure at the roof"; }
            else if (payload?.WindErosion != null && payload.WindErosion.PeakPressurePa > 0) { ctx.Env.PressurePa = payload.WindErosion.PeakPressurePa; ctx.Env.PressureNote = "the Wind & Erosion result's peak pressure"; }
            else
            {
                ctx.Env.PressurePa = 600; ctx.Env.PressureNote = "a PLACEHOLDER 600 Pa: neither the Sun & Shade nor the Wind & Erosion analysis has published a peak pressure yet (run the Wind & Erosion analysis)";
                if (!ctx.Notes.Any(n => n.StartsWith("Wind:"))) ctx.Notes.Add("Wind: " + ctx.Env.PressureNote + ".");
            }
        }

        /// <summary>
        /// Asks what to make, reads the results that kind answers, and finds its hosts. Returns null (with a dialog already shown) when anything stops short of "ready to act":
        /// pergolas and sails come from the Sun &amp; Shade result (run here when there is none), screens from the railings or walls the person selects, fences from the ball analysis.
        /// </summary>
        internal static Context? Prepare(ExternalCommandData data)
        {
            var owner = data.Application.MainWindowHandle;
            var choice = Ask(owner, askKind: true);
            if (choice == null) return null;
            var ctx = new Context { Priority = choice.Priority, Kind = choice.Kind, SailShape = choice.SailShape };

            var layout = AdoptLayoutFrame();
            var payload = ReadPayload();
            var screenKind = ctx.Kind == KineticKind.Slats || ctx.Kind == KineticKind.Fins;

            // A designer who placed this kind by hand in the web app's Kinetics tab decided where and how many
            // already — try that first, for every kind FromPlacements answers (every kind except Slats/Fins,
            // which are tied to picking a real wall/railing in the model, not a 2D footprint on the plan).
            // Whichever path finds hosts, SetEnvironment at the end still sizes the mechanism from real sun/wind
            // numbers — this only ever changes WHERE the hosts come from.
            if (!screenKind)
            {
                var placed = KineticsHosts.FromPlacements(layout, ctx.Kind);
                if (placed.Count > 0)
                {
                    ctx.Sun = payload?.SunAndShading;
                    ctx.Hosts = placed;
                    ctx.Notes.Add(placed.Count + " placed in the web app's Kinetics tab: position and count follow the layout, not an analysis recommendation.");
                    foreach (var h in ctx.Hosts) if (h.SiteNote.Length > 0) ctx.Notes.Add(h.Name + ": " + h.SiteNote + ".");
                    SetEnvironment(ctx, payload);
                    return ctx;
                }
            }

            // ---- nothing of this kind is placed: each kind's own fallback. Overhead/Sail answer the Sun &
            // Shade result, Fence the Ball Trajectory result, Slats/Fins a wall/railing you select — the three
            // ways this worked before the web app's Kinetics tab existed. The six kinds added since (PV canopy,
            // wind-break, divider net, acoustic screen, green screen, membrane roof) have no such analysis-
            // derived fallback — nothing in either published result proposes one — so placement in the web
            // app's Kinetics tab is their only source, and FromPlacements above just found none.
            if (ctx.Kind == KineticKind.Overhead || ctx.Kind == KineticKind.Sail)
            {
                ctx.SunAnalysisMode = ctx.Kind == KineticKind.Sail ? "light" : "fixed";
                if (!HasCurrentSun(payload))
                {
                    if (!RunSunAnalysis(ctx.SunAnalysisMode, out var problem)) { TaskDialog.Show(Title, problem); return null; }
                    ctx.RanSunAnalysis = true; payload = ReadPayload();
                }
                ctx.Sun = payload?.SunAndShading;
                if (ctx.Sun == null) { TaskDialog.Show(Title, "No Sun & Shade Analysis result is available."); return null; }

                var pieces = Pieces(ctx.Sun, ctx.Kind);
                var wanted = ctx.Kind == KineticKind.Sail ? "a sail" : "a pergola or a canopy";
                // A result made with the default choice picks other equipment: offer to run it again for the kind wanted, which replaces it.
                if (pieces.Count == 0 && !ctx.RanSunAnalysis && (ctx.Sun.Equipment?.Count ?? 0) > 0)
                {
                    var ask = new TaskDialog(Title)
                    {
                        MainInstruction = "The Sun & Shade Analysis picked " + string.Join(", ", ctx.Sun.Equipment!.Select(e => e.Name ?? e.Key).Distinct().Select(n => n.ToLowerInvariant())) + ", not " + wanted,
                        MainContent = ctx.Kind == KineticKind.Sail
                            ? "Kinetics can put movable pillars under a shade sail; the analysis chose something else for this layout."
                            : "By default the analysis prefers the lightest piece on the deck, which is a sail. Kinetics adapts the fixed, roof-like types (louvre pergola, solid canopy) here.",
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                    };
                    ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Run it again, allowing only " + (ctx.Kind == KineticKind.Sail ? "sails and parasols" : "fixed structures"),
                        "Replaces the published sun and shade result with one that shades with " + (ctx.Kind == KineticKind.Sail ? "sails" : "pergolas or canopies") + ".");
                    ask.DefaultButton = TaskDialogResult.CommandLink1;       // after the link exists: Revit throws otherwise
                    if (ask.Show() != TaskDialogResult.CommandLink1) return null;
                    if (!RunSunAnalysis(ctx.SunAnalysisMode, out var problem2)) { TaskDialog.Show(Title, problem2); return null; }
                    ctx.RanSunAnalysis = true;
                    payload = ReadPayload();
                    ctx.Sun = payload?.SunAndShading;
                    pieces = Pieces(ctx.Sun, ctx.Kind);
                }
                if (pieces.Count == 0)
                {
                    // Nothing to adapt: no people zone is too sunny. The designer can still try the unit where they want it, at the analysis's own catalogue size.
                    var size = SunModel.Catalogue.First(t => t.Key == (ctx.Kind == KineticKind.Sail ? "sail" : "pergola"));
                    var hand = new TaskDialog(Title)
                    {
                        MainInstruction = "The Sun & Shade Analysis recommended no " + (ctx.Kind == KineticKind.Sail ? "sail" : "pergola or canopy") + " for this layout",
                        MainContent = (ctx.RanSunAnalysis ? "(Kinetics ran it just now, allowing only " + (ctx.Kind == KineticKind.Sail ? "sails and parasols" : "fixed structures") + ".) " : "") +
                                      "No people zone is too sunny at midday, so there is nothing for it to adapt: it needs an activity or a spectators' area on the roof (the courts themselves are never covered).\n\n" +
                                      "You can still place one where you want it: " + size.Sizes[0][0].ToString("0.#") + " x " + size.Sizes[0][1].ToString("0.#") + " m, " + size.HeightM.ToString("0.#") + " m high (the catalogue's own size). Its " +
                                      (ctx.Kind == KineticKind.Sail ? "masts run on their rails" : "blades' spacing and angles follow the sun") + " for that place.",
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                    };
                    hand.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Place one where I pick", "You are asked for the centre of it: a point in the active view.");
                    hand.DefaultButton = TaskDialogResult.CommandLink1;
                    if (hand.Show() != TaskDialogResult.CommandLink1) return null;
                    var byHand = KineticsHosts.PickPiece(data.Application.ActiveUIDocument, ctx.Kind, size);
                    if (byHand == null) return null;
                    pieces = new List<SunEquipmentDto> { byHand };
                    ctx.Notes.Add("Placed by hand at your pick (" + byHand.XM.ToString("0.#") + ", " + byHand.YM.ToString("0.#") + " m of the roof plan): the analysis recommended none.");
                }
                SetEnvironment(ctx, payload);
                ctx.Hosts = KineticsHosts.FromPieces(ctx.Kind, pieces, ctx.Env, ctx.SailShape, ctx.Kind == KineticKind.Sail ? KineticsSite.Read() : null);
                foreach (var h in ctx.Hosts) if (h.SiteNote.Length > 0) ctx.Notes.Add(h.Name + ": " + h.SiteNote + ".");
            }
            else if (screenKind)
            {
                // A screen only needs the place and the sun (latitude, north), not a recommended piece — reads whatever
                // is currently published rather than running a fresh analysis for it; SetEnvironment below falls back
                // to the analysis's own default latitude and a plan with north up when nothing is published yet.
                ctx.Sun = payload?.SunAndShading;
                var hosts = KineticsHosts.FromSelection(data.Application.ActiveUIDocument, ctx.Kind, out var problem);
                if (hosts == null) return null;                                   // the pick was cancelled
                if (hosts.Count == 0) { TaskDialog.Show(Title, problem); return null; }
                ctx.Hosts = hosts;
            }
            else if (ctx.Kind == KineticKind.Fence)
            {
                var fences = payload?.BallTrajectory?.Fences;
                if (fences == null || fences.Count == 0)
                {
                    TaskDialog.Show(Title, "There is no fence to make roller: run the Ball Trajectory analysis (Simulation & Analytics) first — its roof-exit sweep proposes the fences: which edge, how far along it and how high. Or place one by hand in the web app's Kinetics tab.");
                    return null;
                }
                var (roofL, roofW) = SportifyLayoutBuilder.CurrentRoofSizeM;
                if (roofL <= 0 || roofW <= 0)
                {
                    TaskDialog.Show(Title, "The roof's size is not known here: push or import the layout from the web app first, so the fences can be put on its edges.");
                    return null;
                }
                ctx.Sun = payload!.SunAndShading;
                ctx.Hosts = KineticsHosts.FromFences(fences, roofL, roofW);
            }
            else
            {
                TaskDialog.Show(Title, KineticKinds.Get(ctx.Kind).Label + " has nothing placed in the web app's Kinetics tab, and there is no other way to find its position (only Overhead louvre, Sail and Roller fence also answer to a published analysis). Place one in the web app's Kinetics tab, push or sync the layout, then try again.");
                return null;
            }

            SetEnvironment(ctx, payload);
            return ctx;
        }

        /// <summary>Everything a placed unit needs, worked out for each host.</summary>
        internal static List<KineticUnit> BuildUnits(Context ctx, LouvreDesign design) =>
            ctx.Hosts.Select(h => KineticsBuild.Build(h, design, ctx.Env)).ToList();

        internal static KineticMechanicsDto ToDto(LouvreMechanicsReport r) => new KineticMechanicsDto
        {
            ChordMm = Math.Round(r.ChordM * 1000, 1), ThicknessMm = Math.Round(r.ThicknessM * 1000, 1), PitchMm = Math.Round(r.PitchM * 1000, 1), SpanM = Math.Round(r.SpanM, 2),
            BladeMassKg = Math.Round(r.BladeMassKg, 2), TotalMassKg = Math.Round(r.TotalMassKg, 1),
            DesignPressurePa = Math.Round(r.DesignPressurePa, 0), DesignWindMs = Math.Round(r.DesignWindMs, 1), OperatingWindMs = Math.Round(r.OperatingWindMs, 1),
            PeakTorqueOperatingNm = Math.Round(r.PeakTorqueOperatingNm, 3), PeakTorqueAngleDeg = Math.Round(r.PeakTorqueAngleDeg, 1), PeakTorqueGustNm = Math.Round(r.PeakTorqueGustNm, 2),
            FaceOnForceGustN = Math.Round(r.FaceOnForceGustN, 0),
            ActuatorTorqueNm = Math.Round(r.ActuatorTorqueNm, 1), ActuatorForceN = Math.Round(r.ActuatorForceN, 0), HoldingTorqueNm = Math.Round(r.HoldingTorqueNm, 1),
            DeflectionMm = Math.Round(r.DeflectionMm, 1), DeflectionRatio = double.IsInfinity(r.DeflectionRatio) ? 0 : Math.Round(r.DeflectionRatio, 0),
            AllowedSpanM = double.IsInfinity(r.AllowedSpanM) ? 0 : Math.Round(r.AllowedSpanM, 2), DeflectionOk = r.DeflectionOk, StowRequired = r.StowRequired,
            SwingSeconds = r.SwingSeconds, ActuatorPowerW = Math.Round(r.ActuatorPowerW, 2), EnergyWhPerDay = Math.Round(r.EnergyWhPerDay, 4),
            Findings = new List<string>(r.Findings),
        };

        internal static List<KineticStateDto> StatesDto(LouvreMechanicsReport r) => r.States.Select(s => new KineticStateDto
        {
            Label = s.Label, SolarTimeH = Math.Round(s.SolarTimeH, 2), SunElevationDeg = s.SunElevationDeg, LouvreOpenAngleDeg = s.OpenAngleDeg,
            SunStoppedPercent = s.SunStoppedPercent, WindTorqueOperatingNm = Math.Round(s.WindTorqueOperatingNm, 3), WindTorqueGustNm = Math.Round(s.WindTorqueGustNm, 2),
        }).ToList();

        internal static List<AssumptionUseDto> InputsDto(LouvreDesign design) => design.Uses()
            .Select(u => new AssumptionUseDto { Key = u.Key, Label = u.Label, Value = u.Value, State = u.State, Status = u.Status, Reference = u.Reference }).ToList();

        internal static AnalysisResultPayload? Latest() => ReadPayload();

        /// <summary>The distinct built kinds the pushed layout has kinetics placements for, in a stable order — what
        /// ImportKineticAdaptationCommand builds all at once when this is non-empty, instead of asking which single
        /// kind to make. Empty when the layout has none (no layout pushed yet, or nothing placed in the web app's
        /// Kinetics tab, or only not-yet-built concept kinds): the command then falls back to the interactive,
        /// one-kind-at-a-time Prepare() exactly as before this existed.</summary>
        internal static List<KineticKind> PlacedBuiltKinds(SportifyLayout? layout)
        {
            var found = new List<KineticKind>();
            foreach (var p in layout?.Placements ?? new List<PlacementDto>())
            {
                if (!string.Equals(p.Category, "kinetics", StringComparison.OrdinalIgnoreCase)) continue;
                var info = KineticKinds.ByKey(p.Parameters?.Kinetics?.KineticKind);
                if (info == null || !info.Built) continue;
                if (!found.Contains(info.Kind)) found.Add(info.Kind);
            }
            return found;
        }

        /// <summary>One kind's Context, built straight from the layout's own placements — no dialog, no analysis-derived
        /// fallback. Always succeeds for a kind PlacedBuiltKinds returned (FromPlacements is then guaranteed non-empty).</summary>
        static Context PreparePlaced(KineticKind kind, SportifyLayout? layout, AnalysisResultPayload? payload)
        {
            var ctx = new Context { Priority = "sun", Kind = kind, Sun = payload?.SunAndShading, Hosts = KineticsHosts.FromPlacements(layout, kind) };
            foreach (var h in ctx.Hosts) if (h.SiteNote.Length > 0) ctx.Notes.Add(h.Name + ": " + h.SiteNote + ".");
            SetEnvironment(ctx, payload);
            return ctx;
        }

        /// <summary>
        /// The batch alternative to Prepare(): every kind the pushed layout has a placement for, built without asking
        /// which one — the whole point of placing kinds by hand in the web app's Kinetics tab is deciding this already.
        /// Empty when there is nothing to batch (ImportKineticAdaptationCommand then falls back to Prepare()).
        /// </summary>
        internal static List<Context> PrepareAllPlaced()
        {
            var layout = AdoptLayoutFrame();
            var kinds = PlacedBuiltKinds(layout);
            if (kinds.Count == 0) return new List<Context>();
            var payload = ReadPayload();
            return kinds.Select(k => PreparePlaced(k, layout, payload)).ToList();
        }
    }

    /// <summary>Kinetics, step 1: are the two adaptive families (a bar and a membrane) loaded in this project, and what could be made from the results published so far?</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class ChooseKineticFamilyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(KineticsShared.Title, "Open a Revit project first."); return Result.Cancelled; }
            if (KineticsShared.AskPriority(commandData.Application.MainWindowHandle) == null) return Result.Cancelled;

            bool Loaded(string name) => new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Any(s => string.Equals(s.Family?.Name, name, StringComparison.OrdinalIgnoreCase));
            var bar = Loaded(AdaptiveFamilyBuilder.BarName);
            var surface = Loaded(AdaptiveFamilyBuilder.SurfaceName);

            var payload = KineticsShared.Latest();
            var pergolas = payload?.SunAndShading?.Equipment?.Count(e => e.Key == "pergola" || e.Key == "canopy") ?? 0;
            var sails = payload?.SunAndShading?.Equipment?.Count(e => e.Key == "sail") ?? 0;
            var fences = payload?.BallTrajectory?.Fences?.Count ?? 0;

            TaskDialog.Show(KineticsShared.Title,
                "Adaptive families in this project:\n" +
                "  " + AdaptiveFamilyBuilder.BarName + " (blades, fins, posts, rails, masts, rods): " + (bar ? "loaded" : "not loaded") + "\n" +
                "  " + AdaptiveFamilyBuilder.SurfaceName + " (sail fabric, fence curtain): " + (surface ? "loaded" : "not loaded") + "\n\n" +
                "\nWhat the published results could make now:\n" +
                "  overhead louvres: " + pergolas + " (pergolas or canopies in the Sun & Shade result)\n" +
                "  tensile sails: " + sails + " (sails in the Sun & Shade result)\n" +
                "  roller fences: " + fences + " (fences in the Ball Trajectory result)\n" +
                "  vertical slat and fin screens: any railing or wall you select\n\n" +
                "Next: " + (bar && surface ? "run “Import Analysis Adaptation” — it also builds these families itself if they are still missing." : "run “Generate Applicable Family” to build the two families above, then “Import Analysis Adaptation”.") +
                " If you placed kinds by hand in the web app's Kinetics tab, Import Analysis Adaptation builds all of them in one go.");
            return Result.Succeeded;
        }
    }

    /// <summary>
    /// Kinetics, step 2: builds the two adaptive families every kind of unit is made from, as real adaptive components authored in Revit: one bar (eight placement points:
    /// a section at each end) for blades, fins, posts, rails, rods, masts and roller housings, and one membrane (four placement points) for a sail and a fence curtain.
    /// Generated (or taken from the on-disk cache) and loaded into this project. Nothing about a unit is baked into them: where its points go is what makes a blade, a mast or a curtain.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class GenerateKineticFamilyCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(KineticsShared.Title, "Open a Revit project first."); return Result.Cancelled; }
            if (KineticsShared.AskPriority(commandData.Application.MainWindowHandle) == null) return Result.Cancelled;

            // Find Revit's adaptive family template first, before any transaction: the search opens family documents.
            if (AdaptiveFamilyBuilder.FindTemplate(commandData.Application.Application) == null)
                return BimCommandErrors.Failed(KineticsShared.Title, "the adaptive families could not be built", new InvalidOperationException(AdaptiveFamilyBuilder.FailureReason ?? "Revit's adaptive component template was not found"), ref message);

            try
            {
                using var t = new Transaction(doc, "Sportify: build the kinetic adaptive families");
                t.Start();
                var bar = AdaptiveFamilyBuilder.GetOrLoad(doc, true);
                var surface = AdaptiveFamilyBuilder.GetOrLoad(doc, false);
                t.Commit();
                TaskDialog.Show(KineticsShared.Title,
                    "Both adaptive families are in this project:\n  " + bar.Family.Name + " (8 placement points: a section at each end)\n  " + surface.Family.Name + " (4 placement points: a ruled membrane)\n\n" +
                    "They serve every kind: overhead louvres, slat and fin screens, sails on movable pillars, roller fences. Run “Import Analysis Adaptation” next.");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(KineticsShared.Title, "the adaptive families could not be built", ex, ref message);
            }
        }
    }

    /// <summary>
    /// Kinetics, step 3: closes the loop. Every kind the web app's Kinetics tab has a placement for gets built in one
    /// run — position and build size come from the layout, not a dialog — working out from the published analyses how
    /// many blades and how far apart (or how far the sail's masts run on their tracks, or what the fence needs), what
    /// carries them, and how they move; then placing (or replacing) each unit as adaptive components: the moving parts
    /// and the frame that carries them together on the Sportify Kinetic Furniture workset, all in the Post analysis
    /// phase, opened to the sun's solar-noon state (LouvreActuationModel). A layout with no such placements falls back
    /// to the interactive, one-kind-at-a-time dialog exactly as before this batch path existed.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class ImportKineticAdaptationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uidoc = commandData.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (uidoc == null || doc == null) { TaskDialog.Show(KineticsShared.Title, "Open a Revit project first."); return Result.Cancelled; }

            List<KineticsShared.Context> contexts;
            bool batch;
            try
            {
                contexts = KineticsShared.PrepareAllPlaced();
                batch = contexts.Count > 0;
                if (!batch)
                {
                    var single = KineticsShared.Prepare(commandData);
                    if (single == null) return Result.Cancelled;
                    contexts.Add(single);
                }
            }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the layout could not be read", ex, ref message); }

            // The adaptive template is found before the transaction: the search opens family documents.
            if (AdaptiveFamilyBuilder.FindTemplate(commandData.Application.Application) == null)
                return BimCommandErrors.Failed(KineticsShared.Title, "the adaptive families could not be built", new InvalidOperationException(AdaptiveFamilyBuilder.FailureReason ?? "Revit's adaptive component template was not found"), ref message);

            var design = KineticsInputsFile.LoadDesign();
            var allUnits = new List<KineticUnit>();
            try { foreach (var ctx in contexts) allUnits.AddRange(KineticsShared.BuildUnits(ctx, design)); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the mechanics could not be worked out", ex, ref message); }

            var notes = new List<string>();
            try
            {
                var kindsLabel = string.Join(", ", contexts.Select(c => KineticKinds.Get(c.Kind).Label.ToLowerInvariant()));
                using var t = new Transaction(doc, "Sportify: Kinetics — place " + kindsLabel);
                t.Start();
                var bar = AdaptiveFamilyBuilder.GetOrLoad(doc, true);
                var surface = AdaptiveFamilyBuilder.GetOrLoad(doc, false);
                var ws = SportifyWorksetSet.Ensure(doc, new[] { SportifyWorksetSet.KineticFurniture });
                var roofId = (RoofBoundaryServer.ActiveRoofId ?? 0).ToString();
                foreach (var kind in contexts.Select(c => c.Kind).Distinct()) AdaptiveUnitPlacer.ClearKind(doc, kind, roofId);
                var all = new List<ElementId>();
                foreach (var u in allUnits)
                {
                    var placed = AdaptiveUnitPlacer.Place(doc, u.Plan, u.Host.Frame, bar, surface, ws[SportifyWorksetSet.KineticFurniture], ws[SportifyWorksetSet.KineticFurniture], u.Host.Key, roofId);
                    u.Dto.PartsPlaced = placed.Bars + placed.Surfaces;
                    u.Dto.Phase = SportifyPhases.PostAnalysis;
                    all.AddRange(placed.Ids);
                }
                var moved = SportifyPhases.Assign(doc, all, SportifyPhases.PostAnalysis, uidoc.ActiveView, out var phaseNote);
                if (moved > 0) notes.Add(moved + " element(s) are in the “" + SportifyPhases.PostAnalysis + "” phase.");
                if (phaseNote.Length > 0) { notes.Add("Phase: " + phaseNote + "."); foreach (var u in allUnits) u.Dto.Phase = null; }
                if (!doc.IsWorkshared) notes.Add("The project is not workshared, so the parts stay on one workset (Collaborate > Worksets turns it on; the next placement then sorts them).");
                t.Commit();
            }
            catch (Exception ex)
            {
                return BimCommandErrors.Failed(KineticsShared.Title, "the units could not be placed", ex, ref message);
            }

            // ---- publish: the pieces of every kind just built replace those of the same kind already published; other kinds stay
            var builtKeys = contexts.Select(c => KineticKinds.Get(c.Kind).Key).ToHashSet();
            var previous = KineticsShared.Latest()?.Kinetics;
            var kept = (previous?.Pieces ?? new List<KineticPieceDto>()).Where(p => p.Kind != null && !builtKeys.Contains(p.Kind)).ToList();
            var pieces = kept.Concat(allUnits.Select(u => u.Dto)).ToList();
            var preliminary = contexts.Any(c => c.Sun?.Preliminary ?? false) || design.Preliminary;
            AnalysisResultPublisher.PublishKinetics(new KineticsResultDto
            {
                Priority = contexts[0].Priority,
                Preliminary = preliminary,
                PreliminaryNote = string.Join(" ", contexts.Select(c => c.Sun?.PreliminaryNote).Append(design.PreliminaryNote()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct()),
                Pieces = pieces,
                MechanicalInputs = KineticsShared.InputsDto(design),
                VideoPath = previous?.VideoPath, SimulationVideoPath = previous?.SimulationVideoPath,
                PlacementNotes = notes.Concat(contexts.SelectMany(c => c.Notes)).ToList(),
            });

            var perKind = string.Join("\n\n", contexts.Select(ctx =>
            {
                var kindUnits = allUnits.Where(u => u.Dto.Kind == KineticKinds.Get(ctx.Kind).Key).ToList();
                if (kindUnits.Count == 0) return "";
                return KineticKinds.Get(ctx.Kind).Label + " — " + kindUnits.Count + (kindUnits.Count == 1 ? " unit, " : " units, ") + kindUnits.Sum(u => u.Dto.PartsPlaced) + " adaptive parts:\n" + Summary(kindUnits[0]);
            }).Where(s => s.Length > 0));

            TaskDialog.Show(KineticsShared.Title,
                (contexts.Count == 1 ? KineticsShared.RanNote(contexts[0]) : "") +
                (batch ? "Built everything placed in the web app's Kinetics tab: " + contexts.Count + " kind" + (contexts.Count == 1 ? "" : "s") + ", " + allUnits.Count + " unit" + (allUnits.Count == 1 ? "" : "s") + " in all.\n\n" : "") +
                perKind + "\n" +
                (design.Preliminary ? "PRELIMINARY: the mechanical inputs are built-in values. Enter your own in " + KineticsInputsFile.Path + ".\n" : "") +
                (notes.Count > 0 ? "\n" + string.Join("\n", notes) + "\n" : "") +
                "\nNext: “Record Isolated Video” (Unity) shows the states move, or “Simulate” (SOLIDWORKS) builds the mechanism and records its motion study — or run “Bill of Materials” for a parts list with real dimensions.");
            return Result.Succeeded;
        }

        static string Summary(KineticUnit u)
        {
            if (u.Louvre != null)
            {
                var m = u.Louvre;
                return (m.Spacing?.Reason ?? "") + "\n" +
                       (m.Supports != null ? "Carried by " + (m.Supports.Bays + 1) + " post lines, " + m.Supports.BayLengthM.ToString("0.0#") + " m apart (a blade may span " + m.Supports.AllowedSpanM.ToString("0.0#") + " m in the design gust).\n" : "") +
                       "Actuator " + m.ActuatorTorqueNm.ToString("0.#") + " N m (" + m.ActuatorForceN.ToString("0") + " N at the crank), blade deflection " + m.DeflectionMm.ToString("0.#") + " mm" + (m.DeflectionOk ? "" : " — past the limit") + (m.StowRequired ? ", stows in wind" : "") + ".\n";
            }
            if (u.Sail != null) return string.Join("\n", u.Sail.Findings.Take(3)) + "\n";
            if (u.Fence != null) return string.Join("\n", u.Fence.Findings.Take(3)) + "\n";
            return "";
        }
    }

    /// <summary>
    /// Kinetics, "Simulate": the dynamic unit as a real mechanical assembly in SOLIDWORKS, recorded behind the scenes. SOLIDWORKS (hidden) builds the parts (blades, posts, rails,
    /// rod, masts ...) as extrusions of their real sections, puts them together at the places the plan gives, moves the assembly through the states the analysis worked out,
    /// checks it for interferences in each, and records every frame from its own renderer; the tool turns the frames into an MP4 and saves the assembly and its STEP file
    /// for the mechanical engineer. Like the Unity video, but the model is the one a mechanical engineer can open and build on.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class SimulateKineticsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(KineticsShared.Title, "Open a Revit project first."); return Result.Cancelled; }

            if (!MechanicalTool.SolidWorksInstalled())
            {
                TaskDialog.Show(KineticsShared.Title, "SOLIDWORKS is not installed on this computer (its COM application \"SldWorks.Application\" is not registered), so there is nothing to simulate with.\n\nThe Unity video (Record Isolated Video) shows the same states without it.");
                return Result.Cancelled;
            }
            var tool = MechanicalTool.Locate(out var problem);
            if (tool == null) { TaskDialog.Show(KineticsShared.Title, problem); return Result.Cancelled; }

            KineticsShared.Context? ctx;
            try { ctx = KineticsShared.Prepare(commandData); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the layout could not be read", ex, ref message); }
            if (ctx == null) return Result.Cancelled;

            var design = KineticsInputsFile.LoadDesign();
            List<KineticUnit> units;
            try { units = KineticsShared.BuildUnits(ctx, design); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the mechanics could not be worked out", ex, ref message); }
            var unit = units[0];
            if (units.Count > 1)
                TaskDialog.Show(KineticsShared.Title, units.Count + " units were found — the simulation is of the first one (" + unit.Host.Name + ").");

            var runningNow = System.Diagnostics.Process.GetProcessesByName("SLDWORKS").Length > 0;
            if (runningNow)
            {
                var ask = new TaskDialog(KineticsShared.Title)
                {
                    MainInstruction = "SOLIDWORKS is already open",
                    MainContent = "The simulation will use your open SOLIDWORKS (there is only one at a time): the parts and the assembly open in it, visibly, and it stays open afterwards. Close SOLIDWORKS first to have it run hidden.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                };
                ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Use the open SOLIDWORKS", "It leaves your session open; nothing of yours is closed or saved.");
                if (ask.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
            }

            var request = KineticsRender.For(unit, design, ctx.Env);
            var requestFile = Path.Combine(Path.GetTempPath(), "sportify-kinetics-request-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(requestFile, JsonSerializer.Serialize(request));
            var outDir = SportifyWorkspace.PathFor("mechanical");

            MechanicalTool.Outcome run;
            try { run = MechanicalTool.Run(tool, requestFile, outDir, unit.Host.Key, "Simulating the " + KineticKinds.Get(ctx.Kind).Label.ToLowerInvariant() + " in SOLIDWORKS"); }
            finally { try { File.Delete(requestFile); } catch (Exception) { } }

            if (!run.Ok)
            {
                if (!run.Cancelled) TaskDialog.Show(KineticsShared.Title, "The simulation could not be made.\n\n" + run.Message);
                return Result.Succeeded;
            }

            string? videoPath = null;
            try { if (run.VideoPath != null && File.Exists(run.VideoPath)) videoPath = SportifyWorkspace.Adopt("videos", run.VideoPath, DeliverableNaming.FolderFor("videos")); }
            catch (Exception ex) { SportifyLog.Warn("kinetics", "the simulation video could not be adopted into the Sportify folder: " + ex.Message); videoPath = run.VideoPath; }

            var summary = new List<string>();
            var r = run.Result;
            double Num(string p) => r.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            summary.Add("SOLIDWORKS built " + Num("components") + " components from " + Num("parts") + " parts, " + Num("totalMassKg").ToString("0.0") + " kg in all.");
            if (r.TryGetProperty("interferences", out var itf) && itf.ValueKind == JsonValueKind.Array)
            {
                var bad = itf.EnumerateArray().Where(x => x.TryGetProperty("count", out var c) && c.GetInt32() > 0).Select(x => x.GetProperty("state").GetString() + ": " + x.GetProperty("count").GetInt32()).ToList();
                summary.Add(bad.Count == 0 ? "No interference between any two parts in any of the " + itf.GetArrayLength() + " states." : "Interferences (parts touching where they should not): " + string.Join("; ", bad) + ".");
            }
            if (r.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array) foreach (var n in notes.EnumerateArray()) summary.Add(n.GetString() ?? "");

            var kinetics = KineticsShared.Latest()?.Kinetics ?? new KineticsResultDto { Priority = ctx.Priority, Pieces = new List<KineticPieceDto>() };
            kinetics.SimulationVideoPath = videoPath;
            kinetics.PlacementNotes = (kinetics.PlacementNotes ?? new List<string>()).Where(x => !x.StartsWith("SOLIDWORKS")).Concat(summary.Select(x => x.StartsWith("SOLIDWORKS") ? x : "SOLIDWORKS: " + x)).ToList();
            AnalysisResultPublisher.PublishKinetics(kinetics);

            var text = string.Join("\n", summary) + "\n\nFiles in " + outDir + ":\n  " + Path.GetFileName(run.AssemblyPath ?? "") + " (the assembly, to open in SOLIDWORKS)\n  " + Path.GetFileName(run.StepPath ?? "") + " (STEP)" +
                       (videoPath != null ? "\n\nThe motion is recorded as " + Path.GetFileName(videoPath) + "." : "\n\nNo video was made.");
            TaskDialog.Show(KineticsShared.Title, text);
            return Result.Succeeded;
        }
    }

    /// <summary>Kinetics' own "3D video" button: one dynamic unit alone, moving through its states in Unity — not the whole roof.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class RecordKineticsVideoCommand : IExternalCommand
    {
        const int VideoTimeoutMs = 300_000;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(KineticsShared.Title, "Open a Revit project first."); return Result.Cancelled; }

            KineticsShared.Context? ctx;
            try { ctx = KineticsShared.Prepare(commandData); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the layout could not be read", ex, ref message); }
            if (ctx == null) return Result.Cancelled;

            if (!UnityHeadlessRunner.TryLocate(out var unity, out var problem) || UnityHeadlessRunner.IsProjectOpenInUnity(unity!.ProjectDir))
            {
                TaskDialog.Show(KineticsShared.Title, "The isolated video needs the Unity Editor" +
                    (UnityHeadlessRunner.IsProjectOpenInUnity(unity?.ProjectDir ?? "") ? ": close the Sportify.Simulation project in it first and try again." :
                     string.IsNullOrEmpty(problem) ? ", and it (or the Sportify.Simulation project) was not found on this computer." : ": " + problem) +
                    "\n\nKinetics has no PDF alternative yet, unlike the other analyses.");
                return Result.Cancelled;
            }

            var design = KineticsInputsFile.LoadDesign();
            List<KineticUnit> units;
            try { units = KineticsShared.BuildUnits(ctx, design); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the mechanics could not be worked out", ex, ref message); }
            if (units.Count > 1)
                TaskDialog.Show(KineticsShared.Title, units.Count + " units were found — the isolated video shows the first one only (" + units[0].Host.Name + ").");
            var request = KineticsRender.For(units[0], design, ctx.Env);
            var requestJson = JsonSerializer.Serialize(request);

            var run = AnalysisMedia.RunUnity(unity, new UnityHeadlessRunner.Request
            {
                ExecuteMethod = "Sportify.Simulation.Editor.BatchRunner.RunKineticsAnalysis",
                LayoutJson = requestJson,
                ResultsFileName = "kinetics_results.json",
                VideoFileStem = "kinetics",
                LogFileName = "unity_kinetics_batch.log",
                TimeoutMs = VideoTimeoutMs,
            }, "Rendering the isolated Kinetics video");

            if (!run.Ok)
            {
                if (!run.Cancelled) TaskDialog.Show(KineticsShared.Title, "The video couldn't be rendered.\n\n" + run.Message);
                return Result.Succeeded;
            }

            string? videoPath = null;
            try
            {
                var results = JsonSerializer.Deserialize<KineticsUnityResults>(run.ResultsJson!);
                if (results?.Video?.FilePath != null && File.Exists(results.Video.FilePath))
                    videoPath = SportifyWorkspace.Adopt("videos", results.Video.FilePath, DeliverableNaming.FolderFor("videos"));
            }
            catch (Exception ex) { TaskDialog.Show(KineticsShared.Title, "The video was rendered, but Unity's results file couldn't be read: " + ex.Message); }

            var kinetics = KineticsShared.Latest()?.Kinetics ?? new KineticsResultDto { Priority = ctx.Priority, Pieces = new List<KineticPieceDto>() };
            kinetics.VideoPath = videoPath;
            AnalysisResultPublisher.PublishKinetics(kinetics);

            TaskDialog.Show(KineticsShared.Title, videoPath != null
                ? "The isolated video is ready: " + Path.GetFileName(videoPath) + "."
                : "The video did not render (Unity produced no file).");
            return Result.Succeeded;
        }

        private sealed class KineticsUnityResults
        {
            [JsonPropertyName("video")] public KineticsUnityVideo? Video { get; set; }
        }
        private sealed class KineticsUnityVideo
        {
            // Named FilePath rather than Path so it can't be confused with System.IO.Path (same reasoning as SimulateBallTrajectoriesCommand.VideoInfo).
            [JsonPropertyName("path")] public string? FilePath { get; set; }
        }
    }

    /// <summary>
    /// Kinetics, "Bill of Materials": a manufacturing PDF for every placed, built kind — one page per unit, each
    /// with its real parts (blade/panel/curtain, structure, and the mechanism's own hardware: crank, actuator,
    /// drive motor) at the actual dimensions the mechanics compute for whatever is placed in the web app's
    /// Kinetics tab right now, and a plain-language account of how that unit moves, built from the same findings
    /// the Improve tab shows. Grouped so 19 identical blades is one row with Count 19. Recomputed fresh each time,
    /// independent of whether Import Analysis Adaptation has been run: the whole point is answering "what would
    /// this cost to build" before committing to it in Revit. See KineticsBillOfMaterials's and
    /// KineticsManufacturingPdfBuilder's own doc comments for why a PDF (not a ViewSchedule) and why Material is
    /// an honest "assumed" default rather than guessed supplier data.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class KineticsBillOfMaterialsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null) { TaskDialog.Show(KineticsShared.Title, "Open a Revit project first."); return Result.Cancelled; }

            List<KineticsShared.Context> contexts;
            try { contexts = KineticsShared.PrepareAllPlaced(); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the layout could not be read", ex, ref message); }
            if (contexts.Count == 0)
            {
                // Like every other Kinetics command, this reads the ACTIVE roof's own pushed layout only (RoofBoundaryServer.ActiveRoofId) —
                // a pergola placed and confirmed on a different pushed roof is real, but invisible here until that roof is the active one.
                var roofs = RoofBoundaryServer.ListRoofs();
                var activeName = roofs.FirstOrDefault(r => r.Active).Name;
                var otherRoofsHint = roofs.Count > 1
                    ? $" If you placed one on a different roof, switch to it first (the roof picker in the web app, or POST /roofs/active) and run this again — Kinetics always works on whichever roof is active{(string.IsNullOrEmpty(activeName) ? "" : $" (currently \"{activeName}\")")}."
                    : "";
                TaskDialog.Show(KineticsShared.Title, "Nothing to list: place a louvre pergola, sail or roller fence in the web app's Kinetics tab first — the bill of materials is built from what you've placed and confirmed there, at the build size you chose, not from whatever is already in Revit." + otherRoofsHint);
                return Result.Cancelled;
            }

            var design = KineticsInputsFile.LoadDesign();
            List<KineticUnit> units;
            try { units = contexts.SelectMany(c => KineticsShared.BuildUnits(c, design)).ToList(); }
            catch (Exception ex) { return BimCommandErrors.Failed(KineticsShared.Title, "the mechanics could not be worked out", ex, ref message); }

            var partCount = units.Sum(u => KineticsBillOfMaterials.RowsFull(u).Count);
            string path;
            try
            {
                path = SportifyWorkspace.UniquePath("schedules", DeliverableNaming.Named($"Sportify_Kinetics_Manufacturing_{DateTime.Now:yyyyMMdd_HHmmss}.pdf"), DeliverableNaming.FolderFor("schedules"));
                KineticsManufacturingPdfBuilder.Generate(path, units, design.Preliminary, design.Preliminary ? design.PreliminaryNote() : null);
            }
            catch (Exception ex) { TaskDialog.Show(KineticsShared.Title, "Couldn't write the PDF file: " + ex.Message); return Result.Failed; }

            TaskDialog.Show(KineticsShared.Title,
                partCount + " part(s) across " + units.Count + " unit(s) (" + string.Join(", ", contexts.Select(c => KineticKinds.Get(c.Kind).Label.ToLowerInvariant())) + "), written to:\n" + path +
                "\n\nEach unit's own page lists its real parts — including the mechanism's hardware (crank, actuator, motor) that Revit itself never places — and a plain-language account of how it moves, ready to hand to a machinist." +
                "\n\nMaterial is filled in with a plain per-role default, marked \"assumed\" — check it against what the mechanical engineer actually specifies before ordering." +
                (design.Preliminary ? "\n\nPRELIMINARY: the sizes rest on built-in mechanical inputs. Enter your own in " + KineticsInputsFile.Path + " and run this again." : ""));
            return Result.Succeeded;
        }
    }
}
