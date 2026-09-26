using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// Puts the plan of <see cref="RibbonVisibility"/> onto the real Sportify ribbon: every button, drop-down item, drop-down and panel is registered here as it is built
    /// (SportfyRevitApp.BuildRibbon) and shown or hidden by its internal name. The plan itself is pure and tested (Tools/AddinCheck); this is the thin part that needs Revit.
    ///
    /// When it runs: at start-up, once the ribbon is built; and again whenever the web app saves the profile (the view may have changed) or asks the add-in what this computer
    /// has (GET /capabilities?refresh=1), through <see cref="RibbonRefreshBridge"/>, because Revit's ribbon may only be touched from its own thread. A failure to hide one item is
    /// logged and skipped: the worst case is a button that stays visible, never a ribbon that is gone.
    /// </summary>
    internal static class RibbonApplier
    {
        static readonly Dictionary<string, RibbonItem> Items = new();
        static readonly Dictionary<string, RibbonPanel> Panels = new();
        static readonly Dictionary<string, bool> Shown = new();

        public static void Register(string internalName, RibbonItem item) { Items[internalName] = item; }

        public static void RegisterPanel(string panelName, RibbonPanel panel) { Panels[panelName] = panel; }

        /// <summary>SPORTIFY_SHOW_ALL_BUTTONS=1 shows every button whatever the view and the computer say (the way out if a rule ever hides too much).</summary>
        public static bool ShowAll => Environment.GetEnvironmentVariable("SPORTIFY_SHOW_ALL_BUTTONS") == "1";

        /// <summary>The view in force, from the profile in the settings file ("advanced" when there is none).</summary>
        public static string CurrentView() => RibbonVisibility.NormalizeView(SportifyProfile.Read()?["view"]?.GetValue<string>());

        /// <summary>Must run on Revit's thread (start-up, or an external event).</summary>
        public static void Apply()
        {
            var view = CurrentView();
            var plan = RibbonVisibility.Plan(view, SportifyCapabilities.Current(refresh: true), ShowAll);
            int changed = 0, failed = 0;
            foreach (var (key, decision) in plan)
            {
                try
                {
                    if (Shown.TryGetValue(key, out var was) && was == decision.Visible) continue;
                    if (key.StartsWith("panel:", StringComparison.Ordinal))
                    {
                        if (!Panels.TryGetValue(key["panel:".Length..], out var panel)) continue;
                        panel.Visible = decision.Visible;
                    }
                    else
                    {
                        if (!Items.TryGetValue(key, out var item)) continue;
                        item.Visible = decision.Visible;
                    }
                    Shown[key] = decision.Visible;
                    changed++;
                }
                catch (Exception ex)
                {
                    failed++;
                    SportifyLog.Warn("ribbon", "the visibility of " + key + " could not be set: " + ex.Message);
                }
            }
            if (changed > 0 || failed > 0)
                SportifyLog.Info("ribbon", "the ribbon follows the " + view + " view and this computer: " + RibbonVisibility.Hidden(plan).Count + " hidden, " + changed + " changed" + (failed > 0 ? ", " + failed + " could not be set" : ""));
        }
    }

    /// <summary>
    /// Lets the local server (another thread) ask Revit to re-apply the ribbon plan: it raises an external event and Revit runs <see cref="Execute"/> on its own thread when it is
    /// next idle. Coalescing is Revit's: several requests before it gets to run are one run.
    /// </summary>
    internal sealed class RibbonRefreshBridge : IExternalEventHandler
    {
        readonly ExternalEvent _event;

        RibbonRefreshBridge() { _event = ExternalEvent.Create(this); }

        /// <summary>From OnStartup (an external event can only be created in Revit's API context): connects the server to it.</summary>
        public static void Install()
        {
            var bridge = new RibbonRefreshBridge();
            WorkspaceEndpoints.RibbonRefreshRequested = () => bridge._event.Raise();
        }

        public void Execute(UIApplication app)
        {
            try { RibbonApplier.Apply(); }
            catch (Exception ex) { SportifyLog.Warn("ribbon", "the ribbon could not be refreshed: " + ex.Message); }
        }

        public string GetName() => "Sportify ribbon refresh";
    }
}
