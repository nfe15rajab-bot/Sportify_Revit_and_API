using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SportfyRevit;
using Sportify.Simulation.Wind;
using Sportify.Simulation.Water;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Sun;

// usage: ContractCheck [--verbose]                the whole suite (what CI runs)
//        ContractCheck serve-workspace <folder> [seconds] [roof.json]   (manual: the add-in's real local server on :5679 with the Sportify folder in <folder>; the web app sends its layout and runs the analyses itself)
//        ContractCheck serve-batch <layout.json> [seconds | out.json]      (manual: what the Send All button publishes, served on :5679 for the web app, or written to a file)
//        ContractCheck serve <collision.json> <wind.json> <percolation.json> <layout.json> <structural mp4> <dynamic mp4> [seconds] [sun mp4]     (manual, see below)
//
// The results contract between Sportify.Simulation and the Revit add-in, with neither Unity nor Revit running. For every real Unity result in
// fixtures/golden-unity (recorded by fixtures/regenerate-goldens.js) it checks, through the add-in's own sources:
//   - the file parses into the add-in's DTOs,
//   - the add-in's in-process analysis of the same layout agrees with Unity's numbers (the command's Compare) and writes the same case-study line,
//   - the result publishes through the add-in's builder into the analysis-results document the web app reads, keeping what an earlier command published,
//   - the PDF report's rows can be built from it.
// Plus the roof-height rule (RoofHeightAboveGround). What needs Revit itself (Execute(), the collectors, the WPF windows) is not here.
//
// `serve` is a manual tool, not part of the suite: it publishes every analysis through the add-in's builders and keeps the REAL RoofBoundaryServer (:5679) up so the
// web app can be tried against it. It fights with a running Revit for the port.
var verbose = args.Contains("--verbose");
var fails = 0;
var checks = 0;
void Check(string name, bool ok, string extra = "") { checks++; Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}".TrimEnd()); if (!ok) fails++; }
var opts = new JsonSerializerOptions { IncludeFields = true };
const string FireSafetyBefore = "{\"fire_safety\":{\"max_dist_m\":33.5,\"max_travel_distance_m\":60,\"within_limit\":true,\"unreachable_count\":0}}";

/// <summary>Renders the real Analysis Report PDF from a published result (AnalysisReportPdfBuilder, one card per section): the actual end-to-end path
/// GenerateAnalysisReportCommand runs, not just its data shaping. Returns the file's byte length (0 on failure), deleting the scratch file after.</summary>
long PdfRows(string json)
{
    var payload = JsonSerializer.Deserialize<AnalysisResultPayload>(json);
    var tempPath = Path.Combine(Path.GetTempPath(), "sportify_contractcheck_" + Guid.NewGuid().ToString("N") + ".pdf");
    try
    {
        AnalysisReportPdfBuilder.Generate(tempPath, null, payload, null, null);
        var length = File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        if (verbose) Console.WriteLine($"   PDF report: {length} bytes");
        return length;
    }
    finally
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort scratch cleanup */ }
    }
}

if (args.Length > 0 && args[0] == "-batchmode") return FakeUnity(args);        // this program stands in for Unity.exe in the runner tests below
if (args.Length > 0 && args[0] == "serve") return Serve(args);
if (args.Length > 0 && args[0] == "serve-batch") return ServeBatch(args);
if (args.Length > 0 && args[0] == "serve-workspace") return ServeWorkspace(args);

var fixtures = FindFixtures();
if (fixtures == null) { Console.WriteLine("Tools/fixtures not found above " + AppContext.BaseDirectory); return 2; }

// ---------------------------------------------------------------------------------------------------------------- the roof height rule
{
    Console.WriteLine("\n===== roof height above ground =====");
    RoofHeightAboveGround.LevelInfo L(string n, double z) => new(n, z);
    var r1 = RoofHeightAboveGround.Choose(12.4, 0.4, new[] { L("EG", 0) });
    Check("topography wins over levels", r1 is { HeightM: 12.0 } && r1.Source.StartsWith("topography"), r1?.Source ?? "");
    var r2 = RoofHeightAboveGround.Choose(12.2, null, new[] { L("UG", -3.2), L("EG", 0.0), L("OG1", 3.6), L("Dach", 12.0) });
    Check("ground floor found by name, basement ignored", r2 is { HeightM: 12.2 } && r2.Source.Contains("EG"), r2?.Source ?? "");
    var r3 = RoofHeightAboveGround.Choose(155.4, null, new[] { L("Ebene -1", 140.0), L("Ebene 0", 143.2), L("Ebene 1", 146.7) });
    Check("absolute elevations: named level 0", r3 is { HeightM: 12.2 }, r3?.Source ?? "");
    var r4 = RoofHeightAboveGround.Choose(12.0, null, new[] { L("A", -3.0), L("B", 0.2), L("C", 3.5) });
    Check("no ground-floor name: nearest the project zero", r4 is { HeightM: 11.8 } && r4.Source.Contains("nearest"), r4?.Source ?? "");
    var r5 = RoofHeightAboveGround.Choose(12.0, null, new[] { L("Dach", 12.0), L("Technik", 15.0) });
    Check("levels at or above the roof are not ground", r5 == null);
    var r6 = RoofHeightAboveGround.Choose(0.3, null, new[] { L("EG", 0) });
    Check("a slab at ground level is not a building roof", r6 == null);
    var r7 = RoofHeightAboveGround.Choose(9.0, null, new[] { L("Ground Floor", 0), L("Level 1", 4.5) });
    Check("English ground floor name", r7 is { HeightM: 9.0 }, r7?.Source ?? "");
}

// ---------------------------------------------------------------------------------------------------------------- "Send All to Web App"
{
    Console.WriteLine("\n===== the batch that sends every physical analysis to the web app =====");
    var sample = File.ReadAllText(Path.Combine(fixtures, "layouts", "struct", "v1-sample.json"));
    var other = File.ReadAllText(Path.Combine(fixtures, "layouts", "roof", "r1-notch.json"));
    var expected = new[] { "wind_erosion", "soil_percolation", "structural_loads", "dynamic_analysis", "sun_and_shading" };

    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    var sent = PhysicalAnalysisBatch.Run(sample);
    Check("all five analyses are sent, in order, each with a sentence of what it found",
          sent.Select(s => s.Key).SequenceEqual(expected) && sent.All(s => s.Sent && s.Headline.Length > 10), string.Join(" | ", sent.Select(s => s.Title + ": " + (s.Problem ?? s.Headline))));
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var published);
    var doc = JsonDocument.Parse(published!).RootElement;
    Check("every section is in the published document, beside what an earlier command published", expected.All(k => doc.TryGetProperty(k, out _)) && doc.TryGetProperty("fire_safety", out _));
    Check("the ones resting on inputs nobody confirmed say so", sent.Single(s => s.Key == "structural_loads").Preliminary && sent.Single(s => s.Key == "dynamic_analysis").Preliminary && sent.Single(s => s.Key == "sun_and_shading").Preliminary);
    Check("and the PDF rows can be built from the result", PdfRows(published!) > 0);

    // the numbers are the ones the individual commands compute
    var layout = JsonSerializer.Deserialize<SportifyLayout>(sample)!;
    var sIn = StructureLayoutAdapter.ToInputs(layout);
    var sRep = StructureModel.Analyse(sIn);
    Check("the structural section is what the structural command publishes for the same layout",
          doc.GetProperty("structural_loads").GetProperty("bays_over_capacity").GetInt32() == sRep.summary.baysOver && doc.GetProperty("structural_loads").GetProperty("bays").GetArrayLength() == sRep.bays.Count);

    // a recording stays with numbers it shows, and is taken off numbers it does not
    var video = Path.Combine(Path.GetTempPath(), "sportify-contract-video.mp4");
    File.WriteAllText(video, "not really a video");
    var windIn = WindLayoutAdapter.ToInputs(layout);
    var withVideo = AnalyzeWindErosionRiskCommand.BuildPublishedResult(WindModel.Analyse(windIn), WindModel.CaseStudy(windIn), video);
    RoofBoundaryServer.PublishAnalysisResults("{}");
    AnalysisResultPublisher.PublishWindErosion(withVideo);
    var again = PhysicalAnalysisBatch.Run(sample);
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var afterSame);
    Check("sent again for the same layout, the earlier recording stays with its numbers",
          again.Single(s => s.Key == "wind_erosion").VideoKept && JsonDocument.Parse(afterSame!).RootElement.GetProperty("wind_erosion").GetProperty("video_path").GetString() == video);
    var changed = PhysicalAnalysisBatch.Run(other);
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var afterChange);
    var windAfter = JsonDocument.Parse(afterChange!).RootElement.GetProperty("wind_erosion");
    Check("sent for a different roof, it is taken off (it would show other numbers) and the entry says so",
          changed.Single(s => s.Key == "wind_erosion").VideoDropped && (windAfter.TryGetProperty("video_path", out var vp) ? vp.ValueKind == JsonValueKind.Null : true));
    File.Delete(video);

    // what cannot be analysed says why, and publishes nothing
    var empty = JsonNode.Parse(sample)!.AsObject();
    empty["zones"] = new JsonArray(); empty["placements"] = new JsonArray();
    RoofBoundaryServer.PublishAnalysisResults("{}");
    var none = PhysicalAnalysisBatch.Run(empty.ToJsonString());
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var afterNone);
    Check("a layout with nothing on the roof: none sent, each says why, nothing published",
          none.All(s => !s.Sent && !string.IsNullOrEmpty(s.Problem)) && expected.All(k => !JsonDocument.Parse(afterNone!).RootElement.TryGetProperty(k, out var v) || v.ValueKind == JsonValueKind.Null), string.Join(" | ", none.Select(s => s.Problem)));
    var garbage = PhysicalAnalysisBatch.Run("this is not json");
    Check("a layout that cannot be read: every analysis says so, none throws", garbage.Count == 5 && garbage.All(s => !s.Sent && s.Problem!.Contains("could not be read")));

    // the review window's scope: every input of every analysis
    Check("the 'all' scope asks about every editable input", AnalysisAssumptionsPatcher.Read(sample, AnalysisAssumptionsPatcher.AllAnalyses).Count == AnalysisAssumptions.Editable.Count && AnalysisAssumptions.Editable.Count >= 12);
}

