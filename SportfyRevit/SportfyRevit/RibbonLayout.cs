namespace SportfyRevit
{
    // The Sportify tab, as data: which panels it has, and in them which buttons and drop-downs, with their text, tooltip, icon and the command class each one runs.
    // No Revit types in here on purpose: the add-in builds the real ribbon from this (SportfyRevitApp.BuildRibbon), and Tools/AddinCheck reads the same file to check the
    // layout (every command class exists, every internal name is unique, every button has a tooltip and an icon, the spec's panels and drop-downs are there).
    // {APP_URL} in a tooltip is replaced by the address the pane loads.

    internal abstract record RibbonEntry;

    /// <summary>A push button. Icon is a name in RibbonIconData.</summary>
    internal sealed record RibbonButtonSpec(string InternalName, string Text, string CommandClass, string Tooltip, string Icon) : RibbonEntry;

    /// <summary>A drop-down of push buttons (its own icon is the large one, the items' are the 16 x 16 ones).</summary>
    internal sealed record RibbonPulldownSpec(string InternalName, string Text, string Tooltip, string Icon, IReadOnlyList<RibbonButtonSpec> Items) : RibbonEntry;

    internal sealed record RibbonSeparatorSpec : RibbonEntry;

    /// <summary>The "Push to Sportify" drop-down, which SportfyRevitApp.AddPushMenu builds (its own list of push commands).</summary>
    internal sealed record RibbonPushMenuSpec : RibbonEntry;

    internal sealed record RibbonPanelSpec(string Name, IReadOnlyList<RibbonEntry> Entries);

    internal static class RibbonLayout
    {
        public const string TabName = "Sportify";

        public static readonly IReadOnlyList<RibbonPanelSpec> Panels = new[]
        {
            new RibbonPanelSpec("App & Data Import", new RibbonEntry[]
            {
                new RibbonButtonSpec("OpenSportifyApp", "Open\nSportify App", "OpenSportifyAppCommand", "Opens the Sportify web app in a docked pane inside Revit ({APP_URL}).", "app"),
                new RibbonPushMenuSpec(),
                new RibbonButtonSpec("ImportSportifyLayout", "Import\nConfiguration", "ImportSportifyLayoutCommand", "Pick a Combine tab JSON export and build families (or placeholder geometry), name labels and worksets for it.", "import"),
                new RibbonButtonSpec("LoadFamilies", "Load\nFamilies", "LoadFamiliesCommand", "Pick your own .rfa families, load them into this project, and publish their types and writable parameters to the Sportify web app so they can be placed from the Combine tab.", "families"),
                new RibbonButtonSpec("ImportDxf", "Import\nDXF", "ImportDxfCommand", "Imports a DXF export (e.g. the Sport tab's \"Export DXF\") as reference geometry, placed in meters at the origin.", "dxf"),
                new RibbonButtonSpec("SetSunAndLocation", "Set Sun +\nLocation", "SetSunAndLocationCommand", "Sets this project's real Site Location and Sun Settings from the web app's Site tab data.", "location_sun"),
                new RibbonButtonSpec("ToggleAutoImport", "Auto Import:\nOFF", "ToggleAutoImportCommand", "Automatically apply every Combine export pushed from the web app, without opening a file picker. Click to turn on.", "sync"),
            }),
            new RibbonPanelSpec("Algorithmic Analysis", new RibbonEntry[]
            {
                new RibbonButtonSpec("AnalyzeFireSafety", "Fire Safety\nAnalysis", "AnalyzeFireSafetyCommand", "Checks evacuation travel distance from every piece to its nearest entry point against a reference figure from Sportify.Api.", "fire"),
                new RibbonButtonSpec("AnalyzeCarbonImpact", "Carbon Impact\nAnalysis", "AnalyzeCarbonImpactCommand", "Illustrative kinetic-to-electrical energy-harvesting ceiling across active playing surface, assuming piezoelectric-capable flooring.", "carbon"),
                new RibbonButtonSpec("AnalyzeLca", "LCA\nAnalysis", "AnalyzeLcaCommand", "Sums embodied carbon from each piece's picked reference material, using Sportify.Api's Materials table.", "leaf"),
                new RibbonButtonSpec("AnalyzeAccessibility", "Accessibility\nAnalysis", "AnalyzeAccessibilityCommand", "Checks circulation width against a wheelchair two-way reference and whether every piece is reachable from an entry point.", "accessibility"),
            }),
            new RibbonPanelSpec("Simulation & Analytics", new RibbonEntry[]
            {
                new RibbonButtonSpec("SendPhysicalAnalysisToWeb", "Send All to\nWeb App", "SendPhysicalAnalysisToWebCommand", "Sends the physical analyses to the Sportify web app (Analysis tab) in one go, with no questions asked: wind and erosion, rain and soil percolation, static loads, dynamic analysis, sun and shade, run on the layout the web app pushed. Uses whatever inputs you already decided (in this session or in the app's Structure and Site conditions tabs); what is still unconfirmed is marked PRELIMINARY, and a summary offers to review it. Needs no Unity and takes a few seconds. The ball trajectories and the 3D videos need Unity and keep their own buttons below; a video already made for exactly these numbers stays with them.", "send"),
                new RibbonSeparatorSpec(),
                new RibbonPulldownSpec("PullStructural", "Structural", "Static loads on the roof structure and the dynamic behaviour of the deck under people and weather. A physical analysis: it runs in Unity, or gives its charts as a PDF.", "structural", new[]
                {
                    new RibbonButtonSpec("AnalyzeStructuralLoads", "Run Bay Utilization Check", "AnalyzeStructuralLoadsCommand", "Static loads on the roof structure: pulls the structural grid and columns from the Revit model (with Push Roof), adds up the weight of the build-ups, courts, trees and the expected crowds bay by bay, and shows which bays are most loaded against the deck capacity and whether the load sits to one side, with the move or lightening that would balance it. The numbers appear at once and need no Unity; a 3D video is offered afterwards: the video if you have Unity (about a minute, with a progress window you can cancel), or the charts as a PDF if you don't. Before it runs, a window asks for the values the layout cannot know (the deck capacity, above all): enter your own or accept the built-in one knowingly; whatever is left unconfirmed marks the results PRELIMINARY. The same choice is in the app's Structure and Site conditions tabs.", "structural"),
                    new RibbonButtonSpec("AnalyzeDynamicLoads", "Dynamic Frequency & Vibration", "AnalyzeDynamicLoadsCommand", "The dynamic half of the structural analysis, in three scenarios one after the other: (1) crowds - how people move over the roof through a day and how much they weigh where; (2) weather - a cloudburst soaking the green roofs, snow and wind as load cases with the crowd, and the governing case of every bay; (3) resonance - the deck's natural frequency and what walking, court play or a jumping crowd does to it, ending on the frequency spectrum and the vibrating deck. The numbers appear at once and need no Unity; a 3D video is offered afterwards: the video if you have Unity (about two minutes, with a progress window you can cancel), or the charts as a PDF if you don't. Before it runs, a window asks for the values the layout cannot know (deck capacity, natural frequency, snow zone and altitude, the day's schedule, the comfort limits): enter your own or accept the built-in ones knowingly, each with its source; whatever is left unconfirmed marks the results PRELIMINARY. The same choice is in the app's Structure and Site conditions tabs.", "dynamic"),
                }),
                new RibbonPulldownSpec("PullEnvironmental", "Environmental", "Sun, wind and rain on the roof garden. A physical analysis: it runs in Unity, or gives its charts as a PDF.", "environmental", new[]
                {
                    new RibbonButtonSpec("AnalyzeSunShade", "Sun & Shade Analysis", "AnalyzeSunShadeCommand", "Direct sun on the roof through the year's design days (21 June, 21 March, 21 December): the hours of sun every part gets, which play areas and spectator zones are too sunny at midday and which gardens too shaded, and the shading equipment (pergolas, canopies, sails, parasols, a tree) that would fix it without taking the sun from the gardens, with its weight, the wind on it and the deck's answer. The numbers appear at once and need no Unity; a 3D video of the shadows sweeping the roof and the equipment going up is offered afterwards: the video if you have Unity (about two minutes, with a progress window you can cancel), or the charts as a PDF if you don't. Before it runs a window asks for the site, the orientation, the shade target and the sun the gardens need: enter your own or accept the built-in ones; whatever is left unconfirmed marks the results PRELIMINARY.", "sun"),
                    new RibbonButtonSpec("AnalyzeWindErosionRisk", "Wind Uplift & Erosion", "AnalyzeWindErosionRiskCommand", "Screens the roof garden for wind (EN 1991-1-4 roof zones, FLL guideline): would trees be blown over, would a build-up lift off the roof, would growing medium blow away — with fixes (ballast, anchoring, gravel strips). The numbers appear at once and need no Unity; a 3D video of the wind crossing the roof is offered afterwards: the video if you have Unity (about a minute, with a progress window you can cancel), or the charts as a PDF if you don't.", "wind"),
                    new RibbonButtonSpec("SimulateSoilPercolation", "Soil Percolation (Rain)", "SimulateSoilPercolationCommand", "Steps three rain events (steady, heavy shower, cloudburst) through the real layers of each roof-garden build-up: how much rain each keeps, how much runs off and how much later, and whether any fills up — with advice (more water-storing drainage layer or substrate). The numbers appear at once and need no Unity; a cross-section video of the water soaking down is offered afterwards: the video if you have Unity (about a minute, with a progress window you can cancel), or the charts as a PDF if you don't.", "rain"),
                }),
                new RibbonButtonSpec("SimulateBallTrajectories", "Ball Trajectory\nSimulation", "SimulateBallTrajectoriesCommand", "Runs the current layout through Sportify.Simulation (Unity): stray shots from every placed court, checking crossings into neighboring courts, the roof edge and circulation space. Records the flights as an MP4 video, and works out what share of shots leave the roof and where fences should go. Needs the Unity Editor (its physics makes the numbers, so there is no PDF alternative). Takes ~30-90s with a progress window you can cancel. Close the Unity Editor first if it has Sportify.Simulation open.", "ball"),
            }),
            new RibbonPanelSpec("BIM & Documentation", new RibbonEntry[]
            {
                new RibbonButtonSpec("GenerateRevitSchedules", "Generate\nSchedules", "GenerateRevitSchedulesCommand", "Creates automated Equipment Takeoff and Green Roof Build-up schedules.", "schedule"),
                new RibbonButtonSpec("ApplyViewFilters", "Apply View\nFilters", "ApplyViewFiltersCommand", "Applies graphic overrides and color filters for Zone Types to the active view (Structural Stress colouring follows once a utilisation value is written to the model).", "filter"),
                new RibbonPulldownSpec("PullPhasingWorksets", "Phasing &\nWorksets", "Batch-assign the Sportify elements of this project to a phase or to their worksets.", "phasing", new[]
                {
                    new RibbonButtonSpec("AssignPhasing", "Batch Assign Phasing", "AssignPhasingCommand", "Puts every Sportify element of this project (what the last import created) into the phase you choose (Phase Created).", "phase_assign"),
                    new RibbonButtonSpec("AssignWorksets", "Organize Multi-Worksets", "AssignWorksetsCommand", "Puts every Sportify element of this project on its own workset: courts and equipment on Sports, planting and ground on Gardens, boundaries, paths and entries on Combine. Needs a workshared project.", "workset_assign"),
                }),
            }),
            new RibbonPanelSpec("Data Export / Deliverables", new RibbonEntry[]
            {
                new RibbonButtonSpec("GenerateAnalysisReport", "Analysis\nReport\n(PDF)", "GenerateAnalysisReportCommand", "Generates and opens a PDF report: every Analysis check run this session, the component schedule, and the circulation/axonometric diagrams — auto-exported, no manual picking. Saved in the Analysis reports folder of your Sportify folder.", "report"),
                new RibbonButtonSpec("GenerateFunctionalDiagrams", "Functional\nDiagrams", "GenerateFunctionalDiagramsCommand", "Generates a circulation-only floor plan and a 3D massing axonometric, saved as PNG in the Diagrams folder of your Sportify folder (the web app's Deliverables tab can ask for them too). Bubble diagram not built yet.", "diagram"),
                new RibbonButtonSpec("GenerateSchedules", "Schedules\n(CSV)", "GenerateSchedulesCommand", "Exports a CSV schedule of every synced component (category, quality, reference material/provider, area) into the Schedules folder of your Sportify folder.", "csv"),
                new RibbonButtonSpec("OpenSportifyFolder", "Open Sportify\nFolder", "OpenSportifyFolderCommand", "Opens your Sportify folder (chosen when Sportify was installed, by default Documents\\Sportify Workspace) in Explorer: layouts, sport and garden data, analysis charts (PDF), videos, analysis reports, schedules and diagrams, one subfolder each. The web app's Deliverables tab lists the same files.", "folder"),
            }),
        };
    }
}
