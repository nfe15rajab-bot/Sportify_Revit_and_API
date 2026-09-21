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
    /// Five panels, matching the project's own scope split (the layout itself is data, RibbonLayout):
    /// App & Data Import (getting a layout into Revit); Algorithmic Analysis (rules and
    /// calculations on the layout - fire safety, accessibility, LCA, carbon - no engine involved);
    /// Simulation & Analytics (the physical analyses, which specifically need a physics/rendering
    /// engine - Unity - kept apart from the algorithmic ones, in drop-downs by kind: Structural,
    /// Environmental); BIM & Documentation (schedules, view filters, phasing and worksets of what
    /// Sportify put in the model); and Data Export / Deliverables (getting results back out).
    /// </summary>
    public class SportfyRevitApp : IExternalApplication
    {
        private const string TabName = "Sportify";

        public Result OnStartup(UIControlledApplication application)
        {
            // The first thing, before anything can fail: the log, and every exception nobody catches ends up in it.
            SportifyLog.Info("app", "Sportify add-in " + typeof(SportfyRevitApp).Assembly.GetName().Version + " starting in Revit " +
                                     application.ControlledApplication.VersionNumber + " (" + application.ControlledApplication.VersionName + ")");
            AppDomain.CurrentDomain.UnhandledException += (_, args) => SportifyLog.Error("app", "unhandled exception" + (args.IsTerminating ? " (Revit is terminating)" : ""), args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) => { SportifyLog.Error("app", "unobserved task exception", args.Exception); args.SetObserved(); };

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

            // The tab, its panels and their buttons come from RibbonLayout (data), and each entry is added on its own: one that cannot be added (an icon that will not draw,
            // a command class that is gone) is logged and skipped, it never takes the add-in down with it.
            BuildRibbon(application);

            application.Idling += AutoImportSync.OnIdling;

            // An unattended run (a test, a script) has nobody to click the ribbon: SPORTIFY_AUTO_IMPORT=1 turns Auto Import on at startup, as the button does.
            if (Environment.GetEnvironmentVariable("SPORTIFY_AUTO_IMPORT") == "1")
            {
                AutoImportSync.SetEnabled(true);
                SportifyLog.Info("app", "Auto Import turned on at startup (SPORTIFY_AUTO_IMPORT=1)");
            }

            // An unattended run has nobody to click a ribbon button either. SPORTIFY_RUN_COMMAND=<internal names separated by ;> (for example
            // "GenerateRevitSchedules;ApplyViewFilters") posts those commands as if they had been clicked, one every few seconds, once a project is open. SPORTIFY_RUN_COMMAND=watch
            // instead waits for a file %APPDATA%\Sportify\run-commands.txt (the same names, one per line or separated by ;), runs what it names and deletes it: a test can import a
            // layout first and only then ask for the commands.
            var runCommands = Environment.GetEnvironmentVariable("SPORTIFY_RUN_COMMAND");
            if (!string.IsNullOrWhiteSpace(runCommands))
            {
                var watch = runCommands.Trim().Equals("watch", StringComparison.OrdinalIgnoreCase);
                var triggerFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "run-commands.txt");
                var queue = new Queue<string>(watch ? Array.Empty<string>() : runCommands.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                var notBefore = DateTime.MinValue;
                void RunNext(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
                {
                    if (sender is not UIApplication uiApp || uiApp.ActiveUIDocument == null || DateTime.UtcNow < notBefore) return;
                    if (queue.Count == 0 && watch && File.Exists(triggerFile))
                    {
                        try
                        {
                            foreach (var name in File.ReadAllText(triggerFile).Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) queue.Enqueue(name);
                            File.Delete(triggerFile);
                        }
                        catch (Exception ex) { SportifyLog.Warn("app", "SPORTIFY_RUN_COMMAND: " + triggerFile + " could not be read: " + ex.Message); notBefore = DateTime.UtcNow.AddSeconds(5); return; }
                    }
                    if (queue.Count == 0) { if (!watch) application.Idling -= RunNext; return; }
                    var next = queue.Dequeue();
                    try
                    {
                        var id = CommandIdFor(next);
                        if (id == null) { SportifyLog.Warn("app", "SPORTIFY_RUN_COMMAND: no ribbon command named \"" + next + "\""); return; }
                        SportifyLog.Info("app", "SPORTIFY_RUN_COMMAND: running " + next);
                        uiApp.PostCommand(id);
                        notBefore = DateTime.UtcNow.AddSeconds(8);
                    }
                    catch (Exception ex) { SportifyLog.Warn("app", "SPORTIFY_RUN_COMMAND: " + next + " could not be posted: " + ex.Message); }
                }
                application.Idling += RunNext;
            }

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
                try
                {
                    var pane = uiApp.GetDockablePane(SportifyDockablePaneProvider.PaneId);
                    if (!pane.IsShown()) pane.Show();
                }
                catch (Exception ex)
                {
                    // "The requested dockable pane has not been created yet": Revit is not showing its UI frame (an unattended run). The pane is one ribbon click away.
                    SportifyLog.Warn("app", "the Sportify pane was not opened automatically: " + ex.Message);
                }
            }
            application.Idling += ShowPaneOnce;

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            SportifyLog.Info("app", "Sportify add-in shutting down");
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

            // one icon per item (RibbonIconData, the "push" group)
            var icons = new Dictionary<string, string>
            {
                ["PushRoofBoundary"] = "push_all", ["PushRoofOutline"] = "push_outline", ["PushRoofStructure"] = "push_structure", ["PushRoofEntries"] = "push_entries",
                ["PushRoofOpenings"] = "push_openings", ["PushRoofEdge"] = "push_edge", ["PushRoofDrains"] = "push_drains", ["PushRoofEquipment"] = "push_equipment",
                ["PushRoofSlabLevels"] = "push_slab",
            };
            var menuLarge = RibbonIcons.Large("push");
            var menuSmall = RibbonIcons.Small("push");
            if (menuLarge != null) menu.LargeImage = menuLarge;
            if (menuSmall != null) menu.Image = menuSmall;

            void Item(string name, string text, Type command, string tip)
            {
                var data = new PushButtonData(name, text, typeof(SportfyRevitApp).Assembly.Location, command.FullName) { ToolTip = tip };
                if (icons.TryGetValue(name, out var icon))
                {
                    var small = RibbonIcons.Small(icon);
                    var large = RibbonIcons.Large(icon);
                    if (small != null) data.Image = small;
                    if (large != null) data.LargeImage = large;
                }
                menu.AddPushButton(data);
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

        /// <summary>The Sportify tab from RibbonLayout: panels, buttons, drop-downs, separators, with their tooltips and icons.</summary>
        private static void BuildRibbon(UIControlledApplication application)
        {
            application.CreateRibbonTab(TabName);
            var assembly = typeof(SportfyRevitApp).Assembly;
            foreach (var spec in RibbonLayout.Panels)
            {
                RibbonPanel panel;
                try { panel = application.CreateRibbonPanel(TabName, spec.Name); }
                catch (Exception ex) { SportifyLog.Error("ribbon", "the panel \"" + spec.Name + "\" could not be created", ex); continue; }
                foreach (var entry in spec.Entries)
                {
                    try { AddEntry(panel, assembly, entry); }
                    catch (Exception ex) { SportifyLog.Error("ribbon", "an entry of the panel \"" + spec.Name + "\" could not be added (" + Describe(entry) + ")", ex); }
                }
            }
        }

        private static string Describe(RibbonEntry entry) => entry switch
        {
            RibbonButtonSpec b => "button " + b.InternalName,
            RibbonPulldownSpec p => "drop-down " + p.InternalName,
            _ => entry.GetType().Name,
        };

        private static void AddEntry(RibbonPanel panel, System.Reflection.Assembly assembly, RibbonEntry entry)
        {
            switch (entry)
            {
                case RibbonButtonSpec button:
                    var pushButton = (PushButton)panel.AddItem(ButtonData(assembly, button, item: false));
                    if (button.InternalName == "ToggleAutoImport") AutoImportSync.ToggleButton = pushButton;
                    break;
                case RibbonPulldownSpec pulldown:
                    var data = new PulldownButtonData(pulldown.InternalName, pulldown.Text) { ToolTip = Tooltip(pulldown.Tooltip) };
                    var large = RibbonIcons.Large(pulldown.Icon);
                    if (large != null) data.LargeImage = large;
                    var small = RibbonIcons.Small(pulldown.Icon);
                    if (small != null) data.Image = small;
                    var menu = (PulldownButton)panel.AddItem(data);
                    foreach (var item in pulldown.Items)
                    {
                        try { menu.AddPushButton(ButtonData(assembly, item, item: true)); }
                        catch (Exception ex) { SportifyLog.Error("ribbon", "the item " + item.InternalName + " of " + pulldown.InternalName + " could not be added", ex); }
                    }
                    break;
                case RibbonSeparatorSpec:
                    panel.AddSeparator();
                    break;
                case RibbonPushMenuSpec:
                    AddPushMenu(panel);
                    break;
            }
        }

        private static PushButtonData ButtonData(System.Reflection.Assembly assembly, RibbonButtonSpec spec, bool item)
        {
            var type = assembly.GetType("SportfyRevit." + spec.CommandClass) ?? throw new InvalidOperationException("the command class SportfyRevit." + spec.CommandClass + " does not exist");
            var data = new PushButtonData(spec.InternalName, spec.Text, assembly.Location, type.FullName) { ToolTip = Tooltip(spec.Tooltip) };
            // 32 x 32 for a button on the panel, 16 x 16 for an item inside a drop-down (a large image is still given to an item: Revit shows it in the wider menu style)
            var large = RibbonIcons.Large(spec.Icon);
            var small = RibbonIcons.Small(spec.Icon);
            if (item) { if (small != null) data.Image = small; if (large != null) data.LargeImage = large; }
            else { if (large != null) data.LargeImage = large; if (small != null) data.Image = small; }
            return data;
        }

        private static string Tooltip(string text) => text.Replace("{APP_URL}", SportifyBrowserPane.DefaultUrl);

        /// <summary>
        /// The id Revit gives a ribbon command of this add-in (what PostCommand takes), found by the button's internal name, or null. A button on a panel is
        /// CustomCtrl_%CustomCtrl_%Tab%Panel%Button; one inside a drop-down has one more level: CustomCtrl_%CustomCtrl_%CustomCtrl_%Tab%Panel%Dropdown%Button.
        /// </summary>
        internal static RevitCommandId? CommandIdFor(string internalName)
        {
            foreach (var panel in RibbonLayout.Panels)
                foreach (var entry in panel.Entries)
                {
                    if (entry is RibbonButtonSpec b && b.InternalName == internalName)
                        return RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%" + TabName + "%" + panel.Name + "%" + b.InternalName);
                    if (entry is RibbonPulldownSpec p && p.Items.Any(i => i.InternalName == internalName))
                        return RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%CustomCtrl_%" + TabName + "%" + panel.Name + "%" + p.InternalName + "%" + internalName);
                }
            return null;
        }
    }
}