// ---------------------------------------------------------------------------------------------------------------- running Unity without freezing Revit
{
    Console.WriteLine("\n===== the headless Unity run (a stand-in program plays Unity.exe) =====");
    var exe = Path.Combine(AppContext.BaseDirectory, "ContractCheck.exe");
    if (!File.Exists(exe)) exe = Environment.ProcessPath ?? exe;
    var project = Path.Combine(Path.GetTempPath(), "sportify-fake-unity-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    Directory.CreateDirectory(project);
    var install = new UnityHeadlessRunner.UnityInstall(project, exe);
    UnityHeadlessRunner.Request Req(string method, int timeoutMs = 60_000) => new() { ExecuteMethod = method, LayoutJson = "{}", ResultsFileName = "fake_results.json", VideoFileStem = "fake", LogFileName = "fake_unity.log", TimeoutMs = timeoutMs };

    var quick = UnityHeadlessRunner.Run(install, Req("Fake.Quick"));
    Check("a run that finishes gives its results file and the video's path", quick.Ok && quick.ResultsJson!.Contains("\"ok\"") && quick.VideoPath!.StartsWith(Path.Combine(project, "Recordings")), quick.Message);

    var standIns = LiveStandIns();        // this program may itself be one of them, or not (dotnet ContractCheck.dll)
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var cancelled = UnityHeadlessRunner.Run(install, Req("Fake.Slow"), () => clock.ElapsedMilliseconds > 1000);
    var cancelSeconds = clock.Elapsed.TotalSeconds;
    Check("Cancel stops Unity within a couple of seconds and says so", cancelled.Cancelled && !cancelled.Ok && cancelled.Message.Contains("Cancelled") && cancelSeconds < 8, $"({cancelSeconds:0.0} s)");
    Thread.Sleep(600);
    Check("and no Unity is left running", LiveStandIns() == standIns);

    clock.Restart();
    var timedOut = UnityHeadlessRunner.Run(install, Req("Fake.Slow", 1500));
    Check("a run that takes too long is stopped and the message names the time", !timedOut.Ok && !timedOut.Cancelled && timedOut.Message.Contains("didn't finish") && clock.Elapsed.TotalSeconds < 10, timedOut.Message);
    Thread.Sleep(600);

    var lockDir = Path.Combine(project, "Temp");
    Directory.CreateDirectory(lockDir);
    using (var held = new FileStream(Path.Combine(lockDir, "UnityLockfile"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
    {
        var open = UnityHeadlessRunner.Run(install, Req("Fake.Quick"));
        Check("a project the Editor has open is refused before anything starts", !open.Ok && open.Message.Contains("already has") && open.Message.Contains("open"));
        Check("EnsureUnity says the same and hands back nothing (the dialog is offered the PDF)", AnalysisMedia.EnsureUnity("test", install, "{}", "wind_erosion", "") == null);
    }
    Check("EnsureUnity with no install hands back nothing", AnalysisMedia.EnsureUnity("test", null, "{}", "wind_erosion", "") == null);
    Check("EnsureUnity with a free project hands the install back", AnalysisMedia.EnsureUnity("test", install, "{}", "wind_erosion", "") == install);

    // the progress window, end to end: modal on a UI thread, Unity on a worker, the window closes when Unity is done. Needs a desktop session, so not on CI.
    if (Environment.GetEnvironmentVariable("CI") != null) Console.WriteLine("   (the progress window is not run on CI: it needs a desktop session)");
    else
    {
        UnityHeadlessRunner.RunOutcome? shown = null; Exception? failure = null;
        var ui = new Thread(() => { try { shown = AnalysisMedia.RunUnity(install, Req("Fake.Quick"), "Rendering a test video"); } catch (Exception ex) { failure = ex; } });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        var finished = ui.Join(40_000);
        Check("the progress window opens, Unity runs behind it and the window closes with the outcome", finished && failure == null && shown is { Ok: true }, failure?.Message ?? shown?.Message ?? "did not finish");

        var uiPng = Environment.GetEnvironmentVariable("SPORTIFY_PDF_PNG_DIR");
        if (uiPng != null)
        {
            var snap = new Thread(() =>
            {
                var w = new UnityProgressWindow("Rendering the structural loads video", () => { });
                w.Show();
                w.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)w.ActualWidth, (int)w.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(w);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = File.Create(Path.Combine(uiPng, "progress-window.png"))) enc.Save(fs);
                w.Finish();
            });
            snap.SetApartmentState(ApartmentState.STA); snap.Start(); snap.Join(20_000);
        }
    }
    try { Directory.Delete(project, true); } catch (IOException) { /* a temp folder: leave it */ }
}

// ---------------------------------------------------------------------------------------------------------------- the Sportify folder and what the web app asks of the add-in
{
    Console.WriteLine("\n===== the Sportify workspace folder and its endpoints =====");
    var root = Path.Combine(Path.GetTempPath(), "sportify-workspace-" + Guid.NewGuid().ToString("N").Substring(0, 8), "My Sportify");
    var settings = Path.Combine(Path.GetDirectoryName(root)!, "settings.json");
    SportifyWorkspace.UseFolder(root);
    SportifyWorkspace.UseSettingsFile(settings);
    var sample = File.ReadAllText(Path.Combine(fixtures, "layouts", "struct", "v1-sample.json"));
    var none = new System.Collections.Specialized.NameValueCollection();
    System.Collections.Specialized.NameValueCollection Q(params string[] kv) { var c = new System.Collections.Specialized.NameValueCollection(); for (var i = 0; i + 1 < kv.Length; i += 2) c[kv[i]] = kv[i + 1]; return c; }
    EndpointResponse? Call(string method, string path, System.Collections.Specialized.NameValueCollection? query = null, byte[]? body = null) => WorkspaceEndpoints.Handle(method, path, query ?? none, () => body ?? Array.Empty<byte>());
    JsonElement Reply(EndpointResponse? r) => JsonDocument.Parse(r!.Body!).RootElement;

    // the folder
    Check("the default is a \"Sportify Workspace\" folder in Documents (never Documents\\Sportify, where the web app itself lives), and the installer's choice wins", SportifyWorkspace.DefaultFolder.EndsWith("Sportify Workspace") && SportifyWorkspace.Folder == root);
    SportifyWorkspace.EnsureCreated();
    Check("it is made with a subfolder for every kind of deliverable", SportifyWorkspace.Kinds.Length == 10 && SportifyWorkspace.Kinds.All(k => Directory.Exists(Path.Combine(root, k.Folder))), string.Join(", ", SportifyWorkspace.Kinds.Select(k => k.Folder)));
    SportifyWorkspace.SaveSetting(@"D:\Somewhere Else");
    SportifyWorkspace.SaveSetting(@"D:\Another Place");
    Check("the choice is kept in the settings file (the installer writes it, the add-in reads it), other settings untouched",
          JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("workspace_folder").GetString() == @"D:\Another Place");
    SportifyWorkspace.UseFolder(null);
    Check("with no override the add-in reads that file", SportifyWorkspace.Folder == @"D:\Another Place");
    File.WriteAllText(settings, "{ not json");
    Check("an unreadable settings file falls back to the default", SportifyWorkspace.Folder == SportifyWorkspace.DefaultFolder);
    SportifyWorkspace.UseFolder(root);

    // what the web app sees
    var ws = Reply(Call("GET", "/workspace"));
    Check("GET /workspace names the folder and every kind, with a count", ws.GetProperty("folder").GetString() == root && ws.GetProperty("kinds").GetArrayLength() == SportifyWorkspace.Kinds.Length && ws.GetProperty("kinds")[0].TryGetProperty("count", out _));

    // the PROFILE: what the web app keeps with POST /profile and the ribbon reads. Its own folder and settings file, so nothing else here is disturbed.
    {
        var pRoot = Path.Combine(Path.GetDirectoryName(root)!, "profile-test", "My Sportify");
        var pSettings = Path.Combine(Path.GetDirectoryName(pRoot)!, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(pSettings)!);
        SportifyWorkspace.UseFolder(pRoot);
        SportifyWorkspace.UseSettingsFile(pSettings);
        SportifyWorkspace.EnsureCreated();
        File.WriteAllText(pSettings, "{ \"workspace_folder\": \"D:\\\\Elsewhere\", \"version\": \"0.1.0-beta.1\" }");
        var photoBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9 };
        var jpeg = "data:image/jpeg;base64," + Convert.ToBase64String(photoBytes);
        byte[] Json(object o) => JsonSerializer.SerializeToUtf8Bytes(o);
        var profileDir = Path.Combine(pRoot, "Profile");

        var none0 = Reply(Call("GET", "/profile"));
        Check("GET /profile with nothing saved answers a null profile and no file", none0.GetProperty("profile").ValueKind == JsonValueKind.Null && none0.GetProperty("file").ValueKind == JsonValueKind.Null && none0.GetProperty("settings_file").GetString() == pSettings);

        var posted = Call("POST", "/profile", null, Json(new
        {
            schema = 9, name = "PROFILE", updated = "2026-09-25T20:00:00Z", view = "simple", role = "client", theme = "light", evil = "<script>",
            quiz = new { goal = "design", analyses = new[] { "structure", "sun" }, site_data = new object[] { "location", 5, null! }, experience = "beginner", extra = 1 },
            person = new { name = "  Nada\u0007 \n Rajab  ", photo = jpeg, extra = 1 }
        }));
        var kept = Reply(posted).GetProperty("profile");
        Check("POST /profile keeps the profile, rebuilt from the fields it knows (the time in the web app's form, no unknown fields, a clean name)",
              posted!.Status == 200 && kept.GetProperty("view").GetString() == "simple" && kept.GetProperty("role").GetString() == "client" && kept.GetProperty("theme").GetString() == "light" &&
              kept.GetProperty("schema").GetInt32() == 1 && kept.GetProperty("updated").GetString() == "2026-09-25T20:00:00.000Z" && !kept.TryGetProperty("evil", out _) &&
              kept.GetProperty("person").GetProperty("name").GetString() == "Nada Rajab" && kept.GetProperty("person").GetProperty("photo").GetString() == jpeg && !kept.GetProperty("person").TryGetProperty("extra", out _) &&
              kept.GetProperty("quiz").GetProperty("analyses").GetArrayLength() == 2 && kept.GetProperty("quiz").GetProperty("site_data").GetArrayLength() == 1 && !kept.GetProperty("quiz").TryGetProperty("extra", out _));
        var profileFile = Path.Combine(profileDir, "Sportify-PROFILE.json");
        Check("the profile is written into the Profile folder of the Sportify folder as Sportify-PROFILE.json, the file the web app imports", Reply(posted).GetProperty("file").GetString() == profileFile && File.Exists(profileFile) &&
              JsonDocument.Parse(File.ReadAllText(profileFile)).RootElement.GetProperty("kind").GetString() == "sportify-profile" && JsonDocument.Parse(File.ReadAllText(profileFile)).RootElement.GetProperty("profile").GetProperty("person").GetProperty("name").GetString() == "Nada Rajab");
        var photoFile = Path.Combine(profileDir, "Sportify-PROFILE-photo.jpg");
        Check("the photo is written beside it as a picture file, byte for byte", Reply(posted).GetProperty("photo_file").GetString() == photoFile && File.Exists(photoFile) && File.ReadAllBytes(photoFile).SequenceEqual(photoBytes));
        var settingsNow = JsonDocument.Parse(File.ReadAllText(pSettings)).RootElement;
        Check("the settings file holds the profile and every other setting it had", settingsNow.GetProperty("workspace_folder").GetString() == @"D:\Elsewhere" && settingsNow.GetProperty("version").GetString() == "0.1.0-beta.1" && settingsNow.GetProperty("profile").GetProperty("view").GetString() == "simple" && !File.Exists(pSettings + ".tmp"));
        var got0 = Reply(Call("GET", "/profile"));
        Check("GET /profile answers what was kept, and where the file is", got0.GetProperty("profile").GetProperty("person").GetProperty("name").GetString() == "Nada Rajab" && got0.GetProperty("file").GetString() == profileFile && SportifyProfile.Read()!["view"]!.GetValue<string>() == "simple");
        var kinds = Reply(Call("GET", "/deliverables")).GetProperty("files").EnumerateArray().Where(f => f.GetProperty("kind").GetString() == "profile").Select(f => f.GetProperty("name").GetString()).OrderBy(n => n).ToArray();
        Check("the profile files are deliverables of the kind 'profile': listed, and the file can be fetched", string.Join(",", kinds) == "Sportify-PROFILE-photo.jpg,Sportify-PROFILE.json" &&
              Call("GET", "/deliverable", Q("kind", "profile", "name", "Sportify-PROFILE.json"))!.ContentType == "application/json" && Call("GET", "/deliverable", Q("kind", "profile", "name", "Sportify-PROFILE-photo.jpg"))!.ContentType == "image/jpeg");

        // hostile and broken input
        var hostile = Call("POST", "/profile", null, Json(new { view = "expert", role = "boss", theme = "blue", updated = "yesterday", name = new string('x', 300), quiz = "answers", person = new { name = new string('n', 500), photo = "data:image/svg+xml;base64,PHN2Zz48c2NyaXB0PmFsZXJ0KDEpPC9zY3JpcHQ+PC9zdmc+" } }));
        var h = Reply(hostile).GetProperty("profile");
        Check("junk falls back field by field: Advanced, planner, no theme, no time, a short name, no quiz, and a photo that is an SVG is dropped",
              hostile!.Status == 200 && h.GetProperty("view").GetString() == "advanced" && h.GetProperty("role").GetString() == "planner" && h.GetProperty("theme").ValueKind == JsonValueKind.Null && h.GetProperty("updated").ValueKind == JsonValueKind.Null &&
              h.GetProperty("name").GetString()!.Length == 40 && h.GetProperty("quiz").ValueKind == JsonValueKind.Null && h.GetProperty("person").GetProperty("name").GetString()!.Length == 60 && h.GetProperty("person").GetProperty("photo").ValueKind == JsonValueKind.Null);
        Check("a profile without a photo removes the photo file and replaces the profile file (one file, no numbered copies)", !File.Exists(photoFile) && Directory.GetFiles(profileDir).Select(Path.GetFileName).SequenceEqual(new[] { "Sportify-PROFILE.json" }));
        var badPhotos = new[] { "javascript:alert(1)", "https://example.com/x.jpg", "data:text/html;base64,PGgxPmhpPC9oMT4=", "data:image/gif;base64,R0lGODlhAQABAAAAACw=", "data:image/jpeg;base64,not base64!", "data:image/jpeg;base64,", jpeg + "\"onerror=\"x", " " + jpeg,
                                "data:image/jpeg;base64," + new string('A', SportifyProfile.MaxPhotoChars) };
        Check("every photo that is not a small JPEG, PNG or WebP data URL is dropped (SVG, HTML, an address, bad base64, another type, too long)", badPhotos.All(p => SportifyProfile.IsPhoto(p) == false) &&
              badPhotos.All(p => Reply(Call("POST", "/profile", null, Json(new { person = new { name = "x", photo = p } }))).GetProperty("profile").GetProperty("person").GetProperty("photo").ValueKind == JsonValueKind.Null));
        Check("a PNG and a WebP are kept, each under its own file name (and the other picture is removed)", new Func<bool>(() =>
        {
            var png = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
            var r1 = Call("POST", "/profile", null, Json(new { person = new { photo = png } }));
            var ok1 = File.Exists(Path.Combine(profileDir, "Sportify-PROFILE-photo.png")) && !File.Exists(photoFile);
            var webp = "data:image/webp;base64," + Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 });
            Call("POST", "/profile", null, Json(new { person = new { photo = webp } }));
            return r1!.Status == 200 && ok1 && File.Exists(Path.Combine(profileDir, "Sportify-PROFILE-photo.webp")) && !File.Exists(Path.Combine(profileDir, "Sportify-PROFILE-photo.png")) && Call("GET", "/deliverable", Q("kind", "profile", "name", "Sportify-PROFILE-photo.webp"))!.ContentType == "image/webp";
        })());
        var beforeBad = File.ReadAllText(pSettings);
        Check("a body that is empty, not JSON, or not an object is refused with 400 and changes nothing",
              Call("POST", "/profile")!.Status == 400 && Call("POST", "/profile", null, Encoding.UTF8.GetBytes("{ not json"))!.Status == 400 && Call("POST", "/profile", null, Encoding.UTF8.GetBytes("[1,2]"))!.Status == 400 &&
              Call("POST", "/profile", null, Encoding.UTF8.GetBytes("\"a string\""))!.Status == 400 && Call("POST", "/profile", null, Encoding.UTF8.GetBytes("42"))!.Status == 400 && File.ReadAllText(pSettings) == beforeBad);
        Check("only GET and POST are the profile's: another method is left to the server", Call("PUT", "/profile") == null && Call("DELETE", "/profile") == null);
        Check("the person's name goes through as text, markup and all", Reply(Call("POST", "/profile", null, Json(new { person = new { name = "<b>Ali</b> & co" } }))).GetProperty("profile").GetProperty("person").GetProperty("name").GetString() == "<b>Ali</b> & co");

        // a settings file that cannot be read, and a folder that cannot be written
        File.WriteAllText(pSettings, "{ not json");
        var afterCorrupt = Call("POST", "/profile", null, Json(new { view = "simple" }));
        Check("an unreadable settings file is started afresh: the profile is kept and the file is valid again", afterCorrupt!.Status == 200 && JsonDocument.Parse(File.ReadAllText(pSettings)).RootElement.GetProperty("profile").GetProperty("view").GetString() == "simple");
        Directory.Delete(profileDir, true);
        File.WriteAllText(profileDir, "a file where the Profile folder should be");
        var blocked = Call("POST", "/profile", null, Json(new { view = "advanced" }));
        Check("a Profile folder that cannot be made does not fail the save: the settings file has it, and the answer says why the folder has not", blocked!.Status == 200 && Reply(blocked).GetProperty("file").ValueKind == JsonValueKind.Null && Reply(blocked).GetProperty("folder_error").ValueKind == JsonValueKind.String && SportifyProfile.Read()!["view"]!.GetValue<string>() == "advanced");
        File.Delete(profileDir);

        // several saves at once (two browsers, the ribbon): the settings file is never left broken
        Parallel.For(0, 24, i => Call("POST", "/profile", null, Json(new { view = i % 2 == 0 ? "simple" : "advanced", updated = $"2026-09-25T20:00:{i:00}Z", person = new { name = "n" + i } })));
        Check("24 saves at once leave a valid settings file with one of them, and the other settings", JsonDocument.Parse(File.ReadAllText(pSettings)).RootElement.GetProperty("profile").GetProperty("person").GetProperty("name").GetString()!.StartsWith("n") && !File.Exists(pSettings + ".tmp") && Directory.GetFiles(profileDir).Length == 1);

        // what this computer has (GET /capabilities), and the ribbon following the view
        {
            var unityStatus = new ToolStatus(true, @"C:\Unity\Editor\Unity.exe", "Unity was found: the 3D videos can be rendered.");
            SportifyCapabilities.UseProbe(() => new Capabilities(unityStatus, true, new ToolStatus(false, null, "SOLIDWORKS is not installed on this computer."), new ToolStatus(true, @"C:\Chrome\chrome.exe", "Chrome was found.")));
            var refreshes = 0;
            WorkspaceEndpoints.RibbonRefreshRequested = () => refreshes++;
            Call("POST", "/profile", null, Json(new { view = "advanced", updated = "2026-09-26T08:00:00Z" }));
            refreshes = 0;
            var caps = Reply(Call("GET", "/capabilities"));
            Check("GET /capabilities says what this computer has: Unity, SOLIDWORKS and Chrome, each found or not, with where and a sentence", caps.GetProperty("unity").GetProperty("found").GetBoolean() && caps.GetProperty("unity").GetProperty("path").GetString() == @"C:\Unity\Editor\Unity.exe" &&
                  !caps.GetProperty("solidworks").GetProperty("found").GetBoolean() && caps.GetProperty("solidworks").GetProperty("note").GetString() == "SOLIDWORKS is not installed on this computer." && caps.GetProperty("chrome").GetProperty("found").GetBoolean() && caps.GetProperty("unity_project_free").GetBoolean());
            var hiddenNow = caps.GetProperty("ribbon_hidden").EnumerateArray().ToArray();
            Check("...and which buttons of the Revit ribbon are hidden because of it, by name, words and reason (Advanced view: only Simulate (SOLIDWORKS))", caps.GetProperty("view").GetString() == "advanced" && hiddenNow.Length == 1 && hiddenNow[0].GetProperty("name").GetString() == "SimulateKinetics" &&
                  hiddenNow[0].GetProperty("text").GetString() == "Simulate (SOLIDWORKS)" && hiddenNow[0].GetProperty("reason").GetString() == "SOLIDWORKS is not installed on this computer.");
            Check("a plain GET leaves the ribbon alone; ?refresh=1 looks at the computer again and asks the ribbon to follow", refreshes == 0 && Call("GET", "/capabilities", Q("refresh", "1"))!.Status == 200 && refreshes == 1);
            Call("POST", "/profile", null, Json(new { view = "simple", updated = "2026-09-26T08:01:00Z" }));
            Check("saving the profile asks the ribbon to follow the view", refreshes == 2);
            var simpleCaps = Reply(Call("GET", "/capabilities"));
            Check("the Simple view is reported and hides the rest of the ribbon too (the drop-downs, three panels' buttons), each with the reason", simpleCaps.GetProperty("view").GetString() == "simple" && simpleCaps.GetProperty("ribbon_hidden").GetArrayLength() > 15 &&
                  simpleCaps.GetProperty("ribbon_hidden").EnumerateArray().All(h => h.GetProperty("reason").GetString()!.Length > 0) && !simpleCaps.GetProperty("ribbon_hidden").EnumerateArray().Any(h => h.GetProperty("name").GetString() is "OpenSportifyApp" or "OpenSportifyFolder" or "SendPhysicalAnalysisToWeb"));
            WorkspaceEndpoints.RibbonRefreshRequested = () => throw new InvalidOperationException("Revit is busy");
            Check("a ribbon that cannot take the request does not fail the profile save or the answer", Call("POST", "/profile", null, Json(new { view = "advanced" }))!.Status == 200 && Call("GET", "/capabilities", Q("refresh", "1"))!.Status == 200);
            Check("only GET is /capabilities's: a POST is left to the server", Call("POST", "/capabilities") == null);
            WorkspaceEndpoints.RibbonRefreshRequested = null;
            SportifyCapabilities.UseProbe(() => Capabilities.None);
        }

        SportifyWorkspace.UseFolder(root);
        SportifyWorkspace.UseSettingsFile(settings);
        try { Directory.Delete(Path.GetDirectoryName(pRoot)!, true); } catch (IOException) { /* a temp folder: leave it */ }
    }

    // saving from the web app, and refusing what does not belong
    var saved = Call("POST", "/deliverable", Q("kind", "layouts", "name", "sportify_combined_revit.json"), Encoding.UTF8.GetBytes("{\"a\":1}"));
    Check("POST /deliverable puts a file in its kind's folder", saved!.Status == 201 && File.Exists(Path.Combine(root, "Layouts", "sportify_combined_revit.json")));
    var again = Call("POST", "/deliverable", Q("kind", "layouts", "name", "sportify_combined_revit.json"), Encoding.UTF8.GetBytes("{\"a\":2}"));
    Check("the same name again is a new file, nothing is overwritten", Reply(again).GetProperty("name").GetString() == "sportify_combined_revit (2).json" && File.ReadAllText(Path.Combine(root, "Layouts", "sportify_combined_revit.json")) == "{\"a\":1}");
    Check("a name with a folder in it is cut down to the name", Reply(Call("POST", "/deliverable", Q("kind", "sport", "name", "..\\..\\evil.dxf"), new byte[] { 1 })).GetProperty("name").GetString() == "evil.dxf" && !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.dxf")));
    Check("an unknown kind, an empty file and no name are refused", Call("POST", "/deliverable", Q("kind", "system", "name", "a.txt"), new byte[] { 1 })!.Status == 400 && Call("POST", "/deliverable", Q("kind", "layouts", "name", "empty.json"))!.Status == 400 && Call("POST", "/deliverable", Q("kind", "layouts"), new byte[] { 1 })!.Status == 400);

    var list = Reply(Call("GET", "/deliverables"));
    Check("GET /deliverables lists what is there, with a link to each", list.GetProperty("files").GetArrayLength() == 3 && list.GetProperty("files")[0].GetProperty("url").GetString()!.StartsWith("/deliverable?kind="));
    var got = Call("GET", "/deliverable", Q("kind", "layouts", "name", "sportify_combined_revit.json"));
    Check("GET /deliverable streams the file with its type", got!.FilePath != null && got.ContentType == "application/json" && File.ReadAllText(got.FilePath) == "{\"a\":1}");
    Check("only files in the workspace can be fetched: a path out of it, another kind's file, or a missing one is a 404",
          Call("GET", "/deliverable", Q("kind", "layouts", "name", "..\\My Sportify\\Layouts\\sportify_combined_revit.json"))!.Status == 404 && Call("GET", "/deliverable", Q("kind", "sport", "name", "sportify_combined_revit.json"))!.Status == 404 &&
          Call("GET", "/deliverable", Q("kind", "layouts", "name", "nothing.json"))!.Status == 404 && Call("GET", "/deliverable", Q("kind", "layouts", "name", "C:\\Windows\\win.ini"))!.Status == 404);
    Check("a path the endpoints do not own is left to the server", Call("GET", "/roof-boundary") == null && Call("GET", "/deliverable-x") == null);

    // no layout yet: everything that needs one says so, and makes nothing
    RoofBoundaryServer.PublishAnalysisResults("{}");
    var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length;
    Check("with no layout from the app: analysis, charts, PDF and schedule answer 409 and make nothing",
          Call("POST", "/run-analysis")!.Status == 409 && Call("GET", "/charts")!.Status == 409 && Call("POST", "/analysis-pdf")!.Status == 409 && Call("POST", "/schedule")!.Status == 409 && Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length == before);

    // the app sends its layout as it changes: a draft, which does not wake Auto Import
    RoofBoundaryServer.SetCombinedLayoutPayload("{\"first\":true}");
    RoofBoundaryServer.TryGetLatestCombinedLayout(out _, out var versionBefore);
    RoofBoundaryServer.SetDraftLayoutPayload(sample);
    RoofBoundaryServer.TryGetLatestCombinedLayout(out var latest, out var versionAfter);
    Check("a draft layout becomes the newest layout but leaves the export version alone", latest == sample && versionAfter == versionBefore);

    var run = Call("POST", "/run-analysis");
    Check("POST /run-analysis runs the five analyses on it and says what each found", run!.Status == 200 && Reply(run).GetProperty("sent").GetInt32() == 5 && Reply(run).GetProperty("analyses")[2].GetProperty("headline").GetString()!.Length > 10, run.Status.ToString());
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var pub);
    Check("and their results are published for the Analysis tab", pub != null && JsonDocument.Parse(pub).RootElement.TryGetProperty("structural_loads", out _));

    var charts = Reply(Call("GET", "/charts"));
    Check("GET /charts gives every analysis's charts as SVG for the Analysis tab", charts.GetProperty("sections").GetArrayLength() == 5 && charts.GetProperty("sections").EnumerateArray().All(s => s.GetProperty("charts").GetArrayLength() >= 2 && s.GetProperty("charts")[0].GetProperty("svg").GetString()!.StartsWith("<svg")));

    // what was decided in Revit's assumptions dialog comes to the app: only for the project on screen
    AssumptionsSession.Forget();
    Check("GET /analysis-config has nothing until something is decided in Revit", Reply(Call("GET", "/analysis-config")).GetProperty("decisions").GetArrayLength() == 0);
    AssumptionsSession.Remember(sample, new[]
    {
        new AssumptionDecision { Key = AnalysisAssumptions.DeckCapacity, State = AnalysisAssumptions.Entered, Value = "6.5" },
        new AssumptionDecision { Key = AnalysisAssumptions.GardenMinSun, State = AnalysisAssumptions.Accepted },
        new AssumptionDecision { Key = AnalysisAssumptions.ShadeTarget, State = AnalysisAssumptions.Unconfirmed },
    });
    var config = Reply(Call("GET", "/analysis-config")).GetProperty("decisions");
    Check("then GET /analysis-config gives the entered and accepted ones (an unconfirmed one is no decision)", config.GetArrayLength() == 2 && config.EnumerateArray().Any(d => d.GetProperty("key").GetString() == AnalysisAssumptions.DeckCapacity && d.GetProperty("state").GetString() == "entered" && d.GetProperty("value").GetString() == "6.5"));
    Check("and nothing when the layout on screen is another project", AssumptionsSession.DecidedFor(sample).Count == 2 && AssumptionsSession.DecidedFor("{\"roof_context\":{\"length_m\":1,\"width_m\":2}}").Count == 0);
    AssumptionsSession.Forget();

    var pdf = Call("POST", "/analysis-pdf");
    var pdfName = Reply(pdf).GetProperty("name").GetString()!;
    Check("POST /analysis-pdf writes the charts PDF to the Physical analysis folder", pdf!.Status == 201 && File.Exists(Path.Combine(root, "Physical analysis", pdfName)) && File.ReadAllBytes(Path.Combine(root, "Physical analysis", pdfName)).Take(5).SequenceEqual("%PDF-"u8.ToArray()));
    Check("and one analysis alone when asked", Reply(Call("POST", "/analysis-pdf", Q("keys", "sun_and_shading"))).GetProperty("drawn").GetArrayLength() == 1);

    var csv = Call("POST", "/schedule");
    var csvPath = Reply(csv).GetProperty("path").GetString()!;
    var csvText = File.ReadAllText(csvPath);
    Check("POST /schedule writes the component schedule (with a BOM for Excel, a header and a row per piece)", csv!.Status == 201 && File.ReadAllBytes(csvPath).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && csvText.Contains("Category,Label") && csvText.Split('\n').Count(l => l.Trim().Length > 0) == 1 + Reply(csv).GetProperty("rows").GetInt32());

    var report = Call("POST", "/analysis-report");
    Check("POST /analysis-report writes the analysis report PDF to the Reports folder, with what has been run", report!.Status == 201 && Reply(report).GetProperty("has_results").GetBoolean() && File.Exists(Reply(report).GetProperty("path").GetString()!));

    // a Unity video is adopted, and the folder is opened on request
    var video = Path.Combine(Path.GetTempPath(), "sportify-adopt-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".mp4");
    File.WriteAllText(video, "not really a video");
    var adopted = SportifyWorkspace.Adopt("videos", video);
    Check("a video made elsewhere is copied into the Videos folder and the copy is what the result names", adopted != video && Path.GetDirectoryName(adopted) == Path.Combine(root, "Videos") && File.Exists(adopted) && File.Exists(video));
    Check("adopting what is already there changes nothing", SportifyWorkspace.Adopt("videos", adopted) == adopted);
    File.Delete(video);
    string? opened = null;
    WorkspaceEndpoints.OpenFolder = f => opened = f;
    Check("POST /open-folder opens the workspace or one subfolder in Explorer", Call("POST", "/open-folder")!.Status == 200 && opened == root && Call("POST", "/open-folder", Q("kind", "reports"))!.Status == 200 && opened == Path.Combine(root, "Analysis reports"), opened ?? "");

    // the one thing that needs Revit's own views
    Check("the diagrams need Revit: 501 outside it, 400 for another name, 202 once Revit has taken the request",
          Call("POST", "/revit-command", Q("name", "diagrams"))!.Status == 501 && Call("POST", "/revit-command", Q("name", "format-c"))!.Status == 400);
    var asked = "";
    WorkspaceEndpoints.RevitCommandRequested = n => { asked = n; return null; };
    Check("...and it is asked for by name", Call("POST", "/revit-command", Q("name", "diagrams"))!.Status == 202 && asked == "diagrams");
    WorkspaceEndpoints.RevitCommandRequested = null;

    SportifyWorkspace.UseFolder(null);
    SportifyWorkspace.UseSettingsFile(null);
    try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch (IOException) { /* a temp folder: leave it */ }
}

