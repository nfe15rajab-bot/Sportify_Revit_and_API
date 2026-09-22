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
                        // not a ribbon command: writes what the open project contains (TemplateInspector) to the Sportify folder of %APPDATA%, template-inspection, active.json
                        if (next == "InspectActive")
                        {
                            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "template-inspection");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "active.json"), System.Text.Json.JsonSerializer.Serialize(WithNorms(uiApp.ActiveUIDocument.Document), new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                            SportifyLog.Info("app", "SPORTIFY_RUN_COMMAND: InspectActive written");
                            notBefore = DateTime.UtcNow.AddSeconds(2);
                            return;
                        }
                        // TEST-ONLY, like InspectActive: writes views' crop state, worksets' contents and the bounding box of what Sportify built, to extra.json.
                        if (next == "InspectExtra")
                        {
                            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "template-inspection");
                            Directory.CreateDirectory(dir);
                            File.WriteAllText(Path.Combine(dir, "extra.json"), System.Text.Json.JsonSerializer.Serialize(InspectExtra(uiApp.ActiveUIDocument.Document), new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                            SportifyLog.Info("app", "SPORTIFY_RUN_COMMAND: InspectExtra written");
                            notBefore = DateTime.UtcNow.AddSeconds(2);
                            return;
                        }
                        // TEST-ONLY: builds the small test building (TestBuildingBuilder) in the open project, for live-testing Worksets / Push by workset with no families to load.
                        if (next == "BuildTestBuilding")
                        {
                            var doc = uiApp.ActiveUIDocument.Document;
                            using (var t = new Autodesk.Revit.DB.Transaction(doc, "Sportify test building"))
                            {
                                t.Start();
                                var result = TestBuildingBuilder.Build(doc);
                                t.Commit();
                                SportifyLog.Info("app", $"SPORTIFY_RUN_COMMAND: BuildTestBuilding: {result.Made.Count} made, {result.Notes.Count} note(s)");
                            }
                            notBefore = DateTime.UtcNow.AddSeconds(2);
                            return;
                        }
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

            // SPORTIFY_INSPECT_TEMPLATES=<template files separated by ;> writes what each one contains (TemplateInspector) as JSON into SPORTIFY_INSPECT_OUT (default
            // %APPDATA%\Sportify	emplate-inspection), once, at the first idle moment: how the Sportify templates are adapted from Revit's own is decided from these files.
            var inspectList = Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_TEMPLATES");
            if (!string.IsNullOrWhiteSpace(inspectList))
            {
                void InspectOnce(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
                {
                    application.Idling -= InspectOnce;
                    if (sender is not UIApplication uiApp) return;
                    var outDir = Environment.GetEnvironmentVariable("SPORTIFY_INSPECT_OUT");
                    if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify", "template-inspection");
                    var written = TemplateInspector.InspectFiles(uiApp, inspectList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), outDir);
                    SportifyLog.Info("templates", "SPORTIFY_INSPECT_TEMPLATES: " + written.Count + " file(s) written to " + outDir);
                }
                application.Idling += InspectOnce;
            }

            // SPORTIFY_BUILD_TEMPLATE=<folder> makes Sportify_DE.rte and Sportify_EN.rte there (SportifyTemplateFile): Revit's German BIM template / English multi-discipline template with the Sportify templates
            // applied and Revit's own hidden, and checks the round trip; SPORTIFY_VERIFY_IMPERIAL=1 also checks that an imperial project is converted.
            var buildTemplate = Environment.GetEnvironmentVariable("SPORTIFY_BUILD_TEMPLATE");
            if (!string.IsNullOrWhiteSpace(buildTemplate))
            {
                void BuildOnce(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
                {
                    application.Idling -= BuildOnce;
                    if (sender is not UIApplication uiApp) return;
                    foreach (var language in new[] { TemplateLanguage.De, TemplateLanguage.En })
                    {
                        // a folder: both files; a path ending in .rte: only the German one, to that path
                        var single = buildTemplate.EndsWith(".rte", StringComparison.OrdinalIgnoreCase);
                        if (single && language != TemplateLanguage.De) continue;
                        var file = single ? buildTemplate : Path.Combine(buildTemplate, SportifyTemplateFile.FileName(language));
                        try { SportifyTemplateFile.Build(uiApp.Application, file, language); }
                        catch (Exception ex) { SportifyLog.Error("templates", "SPORTIFY_BUILD_TEMPLATE failed for " + language, ex); }
                    }
                    if (Environment.GetEnvironmentVariable("SPORTIFY_VERIFY_IMPERIAL") == "1")
                        foreach (var language in new[] { TemplateLanguage.De, TemplateLanguage.En })
                        {
                            try { SportifyTemplateFile.VerifyImperial(uiApp.Application, language); }
                            catch (Exception ex) { SportifyLog.Error("templates", "SPORTIFY_VERIFY_IMPERIAL failed for " + language, ex); }
                        }
                }
                application.Idling += BuildOnce;
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

        /// <summary>What the InspectActive test hook writes: the project as TemplateInspector reads it, and which DIN 277 / DIN 276 classes the elements carry.</summary>
        private static Dictionary<string, object?> WithNorms(Autodesk.Revit.DB.Document doc)
        {
            var data = TemplateInspector.Inspect(doc);
            data["normClasses"] = TemplateInspector.NormSummary(doc);
            return data;
        }

        /// <summary>
        /// What the InspectExtra test hook writes: the crop state of every real view (the crop-region default), what sits on each Sportify workset
        /// (Worksets, push by workset), and the bounding box of what Sportify built (the roof's centred anchor when nothing was pushed from Revit).
        /// </summary>
        private static Dictionary<string, object?> InspectExtra(Autodesk.Revit.DB.Document doc)
        {
            double M(double feet) => Autodesk.Revit.DB.UnitUtils.ConvertFromInternalUnits(feet, Autodesk.Revit.DB.UnitTypeId.Meters);
            bool? TryCrop(Autodesk.Revit.DB.View v) { try { return v.CropBoxActive; } catch (Exception) { return null; } }

            var views = new Autodesk.Revit.DB.FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.View)).Cast<Autodesk.Revit.DB.View>()
                .Where(v => !v.IsTemplate && v.ViewType != Autodesk.Revit.DB.ViewType.Schedule && v.ViewType != Autodesk.Revit.DB.ViewType.SystemBrowser
                    && v.ViewType != Autodesk.Revit.DB.ViewType.ProjectBrowser && v.ViewType != Autodesk.Revit.DB.ViewType.Undefined && v.ViewType != Autodesk.Revit.DB.ViewType.Internal)
                .Select(v => new { name = v.Name, type = v.ViewType.ToString(), cropBoxActive = TryCrop(v) })
                .ToList();

            var worksets = new Dictionary<string, object?>();
            if (doc.IsWorkshared)
                foreach (var w in new Autodesk.Revit.DB.FilteredWorksetCollector(doc).OfKind(Autodesk.Revit.DB.WorksetKind.UserWorkset))
                {
                    var els = new Autodesk.Revit.DB.FilteredElementCollector(doc).WherePasses(new Autodesk.Revit.DB.ElementWorksetFilter(w.Id)).WhereElementIsNotElementType()
                        .Select(e => new { id = e.Id.Value, name = e.Name, category = e.Category?.Name, kind = PushWorksetAssigner.KindOf(e).ToString() }).ToList();
                    worksets[w.Name] = els;
                }

            var elements = SportifyElementScan.Find(doc).Elements;
            object? bbox = null;
            if (elements.Count > 0)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (var e in elements)
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    minX = Math.Min(minX, bb.Min.X); minY = Math.Min(minY, bb.Min.Y); maxX = Math.Max(maxX, bb.Max.X); maxY = Math.Max(maxY, bb.Max.Y);
                }
                if (minX < double.MaxValue)
                    bbox = new
                    {
                        min_x_m = Math.Round(M(minX), 2), min_y_m = Math.Round(M(minY), 2), max_x_m = Math.Round(M(maxX), 2), max_y_m = Math.Round(M(maxY), 2),
                        center_x_m = Math.Round(M((minX + maxX) / 2), 2), center_y_m = Math.Round(M((minY + maxY) / 2), 2),
                    };
            }

            return new Dictionary<string, object?>
            {
                ["views"] = views,
                ["worksets"] = worksets,
                ["sportifyElementCount"] = elements.Count,
                ["sportifyBoundingBox"] = bbox,
            };
        }

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
            // The "Push to Sportify" drop-down (AddPushMenu) is not in RibbonLayout.Panels: it is built imperatively, in the "App & Data Import" panel.
            if (internalName.StartsWith("PushRoof", StringComparison.Ordinal))
                return RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%CustomCtrl_%" + TabName + "%App & Data Import%PushToSportify%" + internalName);
            return null;
        }
    }
}
