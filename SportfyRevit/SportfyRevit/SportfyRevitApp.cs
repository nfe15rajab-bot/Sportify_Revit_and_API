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
    /// Unity Based Analysis (checks that specifically need a physics/
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

            // Must happen in OnStartup, before any document is open — see
            // SportifyDockablePaneProvider's own notes on the GUID needing
            // to stay constant across builds.
            application.RegisterDockablePane(SportifyDockablePaneProvider.PaneId, "Sportify App",
                new SportifyDockablePaneProvider());

            application.CreateRibbonTab(TabName);

            var importPanel = application.CreateRibbonPanel(TabName, "App & Data Import");
            AddButton(importPanel, "OpenSportifyApp", "Open\nSportify App", typeof(OpenSportifyAppCommand),
                $"Opens the Sportify web app in a docked pane inside Revit ({SportifyBrowserPane.DefaultUrl}).");
            AddButton(importPanel, "PushRoofBoundary", "Push Roof\nto Sportify", typeof(PushRoofBoundaryCommand),
                "Select a roof or floor and send its footprint to the Sportify web app's Combine tab.");
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
            AddButton(analysisPanel, "AnalyzeWaterManagement", "Water Mgmt\nAnalysis", typeof(AnalyzeWaterManagementCommand),
                "Estimates rainwater retention from garden coverage and buildup depth in the synced layout.");
            AddButton(analysisPanel, "AnalyzeLiveLoads", "Live Loads\nAnalysis", typeof(AnalyzeLiveLoadsCommand),
                "Illustrative estimate: converts each field's spectator capacity into a distributed load and compares it to a generic DIN EN 1991-1-1 reference value.");
            AddButton(analysisPanel, "AnalyzeCarbonImpact", "Carbon Impact\nAnalysis", typeof(AnalyzeCarbonImpactCommand),
                "Illustrative kinetic-to-electrical energy-harvesting ceiling across active playing surface, assuming piezoelectric-capable flooring.");
            AddButton(analysisPanel, "AnalyzeSunAndShading", "Sun & Shading\nAnalysis", typeof(AnalyzeSunAndShadingCommand),
                "Confirms Site Location matches the synced layout, then points at Revit's own Sun Path/Shadows for the real render.");
            AddButton(analysisPanel, "AnalyzeLca", "LCA\nAnalysis", typeof(AnalyzeLcaCommand),
                "Sums embodied carbon from each piece's picked reference material, using Sportify.Api's Materials table.");
            AddButton(analysisPanel, "AnalyzeAccessibility", "Accessibility\nAnalysis", typeof(AnalyzeAccessibilityCommand),
                "Checks circulation width against a wheelchair two-way reference and whether every piece is reachable from an entry point.");

            // Adjacent to Analysis on purpose: everything here specifically needs a
            // physics/rendering engine (real rigidbody or particle simulation) rather
            // than a rule check or calculation, which is what sets it apart from the
            // plain Analysis panel — see the project notes on why each of these was
            // judged a genuine Unity fit and the rest weren't.
            var unityAnalysisPanel = application.CreateRibbonPanel(TabName, "Unity Based Analysis");
            AddButton(unityAnalysisPanel, "SimulateCrowds", "Simulate\nCrowds", typeof(SimulateCrowdsCommand),
                "Placeholder — will simulate spectator/participant flow using the circulation paths and entry points from the Combine layout.");
            AddButton(unityAnalysisPanel, "SimulateBallTrajectories", "Ball Trajectory\nSimulation", typeof(SimulateBallTrajectoriesCommand),
                "Runs the current layout through Sportify.Simulation (Unity): stray shots from every placed court, checking crossings into neighboring courts, the roof edge and circulation space. Records the flights as an MP4 video, and works out what share of shots leave the roof and where fences should go. Takes ~30-90s — Revit will be unresponsive while it runs. Close the Unity Editor first if it has Sportify.Simulation open.");
            AddButton(unityAnalysisPanel, "AnalyzeStructuralResonance", "Structural\nResonance", typeof(AnalyzeStructuralResonanceCommand),
                "Placeholder — will simulate synchronized crowd movement and visualize the resulting vibration/load pattern across the existing structure's grid.");
            AddButton(unityAnalysisPanel, "AnalyzeWindErosionRisk", "Wind & Erosion\nAnalysis", typeof(AnalyzeWindErosionRiskCommand),
                "Screens the roof garden for wind (EN 1991-1-4 roof zones, FLL guideline): would trees be blown over, would a build-up lift off the roof, would growing medium blow away — with fixes (ballast, anchoring, gravel strips). The numbers appear at once and need no Unity; a 3D video of the wind crossing the roof is offered afterwards if the Unity Editor is installed (about a minute, Revit unresponsive while it renders).");
            AddButton(unityAnalysisPanel, "SimulateSoilPercolation", "Soil Percolation\nSimulation", typeof(SimulateSoilPercolationCommand),
                "Steps three rain events (steady, heavy shower, cloudburst) through the real layers of each roof-garden build-up: how much rain each keeps, how much runs off and how much later, and whether any fills up — with advice (more water-storing drainage layer or substrate). The numbers appear at once and need no Unity; a cross-section video of the water soaking down is offered afterwards if the Unity Editor is installed (about a minute, Revit unresponsive while it renders).");

            var exportPanel = application.CreateRibbonPanel(TabName, "Data Export / Deliverables");
            AddButton(exportPanel, "GenerateAnalysisReport", "Analysis\nReport\n(PDF)", typeof(GenerateAnalysisReportCommand),
                "Generates and opens a PDF report: every Analysis check run this session, the component schedule, and the circulation/axonometric diagrams — auto-exported, no manual picking.");
            AddButton(exportPanel, "GenerateFunctionalDiagrams", "Functional\nDiagrams", typeof(GenerateFunctionalDiagramsCommand),
                "Generates a circulation-only floor plan and a 3D massing axonometric. Bubble diagram not built yet.");
            AddButton(exportPanel, "GenerateSchedules", "Schedules\n(CSV)", typeof(GenerateSchedulesCommand),
                "Exports a CSV schedule of every synced component (category, quality, reference material/provider, area).");

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