// ---------------------------------------------------------------------------------------------------------------- every result says which layout it is about
{
    Console.WriteLine("\n===== results are tied to a layout and a time =====");
    Check("a layout's identity is the first 16 hex characters of the SHA-256 of what was posted (the web app can compute the same)", LayoutIdentity.Of("abc") == "ba7816bf8f01cfea" && LayoutIdentity.Of("abc") != LayoutIdentity.Of("abd"));

    var layoutA = File.ReadAllText(Path.Combine(fixtures, "layouts", "struct", "v1-sample.json"));
    var noGarden = JsonNode.Parse(layoutA)!.AsObject();
    noGarden["zones"] = new JsonArray();
    noGarden["assemblies"] = new JsonArray();
    noGarden["placements"] = new JsonArray(noGarden["placements"]!.AsArray().Where(x => x!["category"]?.GetValue<string>() is not ("vegetation" or "garden")).Select(x => x!.DeepClone()).ToArray());
    var layoutB = noGarden.ToJsonString();

    RoofBoundaryServer.PublishAnalysisResults("{}");
    RoofBoundaryServer.SetDraftLayoutPayload(layoutA);
    var idA = RoofBoundaryServer.CurrentLayoutId;
    RoofBoundaryServer.TryGetLatestCombinedLayout(out _, out _);                    // what every analysis does first
    var sentA = PhysicalAnalysisBatch.Run(layoutA);
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var docA);
    var a = JsonDocument.Parse(docA!).RootElement;
    var infoA = a.GetProperty("sections");
    bool timesOk = true;
    foreach (var k in new[] { "wind_erosion", "soil_percolation", "structural_loads", "dynamic_analysis", "sun_and_shading" })
        timesOk &= infoA.TryGetProperty(k, out var i) && i.GetProperty("layout_id").GetString() == idA && DateTime.TryParse(i.GetProperty("computed_at").GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var when) && when.Kind == DateTimeKind.Utc;
    Check("every published section says which layout it was computed for and when (UTC)", timesOk && a.GetProperty("layout_id").GetString() == idA && !string.IsNullOrEmpty(idA));

    // the designer removes the garden: the layout has another identity, and what cannot be analysed for it is not left over from the one before
    RoofBoundaryServer.SetDraftLayoutPayload(layoutB);
    var idB = RoofBoundaryServer.CurrentLayoutId;
    RoofBoundaryServer.TryGetLatestCombinedLayout(out _, out _);
    var sentB = PhysicalAnalysisBatch.Run(layoutB);
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var docB);
    var b = JsonDocument.Parse(docB!).RootElement;
    bool Has(JsonElement doc, string k) => doc.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null;
    Check("a different layout has a different identity", idB != idA && !string.IsNullOrEmpty(idB));
    Check("no garden: the wind and rain analyses send nothing, and the wind and rain results of the layout WITH a garden are gone, not left beside the new ones",
          !sentB.Single(s => s.Key == "wind_erosion").Sent && !sentB.Single(s => s.Key == "soil_percolation").Sent && !Has(b, "wind_erosion") && !Has(b, "soil_percolation") && !b.GetProperty("sections").TryGetProperty("wind_erosion", out _));
    Check("what was computed for the new layout carries its identity, and nothing in the document is about the old one",
          Has(b, "structural_loads") && b.GetProperty("sections").EnumerateObject().All(o => o.Value.GetProperty("layout_id").GetString() == idB));

    // the same layout again is the same identity, and the same numbers
    RoofBoundaryServer.SetDraftLayoutPayload(layoutA);
    Check("the layout that comes back has the identity it had before", RoofBoundaryServer.CurrentLayoutId == idA);
    RoofBoundaryServer.TryGetLatestCombinedLayout(out _, out _);
    PhysicalAnalysisBatch.Run(layoutA);
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var docA2);
    string Numbers(string json) { var o = JsonNode.Parse(json)!.AsObject(); o.Remove("updated_at"); o.Remove("sections"); return o.ToJsonString(); }
    Check("and the analyses give the same numbers as the first time (deterministic)", Numbers(docA!) == Numbers(docA2!));

    // a layout that nothing can be analysed for still retires the old results
    var empty = JsonNode.Parse(layoutA)!.AsObject();
    empty["zones"] = new JsonArray(); empty["placements"] = new JsonArray(); empty["assemblies"] = new JsonArray();
    RoofBoundaryServer.SetDraftLayoutPayload(empty.ToJsonString());
    RoofBoundaryServer.TryGetLatestCombinedLayout(out _, out _);
    PhysicalAnalysisBatch.Run(empty.ToJsonString());
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var docE);
    Check("an empty roof: nothing is sent and nothing of the earlier layouts remains", new[] { "wind_erosion", "soil_percolation", "structural_loads", "dynamic_analysis", "sun_and_shading" }.All(k => !Has(JsonDocument.Parse(docE!).RootElement, k)));
    RoofBoundaryServer.PublishAnalysisResults("{}");
}

