using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Structure;

// usage: AddinCheck <layout.json> [<out dir for a sample roof push>]
// Checks the Revit-free parts of the add-in that turn what the designer decided and what the Revit model says into data:
//   - the assumptions dialog's logic (AnalysisAssumptionsPatcher: read, apply, validate, session) against the add-in's own reader of the layout and the analyses,
//   - the roof features (RoofFeaturesGeometry: openings, entries, edge, drains, slab, levels in the roof's canvas coordinates).
// What needs Revit itself (the collectors, the WPF window) is not here; the sources compiled are the ones the add-in builds.
var fails = 0;
void Check(string name, bool ok, string extra = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {extra}"); if (!ok) fails++; }

{
    Console.WriteLine("\n===== analysis assumptions (Revit dialog logic) =====");
    var baseJson = File.ReadAllText(args[0]);
    var rowsDyn = AnalysisAssumptionsPatcher.Read(baseJson, "dynamic");
    var rowsStat = AnalysisAssumptionsPatcher.Read(baseJson, "structural");
    Check("the dynamic analysis asks about 7 inputs, the static one about the deck capacity only", rowsDyn.Count == 7 && rowsStat.Count == 1 && rowsStat[0].Def.Key == "deck_capacity");
    Check("the sample layout has none confirmed", AnalysisAssumptionsPatcher.AnyUnconfirmed(rowsDyn), string.Join(",", rowsDyn.Select(r => r.Decision.State)));

    // decisions -> the layout -> the add-in's reader
    var decisions = new List<AssumptionDecision>
    {
        new() { Key = "deck_capacity", State = "entered", Value = "6.5" },
        new() { Key = "natural_frequency", State = "accepted" },
        new() { Key = "snow_zone", State = "entered", Value = "2a" },
        new() { Key = "altitude", State = "entered", Value = "420" },
        new() { Key = "day_schedule", State = "accepted" },
        new() { Key = "comfort_limit_walking", State = "entered", Value = "0.01" },
        new() { Key = "comfort_limit_rhythmic", State = "unconfirmed" },
    };
    var patched = AnalysisAssumptionsPatcher.Apply(baseJson, decisions);
    var again = AnalysisAssumptionsPatcher.Read(patched, "dynamic").ToDictionary(r => r.Def.Key, r => r.Decision);
    Check("what was decided reads back as decided",
        again["deck_capacity"].State == "entered" && again["deck_capacity"].Value == "6.5" && again["natural_frequency"].State == "accepted" && again["snow_zone"].Value == "2a" &&
        again["altitude"].Value == "420" && again["day_schedule"].State == "accepted" && again["comfort_limit_walking"].Value == "0.01" && again["comfort_limit_rhythmic"].State == "unconfirmed");
    var lay = JsonSerializer.Deserialize<SportifyLayout>(patched)!;
    var dyn = DynamicLayoutAdapter.ToInputs(lay);
    Check("the add-in's reader sees the entered values", dyn.Structure.CapacityKnM2 == 6.5 && dyn.SnowZone == "2a" && dyn.AltitudeM == 420 && dyn.WalkingLimitG == 0.01 && dyn.RhythmicLimitG == null && dyn.NaturalFrequencyHz == null && dyn.Schedule == null);
    Check("and the accepted keys", dyn.Structure.AcceptedAssumptions.Contains("natural_frequency") && dyn.Structure.AcceptedAssumptions.Contains("day_schedule") && dyn.Structure.AcceptedAssumptions.Count == 2, string.Join(",", dyn.Structure.AcceptedAssumptions));
    var rep = DynamicModel.Analyse(dyn);
    var stateOf = rep.assumptionUses.ToDictionary(u => u.key, u => u.state);
    Check("the analysis reports each input's state as decided",
        stateOf["deck_capacity"] == "entered" && stateOf["natural_frequency"] == "accepted" && stateOf["day_schedule"] == "accepted" && stateOf["comfort_limit_rhythmic"] == "unconfirmed" && stateOf["comfort_limit_walking"] == "entered");
    Check("one input still unconfirmed: PRELIMINARY, and the note names it", rep.summary.preliminary && rep.summary.preliminaryNote.Contains("rhythmic limit") && !rep.summary.preliminaryNote.Contains("deck capacity"), rep.summary.preliminaryNote);
    var all = decisions.Select(d => new AssumptionDecision { Key = d.Key, State = d.State == "unconfirmed" ? "accepted" : d.State, Value = d.Value }).ToList();
    var repAll = DynamicModel.Analyse(DynamicLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(AnalysisAssumptionsPatcher.Apply(baseJson, all))!));
    Check("everything decided: not preliminary", !repAll.summary.preliminary && repAll.summary.preliminaryNote == "" && repAll.summary.acceptedNote != "", repAll.summary.acceptedNote);

    // changing a decision, and going back to 'not confirmed'
    var undone = AnalysisAssumptionsPatcher.Apply(patched, new[] { new AssumptionDecision { Key = "deck_capacity", State = "unconfirmed" }, new AssumptionDecision { Key = "natural_frequency", State = "unconfirmed" } });
    var back = DynamicLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(undone)!);
    Check("unconfirmed removes the value and the acceptance", back.Structure.CapacityKnM2 == null && !back.Structure.AcceptedAssumptions.Contains("natural_frequency") && back.Structure.AcceptedAssumptions.Contains("day_schedule") && back.SnowZone == "2a");
    Check("the deck capacity alone is what the static analysis reads", StructureModel.Analyse(StructureLayoutAdapter.ToInputs(lay)).assumptionUses.Single().state == "entered");

    // validation
    var cap = AnalysisAssumptions.Find("deck_capacity")!;
    Check("a comma decimal is accepted", AnalysisAssumptionsPatcher.TryParse(cap, "6,5", out var n1, out _) && n1 == "6.5");
    Check("out of range and text are refused with a reason", !AnalysisAssumptionsPatcher.TryParse(cap, "99", out _, out var e1) && e1.Contains("between") && !AnalysisAssumptionsPatcher.TryParse(cap, "abc", out _, out var e2) && e2.Contains("number") && !AnalysisAssumptionsPatcher.TryParse(cap, "", out _, out _), e1);
    var snow = AnalysisAssumptions.Find("snow_zone")!;
    Check("a choice must be one of the choices", AnalysisAssumptionsPatcher.TryParse(snow, "1a", out var n2, out _) && n2 == "1a" && !AnalysisAssumptionsPatcher.TryParse(snow, "9", out _, out _));

    // the session: decisions belong to the project they were made for
    AssumptionsSession.Forget();
    AssumptionsSession.Remember(baseJson, decisions);
    var again2 = AssumptionsSession.ApplyRemembered(baseJson);
    Check("a later run in the same session gets the decisions again", DynamicLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(again2)!).Structure.CapacityKnM2 == 6.5);
    var other = System.Text.RegularExpressions.Regex.Replace(baseJson, "\"length_m\"\\s*:\\s*[0-9.]+", "\"length_m\": 123.4", System.Text.RegularExpressions.RegexOptions.None);
    var otherResult = AssumptionsSession.ApplyRemembered(other);
    Check("another project (another roof) does not inherit them", other != baseJson && DynamicLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(otherResult)!).Structure.CapacityKnM2 == null);
    Check("...and they are forgotten, not just skipped", DynamicLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(AssumptionsSession.ApplyRemembered(baseJson))!).Structure.CapacityKnM2 == null);

}
{
    Console.WriteLine("\n===== roof features (Revit model -> roof canvas) =====");
    // A roof 40 m long (x) and 20 m wide (Y) whose minimum corner is at model (100, 200); its top face is at Z = 12.
    var roof = new StructureGeometry.RoofRect(100, 200, 140, 220);
    RoofFeaturesGeometry.PointM P(double x, double y) => new(x, y);
    var outline = new[] { P(100, 200), P(140, 200), P(140, 220), P(100, 220) };

    var openings = new[]
    {
        new RoofFeaturesGeometry.OpeningLoop(new[] { P(110, 205), P(112, 205), P(112, 207), P(110, 207) }),                              // a 2 x 2 m rooflight
        new RoofFeaturesGeometry.OpeningLoop(new[] { P(120, 210), P(120.1, 210), P(120.1, 210.1), P(120, 210.1) }),                     // a 1 cm2 artefact
        new RoofFeaturesGeometry.OpeningLoop(new[] { P(130, 212), P(133, 212), P(133, 214), P(130, 214), P(130, 212) }),                // closed loop (first point repeated), 6 m2
        new RoofFeaturesGeometry.OpeningLoop(new[] { P(300, 300), P(302, 300), P(302, 302), P(300, 302) }),                             // nowhere near the roof
    };
    var entries = new[]
    {
        new RoofFeaturesGeometry.EntryRecord("stair", "Stair 1", 110, 210, 1.2, 501),
        new RoofFeaturesGeometry.EntryRecord("stair", "Stair 1 (2nd run)", 110.3, 210.2, 1.2, 502),          // the same stair, seen twice
        new RoofFeaturesGeometry.EntryRecord("door", "Door 1", 141.5, 210, 0.9, 601),                        // a stair house just beyond the edge
        new RoofFeaturesGeometry.EntryRecord("door", "Door far", 200, 210, 0.9, 602),                        // not this roof
        new RoofFeaturesGeometry.EntryRecord("core", "Lift", 125, 205, 0, 701),
    };
    var walls = new[]
    {
        new RoofFeaturesGeometry.EdgeElement("parapet", "Parapet N", 100, 220, 140, 220, 0.6, 0.25),          // the whole north edge (canvas top)
        new RoofFeaturesGeometry.EdgeElement("railing", "Railing E", 140, 200, 140, 212, 1.1, 0.05),          // 12 of the 20 m of the east edge
        new RoofFeaturesGeometry.EdgeElement("parapet", "Parapet W", 100, 200, 100, 204, 0.5, 0.2),           // 4 of 20 m = 20% of the west edge
        new RoofFeaturesGeometry.EdgeElement("parapet", "Wall inside", 100, 210, 140, 210, 1.5, 0.3),         // parallel but 10 m from any edge: not on the edge
        new RoofFeaturesGeometry.EdgeElement("parapet", "Wall across", 120, 200, 120, 220, 1.5, 0.3),         // perpendicular to the north/south edges, 20 m from the west: not on an edge of its own direction
    };
    var drains = new[]
    {
        new RoofFeaturesGeometry.DrainRecord("drain", "Roof drain", 105, 215, 801),
        new RoofFeaturesGeometry.DrainRecord("drain", "Roof drain (again)", 105.01, 215.01, 802),
        new RoofFeaturesGeometry.DrainRecord("overflow", "Emergency overflow", 139, 201, 803),
        new RoofFeaturesGeometry.DrainRecord("drain", "Elsewhere", 250, 250, 804),
    };
    var slab = new RoofFeaturesGeometry.SlabRecord("Flat roof 300", new[]
    {
        new RoofFeaturesGeometry.LayerRecord("Membrane", "Bitumen", 0.01), new RoofFeaturesGeometry.LayerRecord("Insulation", "EPS", 0.16),
        new RoofFeaturesGeometry.LayerRecord("Structure", "Concrete C30/37", 0.25), new RoofFeaturesGeometry.LayerRecord("Finish2", "Plaster", 0.0),
    });
    var levels = new[]
    {
        new RoofFeaturesGeometry.LevelRecord("UG", -3.0), new RoofFeaturesGeometry.LevelRecord("EG", 0.0), new RoofFeaturesGeometry.LevelRecord("OG 3", 11.5),
        new RoofFeaturesGeometry.LevelRecord("Technik", 15.0),
    };

    var f = RoofFeaturesGeometry.Build(roof, outline, openings, entries, walls, drains, slab, levels, 12.0, 0.3, null, new[] { "a note" });

    // openings
    Check("two real openings kept; the artefact and the far one dropped", f.Openings.Count == 2, string.Join(",", f.Openings.Select(o => o.Id)));
    var o1 = f.Openings[0];
    Check("a hole at model x 110-112, Y 205-207 is at canvas x 10-12, y 13-15 (y flipped)", Math.Abs(o1.XM - 10) < 0.01 && Math.Abs(o1.WidthM - 2) < 0.01 && Math.Abs(o1.YM - 13) < 0.01 && Math.Abs(o1.HeightM - 2) < 0.01 && Math.Abs(o1.AreaM2 - 4) < 0.01, $"({o1.XM}, {o1.YM}, {o1.WidthM} x {o1.HeightM}, {o1.AreaM2} m2)");
    Check("a closed loop loses its repeated first point", f.Openings[1].PolygonM.Count == 4 && Math.Abs(f.Openings[1].AreaM2 - 6) < 0.01);

    // entries
    Check("the stair seen twice is one; the far door dropped; three entries left", f.Entries.Count == 3 && f.Entries.Count(e => e.Kind == "stair") == 1 && !f.Entries.Any(e => e.Name == "Door far"), string.Join(",", f.Entries.Select(e => e.Id)));
    var door = f.Entries.Single(e => e.Kind == "door");
    Check("a door 1.5 m beyond the east edge is kept but not on the roof; its canvas position is right", !door.OnRoof && Math.Abs(door.XM - 41.5) < 0.01 && Math.Abs(door.YM - 10) < 0.01, $"({door.XM}, {door.YM})");
    Check("a stair on the roof is on the roof; ids are per kind", f.Entries.Single(e => e.Kind == "stair").OnRoof && f.Entries.Single(e => e.Kind == "stair").Id == "stair_1" && f.Entries.Single(e => e.Kind == "core").Id == "core_1");

    // edges
    Check("four edges of the outline, in order", f.Edges.Count == 4 && f.Edges.Select(e => e.Index).SequenceEqual(new[] { 0, 1, 2, 3 }));
    // outline order in model coordinates: (100,200)->(140,200) is the roof's south edge = canvas bottom (y = 20)
    var south = f.Edges[0]; var east = f.Edges[1]; var north = f.Edges[2]; var west = f.Edges[3];
    Check("the model's south edge is the canvas bottom (y = 20) and is open", Math.Abs(south.StartM.YM - 20) < 0.01 && south.Kind == "open" && south.HeightM == 0);
    Check("the north edge (canvas top, y = 0) is a 0.6 m parapet 0.25 m thick", Math.Abs(north.StartM.YM) < 0.01 && north.Kind == "parapet" && Math.Abs(north.HeightM - 0.6) < 0.01 && Math.Abs(north.ThicknessM - 0.25) < 0.01 && north.ParapetCoverage == 1, $"({north.Kind}, {north.HeightM} m, cov {north.ParapetCoverage})");
    Check("the east edge is a railing over 60% of its length, 1.1 m high", east.Kind == "railing" && Math.Abs(east.RailingCoverage - 0.6) < 0.01 && Math.Abs(east.HeightM - 1.1) < 0.01, $"({east.Kind}, cov {east.RailingCoverage})");
    Check("the west edge, 20% covered, is partial", west.Kind == "partial" && Math.Abs(west.ParapetCoverage - 0.2) < 0.01, $"({west.Kind}, cov {west.ParapetCoverage})");
    Check("a wall in the middle and one across the roof do not count as edge protection", south.ParapetCoverage == 0 && north.ParapetCoverage == 1 && east.ParapetCoverage == 0);

    // drains
    Check("the drain seen twice is one, the far one dropped, the overflow kept", f.Drains.Count == 2 && f.Drains.Any(d => d.Kind == "overflow") && f.Drains.Count(d => d.Kind == "drain") == 1);
    var dr = f.Drains.Single(d => d.Kind == "drain");
    Check("a drain at model (105, 215) is at canvas (5, 5)", Math.Abs(dr.XM - 5) < 0.01 && Math.Abs(dr.YM - 5) < 0.01);

    // slab and levels
    Check("slab: the zero-thickness layer is dropped, the total and the structural depth add up", f.Slab != null && f.Slab.Layers.Count == 3 && Math.Abs(f.Slab.ThicknessM - 0.42) < 1e-6 && Math.Abs(f.Slab.StructuralThicknessM - 0.25) < 1e-6);
    Check("levels sorted, above ground from the ground elevation, the roof level is the highest at or below the top face",
        f.Levels.Select(l => l.Name).SequenceEqual(new[] { "UG", "EG", "OG 3", "Technik" }) && Math.Abs(f.Levels[1].AboveGroundM!.Value + 0.3) < 1e-6 && f.Levels.Single(l => l.IsRoofLevel).Name == "OG 3");
    var named = RoofFeaturesGeometry.Build(roof, outline, openings, entries, walls, drains, slab, levels, 12.0, null, "Technik");
    Check("a level the model names as the roof's own wins; no ground gives no above-ground figure", named.Levels.Single(l => l.IsRoofLevel).Name == "Technik" && named.Levels.All(l => l.AboveGroundM == null));
    Check("notes travel", f.Notes.SequenceEqual(new[] { "a note" }));

    // no outline: the bounding box is the edge
    var boxed = RoofFeaturesGeometry.Build(roof, new RoofFeaturesGeometry.PointM[0], new RoofFeaturesGeometry.OpeningLoop[0], new RoofFeaturesGeometry.EntryRecord[0], walls, new RoofFeaturesGeometry.DrainRecord[0], null, new RoofFeaturesGeometry.LevelRecord[0], 12.0, null, null);
    Check("without an outline the bounding box gives four edges, the parapet still on the top one", boxed.Edges.Count == 4 && boxed.Edges.Count(e => e.Kind == "parapet") == 1 && boxed.Slab == null);

    // an L-shaped roof: a re-entrant corner is an edge like the others
    var lShape = new[] { P(100, 200), P(140, 200), P(140, 210), P(120, 210), P(120, 220), P(100, 220) };
    var l = RoofFeaturesGeometry.Build(roof, lShape, new RoofFeaturesGeometry.OpeningLoop[0], new RoofFeaturesGeometry.EntryRecord[0], walls, new RoofFeaturesGeometry.DrainRecord[0], null, new RoofFeaturesGeometry.LevelRecord[0], 12.0, null, null);
    Check("an L-shaped outline gives six edges; the walls that lie along its inner edges count there, the 20 m parapet on its top edge is 20 m", l.Edges.Count == 6 && l.Edges.Count(e => e.Kind == "parapet") == 3 && l.Edges.Single(e => e.Index == 4).Kind == "parapet" && Math.Abs(l.Edges.Single(e => e.Index == 4).LengthM - 20) < 0.01, string.Join(",", l.Edges.Select(e => e.Kind)));

    // the block survives the JSON the app and the export carry it in
    var wrapped = JsonSerializer.Serialize(new { roof_context = new { length_m = 40, width_m = 20, features = f } });
    var back = JsonSerializer.Deserialize<SportifyLayout>(wrapped)!.RoofContext!.Features!;
    Check("it round-trips through the layout DTO as snake_case JSON", back.Openings.Count == 2 && back.Entries.Count == 3 && back.Edges[2].Kind == "parapet" && back.Slab!.StructuralThicknessM == f.Slab!.StructuralThicknessM && back.Levels.Count == 4 && wrapped.Contains("\"polygon_m\"") && wrapped.Contains("\"on_roof\""));
    if (args.Length > 1)
    {
        Directory.CreateDirectory(args[1]);
        File.WriteAllText(Path.Combine(args[1], "roof_features_push.json"), JsonSerializer.Serialize(new { roof = new { length_m = 40.0, width_m = 20.0, origin_x_m = 100.0, origin_y_m = 200.0, origin_z_m = 12.0, source_element_name = "Flat roof (test)", height_above_ground_m = 11.7, height_source = "test", features = f } }));
        Console.WriteLine("   wrote roof_features_push.json to " + args[1]);
    }
}

