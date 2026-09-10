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
                "Placeholder — will simulate real ball trajectories (shots, serves, kicks) with rigidbody physics to validate FIBA/DIN clearance buffers against neighboring courts, the roof edge and circulation space.");
            AddButton(unityAnalysisPanel, "AnalyzeStructuralResonance", "Structural\nResonance", typeof(AnalyzeStructuralResonanceCommand),
                "Placeholder — will simulate synchronized crowd movement and visualize the resulting vibration/load pattern across the existing structure's grid.");
            AddButton(unityAnalysisPanel, "AnalyzeWindErosionRisk", "Wind & Erosion\nAnalysis", typeof(AnalyzeWindErosionRiskCommand),
                "Placeholder — will apply a simplified wind force field, stronger at roof edges/corners, to flag wind-uplift stress on tall vegetation and erosion/scour risk on exposed growing medium.");
            AddButton(unityAnalysisPanel, "SimulateSoilPercolation", "Soil Percolation\nSimulation", typeof(SimulateSoilPercolationCommand),
                "Placeholder — will show water filtering down through a garden buildup's actual substrate/drainage layers in cross-section, complementing the Water Management surface animation.");

            var exportPanel = application.CreateRibbonPanel(TabName, "Data Export / Deliverables");
            AddButton(exportPanel, "GenerateAnalysisReport", "Analysis\nReport", typeof(GenerateAnalysisReportCommand),
                "Places a text summary of every Analysis check run this session, plus an optional chart image, on the active view.");
            AddButton(exportPanel, "GenerateFunctionalDiagrams", "Functional\nDiagrams", typeof(GenerateFunctionalDiagramsCommand),
                "Generates a circulation-only floor plan and a 3D massing axonometric. Bubble diagram not built yet.");
            AddButton(exportPanel, "GenerateSchedules", "Schedules\n(CSV)", typeof(GenerateSchedulesCommand),
                "Exports a CSV schedule of every synced component (category, quality, reference material/provider, area).");

            application.Idling += AutoImportSync.OnIdling;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= AutoImportSync.OnIdling;
            RoofBoundaryServer.Stop();
            return Result.Succeeded;
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