// ---------------------------------------------------------------------------------------------------------------- the charts as a PDF (no Unity)
{
    Console.WriteLine("\n===== the physical analyses as a PDF of charts =====");
    var sample = File.ReadAllText(Path.Combine(fixtures, "layouts", "struct", "v1-sample.json"));
    var built = PhysicalAnalysisPdf.Build(sample);
    Check("all five analyses give a section, in order", built.Sections.Select(s => s.Key).SequenceEqual(PhysicalAnalysisPdf.AllKeys), string.Join(",", built.Sections.Select(s => s.Key)) + " skipped: " + string.Join("; ", built.Skipped.Select(k => k.Title + " " + k.Reason)));
    Check("each has a headline, numbers and at least one chart", built.Sections.All(s => s.Headline.Length > 10 && s.Facts.Count >= 4 && s.Charts.Count >= 2));
    var charts = built.Sections.SelectMany(s => s.Charts.Select(c => (s.Key, c))).ToList();
    var badXml = new List<string>();
    foreach (var (key, chart) in charts)
    {
        try { System.Xml.Linq.XDocument.Parse(chart.Svg); } catch (Exception ex) { badXml.Add(key + ": " + ex.Message); }
    }
    Check("every chart is well-formed SVG", badXml.Count == 0, $"({charts.Count} charts) " + string.Join("; ", badXml));
    Check("no chart names a font (the renderer draws no text with one)", charts.All(c => !c.c.Svg.Contains("font-family")));
    Check("no chart has NaN or infinity in it", charts.All(c => !c.c.Svg.Contains("NaN") && !c.c.Svg.Contains("Infinity") && !c.c.Svg.Contains("∞")));
    Check("the structure plan draws every bay", built.Sections[0].Charts[0].Svg.Split("<rect ").Length - 1 + built.Sections[0].Charts[0].Svg.Split("<polygon ").Length - 1 >= 16);

    var folder = Path.Combine(Path.GetTempPath(), "sportify-pdf-test-" + Guid.NewGuid().ToString("N").Substring(0, 8), "Physical Analysis");
    var pdf = PhysicalAnalysisPdf.Export(sample, folder, "Contract test");
    Check("the PDF is written to the folder it was given", pdf.Ok && File.Exists(pdf.Path!) && Path.GetDirectoryName(pdf.Path!) == folder && pdf.Path!.EndsWith(".pdf"), pdf.Path ?? pdf.Error ?? "");
    var bytes = pdf.Ok ? File.ReadAllBytes(pdf.Path!) : Array.Empty<byte>();
    Check("it is a real PDF of some size", bytes.Length > 30_000 && System.Text.Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-", bytes.Length + " bytes");
    var pngDir = Environment.GetEnvironmentVariable("SPORTIFY_PDF_PNG_DIR");
    if (pngDir != null)
    {
        Directory.CreateDirectory(pngDir);
        var k = 0;
        foreach (var png in PhysicalAnalysisPdf.PagesAsImages(built, "Contract test")) File.WriteAllBytes(Path.Combine(pngDir, $"page{k++:00}.png"), png);
        Console.WriteLine("   wrote " + k + " page image(s) to " + pngDir);
    }
    Check("the report has a cover and a page or more for each analysis", PhysicalAnalysisPdf.PagesAsImages(built, "Contract test").Count() >= 1 + built.Sections.Count);

    var single = PhysicalAnalysisPdf.Export(sample, folder, "", new[] { "sun_and_shading" });
    Check("one analysis alone: one section, named after it", single.Ok && single.Sections.Count == 1 && Path.GetFileName(single.Path!).Contains("sun_and_shading"));
    var nothing = JsonNode.Parse(sample)!.AsObject();
    nothing["zones"] = new JsonArray(); nothing["placements"] = new JsonArray();
    var none = PhysicalAnalysisPdf.Export(nothing.ToJsonString(), folder, "");
    Check("a layout with nothing on the roof gives no file and says why", !none.Ok && none.Path == null && none.Skipped.Count == 5 && none.Error!.Contains("nothing to draw"));
    Check("a layout that cannot be read gives no file and says why", !PhysicalAnalysisPdf.Export("not json", folder, "").Ok);
    SportifyWorkspace.UseFolder(Path.Combine(Path.GetDirectoryName(folder)!, "workspace"));
    Check("the default folder is the workspace's Physical analysis folder", PhysicalAnalysisPdf.DefaultFolder() == Path.Combine(Path.GetDirectoryName(folder)!, "workspace", "Physical analysis"));
    SportifyWorkspace.UseFolder(null);
    try { Directory.Delete(Path.GetDirectoryName(folder)!, true); } catch (IOException) { /* a temp folder: leave it */ }

    // the roofs with a shape of their own: the plan follows the outline, nothing breaks
    foreach (var name in new[] { "r3-notch-skew", "r5-stepped-skew" })
    {
        var shaped = PhysicalAnalysisPdf.Build(File.ReadAllText(Path.Combine(fixtures, "layouts", "roof", name + ".json")));
        var xml = shaped.Sections.SelectMany(s => s.Charts).All(c => { try { System.Xml.Linq.XDocument.Parse(c.Svg); return true; } catch { return false; } });
        Check($"a notched / skewed roof ({name}): five sections, every chart well-formed", shaped.Sections.Count == 5 && xml);
    }
}

// ---------------------------------------------------------------------------------------------------------------- every golden
var goldenDir = Path.Combine(fixtures, "golden-unity");
var goldens = Directory.GetFiles(goldenDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
var covered = new HashSet<string>();
foreach (var golden in goldens)
{
    var name = Path.GetFileNameWithoutExtension(golden);              // <analysis>__<group>-<layout>
    var parts = name.Split("__");
    var rest = parts[1];
    var layoutPath = Path.Combine(fixtures, "layouts", rest[..rest.IndexOf('-')], rest[(rest.IndexOf('-') + 1)..] + ".json");
    Console.WriteLine($"\n===== {parts[0]}: {rest} =====");
    if (!File.Exists(layoutPath)) { Check("the layout this golden was run on exists", false, layoutPath); continue; }
    var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(layoutPath))!;
    try
    {
        switch (parts[0])
        {
            case "wind": Wind(golden, layout); break;
            case "percolation": Percolation(golden, layout); break;
            case "structure": Structure(golden, layout); break;
            case "dynamic": Dynamic(golden, layout); break;
            case "sun": Sun(golden, layout); break;
            case "collision": Ball(golden); break;
            default: Check("a known analysis name in the golden's file name", false, parts[0]); continue;
        }
        covered.Add(parts[0]);
    }
    catch (Exception ex) { Check("no exception", false, ex.GetType().Name + ": " + ex.Message); }
}
Console.WriteLine("\n===== the suite covers every analysis =====");
foreach (var analysis in new[] { "wind", "percolation", "structure", "dynamic", "sun", "collision" })
    Check($"at least one real Unity result for {analysis}", covered.Contains(analysis), "(fixtures/golden-unity, recorded by fixtures/regenerate-goldens.js)");

Console.WriteLine(fails == 0 ? $"\nALL CONTRACT CHECKS PASSED ({checks} checks)" : $"\n{fails} OF {checks} CHECK(S) FAILED");
return fails == 0 ? 0 : 1;

// ---------------------------------------------------------------------------------------------------------------- one block per analysis

void Wind(string path, SportifyLayout layout)
{
    var results = JsonSerializer.Deserialize<AnalyzeWindErosionRiskCommand.WindResultsFile>(File.ReadAllText(path), opts)!;
    Check("real Unity wind_results.json parses into the add-in's DTOs", results.Analysis != null && results.Video != null && results.Analysis.zones.Count > 0,
          $"(zones {results.Analysis?.zones.Count}, plants {results.Analysis?.plants.Count}, video {(results.Video?.FilePath is { Length: > 0 } ? "yes" : "no")})");
    var inputs = WindLayoutAdapter.ToInputs(layout);
    var report = WindModel.Analyse(inputs);
    var caseStudy = WindModel.CaseStudy(inputs);
    var disagreement = AnalyzeWindErosionRiskCommand.Compare(report, results.Analysis!);
    Check("add-in's in-process numbers agree with Unity's (Compare)", disagreement == null, disagreement ?? "");
    Check("case study line is the same text Unity wrote", results.CaseStudy!.EndsWith(caseStudy), $"(\"{caseStudy}\")");

    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    AnalysisResultPublisher.PublishWindErosion(AnalyzeWindErosionRiskCommand.BuildPublishedResult(report, caseStudy, results.Video?.FilePath));
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var json);
    var merged = JsonDocument.Parse(json!).RootElement;
    Check("merge keeps the earlier Fire Safety result", merged.TryGetProperty("fire_safety", out _));
    Check("merge adds the wind_erosion section", merged.TryGetProperty("wind_erosion", out var we));
    Check("published counts match the report", we.GetProperty("trees_failing").GetInt32() == report.summary.plantsFailing && we.GetProperty("zones_uplift_flagged").GetInt32() == report.summary.zonesUpliftFlagged,
          $"(trees failing {report.summary.plantsFailing}, zones lifting {report.summary.zonesUpliftFlagged}, findings {we.GetProperty("findings").GetArrayLength()}, assumptions {we.GetProperty("assumptions").GetArrayLength()})");
    Check("the PDF report has rows for it", PdfRows(json!) > 0);
}

