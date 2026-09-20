using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Lets the web app ask Revit for the one deliverable that needs Revit's own views, the functional diagrams. Revit's API may only be used from its own thread, so the
    /// local server (another thread) raises an external event, and Revit runs the handler when it is next idle; the handler presses the ribbon's own button, so the
    /// command is the one the person would have clicked (and its images land in the workspace's Diagrams folder).
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
            if (name != "diagrams") return "Unknown command.";
            _pending = name;
            var raised = _event.Raise();
            return raised == ExternalEventRequest.Accepted || raised == ExternalEventRequest.Pending ? null : "Revit could not take the request just now (" + raised + "): try again in a moment.";
        }

        public void Execute(UIApplication app)
        {
            var name = _pending;
            _pending = null;
            if (name != "diagrams") return;
            try
            {
                var id = RevitCommandId.LookupCommandId(DiagramsCommandId);
                if (id != null && app.CanPostCommand(id)) app.PostCommand(id);
            }
            catch (Exception) { /* nothing to report to: the web app shows what is in the workspace */ }
        }

        public string GetName() => "Sportify command bridge";
    }
}
