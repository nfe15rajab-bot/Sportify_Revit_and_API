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
    /// out). Most Analysis/Unity/Export buttons are placeholders for
    /// features that don't exist yet (see PlaceholderCommand) — wired into
    /// the ribbon anyway so the tab shows the project's full intended
    /// shape, not just what's been built. Toggle Auto Import and Set Sun +
    /// Location, from the previous Import/Sun & Site panels, aren't part
    /// of this ribbon shape and were dropped from here — their command
    /// classes are untouched and still work, just not wired to a button
    /// right now.
    /// </summary>
    public class SportfyRevitApp : IExternalApplication
    {
        private const string TabName = "Sportify";

        public Result OnStartup(UIControlledApplication application)
        {
            RoofBoundaryServer.Start();

            application.CreateRibbonTab(TabName);

            var importPanel = application.CreateRibbonPanel(TabName, "App & Data Import");
            AddButton(importPanel, "OpenSportifyApp", "Open\nSportify App", typeof(OpenSportifyAppCommand),
                "Opens the Sportify web app inside Revit. Placeholder for now — needs a browser pane docked inside Revit.");
            AddButton(importPanel, "PushRoofBoundary", "Push Roof\nto Sportify", typeof(PushRoofBoundaryCommand),
                "Select a roof or floor and send its footprint to the Sportify web app's Combine tab.");
            AddButton(importPanel, "ImportSportifyLayout", "Import\nConfiguration", typeof(ImportSportifyLayoutCommand),
                "Pick a Combine tab JSON export and build families (or placeholder geometry), name labels and worksets for it.");
            AddButton(importPanel, "ImportDxf", "Import\nDXF", typeof(ImportDxfCommand),
                "Placeholder — will import a DXF export (e.g. the Sport tab's \"Export DXF\") as reference geometry.");

            var analysisPanel = application.CreateRibbonPanel(TabName, "Analysis");
            AddButton(analysisPanel, "AnalyzeFireSafety", "Fire Safety\nAnalysis", typeof(AnalyzeFireSafetyCommand),
                "Placeholder — will check evacuation routes, travel distances and exit counts against fire-safety norms.");
            AddButton(analysisPanel, "AnalyzeWaterManagement", "Water Mgmt\nAnalysis", typeof(AnalyzeWaterManagementCommand),
                "Placeholder — will estimate rainwater absorption/retention across the roof's garden coverage and the runoff the drainage design would need to handle.");
            AddButton(analysisPanel, "AnalyzeLiveLoads", "Live Loads\nAnalysis", typeof(AnalyzeLiveLoadsCommand),
                "Placeholder — live-load analysis against the existing building's structural constraints; scope still being defined.");
            AddButton(analysisPanel, "AnalyzeCarbonImpact", "Carbon Impact\nAnalysis", typeof(AnalyzeCarbonImpactCommand),
                "Placeholder — will estimate the kinetic-to-electrical energy generation potential of a configuration from player activity and the materials assigned to each family.");
            AddButton(analysisPanel, "AnalyzeSunAndShading", "Sun & Shading\nAnalysis", typeof(AnalyzeSunAndShadingCommand),
                "Placeholder — will analyze shading across the layout using the site's sun position data from the web app's Site tab.");
            AddButton(analysisPanel, "AnalyzeLca", "LCA\nAnalysis", typeof(AnalyzeLcaCommand),
                "Placeholder — will run a Life Cycle Assessment (phases A-D) using material/provider data from the Sportify reference database.");
            AddButton(analysisPanel, "AnalyzeAccessibility", "Accessibility\nAnalysis", typeof(AnalyzeAccessibilityCommand),
                "Placeholder — will check circulation width/turning radius for wheelchair users, tactile/contrast guidance for blind users, and child-scaled equipment + fall-safety surfacing for children.");

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
                "Placeholder — will generate a report from the analyses above, letting the user toggle which sections to include.");
            AddButton(exportPanel, "GenerateFunctionalDiagrams", "Functional\nDiagrams", typeof(GenerateFunctionalDiagramsCommand),
                "Placeholder — will generate template-based diagrams: circulation, bubble diagram, and a box-like 3D axonometric.");
            AddButton(exportPanel, "GenerateSchedules", "Schedules\n(XLS)", typeof(GenerateSchedulesCommand),
                "Placeholder — will export an XLS schedule of the families in the model (materials, provider, quality, quantity, description), letting the user toggle which components to include.");

            application.Idling += AutoImportSync.OnIdling;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            application.Idling -= AutoImportSync.OnIdling;
            RoofBoundaryServer.Stop();
            return Result.Succeeded;
        }

        private static void AddButton(RibbonPanel panel, string internalName, string text, Type commandType, string tooltip)
        {
            var data = new PushButtonData(internalName, text, typeof(SportfyRevitApp).Assembly.Location, commandType.FullName)
            {
                ToolTip = tooltip,
            };
            panel.AddItem(data);
        }
    }
}