void Percolation(string path, SportifyLayout layout)
{
    var results = JsonSerializer.Deserialize<SimulateSoilPercolationCommand.PercolationResultsFile>(File.ReadAllText(path), opts)!;
    Check("real Unity percolation_results.json parses into the add-in's DTOs", results.Analysis != null && results.Video != null && results.Analysis.zones.Count > 0 && results.Analysis.roof.Count == 3,
          $"(zones {results.Analysis?.zones.Count}, roof events {results.Analysis?.roof.Count}, hydrograph points {results.Analysis?.roof[0].flowLps.Length})");
    var wi = WindLayoutAdapter.ToInputs(layout);
    var inputs = new WaterInputs { RoofLength = wi.RoofLength, RoofWidth = wi.RoofWidth, RoofAreaM2 = wi.Shape.Area, Zones = wi.Zones };
    var report = PercolationModel.Analyse(inputs);
    var disagreement = SimulateSoilPercolationCommand.Compare(report, results.Analysis!);
    Check("add-in's in-process numbers agree with Unity's (Compare)", disagreement == null, disagreement ?? "");
    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    AnalysisResultPublisher.PublishSoilPercolation(SimulateSoilPercolationCommand.BuildPublishedResult(report, SimulateSoilPercolationCommand.CaseStudy(inputs), results.Video?.FilePath));
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var json);
    var merged = JsonDocument.Parse(json!).RootElement;
    Check("merge keeps the earlier Fire Safety result", merged.TryGetProperty("fire_safety", out _));
    Check("merge adds the soil_percolation section", merged.TryGetProperty("soil_percolation", out var sp));
    Check("published figures match the report", Math.Abs(sp.GetProperty("heavy_shower_retained_percent").GetDouble() - Math.Round(report.roof[1].retainedPercent, 1)) < 1e-9 && sp.GetProperty("zones").GetArrayLength() == report.zones.Count,
          $"(shower {report.roof[1].retainedPercent:0.#}%, findings {sp.GetProperty("findings").GetArrayLength()}, assumptions {sp.GetProperty("assumptions").GetArrayLength()})");
    Check("the PDF report has rows for it", PdfRows(json!) > 0);
}

