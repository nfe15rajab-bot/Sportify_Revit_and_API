namespace SportfyRevit
{
    /// <summary>What a ribbon button needs of the computer besides Revit.</summary>
    internal enum RibbonNeed { Unity, SolidWorks }

    /// <summary>Whether one ribbon entry is shown, and why not when it is not (a sentence a person can act on).</summary>
    internal sealed record RibbonDecision(bool Visible, string Reason);

    /// <summary>
    /// Which buttons of the Sportify tab are shown: a pure function of the view the person chose (the PROFILE: Simple or Advanced) and of what this computer has
    /// (SportifyCapabilities), over the layout in RibbonLayout. Nothing here touches Revit; SportfyRevitApp applies the plan to the real ribbon, and Tools/AddinCheck tests it.
    ///
    /// Two reasons hide a button, and neither is a lock:
    ///  - the SIMPLE view keeps the main path (SimpleButtons) and hides the rest, which Advanced shows again;
    ///  - a button that cannot work on this computer is hidden instead of failing when it is clicked: the Ball Trajectory Simulation and Record Isolated Video need Unity
    ///    (their physics makes the numbers: there is no PDF alternative), Simulate needs SOLIDWORKS. The five physical analyses stay: without Unity they give a PDF.
    ///
    /// A drop-down is shown when any of its items is, a panel when any of its entries is. AlwaysVisible are never hidden by either rule, so the ribbon can never be left with
    /// no way back; SPORTIFY_SHOW_ALL_BUTTONS=1 turns both rules off.
    /// </summary>
    internal static class RibbonVisibility
    {
        /// <summary>The internal name of the "Push to Sportify" drop-down (RibbonPushMenuSpec, which SportfyRevitApp.AddPushMenu builds).</summary>
        public const string PushMenuName = "PushToSportify";

        /// <summary>The key of a panel in the plan.</summary>
        public static string PanelKey(string panelName) => "panel:" + panelName;

        public static readonly IReadOnlyDictionary<string, RibbonNeed> Needs = new Dictionary<string, RibbonNeed>
        {
            ["SimulateBallTrajectories"] = RibbonNeed.Unity,
            ["RecordKineticsVideo"] = RibbonNeed.Unity,
            ["SimulateKinetics"] = RibbonNeed.SolidWorks,
        };

        /// <summary>The main path, as it is in the web app's Simple view: open the app, push the roof, import the layout, send the analyses, and take the deliverables.</summary>
        public static readonly IReadOnlyList<string> SimpleButtons = new[]
        {
            "OpenSportifyApp", PushMenuName, "ImportSportifyLayout", "SendPhysicalAnalysisToWeb",
            "GenerateAnalysisReport", "GenerateFunctionalDiagrams", "GenerateSchedules", "OpenSportifyFolder",
        };

        /// <summary>Never hidden: the way into the web app (where the view is changed) and into the person's own files.</summary>
        public static readonly IReadOnlyList<string> AlwaysVisible = new[] { "OpenSportifyApp", "OpenSportifyFolder" };

        /// <summary>The view as the ribbon understands it: "simple", or anything else is "advanced" (the default).</summary>
        public static string NormalizeView(string? view) => string.Equals(view, "simple", StringComparison.OrdinalIgnoreCase) ? "simple" : "advanced";

        /// <summary>The decision for one button (or drop-down item, or the push menu) by its internal name.</summary>
        public static RibbonDecision ForButton(string internalName, string? view, Capabilities caps, bool showAll = false)
        {
            if (showAll) return new RibbonDecision(true, "");
            if (AlwaysVisible.Contains(internalName)) return new RibbonDecision(true, "");
            if (NormalizeView(view) == "simple" && !SimpleButtons.Contains(internalName))
                return new RibbonDecision(false, "Hidden in the Simple view: choose Advanced in the web app's Profile tab to show it.");
            if (Needs.TryGetValue(internalName, out var need))
            {
                var tool = need == RibbonNeed.Unity ? caps.Unity : caps.SolidWorks;
                if (!tool.Found) return new RibbonDecision(false, tool.Note);
            }
            return new RibbonDecision(true, "");
        }

        /// <summary>
        /// The decision for every button, drop-down item, drop-down and panel of the layout, by internal name (panels as PanelKey(name)). A drop-down and a panel are visible when
        /// anything in them is.
        /// </summary>
        public static IReadOnlyDictionary<string, RibbonDecision> Plan(string? view, Capabilities caps, bool showAll = false)
        {
            var plan = new Dictionary<string, RibbonDecision>();
            foreach (var panel in RibbonLayout.Panels)
            {
                var anyVisible = false;
                foreach (var entry in panel.Entries)
                {
                    switch (entry)
                    {
                        case RibbonButtonSpec button:
                            plan[button.InternalName] = ForButton(button.InternalName, view, caps, showAll);
                            anyVisible |= plan[button.InternalName].Visible;
                            break;
                        case RibbonPulldownSpec pulldown:
                            foreach (var item in pulldown.Items) plan[item.InternalName] = ForButton(item.InternalName, view, caps, showAll);
                            var shown = pulldown.Items.Any(i => plan[i.InternalName].Visible);
                            plan[pulldown.InternalName] = new RibbonDecision(shown, shown ? "" : "Every item of this drop-down is hidden.");
                            anyVisible |= shown;
                            break;
                        case RibbonPushMenuSpec:
                            plan[PushMenuName] = ForButton(PushMenuName, view, caps, showAll);
                            anyVisible |= plan[PushMenuName].Visible;
                            break;
                    }
                }
                plan[PanelKey(panel.Name)] = new RibbonDecision(anyVisible, anyVisible ? "" : "Nothing in this panel is shown.");
            }
            return plan;
        }

        /// <summary>The words on a button (or drop-down) as the person reads them, on one line; the internal name when the layout has no such entry.</summary>
        public static string TextOf(string internalName)
        {
            if (internalName == PushMenuName) return "Push to Sportify";
            static string OneLine(string text) => text.Replace("\n", " ");
            foreach (var panel in RibbonLayout.Panels)
                foreach (var entry in panel.Entries)
                {
                    if (entry is RibbonButtonSpec b && b.InternalName == internalName) return OneLine(b.Text);
                    if (entry is not RibbonPulldownSpec p) continue;
                    if (p.InternalName == internalName) return OneLine(p.Text);
                    var item = p.Items.FirstOrDefault(i => i.InternalName == internalName);
                    if (item != null) return OneLine(item.Text);
                }
            return internalName;
        }

        /// <summary>The names of what a plan hides (buttons and drop-downs, not panels), in the order of the ribbon: what the web app tells the person is hidden.</summary>
        public static IReadOnlyList<string> Hidden(IReadOnlyDictionary<string, RibbonDecision> plan) =>
            plan.Where(p => !p.Value.Visible && !p.Key.StartsWith("panel:", StringComparison.Ordinal)).Select(p => p.Key).ToList();
    }
}