// the recordings the web app plays beside the numbers (RoofBoundaryServer: GET /recording)
{
    var mp4 = Path.Combine(Path.GetTempPath(), "sportify_addincheck_" + Guid.NewGuid().ToString("N") + ".mp4");
    var bytes = new byte[100_000];
    new Random(7).NextBytes(bytes);
    File.WriteAllBytes(mp4, bytes);
    var secret = Path.Combine(Path.GetTempPath(), "sportify_addincheck_secret_" + Guid.NewGuid().ToString("N") + ".mp4");
    File.WriteAllBytes(secret, new byte[10]);
    try
    {
        var found = RoofBoundaryServer.VideoPathsIn(JsonSerializer.Serialize(new { structural_loads = new { video_path = mp4 }, wind_erosion = new { video_path = (string?)null }, other = new { video_path = "C:/not-a-video.txt" } }));
        Check("only the .mp4 named as a video_path is collected", found.Count == 1 && found[0] == Path.GetFullPath(mp4));
        Check("a range header is read: normal, open-ended, suffix, past the end, and rubbish",
            RoofBoundaryServer.ParseRange("bytes=0-99", 1000) is { Start: 0, End: 99 } && RoofBoundaryServer.ParseRange("bytes=500-", 1000) is { Start: 500, End: 999 } &&
            RoofBoundaryServer.ParseRange("bytes=-100", 1000) is { Start: 900, End: 999 } && RoofBoundaryServer.ParseRange("bytes=0-5000", 1000) is { Start: 0, End: 999 } &&
            RoofBoundaryServer.ParseRange("bytes=2000-", 1000) is { Start: -1 } && RoofBoundaryServer.ParseRange("bytes=9-3", 1000) == null && RoofBoundaryServer.ParseRange("frames=1-2", 1000) == null && RoofBoundaryServer.ParseRange(null, 1000) == null);

        RoofBoundaryServer.PublishAnalysisResults(JsonSerializer.Serialize(new { structural_loads = new { video_path = mp4 } }));
        Check("a published recording is allowed, another file is not, a path with .. cannot escape", RoofBoundaryServer.IsAllowedRecording(mp4) && !RoofBoundaryServer.IsAllowedRecording(secret)
            && !RoofBoundaryServer.IsAllowedRecording(Path.Combine(Path.GetDirectoryName(mp4)!, "x", "..", Path.GetFileName(secret))));

        var started = false;
        try { RoofBoundaryServer.Start(); started = true; } catch (Exception ex) { Console.WriteLine("   (live HTTP check skipped: " + ex.Message + ")"); }
        if (started)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var url = "http://localhost:5679/recording?path=" + Uri.EscapeDataString(mp4);
            var whole = await http.GetAsync(url);
            var body = await whole.Content.ReadAsByteArrayAsync();
            Check("GET /recording serves the whole file as video/mp4 with byte ranges on offer", whole.StatusCode == System.Net.HttpStatusCode.OK && body.SequenceEqual(bytes) && whole.Content.Headers.ContentType?.MediaType == "video/mp4" && whole.Headers.AcceptRanges.Contains("bytes"));
            var req = new HttpRequestMessage(HttpMethod.Get, url); req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(1000, 1999);
            var part = await http.SendAsync(req);
            var partBody = await part.Content.ReadAsByteArrayAsync();
            Check("a range request gets 206 and exactly those bytes", part.StatusCode == System.Net.HttpStatusCode.PartialContent && partBody.SequenceEqual(bytes.Skip(1000).Take(1000)) && part.Content.Headers.ContentRange?.ToString() == "bytes 1000-1999/100000");
            var late = new HttpRequestMessage(HttpMethod.Get, url); late.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(500_000, null);
            Check("a range past the end is 416", (int)(await http.SendAsync(late)).StatusCode == 416);
            var denied = await http.GetAsync("http://localhost:5679/recording?path=" + Uri.EscapeDataString(secret));
            Check("a file no result names is not served", denied.StatusCode == System.Net.HttpStatusCode.NotFound);
            Check("the results document itself is still served", (await http.GetStringAsync("http://localhost:5679/analysis-results")).Contains("video_path"));
            RoofBoundaryServer.Stop();
        }
    }
    finally
    {
        File.Delete(mp4); File.Delete(secret);
    }
}

Console.WriteLine(fails == 0 ? "\nALL ADD-IN CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