void Structure(string path, SportifyLayout layout)
{
    var results = JsonSerializer.Deserialize<AnalyzeStructuralLoadsCommand.StructureResultsFile>(File.ReadAllText(path), opts)!;
    Check("real Unity structure_results.json parses into the add-in's DTOs", results.Analysis != null && results.Video != null && results.Analysis.bays.Count > 0,
          $"(bays {results.Analysis?.bays.Count}, columns {results.Analysis?.columns.Count}, recommendations {results.Analysis?.recommendations.Count})");
    var inputs = StructureLayoutAdapter.ToInputs(layout);
    var report = StructureModel.Analyse(inputs);
    var disagreement = AnalyzeStructuralLoadsCommand.Compare(report, results.Analysis!);
    Check("add-in's in-process numbers agree with Unity's (Compare)", disagreement == null, disagreement ?? "");
    var caseStudy = AnalyzeStructuralLoadsCommand.CaseStudy(inputs, report);
    Check("case study line is the same text Unity wrote", results.CaseStudy!.EndsWith(caseStudy), $"(\"{caseStudy}\")");
    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    AnalysisResultPublisher.PublishStructuralLoads(AnalyzeStructuralLoadsCommand.BuildPublishedResult(report, caseStudy, results.Video?.FilePath));
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var json);
    var merged = JsonDocument.Parse(json!).RootElement;
    Check("merge keeps the earlier Fire Safety result", merged.TryGetProperty("fire_safety", out _));
    Check("merge adds the structural_loads section", merged.TryGetProperty("structural_loads", out var sl));
    Check("published figures match the report", sl.GetProperty("bays_over_capacity").GetInt32() == report.summary.baysOver && sl.GetProperty("bays").GetArrayLength() == report.bays.Count,
          $"(over {report.summary.baysOver}, findings {sl.GetProperty("findings").GetArrayLength()}, assumptions {sl.GetProperty("assumptions").GetArrayLength()})");
    Check("the PDF report has rows for it", PdfRows(json!) > 0);
}

