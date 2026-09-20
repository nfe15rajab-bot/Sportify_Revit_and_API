using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Starts/stops RoofBoundaryServer alongside Revit itself (so it's
    /// already listening by the time anyone opens the frontend's Combine
    /// tab) and builds the "Sportify" ribbon tab — a real top-level tab
    /// (not a panel tucked under Add-Ins), so "Sportify" is what you
    /// actually see in the ribbon.
    ///
    /// Four panels, matching the project's own scope split rather than
    /// "whatever's been built so far": App & Data Import (getting a layout
    /// into Revit), Analysis (rule/geometry checks — no engine involved),
    /// Physical Analysis (Unity based) (checks that specifically need a physics/
    /// rendering engine — real rigidbody/particle simulation, not just a
    /// calculation), and Data Export / Deliverables (getting results back
    /// out). Most Unity/Export buttons are still placeholders for features
    /// that don't exist yet (see PlaceholderCommand) — wired into the
    /// ribbon anyway so the tab shows the project's full intended shape,
    /// not just what's been built. Toggle Auto Import and Set Sun +
    /// Location, from the previous Import/Sun & Site panels, dropped out
    /// of this ribbon shape during the restructure even though their
    /// command classes kept working — both are back in App & Data Import
    /// below now that they're needed again (Set Sun + Location as a plain
    /// setup action; Auto Import as the live-sync toggle).
    /// </summary>
    public class SportfyRevitApp : IExternalApplication
    {
        private const string TabName = "Sportify";

        public Result OnStartup(UIControlledApplication application)
        {
            RoofBoundaryServer.Start();
            StaticWebServer.Start();
            EnsureApiRunning();

            // The Sportify folder (layouts, reports, charts, videos ... one subfolder each): made now if the installer has not, so the web app always has somewhere to put
            // and find its files; and the way for the web app to ask Revit for the functional diagrams.
            try { SportifyWorkspace.EnsureCreated(); } catch (System.Exception) { /* read-only Documents: the folder is made when the first file is saved */ }
            RevitCommandBridge.Install();

            // Must happen in OnStartup, before any document is open — see
            // SportifyDockablePaneProvider's own notes on the GUID needing
            // to stay constant across builds.
            application.RegisterDockablePane(SportifyDockablePaneProvider.PaneId, "Sportify App",
                new SportifyDockablePaneProvider());

            application.CreateRibbonTab(TabName);

            var importPanel = application.CreateRibbonPanel(TabName, "App & Data Import");
            AddButton(importPanel, "OpenSportifyApp", "Open\nSportify App", typeof(OpenSportifyAppCommand),
                $"Opens the Sportify web app in a docked pane inside Revit ({SportifyBrowserPane.DefaultUrl}).");
            AddPushMenu(importPanel);
            AddButton(importPanel, "ImportSportifyLayout", "Import\nConfiguration", typeof(ImportSportifyLayoutCommand),
                "Pick a Combine tab JSON export and build families (or placeholder geometry), name labels and worksets for it.");
            AddButton(importPanel, "LoadFamilies", "Load\nFamilies", typeof(LoadFamiliesCommand),
                "Pick your own .rfa families, load them into this project, and publish their types and writable parameters to the Sportify web app so they can be placed from the Combine tab.");
            AddButton(importPanel, "ImportDxf", "Import\nDXF", typeof(ImportDxfCommand),
                "Imports a DXF export (e.g. the Sport tab's \"Export DXF\") as reference geometry, placed in meters at the origin.");
            AddButton(importPanel, "SetSunAndLocation", "Set Sun +\nLocation", typeof(SetSunAndLocationCommand),
                "Sets this project's real Site Location and Sun Settings from the web app's Site tab data.");
            AutoImportSync.ToggleButton = AddButton(importPanel, "ToggleAutoImport", "Auto Import:\nOFF", typeof(ToggleAutoImportCommand),
                "Automatically apply every Combine export pushed from the web app, without opening a file picker. Click to turn on.");

            var analysisPanel = application.CreateRibbonPanel(TabName, "Analysis");
            AddButton(analysisPanel, "AnalyzeFireSafety", "Fire Safety\nAnalysis", typeof(AnalyzeFireSafetyCommand),
                "Checks evacuation travel distance from every piece to its nearest entry point against a reference figure from Sportify.Api.");
            AddButton(analysisPanel, "AnalyzeCarbonImpact", "Carbon Impact\nAnalysis", typeof(AnalyzeCarbonImpactCommand),
                "Illustrative kinetic-to-electrical energy-harvesting ceiling across active playing surface, assuming piezoelectric-capable flooring.");
            AddButton(analysisPanel, "AnalyzeLca", "LCA\nAnalysis", typeof(AnalyzeLcaCommand),
                "Sums embodied carbon from each piece's picked reference material, using Sportify.Api's Materials table.");
            AddButton(analysisPanel, "AnalyzeAccessibility", "Accessibility\nAnalysis", typeof(AnalyzeAccessibilityCommand),
                "Checks circulation width against a wheelchair two-way reference and whether every piece is reachable from an entry point.");

            // Adjacent to Analysis on purpose: everything here specifically needs a
            // physics/rendering engine (real rigidbody or particle simulation) rather
            // than a rule check or calculation, which is what sets it apart from the
            // plain Analysis panel — see the project notes on why each of these was
            // judged a genuine Unity fit and the rest weren't.
            var unityAnalysisPanel = application.CreateRibbonPanel(TabName, "Physical Analysis (Unity based)");
            AddButton(unityAnalysisPanel, "SendPhysicalAnalysisToWeb", "Send All to\nWeb App", typeof(SendPhysicalAnalysisToWebCommand),
                "Sends the physical analyses to the Sportify web app (Analysis tab) in one go, with no questions asked: wind and erosion, rain and soil percolation, static loads, dynamic analysis, sun and shade, run on the layout the web app pushed. Uses whatever inputs you already decided (in this session or in the app's Structure and Site conditions tabs); what is still unconfirmed is marked PRELIMINARY, and a summary offers to review it. Needs no Unity and takes a few seconds. The ball trajectories and the 3D videos need Unity and keep their own buttons below; a video already made for exactly these numbers stays with them.");
            unityAnalysisPanel.AddSeparator();
            AddButton(unityAnalysisPanel, "SimulateBallTrajectories", "Ball Trajectory\nSimulation", typeof(SimulateBallTrajectoriesCommand),
                "Runs the current layout through Sportify.Simulation (Unity): stray shots from every placed court, checking crossings into neighboring courts, the roof edge and circulation space. Records the flights as an MP4 video, and works out what share of shots leave the roof and where fences should go. Needs the Unity Editor (its physics makes the numbers, so there is no PDF alternative). Takes ~30-90s with a progress window you can cancel. Close the Unity Editor first if it has Sportify.Simulation open.");
            AddButton(unityAnalysisPanel, "AnalyzeStructuralLoads", "Structural\nLoads", typeof(AnalyzeStructuralLoadsCommand),
                "Static loads on the roof structure: pulls the structural grid and columns from the Revit model (with Push Roof), adds up the weight of the build-ups, courts, trees and the expected crowds bay by bay, and shows which bays are most loaded against the deck capacity and whether the load sits to one side, with the move or lightening that would balance it. The numbers appear at once and need no Unity; a 3D video is offered afterwards: the video if you have Unity (about a minute, with a progress window you can cancel), or the charts as a PDF if you don't. Before it runs, a window asks for the values the layout cannot know (the deck capacity, above all): enter your own or accept the built-in one knowingly; whatever is left unconfirmed marks the results PRELIMINARY. The same choice is in the app's Structure and Site conditions tabs.");
            AddButton(unityAnalysisPanel, "AnalyzeDynamicLoads", "Dynamic\nAnalysis", typeof(AnalyzeDynamicLoadsCommand),
                "The dynamic half of the structural analysis, in three scenarios one after the other: (1) crowds - how people move over the roof through a day and how much they weigh where; (2) weather - a cloudburst soaking the green roofs, snow and wind as load cases with the crowd, and the governing case of every bay; (3) resonance - the deck's natural frequency and what walking, court play or a jumping crowd does to it, ending on the frequency spectrum and the vibrating deck. The numbers appear at once and need no Unity; a 3D video is offered afterwards: the video if you have Unity (about two minutes, with a progress window you can cancel), or the charts as a PDF if you don't. Before it runs, a window asks for the values the layout cannot know (deck capacity, natural frequency, snow zone and altitude, the day's schedule, the comfort limits): enter your own or accept the built-in ones knowingly, each with its source; whatever is left unconfirmed marks the results PRELIMINARY. The same choice is in the app's Structure and Site conditions tabs.");
            AddButton(unityAnalysisPanel, "AnalyzeSunShade", "Sun & Shade\nAnalysis", typeof(AnalyzeSunShadeCommand),
                "Direct sun on the roof through the year's design days (21 June, 21 March, 21 December): the hours of sun every part gets, which play areas and spectator zones are too sunny at midday and which gardens too shaded, and the shading equipment (pergolas, canopies, sails, parasols, a tree) that would fix it without taking the sun from the gardens, with its weight, the wind on it and the deck's answer. The numbers appear at once and need no Unity; a 3D video of the shadows sweeping the roof and the equipment going up is offered afterwards: the video if you have Unity (about two minutes, with a progress window you can cancel), or the charts as a PDF if you don't. Before it runs a window asks for the site, the orientation, the shade target and the sun the gardens need: enter your own or accept the built-in ones; whatever is left unconfirmed marks the results PRELIMINARY.");
            AddButton(unityAnalysisPanel, "AnalyzeWindErosionRisk", "Wind & Erosion\nAnalysis", typeof(AnalyzeWindErosionRiskCommand),
                "Screens the roof garden for wind (EN 1991-1-4 roof zones, FLL guideline): would trees be blown over, would a build-up lift off the roof, would growing medium blow away — with fixes (ballast, anchoring, gravel strips). The numbers appear at once and need no Unity; a 3D video of the wind crossing the roof is offered afterwards: the video if you have Unity (about a minute, with a progress window you can cancel), or the charts as a PDF if you don't.");
            AddButton(unityAnalysisPanel, "SimulateSoilPercolation", "Soil Percolation\nSimulation", typeof(SimulateSoilPercolationCommand),
                "Steps three rain events (steady, heavy shower, cloudburst) through the real layers of each roof-garden build-up: how much rain each keeps, how much runs off and how much later, and whether any fills up — with advice (more water-storing drainage layer or substrate). The numbers appear at once and need no Unity; a cross-section video of the water soaking down is offered afterwards: the video if you have Unity (about a minute, with a progress window you can cancel), or the charts as a PDF if you don't.");

            var exportPanel = application.CreateRibbonPanel(TabName, "Data Export / Deliverables");
            AddButton(exportPanel, "GenerateAnalysisReport", "Analysis\nReport\n(PDF)", typeof(GenerateAnalysisReportCommand),
                "Generates and opens a PDF report: every Analysis check run this session, the component schedule, and the circulation/axonometric diagrams — auto-exported, no manual picking. Saved in the Analysis reports folder of your Sportify folder.");
            AddButton(exportPanel, "GenerateFunctionalDiagrams", "Functional\nDiagrams", typeof(GenerateFunctionalDiagramsCommand),
                "Generates a circulation-only floor plan and a 3D massing axonometric, saved as PNG in the Diagrams folder of your Sportify folder (the web app's Deliverables tab can ask for them too). Bubble diagram not built yet.");
            AddButton(exportPanel, "GenerateSchedules", "Schedules\n(CSV)", typeof(GenerateSchedulesCommand),
                "Exports a CSV schedule of every synced component (category, quality, reference material/provider, area) into the Schedules folder of your Sportify folder.");
            AddButton(exportPanel, "OpenSportifyFolder", "Open Sportify\nFolder", typeof(OpenSportifyFolderCommand),
                "Opens your Sportify folder (chosen when Sportify was installed, by default Documents\\Sportify Workspace) in Explorer: layouts, sport and garden data, analysis charts (PDF), videos, analysis reports, schedules and diagrams, one subfolder each. The web app's Deliverables tab lists the same files.");

            application.Idling += AutoImportSync.OnIdling;

            // Auto-opens the docked Sportify pane the first time Revit goes
            // idle after startup, so a freshly-installed add-in shows the web
            // app immediately instead of waiting for someone to find the
            // ribbon button. Calling pane.Show() directly here in OnStartup is
            // unreliable (Revit's UI frame isn't fully constructed that
            // early) — Idling is the same "safe to touch the UI now" signal
            // AutoImportSync already relies on. Unsubscribes itself so this
            // only ever fires once per Revit session.
            void ShowPaneOnce(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
            {
                application.Idling -= ShowPaneOnce;
                if (sender is not UIApplication uiApp) return;
                var pane = uiApp.GetDockablePane(SportifyDockablePaneProvider.PaneId);
                if (!pane.IsShown()) pane.Show();
            }
            application.Idling += ShowPaneOnce;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= AutoImportSync.OnIdling;
            RoofBoundaryServer.Stop();
            StaticWebServer.Stop();
            return Result.Succeeded;
        }

        /// <summary>
        /// Best-effort: if Sportify.Api is already reachable (dev workflow —
        /// someone ran "dotnet run" themselves, exactly as done all session),
        /// this is a no-op. Otherwise looks for a bundled Sportify.Api.exe
        /// next to this add-in and launches it — the piece that makes a
        /// packaged installer "seamless" instead of needing a separate
        /// manual step. In this dev checkout no such exe is bundled yet, so
        /// this quietly does nothing and every Analyze* command keeps
        /// working off its own offline fallback defaults, same as always.
        /// </summary>
        private static void EnsureApiRunning()
        {
            try
            {
                using var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(1) };
                var response = client.GetAsync("http://localhost:5107/api/AnalysisParameters").GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode) return;
            }
            catch (System.Exception)
            {
                // Not reachable — fall through and try to launch a bundled copy below.
            }

            var assemblyDir = Path.GetDirectoryName(typeof(SportfyRevitApp).Assembly.Location);
            var apiExePath = assemblyDir != null ? Path.Combine(assemblyDir, "api", "Sportify.Api.exe") : null;
            if (apiExePath == null || !File.Exists(apiExePath)) return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = apiExePath,
                    WorkingDirectory = Path.GetDirectoryName(apiExePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (System.Exception)
            {
                // Best-effort — Analyze* commands already degrade gracefully if the API stays unreachable.
            }
        }

        /// <summary>
        /// The "Push to Sportify" drop-down: everything the model knows about the selected roof at once, or one part of it at a time (its structure, its
        /// entries, its openings ...). The roof's outline and size go with every part; a part pushed alone is laid onto the roof pushed before it.
        /// </summary>
        private static void AddPushMenu(RibbonPanel panel)
        {
            var menu = (PulldownButton)panel.AddItem(new PulldownButtonData("PushToSportify", "Push to\nSportify")
            {
                ToolTip = "Send the selected roof or floor to the Sportify web app's Combine tab: everything the model knows about it at once, or one part at a time.",
                LongDescription = "The roof's outline and size always go with it (they fix the plan). A part pushed alone is laid onto the roof pushed before, so you can push the " +
                                  "structure, then the entries, then the equipment without walking the whole model again. Select the roof (or floor) in the model first; if nothing is " +
                                  "selected, a partial push uses the roof pushed last, and Everything asks you to pick one.",
            });

            void Item(string name, string text, Type command, string tip)
            {
                menu.AddPushButton(new PushButtonData(name, text, typeof(SportfyRevitApp).Assembly.Location, command.FullName) { ToolTip = tip });
            }

            Item("PushRoofBoundary", "Everything", typeof(PushRoofBoundaryCommand),
                "The roof's outline and size, its structure (grid, columns, beams, bearing walls), entries, openings, edge and walls on the roof, drains, equipment, and the slab build-up and levels.");
            menu.AddSeparator();
            Item("PushRoofOutline", "Roof outline and size", typeof(PushRoofOutlineCommand),
                "Only the roof's outline, size and height above ground. Enough to place pieces on it.");
            Item("PushRoofStructure", "Structure: grid, columns, beams, walls", typeof(PushRoofStructureCommand),
                "The structural grid, columns, the beams under the slab and the walls that reach it (marked bearing or not). What the structural and dynamic analyses lay the bays on.");
            Item("PushRoofEntries", "Entries: stairs, lifts, doors", typeof(PushRoofEntriesCommand),
                "The stairs, lifts and doors by which people reach the roof: where they arrive.");
            Item("PushRoofOpenings", "Openings", typeof(PushRoofOpeningsCommand),
                "Holes in the roof's top face (skylights, shafts, rooflights): nothing may stand there.");
            Item("PushRoofEdge", "Edge and walls on the roof", typeof(PushRoofEdgeCommand),
                "Parapets and railings along the roof's edge, and the walls standing on the roof (which shade it).");
            Item("PushRoofDrains", "Drains", typeof(PushRoofDrainsCommand),
                "Roof drains, overflows and scuppers (found by their family names).");
            Item("PushRoofEquipment", "Equipment on the roof", typeof(PushRoofEquipmentCommand),
                "Mechanical and electrical equipment standing on the roof: footprint, height and, where the family has a weight, its weight.");
            Item("PushRoofSlabLevels", "Slab build-up and levels", typeof(PushRoofSlabLevelsCommand),
                "The layers of the roof slab (its structural thickness is what the deck's resonance estimate needs) and the project's levels.");
        }

        private static PushButton AddButton(RibbonPanel panel, string internalName, string text, Type commandType, string tooltip)
        {
            var data = new PushButtonData(internalName, text, typeof(SportfyRevitApp).Assembly.Location, commandType.FullName)
            {
                ToolTip = tooltip,
            };
            return (PushButton)panel.AddItem(data);
        }
    }
}
