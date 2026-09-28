using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Lets the web app ask Revit to do something that needs Revit's own API thread: the functional diagrams (needs Revit's own views), and an
    /// immediate import of whatever layout it just pushed ("Sync with Revit" — combineController.js/workspaceBridge.js's syncCombineToRevit).
    /// Revit's API may only be used from its own thread, so the local server (another thread) raises an external event, and Revit runs the
    /// handler when it is next idle. Diagrams presses the ribbon's own button, so the command is the one the person would have clicked; import-now
    /// runs exactly what AutoImportSync's Idling poll runs, just once and right away, so "Sync with Revit" imports the pushed layout whether or
    /// not Auto Import happens to be turned on — the whole point of a person clicking a button that says "Sync", rather than waiting up to two
    /// seconds and hoping a ribbon toggle they may never have touched is already on.
    /// Everything else the web app makes (analysis, charts, PDFs, schedule) is made by the server with no Revit call: WorkspaceEndpoints.
    /// </summary>
    internal sealed class RevitCommandBridge : IExternalEventHandler
    {
        const string DiagramsCommandId = "CustomCtrl_%CustomCtrl_%Sportify%Data Export / Deliverables%GenerateFunctionalDiagrams";

        readonly ExternalEvent _event;
        string? _pending;

        RevitCommandBridge() { _event = ExternalEvent.Create(this); }

        /// <summary>Creates the bridge (from OnStartup: an external event can only be created in Revit's API context) and connects the server to it.</summary>
        public static void Install()
        {
            var bridge = new RevitCommandBridge();
            WorkspaceEndpoints.RevitCommandRequested = name => bridge.Request(name);
        }

        string? Request(string name)
        {
            if (name != "diagrams" && name != "import-now") return "Unknown command.";
            _pending = name;
            var raised = _event.Raise();
            return raised == ExternalEventRequest.Accepted || raised == ExternalEventRequest.Pending ? null : "Revit could not take the request just now (" + raised + "): try again in a moment.";
        }

        public void Execute(UIApplication app)
        {
            var name = _pending;
            _pending = null;
            if (name == "diagrams") RunDiagrams(app);
            else if (name == "import-now") RunImportNow(app);
        }

        void RunDiagrams(UIApplication app)
        {
            try
            {
                var id = RevitCommandId.LookupCommandId(DiagramsCommandId);
                if (id != null && app.CanPostCommand(id)) app.PostCommand(id);
            }
            catch (Exception) { /* nothing to report to: the web app shows what is in the workspace */ }
        }

        /// <summary>
        /// The same import AutoImportSync's Idling handler runs, triggered once on request instead of on a poll: reads whatever RoofBoundaryServer
        /// currently has (syncCombineToRevit posts the real export just before asking for this), and imports it into the active document. A
        /// project with Auto Import already on double-imports nothing extra here — LayoutImporter.Run always replaces the previous import of the
        /// project (ImportLedger), never adds a second copy — and the next Idling tick simply sees a version it has already applied.
        /// </summary>
        void RunImportNow(UIApplication app)
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument) return;
            if (!RoofBoundaryServer.TryGetLatestCombinedLayout(out var json, out _, out var roofId) || json == null) return;

            SportifyLayout? layout;
            try { layout = JsonSerializer.Deserialize<SportifyLayout>(json); }
            catch (Exception ex) { SportifyLog.Error("import-now", "the pushed layout could not be read", ex); return; }
            if (layout?.Placements == null) return;

            var outcome = LayoutImporter.Run(doc, layout, ImportSource.Auto, AutoImportSync.ClearIterationsToo, roofId.ToString());
            if (outcome.Cancelled || outcome.Succeeded) return;
            TaskDialog.Show("Sportify — Sync with Revit",
                "The layout sent from the web app could not be imported into \"" + doc.Title + "\":\n" + outcome.Error +
                "\n\nThe project is as it was before. Details: " + SportifyLog.CurrentFile);
        }

        public string GetName() => "Sportify command bridge";
    }
}