void Dynamic(string path, SportifyLayout layout)
{
    var results = JsonSerializer.Deserialize<AnalyzeDynamicLoadsCommand.DynamicResultsFile>(File.ReadAllText(path), opts)!;
    Check("real Unity dynamic_results.json parses into the add-in's DTOs", results.Analysis != null && results.Video != null && results.Analysis.weather.cases.Count == 5,
          $"(samples {results.Analysis?.crowd.samples.Count}, cases {results.Analysis?.weather.cases.Count}, bays {results.Analysis?.resonance.bays.Count})");
    var inputs = DynamicLayoutAdapter.ToInputs(layout);
    var report = DynamicModel.Analyse(inputs);
    var disagreement = AnalyzeDynamicLoadsCommand.Compare(report, results.Analysis!);
    Check("add-in's in-process numbers agree with Unity's (Compare)", disagreement == null, disagreement ?? "");
    var caseStudy = DynamicModel.CaseStudy(inputs, report);
    Check("case study line is the same text Unity wrote", results.CaseStudy!.EndsWith(caseStudy), "(" + caseStudy + ")");
    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    AnalysisResultPublisher.PublishDynamicAnalysis(AnalyzeDynamicLoadsCommand.BuildPublishedResult(report, caseStudy, results.Video?.FilePath));
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var json);
    var merged = JsonDocument.Parse(json!).RootElement;
    Check("merge keeps the earlier Fire Safety result", merged.TryGetProperty("fire_safety", out _));
    Check("merge adds the dynamic_analysis section", merged.TryGetProperty("dynamic_analysis", out var da));
    Check("published figures match the report", da.GetProperty("cases").GetArrayLength() == report.weather.cases.Count && Math.Abs(da.GetProperty("peak_persons").GetDouble() - report.crowd.peakPersons) < 1e-9,
          $"(findings {da.GetProperty("findings").GetArrayLength()}, assumptions {da.GetProperty("assumptions").GetArrayLength()})");
    Check("the published result carries the inputs and the preliminary flag", da.TryGetProperty("preliminary", out _) && da.GetProperty("inputs").GetArrayLength() == report.assumptionUses.Count && report.assumptionUses.Count > 0,
          $"(inputs {da.GetProperty("inputs").GetArrayLength()})");
    Check("the PDF report has rows for it", PdfRows(json!) > 0);
}

