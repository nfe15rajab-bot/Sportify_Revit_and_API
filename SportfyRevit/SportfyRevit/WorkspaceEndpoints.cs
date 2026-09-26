using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SportfyRevit
{
    /// <summary>What an endpoint answers: JSON, or a file for the server to stream (with ranges, for a video).</summary>
    internal sealed class EndpointResponse
    {
        public int Status = 200;
        public string ContentType = "application/json";
        public byte[]? Body;
        /// <summary>When set, the server streams this file instead of Body.</summary>
        public string? FilePath;

        public static EndpointResponse Json(object value, int status = 200)
            => new() { Status = status, Body = JsonSerializer.SerializeToUtf8Bytes(value) };

        public static EndpointResponse Error(int status, string message) => Json(new { error = message }, status);
    }

    /// <summary>
    /// What the web app asks of the add-in besides the roof and the layout: the workspace folder and what is in it, saving a file into it, running the physical analyses,
    /// drawing their charts, and making the PDFs and the schedule. All of it works on the layout the web app has sent (RoofBoundaryServer keeps the latest), with no Revit
    /// call, so it runs whenever Revit is open with the add-in loaded: the user imports and exports nothing by hand. Only the functional diagrams need Revit's own
    /// views: they go through <see cref="RevitCommandRequested"/>, which the add-in wires to an external event.
    ///
    /// Written as a plain function of (method, path, query, body) so Tools/ContractCheck can call it without a listener; RoofBoundaryServer only carries the bytes.
    /// </summary>
    internal static class WorkspaceEndpoints
    {
        const int MaxUploadBytes = 200 * 1024 * 1024;

        /// <summary>Set by the add-in to ask Revit to run a command that needs its API ("diagrams"); returns null when started, else why not. Null = not wired.</summary>
        public static Func<string, string?>? RevitCommandRequested;

        /// <summary>The version of the Revit this add-in runs in ("2025"), set at start-up; "" when not known. The web app's status pill says "Revit 2025 connected".</summary>
        public static string RevitVersion = "";

        /// <summary>Set by the add-in to have the ribbon re-follow the view and this computer (RibbonRefreshBridge: on Revit's own thread). Null = not wired (tests).</summary>
        public static Action? RibbonRefreshRequested;

        /// <summary>Opens a folder in Explorer. Replaced in tests.</summary>
        public static Action<string> OpenFolder = folder => Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

        static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".webp"] = "image/webp", [".mp4"] = "video/mp4", [".csv"] = "text/csv; charset=utf-8",
            [".json"] = "application/json", [".dxf"] = "application/dxf", [".svg"] = "image/svg+xml", [".txt"] = "text/plain; charset=utf-8",
        };

        public static string ContentTypeOf(string path) => ContentTypes.TryGetValue(Path.GetExtension(path), out var t) ? t : "application/octet-stream";

        static string Url(string kind, string name) => $"/deliverable?kind={Uri.EscapeDataString(kind)}&name={Uri.EscapeDataString(name)}";

        /// <summary>The answer for one of these paths, or null when the path is not one of theirs.</summary>
        public static EndpointResponse? Handle(string method, string? path, NameValueCollection query, Func<byte[]> readBody)
        {
            switch (path)
            {
                case "/workspace" when method == "GET": return Workspace();
                case "/profile" when method == "GET": return GetProfile();
                case "/profile" when method == "POST": return SaveProfile(readBody);
                case "/capabilities" when method == "GET": return GetCapabilities(query["refresh"] == "1");
                case "/deliverables" when method == "GET": return Deliverables();
                case "/deliverable" when method == "GET" || method == "HEAD": return GetDeliverable(query["kind"], query["name"]);
                case "/deliverable" when method == "POST": return SaveDeliverable(query["kind"], query["name"], readBody);
                case "/run-analysis" when method == "POST": return RunAnalysis();
                case "/analysis-pdf" when method == "POST": return AnalysisPdf(query["keys"]);
                case "/analysis-report" when method == "POST": return AnalysisReport();
                case "/schedule" when method == "POST": return Schedule();
                case "/charts" when method == "GET": return Charts();
                case "/analysis-config" when method == "GET": return AnalysisConfig();
                case "/open-folder" when method == "POST": return OpenWorkspace(query["kind"]);
                case "/revit-command" when method == "POST": return RevitCommand(query["name"]);
                default: return null;
            }
        }

        // ------------------------------------------------------------------------------------------------------------ the workspace

        static EndpointResponse Workspace()
        {
            var root = SportifyWorkspace.EnsureCreated();
            var files = SportifyWorkspace.List();
            return EndpointResponse.Json(new
            {
                folder = root,
                default_folder = SportifyWorkspace.DefaultFolder,
                settings_file = SportifyWorkspace.SettingsPath,
                revit_version = RevitVersion,
                kinds = SportifyWorkspace.Kinds.Select(k => new { key = k.Key, folder = k.Folder, title = k.Title, hint = k.Hint, count = files.Count(f => f.Kind == k.Key) }),
            });
        }

        // ------------------------------------------------------------------------------------------------------------ the profile

        /// <summary>The PROFILE the settings file holds (null when there is none yet) and where its file is in the Sportify folder.</summary>
        static EndpointResponse GetProfile()
        {
            var profile = SportifyProfile.Read();
            return EndpointResponse.Json(new { profile, file = profile == null ? null : SportifyProfile.FilePath(), settings_file = SportifyWorkspace.SettingsPath });
        }

        /// <summary>
        /// Keeps the profile the web app sends: rebuilt from the fields it knows (SportifyProfile.Sanitize), written into the settings file, and as Sportify-PROFILE.json (and the photo) into the
        /// Profile folder of the Sportify folder. The answer is the profile as kept, and where the file is; a folder that cannot be written does not fail the save (folder_error says why).
        /// </summary>
        static EndpointResponse SaveProfile(Func<byte[]> readBody)
        {
            var body = readBody();
            if (body.Length == 0) return EndpointResponse.Error(400, "The profile is empty.");
            System.Text.Json.Nodes.JsonNode? node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(body); }
            catch (JsonException) { return EndpointResponse.Error(400, "The profile is not valid JSON."); }
            var clean = SportifyProfile.Sanitize(node);
            if (clean == null) return EndpointResponse.Error(400, "The profile has to be a JSON object.");
            try { SportifyProfile.SaveToSettings(clean); }
            catch (Exception ex) { return EndpointResponse.Error(500, "The settings file could not be written: " + ex.Message); }
            var (file, photo, error) = SportifyProfile.WriteToFolder(clean);
            RequestRibbonRefresh();      // the view may have changed: the Sportify tab in Revit follows it
            return EndpointResponse.Json(new { profile = clean, file, photo_file = photo, folder_error = error });
        }

        static void RequestRibbonRefresh()
        {
            try { RibbonRefreshRequested?.Invoke(); }
            catch (Exception) { /* Revit could not take the request just now: the ribbon follows at the next change */ }
        }

        // ------------------------------------------------------------------------------------------------------------ what this computer has

        /// <summary>
        /// What this computer can do (Unity for the 3D videos, SOLIDWORKS for the mechanical assemblies, Chrome for the web app), and what the Sportify tab in Revit therefore hides
        /// besides what the person's Simple view hides. The add-in knows, so nobody is asked. ?refresh=1 looks at the computer again (the Profile tab does when it opens: Unity may
        /// have been installed since Revit started) and has the ribbon follow.
        /// </summary>
        static EndpointResponse GetCapabilities(bool refresh)
        {
            var caps = SportifyCapabilities.Current(refresh);
            var profile = SportifyProfile.Read();
            var view = RibbonVisibility.NormalizeView(profile?["view"]?.GetValue<string>());
            var plan = RibbonVisibility.Plan(view, caps, Environment.GetEnvironmentVariable("SPORTIFY_SHOW_ALL_BUTTONS") == "1", SportifyProfile.ExtrasIn(profile));
            var body = caps.ToJson();
            body["view"] = view;
            body["ribbon_hidden"] = new System.Text.Json.Nodes.JsonArray(RibbonVisibility.Hidden(plan)
                .Select(name => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject { ["name"] = name, ["text"] = RibbonVisibility.TextOf(name), ["reason"] = plan[name].Reason }).ToArray());
            if (refresh) RequestRibbonRefresh();
            return EndpointResponse.Json(body);
        }

        static EndpointResponse Deliverables()
        {
            var root = SportifyWorkspace.EnsureCreated();
            return EndpointResponse.Json(new
            {
                folder = root,
                files = SportifyWorkspace.List().Select(f => new { kind = f.Kind, name = f.Name, size = f.Size, modified_utc = f.ModifiedUtc.ToString("o"), url = Url(f.Kind, f.Name) }),
            });
        }

        static EndpointResponse GetDeliverable(string? kind, string? name)
        {
            if (!SportifyWorkspace.TryResolve(kind, name, out var path)) return EndpointResponse.Error(404, "No such file in the workspace.");
            return new EndpointResponse { ContentType = ContentTypeOf(path), FilePath = path };
        }

        static EndpointResponse SaveDeliverable(string? kind, string? name, Func<byte[]> readBody)
        {
            if (SportifyWorkspace.KindOf(kind) == null) return EndpointResponse.Error(400, "Unknown kind \"" + kind + "\".");
            if (SportifyWorkspace.SafeName(name).Length == 0) return EndpointResponse.Error(400, "The file needs a name.");
            var body = readBody();
            if (body.Length == 0) return EndpointResponse.Error(400, "The file is empty.");
            if (body.Length > MaxUploadBytes) return EndpointResponse.Error(413, "The file is larger than 200 MB.");
            var saved = SportifyWorkspace.Save(kind!, name!, body);
            var savedName = Path.GetFileName(saved);
            return EndpointResponse.Json(new { kind = SportifyWorkspace.KindOf(kind)!.Key, name = savedName, path = saved, url = Url(SportifyWorkspace.KindOf(kind)!.Key, savedName) }, 201);
        }

        static EndpointResponse OpenWorkspace(string? kind)
        {
            try
            {
                var folder = SportifyWorkspace.KindOf(kind) != null ? SportifyWorkspace.PathFor(kind!) : SportifyWorkspace.EnsureCreated();
                OpenFolder(folder);
                return EndpointResponse.Json(new { opened = folder });
            }
            catch (Exception ex) { return EndpointResponse.Error(500, "The folder could not be opened: " + ex.Message); }
        }

        // ------------------------------------------------------------------------------------------------------------ what the layout gives

        /// <summary>The layout the web app has sent, with what the designer decided in this Revit session put in; null (and why) when none has arrived.</summary>
        static bool TryLayout(out string json, out EndpointResponse? failure)
        {
            failure = null;
            json = "";
            if (!RoofBoundaryServer.TryGetLatestCombinedLayout(out var latest, out _) || latest == null)
            {
                failure = EndpointResponse.Error(409, "No layout has reached the add-in yet: open the Sportify app and place something in Combine.");
                return false;
            }
            json = AssumptionsSession.ApplyRemembered(latest);
            return true;
        }

        static EndpointResponse RunAnalysis()
        {
            if (!TryLayout(out var json, out var failure)) return failure!;
            var sent = PhysicalAnalysisBatch.Run(json);
            return EndpointResponse.Json(new
            {
                layout_id = RoofBoundaryServer.LayoutIdForPublishing,
                analyses = sent.Select(s => new { key = s.Key, title = s.Title, sent = s.Sent, headline = s.Headline, preliminary = s.Preliminary, video_kept = s.VideoKept, video_dropped = s.VideoDropped, problem = s.Problem }),
                sent = sent.Count(s => s.Sent),
            });
        }

        /// <summary>The inputs the designer entered or accepted in Revit's assumptions dialog for the project on screen, so the web app can show them as its own.</summary>
        static EndpointResponse AnalysisConfig()
        {
            var decisions = RoofBoundaryServer.TryGetLatestCombinedLayout(out var latest, out _) && latest != null ? AssumptionsSession.DecidedFor(latest) : new List<AssumptionDecision>();
            return EndpointResponse.Json(new { decisions = decisions.Select(d => new { key = d.Key, state = d.State, value = d.Value }) });
        }

        static EndpointResponse Charts()
        {
            if (!TryLayout(out var json, out var failure)) return failure!;
            var built = PhysicalAnalysisPdf.Build(json);
            return EndpointResponse.Json(new
            {
                generated_utc = DateTime.UtcNow.ToString("o"),
                layout_id = RoofBoundaryServer.LayoutIdForPublishing,
                sections = built.Sections.Select(s => new
                {
                    key = s.Key, title = s.Title, headline = s.Headline, preliminary = s.Preliminary,
                    charts = s.Charts.Select(c => new { caption = c.Caption, aspect = c.Aspect, svg = c.Svg }),
                }),
                skipped = built.Skipped.Select(k => new { title = k.Title, reason = k.Reason }),
            });
        }

        static EndpointResponse AnalysisPdf(string? keys)
        {
            if (!TryLayout(out var json, out var failure)) return failure!;
            var wanted = string.IsNullOrWhiteSpace(keys) ? null : keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var pdf = PhysicalAnalysisPdf.Export(json, SportifyWorkspace.PathFor("analysis"), "", wanted);
            if (!pdf.Ok) return EndpointResponse.Error(409, pdf.Error ?? "The PDF could not be made.");
            var name = Path.GetFileName(pdf.Path!);
            return EndpointResponse.Json(new
            {
                kind = "analysis", name, path = pdf.Path, url = Url("analysis", name),
                drawn = pdf.Sections.Select(s => s.Title), skipped = pdf.Skipped.Select(k => new { title = k.Title, reason = k.Reason }),
            }, 201);
        }

        static EndpointResponse Schedule()
        {
            if (!TryLayout(out var json, out var failure)) return failure!;
            SportifyLayout? layout;
            try { layout = JsonSerializer.Deserialize<SportifyLayout>(json); }
            catch (Exception ex) { return EndpointResponse.Error(400, "The layout could not be read: " + ex.Message); }
            if (layout?.Placements == null || layout.Placements.Count == 0) return EndpointResponse.Error(409, "The layout has no pieces yet: place a sport, an activity or a garden piece in Combine.");
            var name = $"Sportify_Schedule_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
            var path = SportifyWorkspace.Save("schedules", name, new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(ScheduleCsv.Build(layout))).ToArray());   // BOM: Excel reads the umlauts
            return EndpointResponse.Json(new { kind = "schedules", name = Path.GetFileName(path), path, url = Url("schedules", Path.GetFileName(path)), rows = layout.Placements.Count }, 201);
        }

        static EndpointResponse AnalysisReport()
        {
            SportifyLayout? layout = null;
            if (RoofBoundaryServer.TryGetLatestCombinedLayout(out var latest, out _) && latest != null)
            {
                try { layout = JsonSerializer.Deserialize<SportifyLayout>(latest); } catch (Exception) { /* a report without it */ }
            }
            AnalysisResultPayload? results = null;
            if (RoofBoundaryServer.TryGetLatestAnalysisResults(out var resultsJson) && resultsJson != null)
            {
                try { results = JsonSerializer.Deserialize<AnalysisResultPayload>(resultsJson); } catch (Exception) { /* malformed: without it */ }
            }
            var path = Path.Combine(SportifyWorkspace.PathFor("reports"), $"Sportify_Analysis_Report_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
            try
            {
                // the diagram images Revit exported to the workspace, when it has (the newest of each)
                AnalysisReportPdfBuilder.Generate(path, layout, results, NewestDiagram("circulation"), NewestDiagram("axonometric"));
            }
            catch (Exception ex) { return EndpointResponse.Error(500, "The report could not be made: " + ex.Message); }
            var name = Path.GetFileName(path);
            return EndpointResponse.Json(new { kind = "reports", name, path, url = Url("reports", name), has_results = results != null, has_diagrams = NewestDiagram("circulation") != null }, 201);
        }

        static string? NewestDiagram(string prefix)
        {
            try
            {
                return new DirectoryInfo(SportifyWorkspace.PathFor("diagrams")).EnumerateFiles(prefix + "*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
            }
            catch (Exception) { return null; }
        }

        static EndpointResponse RevitCommand(string? name)
        {
            if (name != "diagrams") return EndpointResponse.Error(400, "Unknown command \"" + name + "\".");
            if (RevitCommandRequested == null) return EndpointResponse.Error(501, "This is not running inside Revit.");
            var problem = RevitCommandRequested(name);
            return problem == null ? EndpointResponse.Json(new { requested = name }, 202) : EndpointResponse.Error(409, problem);
        }
    }
}