void Sun(string path, SportifyLayout layout)
{
    var results = JsonSerializer.Deserialize<AnalyzeSunShadeCommand.SunResultsFile>(File.ReadAllText(path), opts)!;
    Check("real Unity sun_results.json parses into the add-in's DTOs", results.Analysis != null && results.Video != null && results.Analysis.zones.Count > 0 && results.Analysis.days.Count == 3,
          $"(zones {results.Analysis?.zones.Count}, days {results.Analysis?.days.Count}, pieces {results.Analysis?.equipment.Count})");
    var inputs = SunLayoutAdapter.ToInputs(layout);
    var report = SunModel.Analyse(inputs);
    var disagreement = AnalyzeSunShadeCommand.Compare(report, results.Analysis!);
    Check("add-in's in-process numbers agree with Unity's (Compare)", disagreement == null, disagreement ?? "");
    var caseStudy = SunModel.CaseStudy(inputs, report);
    Check("case study line is the same text Unity wrote", results.CaseStudy!.EndsWith(caseStudy), "(" + caseStudy + ")");
    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    AnalysisResultPublisher.PublishSunAndShading(AnalyzeSunShadeCommand.BuildPublishedResult(report, caseStudy, results.Video?.FilePath));
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var json);
    var merged = JsonDocument.Parse(json!).RootElement;
    Check("merge keeps the earlier Fire Safety result", merged.TryGetProperty("fire_safety", out _));
    Check("merge adds the sun_and_shading section", merged.TryGetProperty("sun_and_shading", out var ss));
    Check("published figures match the report", ss.GetProperty("zones").GetArrayLength() == report.zones.Count,
          $"(zones {report.zones.Count}, pieces {report.equipment.Count})");
    Check("the PDF report has rows for it", PdfRows(json!) > 0);
}

void Ball(string path)
{
    var build = typeof(SimulateBallTrajectoriesCommand).GetMethod("BuildPublishedResult", BindingFlags.NonPublic | BindingFlags.Static)!;
    var results = JsonSerializer.Deserialize<SimulateBallTrajectoriesCommand.CollisionResults>(File.ReadAllText(path))!;
    var crossings = results.Violations?.Count ?? 0;
    Check("real Unity results JSON parses into the add-in's DTOs", results.ShotsSimulated > 0 && results.RoofExit != null && results.Video != null);
    RoofBoundaryServer.PublishAnalysisResults(FireSafetyBefore);
    var dto = (BallTrajectoryResultDto)build.Invoke(null, new object?[] { results, crossings, results.Video?.FilePath })!;
    AnalysisResultPublisher.PublishBallTrajectory(dto);
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var json);
    var merged = JsonDocument.Parse(json!).RootElement;
    Check("merge keeps the earlier Fire Safety result", merged.TryGetProperty("fire_safety", out _));
    Check("merge adds the ball_trajectory section", merged.TryGetProperty("ball_trajectory", out _));
    Check("the PDF report has rows for it", PdfRows(json!) > 0);
}

// ---------------------------------------------------------------------------------------------------------------- helpers

static string? FindFixtures()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        var f = Path.Combine(dir.FullName, "Tools", "fixtures");
        if (Directory.Exists(Path.Combine(f, "golden-unity"))) return f;
    }
    return null;
}

// processes named like this program that are still running (a killed one can linger in the list while a handle to it is open)
int LiveStandIns() => System.Diagnostics.Process.GetProcessesByName("ContractCheck").Count(p => { try { return !p.HasExited; } catch (Exception) { return false; } });

int FakeUnity(string[] a)
{
    // -batchmode -projectPath <dir> -executeMethod <Fake.Quick | Fake.Slow> -layoutFile=... -videoFile=... -logFile <log>
    string Arg(string name) { var i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : ""; }
    var method = Arg("-executeMethod");
    var logFile = Arg("-logFile");
    if (logFile.Length > 0) File.WriteAllText(logFile, "Initialize engine version: fake\n");
    if (method == "Fake.Slow") { Thread.Sleep(60_000); return 0; }
    var recordings = Path.Combine(Arg("-projectPath"), "Recordings");
    Directory.CreateDirectory(recordings);
    File.WriteAllText(Path.Combine(recordings, "fake_results.json"), "{\"ok\":true}");
    return 0;
}

int ServeBatch(string[] a)
{
    // The five analyses the ribbon's Send All button sends, on a layout, with the real RoofBoundaryServer up so the web app's Analysis tab can be tried against it.
    RoofBoundaryServer.PublishAnalysisResults("{}");
    foreach (var s in PhysicalAnalysisBatch.Run(File.ReadAllText(a[1]))) Console.WriteLine((s.Sent ? "sent     " : "not sent ") + s.Title + ": " + (s.Problem ?? s.Headline));
    if (a.Length > 2 && a[2].EndsWith(".json"))     // a file instead of a server: for trying the web app while a real Revit holds :5679
    {
        RoofBoundaryServer.TryGetLatestAnalysisResults(out var doc);
        File.WriteAllText(a[2], doc!);
        Console.WriteLine("wrote " + a[2]);
        return 0;
    }
    RoofBoundaryServer.Start();
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var served);
    Console.WriteLine("serving " + served!.Length + " bytes of results on http://localhost:5679/analysis-results");
    Thread.Sleep((a.Length > 2 ? int.Parse(a[2]) : 1800) * 1000);
    return 0;
}

int ServeWorkspace(string[] a)
{
    SportifyWorkspace.UseFolder(Path.GetFullPath(a[1]));
    SportifyWorkspace.EnsureCreated();
    if (a.Length > 3) RoofBoundaryServer.SetPayload(File.ReadAllText(a[3]));         // a roof as the push commands would have sent it
    RoofBoundaryServer.Start();
    Console.WriteLine("serving the workspace " + SportifyWorkspace.Folder + " on http://localhost:5679");
    Thread.Sleep((a.Length > 2 ? int.Parse(a[2]) : 1800) * 1000);
    return 0;
}

int Serve(string[] a)
{
    // serve <collision_results.json> <wind_results.json> <percolation_results.json> <layout.json> <structural mp4> <dynamic mp4> [seconds] [sun mp4]
    var layout = JsonSerializer.Deserialize<SportifyLayout>(File.ReadAllText(a[4]))!;
    RoofBoundaryServer.PublishAnalysisResults("{}");

    var cr = JsonSerializer.Deserialize<SimulateBallTrajectoriesCommand.CollisionResults>(File.ReadAllText(a[1]))!;
    var ballBuild = typeof(SimulateBallTrajectoriesCommand).GetMethod("BuildPublishedResult", BindingFlags.NonPublic | BindingFlags.Static)!;
    AnalysisResultPublisher.PublishBallTrajectory((BallTrajectoryResultDto)ballBuild.Invoke(null, new object?[] { cr, cr.Violations?.Count ?? 0, cr.Video?.FilePath })!);

    var wr = JsonSerializer.Deserialize<AnalyzeWindErosionRiskCommand.WindResultsFile>(File.ReadAllText(a[2]), opts)!;
    var wi = WindLayoutAdapter.ToInputs(layout);
    AnalysisResultPublisher.PublishWindErosion(AnalyzeWindErosionRiskCommand.BuildPublishedResult(WindModel.Analyse(wi), WindModel.CaseStudy(wi), wr.Video?.FilePath));

    var pr = JsonSerializer.Deserialize<SimulateSoilPercolationCommand.PercolationResultsFile>(File.ReadAllText(a[3]), opts)!;
    var pin = new WaterInputs { RoofLength = wi.RoofLength, RoofWidth = wi.RoofWidth, RoofAreaM2 = wi.Shape.Area, Zones = wi.Zones };
    AnalysisResultPublisher.PublishSoilPercolation(SimulateSoilPercolationCommand.BuildPublishedResult(PercolationModel.Analyse(pin), SimulateSoilPercolationCommand.CaseStudy(pin), pr.Video?.FilePath));

    var sIn = StructureLayoutAdapter.ToInputs(layout);
    var sRep = StructureModel.Analyse(sIn);
    AnalysisResultPublisher.PublishStructuralLoads(AnalyzeStructuralLoadsCommand.BuildPublishedResult(sRep, AnalyzeStructuralLoadsCommand.CaseStudy(sIn, sRep), a[5]));
    var dIn = DynamicLayoutAdapter.ToInputs(layout);
    var dRep = DynamicModel.Analyse(dIn);
    AnalysisResultPublisher.PublishDynamicAnalysis(AnalyzeDynamicLoadsCommand.BuildPublishedResult(dRep, DynamicModel.CaseStudy(dIn, dRep), a[6]));

    AnalysisResultPublisher.PublishFireSafety(new FireSafetyResultDto { MaxDistM = 41.3, MaxTravelDistanceM = 35, WithinLimit = false, UnreachableCount = 0 });
    AnalysisResultPublisher.PublishAccessibility(new AccessibilityResultDto { WidthOk = true, ReachOk = true, CurrentWidthM = 1.2, MinWidthM = 1.5 });
    AnalysisResultPublisher.PublishLca(new LcaResultDto { TotalKg = 18400, CoveredCount = 7, MissingCount = 4, TotalCount = 11 });
    AnalysisResultPublisher.PublishCarbonImpact(new CarbonImpactResultDto { ActiveSurfaceAreaM2 = 396, EstimatedDailyWh = 1290 });
    {
        var sunIn = SunLayoutAdapter.ToInputs(layout);
        var sunRep = SunModel.Analyse(sunIn);
        AnalysisResultPublisher.PublishSunAndShading(AnalyzeSunShadeCommand.BuildPublishedResult(sunRep, SunModel.CaseStudy(sunIn, sunRep), a.Length > 8 ? a[8] : null));
    }

    RoofBoundaryServer.Start();
    RoofBoundaryServer.TryGetLatestAnalysisResults(out var served);
    Console.WriteLine("serving " + served!.Length + " bytes of results on http://localhost:5679/analysis-results");
    Thread.Sleep((a.Length > 7 ? int.Parse(a[7]) : 1800) * 1000);
    return 0;
}
