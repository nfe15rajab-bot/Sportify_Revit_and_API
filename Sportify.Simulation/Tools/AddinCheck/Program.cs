using System.Text.Json;
using SportfyRevit;
using Sportify.Simulation.Dynamics;
using Sportify.Simulation.Structure;
using Sportify.Simulation.Sun;

// usage: AddinCheck [<layout.json> [<out dir for a sample roof push>]]      (no arguments: the bundled sample layout, no push written)
// Checks the Revit-free parts of the add-in that turn what the designer decided and what the Revit model says into data:
//   - the assumptions dialog's logic (AnalysisAssumptionsPatcher: read, apply, validate, session) against the add-in's own reader of the layout and the analyses,
//   - the roof features (RoofFeaturesGeometry: openings, entries, edge, drains, slab, levels in the roof's canvas coordinates).
// What needs Revit itself (the collectors, the WPF window) is not here; the sources compiled are the ones the add-in builds.
//
// AddinCheck --ribbon-matrix     reads [{ "id", "view", "extras": [...] }] on stdin and prints, as JSON, what the ribbon shows for each of those profiles on a computer with every tool
//                                and on one with none (RibbonVisibility.Plan, the rules the add-in applies). Tools/ProfileMatrix compares it with what the web app shows for the same profile.
if (args.Length >= 1 && args[0] == "--ribbon-matrix")
{
    var found0 = new ToolStatus(true, @"C:\Tools\x.exe", "found");
    var withTools = new Capabilities(found0, true, found0, found0);
    var withoutTools = withTools with
    {
        Unity = new ToolStatus(false, null, "Unity was not found on this computer."), UnityProjectFree = false,
        SolidWorks = new ToolStatus(false, null, "SOLIDWORKS is not installed on this computer."), Chrome = new ToolStatus(false, null, "no Chrome"),
    };
    var panelsOfLayout = RibbonLayout.Panels;
    var buttons = panelsOfLayout.SelectMany(p => p.Entries).SelectMany(e => e switch
    {
        RibbonButtonSpec b => new[] { b.InternalName },
        RibbonPulldownSpec pd => pd.Items.Select(i => i.InternalName),
        _ => Array.Empty<string>(),
    }).Append(RibbonVisibility.PushMenuName).ToList();
    var requests = JsonSerializer.Deserialize<List<MatrixRequest>>(Console.In.ReadToEnd(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<MatrixRequest>();
    object Shown(string? view, string[] extras, Capabilities caps)
    {
        var plan = RibbonVisibility.Plan(view, caps, false, extras);
        return new
        {
            panels = panelsOfLayout.Where(p => plan[RibbonVisibility.PanelKey(p.Name)].Visible).Select(p => p.Name).ToArray(),
            buttons = buttons.Where(n => plan[n].Visible).ToArray(),
        };
    }
    var matrix = new
    {
        panels = panelsOfLayout.Select(p => p.Name).ToArray(),
        buttons = buttons.ToArray(),
        simple_buttons = RibbonVisibility.SimpleButtons.ToArray(),
        always_visible = RibbonVisibility.AlwaysVisible.ToArray(),
        extra_buttons = RibbonVisibility.ExtraButtons.ToDictionary(kv => kv.Key, kv => kv.Value),
        profiles = requests.Select(r => new
        {
            id = r.Id, view = r.View, extras = r.Extras ?? Array.Empty<string>(),
            with_every_tool = Shown(r.View, r.Extras ?? Array.Empty<string>(), withTools),
            with_no_tool = Shown(r.View, r.Extras ?? Array.Empty<string>(), withoutTools),
        }).ToArray(),
    };
    Console.Out.Write(JsonSerializer.Serialize(matrix));
    return 0;
}
if (args.Length == 0)
{
    // no arguments: run from anywhere, on the sample layout the Unity project bundles
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        var sample = Path.Combine(dir.FullName, "Assets", "StreamingAssets", "sample_layout_roofgarden.json");
        if (File.Exists(sample)) { args = new[] { sample }; break; }
    }
    if (args.Length == 0) { Console.WriteLine("no layout given and Assets/StreamingAssets/sample_layout_roofgarden.json not found above " + AppContext.BaseDirectory); return 2; }
}
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

    // the walls that cast shadows
    var shading = RoofFeaturesGeometry.Build(roof, outline, new RoofFeaturesGeometry.OpeningLoop[0], new RoofFeaturesGeometry.EntryRecord[0], walls, new RoofFeaturesGeometry.DrainRecord[0], null, new RoofFeaturesGeometry.LevelRecord[0], 12.0, null, null, null,
        new[] { new RoofFeaturesGeometry.EdgeElement("wall", "Stair house N", 110, 215, 114, 215, 3.0, 0.3), new RoofFeaturesGeometry.EdgeElement("wall", "Far wall", 300, 300, 304, 300, 3.0, 0.3), new RoofFeaturesGeometry.EdgeElement("wall", "Beyond the edge", 141, 210, 141, 214, 2.5, 0.2) });
    Check("a wall standing on the roof is an obstacle in canvas coordinates (y flipped); a far one is dropped, one just beyond the edge is kept",
        shading.Obstacles.Count == 2 && Math.Abs(shading.Obstacles[0].StartM.XM - 10) < 0.01 && Math.Abs(shading.Obstacles[0].StartM.YM - 5) < 0.01 && Math.Abs(shading.Obstacles[0].EndM.XM - 14) < 0.01 && shading.Obstacles[0].HeightM == 3 && Math.Abs(shading.Obstacles[0].ThicknessM - 0.3) < 1e-9
        && shading.Obstacles[1].Name == "Beyond the edge");
    var shadingJson = JsonSerializer.Serialize(new { roof_context = new { length_m = 40, width_m = 20, features = shading } });
    Check("obstacles travel in the roof context JSON the model reads", shadingJson.Contains("\"obstacles\"") && JsonSerializer.Deserialize<SportifyLayout>(shadingJson)!.RoofContext!.Features!.Obstacles.Count == 2);

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

// the sun and shade analysis: its inputs through the dialog's logic, the add-in's reader and the model
{
    var baseSun = File.ReadAllText(args[0]);
    var sunRows = AnalysisAssumptionsPatcher.Read(baseSun, "sun");
    var st = sunRows.ToDictionary(r => r.Def.Key, r => r.Decision.State);
    Check("the sun analysis asks about the site, the orientation, the shade target, the garden sun, the equipment and the deck capacity", sunRows.Count == 6 && new[] { "site_latitude", "roof_north", "shade_target", "garden_min_sun", "shade_equipment", "deck_capacity" }.All(st.ContainsKey));
    Check("the sample already has a site and an orientation (entered); the rest is unconfirmed", st["site_latitude"] == "entered" && st["roof_north"] == "entered" && st["shade_target"] == "unconfirmed" && st["shade_equipment"] == "unconfirmed" && st["deck_capacity"] == "unconfirmed");

    var sunDecisions = new List<AssumptionDecision>
    {
        new() { Key = "site_latitude", State = "entered", Value = "47.5" }, new() { Key = "roof_north", State = "entered", Value = "90" },
        new() { Key = "shade_target", State = "entered", Value = "65" }, new() { Key = "garden_min_sun", State = "entered", Value = "5.5" },
        new() { Key = "shade_equipment", State = "entered", Value = "light" }, new() { Key = "deck_capacity", State = "accepted" },
    };
    var sunPatched = AnalysisAssumptionsPatcher.Apply(baseSun, sunDecisions);
    var sunInputs = SunLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(sunPatched)!);
    Check("what was decided is what the add-in's reader hands the model (a typed latitude beats the map's)", sunInputs.LatitudeDeg == 47.5 && sunInputs.NorthDeg == 90 && sunInputs.ShadeTargetPercent == 65 && sunInputs.GardenMinSunHours == 5.5 && sunInputs.Equipment == "light" && sunInputs.Structure.AcceptedAssumptions.Contains("deck_capacity"));
    var sunReport = SunModel.Analyse(sunInputs);
    var sunState = sunReport.assumptionUses.ToDictionary(u => u.key, u => u.state);
    Check("the analysis reports each input as decided, and is not preliminary", sunReport.assumptionUses.Count == 6 && sunState.Values.All(v => v != "unconfirmed") && !sunReport.summary.preliminary && sunState["deck_capacity"] == "accepted");
    Check("the latitude and orientation reach the report", Math.Abs(sunReport.summary.latitudeDeg - 47.5) < 1e-4 && Math.Abs(sunReport.summary.northDeg - 90) < 1e-4 && !sunReport.summary.latitudeAssumed);

    var sunBack = AnalysisAssumptionsPatcher.Apply(sunPatched, new[] { new AssumptionDecision { Key = "roof_north", State = "unconfirmed" }, new AssumptionDecision { Key = "site_latitude", State = "unconfirmed" } });
    var back2 = SunLayoutAdapter.ToInputs(JsonSerializer.Deserialize<SportifyLayout>(sunBack)!);
    Check("un-deciding the orientation removes it; the latitude falls back to the site's own (the map)", back2.NorthDeg == null && back2.LatitudeDeg is { } l && Math.Abs(l - 53.5511) < 1e-4);
    var sunNoSite = JsonSerializer.Deserialize<SportifyLayout>(baseSun)!; sunNoSite.SiteLocation = null; sunNoSite.SiteConditions!.NorthDeg = null;
    Check("no site, no orientation: none given (the model assumes and says so)", SunLayoutAdapter.ToInputs(sunNoSite).LatitudeDeg == null && SunLayoutAdapter.ToInputs(sunNoSite).NorthDeg == null && SunModel.Analyse(SunLayoutAdapter.ToInputs(sunNoSite)).summary.latitudeAssumed);
    Check("the sample has no people zone, so no equipment; its gardens all have their sun", sunReport.summary.pieces == 0 && sunReport.summary.gardenZonesTooShaded == 0);
    foreach (var bad in new[] { ("shade_target", "0"), ("shade_target", "150"), ("garden_min_sun", "30"), ("site_latitude", "80"), ("roof_north", "400"), ("shade_equipment", "everything") })
        Check($"{bad.Item1} = {bad.Item2} is refused", !AnalysisAssumptionsPatcher.TryParse(AnalysisAssumptions.Find(bad.Item1)!, bad.Item2, out _, out _));
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
            // the session: the app asks for its token, and every other request carries it
            string Token(string origin)
            {
                var r = new HttpRequestMessage(HttpMethod.Get, "http://localhost:5679/session"); r.Headers.Add("Origin", origin);
                return JsonDocument.Parse(http.Send(r).Content.ReadAsStringAsync().Result).RootElement.GetProperty("token").GetString()!;
            }
            var token = Token("http://localhost:8123");
            http.DefaultRequestHeaders.Add("X-Sportify-Token", token);
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

            // ---- the lock on the local servers, against the real listener
            Console.WriteLine("\n===== the local server admits only the app, with its token, and only so much =====");
            HttpRequestMessage Ask(HttpMethod m, string path, string? origin = null, string? tokenHeader = null, HttpContent? content = null)
            {
                var r = new HttpRequestMessage(m, "http://localhost:5679" + path) { Content = content };
                if (origin != null) r.Headers.Add("Origin", origin);
                if (tokenHeader != null) r.Headers.Add("X-Sportify-Token", tokenHeader);
                return r;
            }
            using var bare = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };          // no default headers: a client that has no token
            var noToken = await bare.SendAsync(Ask(HttpMethod.Get, "/analysis-results"));
            Check("a request with no token is refused (401), so a page that has none reads nothing", (int)noToken.StatusCode == 401);
            var wrongToken = await bare.SendAsync(Ask(HttpMethod.Get, "/analysis-results", "http://localhost:8123", "0000"));
            Check("a wrong token is refused too", (int)wrongToken.StatusCode == 401);
            var foreign = await bare.SendAsync(Ask(HttpMethod.Get, "/analysis-results", "https://evil.example", token));
            Check("a page from another origin is refused (403) even holding the token, and gets no CORS header (the browser then hides the answer)", (int)foreign.StatusCode == 403 && !foreign.Headers.Contains("Access-Control-Allow-Origin"));
            var foreignSession = await bare.SendAsync(Ask(HttpMethod.Get, "/session", "https://evil.example"));
            Check("the session token is not given to another origin", (int)foreignSession.StatusCode == 403 && !(await foreignSession.Content.ReadAsStringAsync()).Contains(token));
            var noOriginSession = await bare.SendAsync(Ask(HttpMethod.Get, "/session"));
            Check("...nor to a request that has no Origin at all (a page always has one)", (int)noOriginSession.StatusCode == 403 && !(await noOriginSession.Content.ReadAsStringAsync()).Contains(token));
            var nullOrigin = await bare.SendAsync(Ask(HttpMethod.Get, "/session", "null"));
            Check("...nor to the opaque origin \"null\" (a sandboxed frame, a local file)", (int)nullOrigin.StatusCode == 403);
            foreach (var origin in new[] { "http://localhost:8123", "http://127.0.0.1:8123", "http://localhost:8124" })
            {
                var ok = await bare.SendAsync(Ask(HttpMethod.Get, "/analysis-results", origin, token));
                Check($"the app at {origin} with its token is served, and the CORS header names that origin, never *",
                    ok.StatusCode == System.Net.HttpStatusCode.OK && ok.Headers.GetValues("Access-Control-Allow-Origin").Single() == origin);
            }
            var refusedButReadable = await bare.SendAsync(Ask(HttpMethod.Get, "/analysis-results", "http://localhost:8123"));
            Check("the app's own refusal (no token) is readable by the app, so that it can fetch a new session after Revit restarted", (int)refusedButReadable.StatusCode == 401 && refusedButReadable.Headers.GetValues("Access-Control-Allow-Origin").Single() == "http://localhost:8123");
            var media = await bare.GetAsync("http://localhost:5679/recording?path=" + Uri.EscapeDataString(mp4) + "&token=" + token);
            Check("a video or a download link, which cannot send a header, carries the token in the address", media.StatusCode == System.Net.HttpStatusCode.OK);
            var pre = await bare.SendAsync(Ask(HttpMethod.Options, "/combined-layout", "http://localhost:8123"));
            Check("the preflight of the app is answered, and allows the token header", (int)pre.StatusCode == 204 && string.Join(",", pre.Headers.GetValues("Access-Control-Allow-Headers")).Contains("X-Sportify-Token") && pre.Headers.GetValues("Access-Control-Allow-Origin").Single() == "http://localhost:8123");
            var preForeign = await bare.SendAsync(Ask(HttpMethod.Options, "/combined-layout", "https://evil.example"));
            Check("the preflight of another origin is refused, so its POST is never sent", (int)preForeign.StatusCode == 403);

            // sizes
            var smallLayout = await bare.SendAsync(Ask(HttpMethod.Post, "/combined-layout?draft=1", "http://localhost:8123", token, new StringContent("{\"placements\":[]}", System.Text.Encoding.UTF8, "application/json")));
            Check("a layout of ordinary size is taken and its identity answered", smallLayout.StatusCode == System.Net.HttpStatusCode.OK && (await smallLayout.Content.ReadAsStringAsync()).Contains("layout_id"));
            var big = new byte[LocalRequestGuard.MaxOtherBytes + 1];
            var bigOther = await bare.SendAsync(Ask(HttpMethod.Post, "/run-analysis", "http://localhost:8123", token, new ByteArrayContent(big)));
            Check("a body beyond what an endpoint takes (1 MB for the ones that take none) is refused with 413", (int)bigOther.StatusCode == 413);
            System.Net.HttpStatusCode? chunkedStatus = null;
            try { chunkedStatus = (await bare.SendAsync(Ask(HttpMethod.Post, "/run-analysis", "http://localhost:8123", token, new StreamContent(new MemoryStream(new byte[LocalRequestGuard.MaxOtherBytes * 2]))))).StatusCode; }
            catch (Exception) { /* the server closed the connection while the body was still being sent: refused as well */ }
            Check("a body sent in chunks, with no length to check up front, is cut off as it passes the cap", chunkedStatus == null || (int)chunkedStatus == 413);
            using (var tcp = new System.Net.Sockets.TcpClient("localhost", 5679))
            {
                var stream = tcp.GetStream();
                var head = $"POST /combined-layout HTTP/1.1\r\nHost: localhost:5679\r\nOrigin: http://localhost:8123\r\nX-Sportify-Token: {token}\r\nContent-Type: application/json\r\nContent-Length: 70000000\r\n\r\n";
                stream.Write(System.Text.Encoding.ASCII.GetBytes(head));
                stream.ReadTimeout = 5000;
                var answerBuf = new byte[512]; var got = 0;
                try { got = stream.Read(answerBuf, 0, answerBuf.Length); } catch (IOException) { }
                Check("a Content-Length of 70 MB is answered 413 before a byte of the body is sent", System.Text.Encoding.ASCII.GetString(answerBuf, 0, got).StartsWith("HTTP/1.1 413"));
            }
            var after = await bare.SendAsync(Ask(HttpMethod.Get, "/analysis-results", "http://localhost:8123", token));
            Check("the server is still serving after the refused bodies", after.StatusCode == System.Net.HttpStatusCode.OK);
            RoofBoundaryServer.Stop();
        }
    }
    finally
    {
        File.Delete(mp4); File.Delete(secret);
    }
}

// the holes of the roof finish (PlanGeometry) and the furniture families' shapes (FurnitureShape)
{
    Console.WriteLine("\n===== holes in the roof finish =====");
    PlanGeometry.P P(double x, double y) => new(x, y);
    List<PlanGeometry.P> Rect(double x0, double y0, double x1, double y1) => new() { P(x0, y0), P(x1, y0), P(x1, y1), P(x0, y1) };
    var roof = Rect(0, 0, 40, 20);
    var lRoof = new List<PlanGeometry.P> { P(0, 0), P(40, 0), P(40, 10), P(20, 10), P(20, 20), P(0, 20) };      // an L: the notch is x 20..40, y 10..20
    const double tol = 1e-3;

    Check("a rectangle inside the roof is a hole; one that touches the edge is too (a court on the edge is an ordinary layout)", PlanGeometry.HoleInside(roof, Rect(5, 5, 10, 10), tol) && PlanGeometry.HoleInside(roof, Rect(0, 0, 10, 5), tol));
    Check("one that pokes out of the roof is a notch, not a hole", !PlanGeometry.HoleInside(roof, Rect(35, 5, 45, 10), tol) && !PlanGeometry.HoleInside(roof, Rect(-2, 5, 3, 10), tol));
    var triangle = new List<PlanGeometry.P> { P(5, 5), P(15, 5), P(10, 12) };
    Check("a polygon (a zone whose corners were moved) inside the roof is a hole", PlanGeometry.HoleInside(roof, triangle, tol) && Math.Abs(PlanGeometry.Area(triangle) - 35) < 1e-9);
    var lZone = new List<PlanGeometry.P> { P(2, 2), P(12, 2), P(12, 6), P(6, 6), P(6, 12), P(2, 12) };      // an L-shaped bed: its box (2..12 x 2..12) is much bigger than it is
    Check("...an L-shaped bed too, and the area is the bed's, not its box's (64 m2, not 100)", PlanGeometry.HoleInside(roof, lZone, tol) && Math.Abs(PlanGeometry.Area(lZone) - 64) < 1e-9);
    var inNotch = Rect(22, 12, 26, 16);
    Check("in an L-shaped roof a hole in the notch (outside the roof) is refused even though it is inside the roof's box", !PlanGeometry.HoleInside(lRoof, inNotch, tol) && PlanGeometry.HoleInside(lRoof, Rect(2, 2, 10, 8), tol));
    var acrossNotch = new List<PlanGeometry.P> { P(15, 5), P(30, 5), P(30, 8), P(25, 8), P(25, 5.5), P(15, 6) };
    Check("a hole whose corners are inside an L-shaped roof but whose edge cuts across the notch is refused", !PlanGeometry.HoleInside(lRoof, new List<PlanGeometry.P> { P(15, 5), P(30, 5), P(30, 12), P(15, 12) }, tol));
    var bowTie = new List<PlanGeometry.P> { P(0, 0), P(10, 10), P(10, 0), P(0, 10) };
    Check("a polygon that crosses itself is not simple", !PlanGeometry.IsSimple(bowTie, tol) && PlanGeometry.IsSimple(lZone, tol) && PlanGeometry.IsSimple(triangle, tol));

    Check("two holes that share a side do not overlap; two that share a corner do not", !PlanGeometry.Overlap(Rect(0, 0, 5, 5), Rect(5, 0, 10, 5), tol) && !PlanGeometry.Overlap(Rect(0, 0, 5, 5), Rect(5, 5, 10, 10), tol));
    Check("two that share area do: crossing, one inside the other, the same one twice", PlanGeometry.Overlap(Rect(0, 0, 6, 6), Rect(4, 4, 10, 10), tol) && PlanGeometry.Overlap(Rect(0, 0, 10, 10), Rect(2, 2, 4, 4), tol) && PlanGeometry.Overlap(Rect(0, 0, 5, 5), Rect(0, 0, 5, 5), tol));
    Check("a polygon and a rectangle: the bed's L overlaps a court on its arm and not one in its corner notch", PlanGeometry.Overlap(lZone, Rect(8, 3, 11, 5), tol) && !PlanGeometry.Overlap(lZone, Rect(7, 7, 11, 11), tol));
    Check("the skylight Revit has in the roof and a zone placed over it overlap (the second is refused, not the whole finish)", PlanGeometry.Overlap(Rect(10, 10, 14, 14), Rect(12, 8, 20, 12), tol));
    var dupes = PlanGeometry.Clean(new[] { P(0, 0), P(0, 0), P(5, 0), P(5, 5), P(0, 5), P(0, 0) }, tol);
    Check("a repeated point, and a closing point equal to the first, are dropped", dupes.Count == 4);
    Check("a point on the boundary is inside-or-on, not strictly inside", PlanGeometry.InsideOrOn(roof, P(0, 10), tol) && !PlanGeometry.StrictlyInside(roof, P(0, 10), tol) && PlanGeometry.StrictlyInside(roof, P(20, 10), tol) && !PlanGeometry.InsideOrOn(roof, P(41, 10), tol));

    // Revit refuses a sketch whose loops touch (the live import: every zone and court of a packed roof shares an edge): each hole is drawn a few millimetres smaller
    var innerRect = PlanGeometry.Inset(Rect(10, 5, 20, 15), 0.1)!;
    Check("a rectangle inset by 0.1 is 0.1 smaller on every side (9.8 x 9.8)", innerRect != null && Math.Abs(PlanGeometry.Area(innerRect) - 9.8 * 9.8) < 1e-9 && innerRect.Min(q => q.X) > 10.099 && innerRect.Max(q => q.X) < 19.901 && innerRect.Min(q => q.Y) > 5.099 && innerRect.Max(q => q.Y) < 14.901);
    var cw = new List<PlanGeometry.P>(Rect(0, 0, 10, 10)); cw.Reverse();
    Check("the turning of the polygon does not matter (clockwise gives the same)", Math.Abs(PlanGeometry.Area(PlanGeometry.Inset(cw, 0.1)!) - 9.8 * 9.8) < 1e-9);
    var lInset = PlanGeometry.Inset(lZone, 0.05);
    Check("an L-shaped bed inset keeps its shape: smaller, still simple, still inside its own outline", lInset != null && lInset.Count == 6 && PlanGeometry.Area(lInset) < 64 && PlanGeometry.Area(lInset) > 62 && PlanGeometry.IsSimple(lInset, 1e-6) && lInset.All(q => PlanGeometry.StrictlyInside(lZone, q, 1e-6)));
    Check("a sliver thinner than the inset is not a hole (null), not a bow-tie", PlanGeometry.Inset(Rect(0, 0, 10, 0.15), 0.1) == null);
    var left = PlanGeometry.Inset(Rect(0, 0, 5, 5), 0.005)!; var right = PlanGeometry.Inset(Rect(5, 0, 10, 5), 0.005)!;
    Check("two zones that share a side end up 1 cm apart: they no longer touch, and are not overlapping", left.Max(q => q.X) < 4.9951 && right.Min(q => q.X) > 5.0049 && !PlanGeometry.Overlap(left, right, 1e-6));
    var onEdge = PlanGeometry.Inset(Rect(0, 0, 10, 5), 0.005)!;
    Check("a hole on the roof's outline ends up strictly inside it (5 mm from the edge), so the loops do not touch", onEdge.All(q => PlanGeometry.StrictlyInside(roof, q, 1e-6)) && PlanGeometry.HoleInside(roof, onEdge, 1e-3));
    Check("a triangle inset stays a triangle of smaller area", PlanGeometry.Inset(triangle, 0.05) is { Count: 3 } tri && PlanGeometry.Area(tri) < 35 && PlanGeometry.Area(tri) > 33);

    Console.WriteLine("\n===== the shapes of furniture families =====");
    bool Inside(ShapePart q, double l, double w, double h) => q.X0 >= -l / 2 - 1e-9 && q.X1 <= l / 2 + 1e-9 && q.Y0 >= -w / 2 - 1e-9 && q.Y1 <= w / 2 + 1e-9 && q.Z0 >= -1e-9 && q.Z1 <= h + 1e-9 && q.X1 > q.X0 && q.Y1 > q.Y0 && q.Z1 > q.Z0;
    foreach (var (cat, l, w, h) in new[] { ("bench", 1.8, 0.7, 0.8), ("table", 1.6, 0.8, 0.75), ("bin", 0.4, 0.4, 0.9), ("bollard", 0.15, 0.15, 1.0), ("light", 0.3, 0.3, 3.5), ("planter", 1.0, 0.5, 0.6), ("bench", 0.4, 0.3, 0.5) })
    {
        var parts = FurnitureShape.Parts(cat, l, w, h);
        Check($"{cat} {l} x {w} x {h}: {parts.Count} solid(s), every one inside the product's box and with a size", parts.Count >= 1 && parts.All(q => Inside(q, l, w, h)));
        Check($"{cat}: it reaches the product's height and touches the ground", Math.Abs(parts.Max(q => q.Z1) - h) < 1e-9 && parts.Min(q => q.Z0) < 1e-9);
    }
    Check("a bench has a seat, a backrest and two end frames; a table a top and four legs; a light a base, a pole and a head", FurnitureShape.Parts("bench", 1.8, 0.7, 0.8).Count == 4 && FurnitureShape.Parts("table", 1.6, 0.8, 0.75).Count == 5 && FurnitureShape.Parts("light", 0.3, 0.3, 3.5).Select(q => q.Name).SequenceEqual(new[] { "base", "pole", "head" }));
    Check("a bin, a bollard and a light are round; a bench is not", FurnitureShape.Parts("bin", 0.4, 0.4, 0.9).All(q => q.IsCylinder) && FurnitureShape.Parts("bollard", 0.15, 0.15, 1).All(q => q.IsCylinder) && FurnitureShape.Parts("bench", 1.8, 0.7, 0.8).All(q => !q.IsCylinder));
    Check("a bench's seat is at seat height (0.44 m of 0.8) and its back rises above it", FurnitureShape.Parts("bench", 1.8, 0.7, 0.8).Single(q => q.Name == "seat").Z1 is > 0.4 and < 0.5 && FurnitureShape.Parts("bench", 1.8, 0.7, 0.8).Single(q => q.Name == "backrest").Z1 == 0.8);
    Check("a category the catalogue grows that has no shape yet is a box of the product's size, not a failure", FurnitureShape.Parts("sculpture", 2, 1, 1.5).Single() is { Name: "body" } b && b.Volume == 3.0);
    Check("the family name carries the size in cm, so a product that changed size in the catalogue is a new family: \"Sportify - X [180x70x80 cm]\"", FurnitureShape.FamilyName(null, "X", "k", 1.8, 0.7, 0.8) == "Sportify - X [180x70x80 cm]" && FurnitureShape.FamilyName("Sportify - ABES Public Design Parkbank 1.114", "l", "k", 1.8, 0.7, 0.8) == "Sportify - ABES Public Design Parkbank 1.114 [180x70x80 cm]");
}

// the guard's rules on their own (no listener): origins, hosts, tokens, sizes and the web folder's walls
{
    Console.WriteLine("\n===== who may ask, and how much =====");
    var g = new LocalRequestGuard(5679, new[] { "http://dev.local:9000/", "*", "null", "ftp://x", "http://a.b/path" }, "tok");
    Check("the app's four addresses are allowed; a stranger is not", new[] { "http://localhost:8123", "http://127.0.0.1:8123", "http://localhost:8124", "http://127.0.0.1:8124" }.All(g.IsAllowedOrigin)
        && !g.IsAllowedOrigin("http://localhost:9999") && !g.IsAllowedOrigin("https://localhost:8123") && !g.IsAllowedOrigin("http://localhost.evil.example:8123") && !g.IsAllowedOrigin(null) && !g.IsAllowedOrigin(""));
    Check("an origin added by the environment is allowed, normalised; a wildcard, \"null\", another scheme and a path are never added", g.IsAllowedOrigin("HTTP://Dev.Local:9000") && !g.IsAllowedOrigin("*") && !g.IsAllowedOrigin("null") && !g.IsAllowedOrigin("ftp://x") && !g.IsAllowedOrigin("http://a.b"));
    LocalRequestGuard.Verdict J(string method, string? host, string? origin, string? path, string? th = null, string? tq = null) => g.Judge(method, host, origin, path, th, tq);
    Check("a Host that is not this machine's name for the port is refused (DNS rebinding)", J("GET", "evil.example:5679", null, "/x", "tok").Outcome == LocalRequestGuard.Outcome.Refuse && J("GET", null, null, "/x", "tok").Outcome == LocalRequestGuard.Outcome.Refuse && J("GET", "localhost:1234", null, "/x", "tok").Outcome == LocalRequestGuard.Outcome.Refuse);
    Check("with the token, no Origin (a local tool) or the app's Origin proceeds; another Origin does not", J("GET", "localhost:5679", null, "/x", "tok").Outcome == LocalRequestGuard.Outcome.Proceed && J("GET", "127.0.0.1:5679", "http://localhost:8123", "/x", "tok").Outcome == LocalRequestGuard.Outcome.Proceed && J("GET", "localhost:5679", "http://evil.example", "/x", "tok").Status == 403);
    Check("the token may also come in the query; a token of another length, or empty, never matches", J("GET", "localhost:5679", null, "/x", null, "tok").Outcome == LocalRequestGuard.Outcome.Proceed && J("GET", "localhost:5679", null, "/x", "to").Status == 401 && J("GET", "localhost:5679", null, "/x", "").Status == 401 && J("GET", "localhost:5679", null, "/x", "tok0").Status == 401);
    Check("the session goes only to a GET carrying an allowed Origin", J("GET", "localhost:5679", "http://localhost:8123", "/session").Outcome == LocalRequestGuard.Outcome.Session && J("GET", "localhost:5679", null, "/session").Status == 403 && J("POST", "localhost:5679", "http://localhost:8123", "/session").Status == 405);
    Check("a preflight of a foreign origin is refused, of the app's answered without a token, of no origin answered with no CORS", J("OPTIONS", "localhost:5679", "http://evil.example", "/x").Status == 403 && J("OPTIONS", "localhost:5679", "http://localhost:8123", "/x") is { Outcome: LocalRequestGuard.Outcome.Preflight, CorsOrigin: "http://localhost:8123" } && J("OPTIONS", "localhost:5679", null, "/x").CorsOrigin == null);
    var g2 = new LocalRequestGuard(5679); var g3 = new LocalRequestGuard(5679);
    Check("the token is 256 random bits, different for every guard", g2.Token.Length == 64 && g2.Token.All(Uri.IsHexDigit) && g2.Token != g3.Token);

    Check("limits: a layout and a saved file 64 MB, everything else 1 MB", LocalRequestGuard.BodyLimit("POST", "/combined-layout") == 64L * 1024 * 1024 && LocalRequestGuard.BodyLimit("POST", "/deliverable") == 64L * 1024 * 1024 && LocalRequestGuard.BodyLimit("POST", "/run-analysis") == 1024 * 1024 && LocalRequestGuard.BodyLimit("GET", "/combined-layout") == 1024 * 1024);
    Check("a body inside the limit is read whole", LocalRequestGuard.ReadBounded(new MemoryStream(new byte[1000]), 1000).Length == 1000);
    var tooBig = false; try { LocalRequestGuard.ReadBounded(new MemoryStream(new byte[1001]), 1000); } catch (LocalRequestGuard.BodyTooLargeException) { tooBig = true; }
    var claimed = false; var untouched = new MemoryStream(new byte[10]); try { LocalRequestGuard.ReadBounded(untouched, 1000, 5000); } catch (LocalRequestGuard.BodyTooLargeException) { claimed = untouched.Position == 0; }
    Check("one byte over is refused while reading, and a length that is over is refused before any byte is read", tooBig && claimed);

    var web = Path.Combine(Path.GetTempPath(), "sportify-web-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    var sibling = web + "-secrets";
    Directory.CreateDirectory(Path.Combine(web, "vendor")); Directory.CreateDirectory(sibling);
    File.WriteAllText(Path.Combine(web, "index.html"), "x"); File.WriteAllText(Path.Combine(web, "vendor", "a b.js"), "x"); File.WriteAllText(Path.Combine(sibling, "key.txt"), "secret");
    Check("the web server serves a file in the folder and in a subfolder, with a space in its name", LocalRequestGuard.ResolveInside(web, "/index.html") != null && LocalRequestGuard.ResolveInside(web, "/vendor/a%20b.js") != null);
    Check("...but not one outside it: .. , an encoded .. , a sibling folder whose name begins with the web folder's, or a folder", LocalRequestGuard.ResolveInside(web, "/../" + Path.GetFileName(sibling) + "/key.txt") == null
        && LocalRequestGuard.ResolveInside(web, "/%2e%2e/" + Path.GetFileName(sibling) + "/key.txt") == null && LocalRequestGuard.ResolveInside(web, "/vendor") == null && LocalRequestGuard.ResolveInside(web, "/nothing.js") == null);
    try { Directory.Delete(web, true); Directory.Delete(sibling, true); } catch (IOException) { }
}

// the roof's own plan frame: a roof turned against the model's axes gets a plan of its own (RoofFrame), and everything pushed and imported goes through it
{
    Console.WriteLine("\n===== the roof's plan frame (turned roofs) =====");
    bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;
    (double x, double y) Rot(double x, double y, double deg) { var t = deg * Math.PI / 180; return (x * Math.Cos(t) - y * Math.Sin(t), x * Math.Sin(t) + y * Math.Cos(t)); }
    // a length x width rectangle whose long side runs at deg from the model's X axis, its corner at (ox, oy)
    List<(double X, double Y)> TurnedRect(double length, double width, double deg, double ox, double oy)
    {
        var pts = new[] { (0.0, 0.0), (length, 0.0), (length, width), (0.0, width) };
        return pts.Select(q => { var r = Rot(q.Item1, q.Item2, deg); return (r.x + ox, r.y + oy); }).ToList();
    }
    (double, double, double, double) Box(IEnumerable<(double X, double Y)> pts) => (pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y));

    // ---- a roof square to the model keeps the frame it always had
    var square = TurnedRect(40, 20, 0, 100, 200);
    var (sx0, sy0, sx1, sy1) = Box(square);
    var f0 = RoofFrame.Fit(square, sx0, sy0, sx1, sy1);
    Check("a roof square to the model is not turned: origin at the box's minimum corner, the box's size", !f0.IsTurned && Near(f0.OriginX, 100) && Near(f0.OriginY, 200) && Near(f0.Length, 40) && Near(f0.Width, 20));
    var okOld = true;
    foreach (var (mx, my) in new[] { (100.0, 200.0), (140.0, 220.0), (117.3, 203.9), (99.9, 221.0) })
    {
        var (px, py) = f0.ToPlan(mx, my);
        okOld &= Near(px, mx - 100) && Near(py, 20 - (my - 200));                 // the old formula: canvas y = width - (Y - minimum Y)
    }
    Check("and its plan coordinates are the old formula exactly (x - minimum, width - (Y - minimum))", okOld);
    Check("a turn under a quarter degree is no turn", !RoofFrame.Fit(TurnedRect(40, 20, 0.1, 100, 200), 100, 200, 140.1, 220.1).IsTurned);
    var round = Enumerable.Range(0, 24).Select(i => (Math.Cos(i * Math.PI / 12) * 10 + 50, Math.Sin(i * Math.PI / 12) * 10 + 50)).ToList();
    Check("a round roof has no axis to follow", !RoofFrame.Fit(round, 40, 40, 60, 60).IsTurned);

    // ---- a roof turned 30 degrees
    foreach (var (deg, expectAngle, expectL, expectW) in new[] { (30.0, 30.0, 30.0, 12.0), (-20.0, -20.0, 30.0, 12.0), (60.0, -30.0, 12.0, 30.0), (10.0, 10.0, 30.0, 12.0) })
    {
        var pts = TurnedRect(30, 12, deg, 350.5, -120.25);
        var (bx0, by0, bx1, by1) = Box(pts);
        var f = RoofFrame.Fit(pts, bx0, by0, bx1, by1);
        Check($"a 30 x 12 m roof turned {deg} degrees: the plan turns {expectAngle} degrees and is {expectL} x {expectW} m", f.IsTurned && Near(f.AngleDeg, expectAngle, 1e-6) && Near(f.Length, expectL, 1e-6) && Near(f.Width, expectW, 1e-6),
              $"(angle {f.AngleDeg:0.####}, {f.Length:0.###} x {f.Width:0.###})");
        // every corner is a corner of the plan rectangle, and the plan's origin (bottom-left) is a corner of the roof
        var plan = pts.Select(q => f.ToPlan(q.X, q.Y)).ToList();
        var corners = new[] { (0.0, 0.0), (f.Length, 0.0), (f.Length, f.Width), (0.0, f.Width) };
        Check("  its corners are the plan's corners", plan.All(q => corners.Any(c => Near(q.X, c.Item1, 1e-6) && Near(q.Y, c.Item2, 1e-6))));
        // model -> plan -> model, and plan -> model -> plan
        var (mx, my) = f.ToModel(7.25, 3.5);
        var (px, py) = f.ToPlan(mx, my);
        var (ax, ay) = f.ToLocalUp(mx, my);
        var (rx, ry) = f.FromLocalUp(ax, ay);
        Check("  plan -> model -> plan (and y up -> model -> y up) returns the same point", Near(px, 7.25, 1e-9) && Near(py, 3.5, 1e-9) && Near(rx, mx, 1e-9) && Near(ry, my, 1e-9) && Near(ay, f.Width - 3.5, 1e-9));
    }
    var f30 = RoofFrame.Fit(TurnedRect(30, 12, 30, 350.5, -120.25), 0, 0, 0, 0);
    var o = f30.ToModel(0, f30.Width);
    var along = f30.ToModel(5, f30.Width);
    var down = f30.ToModel(0, f30.Width - 1);       // one metre UP on the roof in plan y terms (y is measured down from the top)
    Check("the plan's bottom-left corner is the frame's origin; 5 m along x runs along the roof at 30 degrees", Near(o.X, f30.OriginX) && Near(o.Y, f30.OriginY) && Near(along.X - o.X, 5 * Math.Cos(Math.PI / 6), 1e-9) && Near(along.Y - o.Y, 5 * Math.Sin(Math.PI / 6), 1e-9));
    Check("a step up the plan (y decreasing) is a step along the roof's own +v, 90 degrees left of x", Near(down.X - o.X, -Math.Sin(Math.PI / 6), 1e-9) && Near(down.Y - o.Y, Math.Cos(Math.PI / 6), 1e-9));
    // the floor builder works out a floor in the roof's own axes (origin + plan x, origin + height above the bottom edge: the plain convention) and turns the finished
    // points about the origin (RoofFrame.TurnAbout): that must be the frame's own mapping, in metres and in the feet the builder uses
    {
        var okTurn = true; var okFeet = true; var worst = 0.0;
        foreach (var deg in new[] { 0.0, 30.0, -20.0, 60.0, 10.0 })
        {
            var pts = TurnedRect(30, 12, deg, 350.5, -120.25);
            var (bx0, by0, bx1, by1) = Box(pts);
            var f = RoofFrame.Fit(pts, bx0, by0, bx1, by1);
            const double Ft = 3.280839895013123;
            foreach (var (px, py) in new[] { (0.0, 0.0), (7.25, 3.5), (30.0, 12.0), (12.5, 0.0), (0.0, 12.0), (21.1, 9.9) })
            {
                var (mx, my) = f.ToModel(px, py);
                var (tx, ty) = RoofFrame.TurnAbout(f.OriginX, f.OriginY, f.AngleRad, f.OriginX + px, f.OriginY + (f.Width - py));
                okTurn &= Near(tx, mx, 1e-9) && Near(ty, my, 1e-9);
                worst = Math.Max(worst, Math.Max(Math.Abs(tx - mx), Math.Abs(ty - my)));
                var (fx, fy) = RoofFrame.TurnAbout(f.OriginX * Ft, f.OriginY * Ft, f.AngleRad, (f.OriginX + px) * Ft, (f.OriginY + (f.Width - py)) * Ft);
                okFeet &= Near(fx, mx * Ft, 1e-6) && Near(fy, my * Ft, 1e-6);
            }
        }
        Check("the floor builder's own-axes points, turned about the origin, are the frame's plan -> model mapping (roof finish and zone outlines of a turned roof)", okTurn, $"(worst {worst:0.#e+0} m)");
        Check("  and the same in feet, the unit the builder works in", okFeet);
        Check("  a roof square to the model is left exactly as it was", RoofFrame.TurnAbout(100, 200, 0, 117.3, 203.9) == (117.3, 203.9));
    }

    // a piece turned 90 degrees clockwise on the canvas points its x axis DOWN the canvas; in the model that is the world angle (frame angle - 90 degrees)
    var top = f30.ToModel(0, 0); var below = f30.ToModel(0, 1);
    var dirDeg = Math.Atan2(below.Y - top.Y, below.X - top.X) * 180 / Math.PI;
    Check("a piece turned 90 degrees clockwise on the canvas lies at (frame angle - 90) in the model: the import's rotation is frame - rotation", Near(dirDeg, 30 - 90, 1e-6), $"({dirDeg:0.###} degrees)");

    // ---- structure: grid lines and columns of a turned roof are square in the plan
    {
        var pts = TurnedRect(30, 12, 30, 350.5, -120.25);
        var (bx0, by0, bx1, by1) = Box(pts);
        var f = RoofFrame.Fit(pts, bx0, by0, bx1, by1);
        // model-space points of the roof's own grid: three lines across (every 10 m along x) and two along (every 6 m along y), in roof-local metres
        (double X, double Y) M(double a, double b) { var r = Rot(a, b, 30); return (r.x + 350.5, r.y - 120.25); }
        var grids = new List<StructureGeometry.GridSegment>();
        foreach (var a in new[] { 0.0, 10.0, 20.0, 30.0 }) { var p0 = M(a, -3); var p1 = M(a, 15); grids.Add(new StructureGeometry.GridSegment("V" + a, p0.X, p0.Y, p1.X, p1.Y)); }
        foreach (var b in new[] { 0.0, 6.0, 12.0 }) { var p0 = M(-3, b); var p1 = M(33, b); grids.Add(new StructureGeometry.GridSegment("H" + b, p0.X, p0.Y, p1.X, p1.Y)); }
        var far = M(500, 500); grids.Add(new StructureGeometry.GridSegment("far", far.X, far.Y, far.X + 10, far.Y));
        var cols = new List<StructureGeometry.ColumnPoint>();
        foreach (var a in new[] { 0.0, 10.0, 20.0, 30.0 }) foreach (var b in new[] { 0.0, 6.0, 12.0 }) { var q = M(a, b); cols.Add(new StructureGeometry.ColumnPoint($"C{a}-{b}", q.X, q.Y)); }
        var farCol = M(300, 300); cols.Add(new StructureGeometry.ColumnPoint("far", farCol.X, farCol.Y));
        var dto = StructureGeometry.ToRoofLocal(grids, cols, f);
        var vertical = dto.GridLines!.Where(l => l.Name!.StartsWith("V")).ToList();
        var horizontal = dto.GridLines!.Where(l => l.Name!.StartsWith("H")).ToList();
        Check("the grid of a turned roof is exactly vertical and horizontal in the plan (no line is 'skewed' any more)", vertical.Count == 4 && horizontal.Count == 3 &&
              vertical.All(l => Near(l.StartM.XM, l.EndM.XM, 0.005)) && horizontal.All(l => Near(l.StartM.YM, l.EndM.YM, 0.005)), $"({dto.GridLines!.Count} lines)");
        Check("a grid line far from the roof is dropped; the others are cut to the roof's box (3 m of overhang gone)", dto.GridLines!.All(l => l.Name != "far") &&
              vertical.All(l => Near(Math.Min(l.StartM.YM, l.EndM.YM), 0, 0.26) && Near(Math.Max(l.StartM.YM, l.EndM.YM), 12, 0.26)));
        Check("the columns land on the grid's intersections, in the plan, and the far one is dropped", dto.Columns!.Count == 12 && dto.Columns!.All(c => new[] { 0.0, 10.0, 20.0, 30.0 }.Any(x => Near(c.XM, x, 0.005)) && new[] { 0.0, 6.0, 12.0 }.Any(y => Near(c.YM, y, 0.005))));
        var flipped = dto.Columns!.First(c => c.Label == "C0-0");
        Check("y is measured DOWN from the top: the column at the roof's origin corner is at the plan's bottom-left", Near(flipped.XM, 0, 0.005) && Near(flipped.YM, 12, 0.005));
    }

    // ---- features of a turned roof
    {
        var pts = TurnedRect(30, 12, 30, 350.5, -120.25);
        var (bx0, by0, bx1, by1) = Box(pts);
        var f = RoofFrame.Fit(pts, bx0, by0, bx1, by1);
        (double X, double Y) M(double a, double b) { var r = Rot(a, b, 30); return (r.x + 350.5, r.y - 120.25); }
        RoofFeaturesGeometry.PointM PM(double a, double b) { var q = M(a, b); return new(q.X, q.Y); }
        var outline = new[] { PM(0, 0), PM(30, 0), PM(30, 12), PM(0, 12) };
        var openings = new[] { new RoofFeaturesGeometry.OpeningLoop(new[] { PM(10, 4), PM(12, 4), PM(12, 6), PM(10, 6) }) };
        var e1 = M(5, 6); var e2 = M(31.5, 6); var e3 = M(60, 6);
        var entries = new[] { new RoofFeaturesGeometry.EntryRecord("stair", "Stair", e1.X, e1.Y, 1.2, 1), new RoofFeaturesGeometry.EntryRecord("door", "Beyond the edge", e2.X, e2.Y, 0.9, 2), new RoofFeaturesGeometry.EntryRecord("door", "Far", e3.X, e3.Y, 0.9, 3) };
        var w0 = M(0, 12); var w1 = M(30, 12);
        var walls = new[] { new RoofFeaturesGeometry.EdgeElement("parapet", "Parapet on the top edge", w0.X, w0.Y, w1.X, w1.Y, 0.6, 0.25) };
        var ob0 = M(20, 3); var ob1 = M(24, 3);
        var obstacles = new[] { new RoofFeaturesGeometry.EdgeElement("parapet", "Stair house", ob0.X, ob0.Y, ob1.X, ob1.Y, 3.0, 0.3) };
        var dr = M(3, 3); var drFar = M(80, 3);
        var drains = new[] { new RoofFeaturesGeometry.DrainRecord("drain", "Drain", dr.X, dr.Y, 9), new RoofFeaturesGeometry.DrainRecord("drain", "Far drain", drFar.X, drFar.Y, 10) };
        var dto = RoofFeaturesGeometry.Build(f, outline, openings, entries, walls, drains, null, Array.Empty<RoofFeaturesGeometry.LevelRecord>(), 12, 0, null, null, obstacles);
        Check("an opening keeps its size and lands in the plan (10 to 12 m along, 4 to 6 m up from the origin edge)", dto.Openings.Count == 1 && Near(dto.Openings[0].AreaM2, 4, 0.01) && Near(dto.Openings[0].WidthM, 2, 0.02) && Near(dto.Openings[0].XM, 10, 0.02) && Near(dto.Openings[0].YM, 12 - 6, 0.02));
        Check("the stair is on the roof, the door beyond the edge is kept but off it, the far door is not the roof's", dto.Entries.Count == 2 && dto.Entries.First(e => e.Kind == "stair").OnRoof && !dto.Entries.First(e => e.Kind == "door").OnRoof);
        Check("the outline has four edges of 30, 12, 30 and 12 m, and the parapet on the top edge makes exactly one of them a parapet", dto.Edges.Count == 4 && dto.Edges.Count(e => e.Kind == "parapet") == 1 && dto.Edges.Count(e => e.Kind == "open") == 3 &&
              dto.Edges.Where(e => e.Kind == "parapet").All(e => Near(e.StartM.YM, 0, 0.02) && Near(e.EndM.YM, 0, 0.02)), $"({string.Join(", ", dto.Edges.Select(e => e.Kind + " " + e.LengthM))})");
        Check("an obstacle keeps its length (4 m), height and thickness, and stands 9 m from the plan's bottom (3 m up from the origin edge)", dto.Obstacles.Count == 1 && Near(Math.Sqrt(Math.Pow(dto.Obstacles[0].EndM.XM - dto.Obstacles[0].StartM.XM, 2) + Math.Pow(dto.Obstacles[0].EndM.YM - dto.Obstacles[0].StartM.YM, 2)), 4, 0.02) &&
              Near(dto.Obstacles[0].StartM.YM, 12 - 3, 0.02) && Near(dto.Obstacles[0].EndM.YM, 12 - 3, 0.02) && Near(dto.Obstacles[0].StartM.XM, 20, 0.02));
        Check("the drain lands at (3, 9) and the far one is dropped", dto.Drains.Count == 1 && Near(dto.Drains[0].XM, 3, 0.02) && Near(dto.Drains[0].YM, 9, 0.02));
    }

    // ---- an L-shaped roof turned 20 degrees
    {
        var lLocal = new[] { (0.0, 0.0), (40.0, 0.0), (40.0, 10.0), (20.0, 10.0), (20.0, 20.0), (0.0, 20.0) };
        var pts = lLocal.Select(q => { var r = Rot(q.Item1, q.Item2, 20); return (X: r.x + 500, Y: r.y + 300); }).ToList();
        var (bx0, by0, bx1, by1) = Box(pts);
        var f = RoofFrame.Fit(pts, bx0, by0, bx1, by1);
        var plan = pts.Select(q => f.ToPlan(q.X, q.Y)).ToList();
        var area = Math.Abs(plan.Select((q, i) => q.X * plan[(i + 1) % plan.Count].Y - plan[(i + 1) % plan.Count].X * q.Y).Sum()) / 2;
        Check("an L turned 20 degrees: the plan follows it (20 degrees, 40 x 20 m) and the outline keeps its area", Near(f.AngleDeg, 20, 1e-6) && Near(f.Length, 40, 1e-6) && Near(f.Width, 20, 1e-6) && Near(area, 600, 1e-6), $"(angle {f.AngleDeg:0.###}, {f.Length:0.##} x {f.Width:0.##}, area {area:0.###})");
    }

    // ---- the contract: rotation_deg travels in the layout
    {
        var json = "{\"roof_context\":{\"length_m\":30,\"width_m\":12,\"rotation_deg\":30.5,\"world_origin_x_m\":350.5,\"world_origin_y_m\":-120.25}}";
        var layout = System.Text.Json.JsonSerializer.Deserialize<SportifyLayout>(json)!;
        var old = System.Text.Json.JsonSerializer.Deserialize<SportifyLayout>("{\"roof_context\":{\"length_m\":30,\"width_m\":12}}")!;
        Check("roof_context.rotation_deg is read; an older export without it is not turned", Near(layout.RoofContext!.RotationDeg, 30.5) && old.RoofContext!.RotationDeg == 0);
    }
}

// the "Push to Sportify" drop-down: what each part carries, and how a part pushed alone is laid onto the roof pushed before
{
    Console.WriteLine("\n===== push scopes, beams, walls, equipment =====");
    bool Near(double a, double b, double tol = 1e-6) => Math.Abs(a - b) <= tol;

    // ---- scopes
    Check("the scope's names round-trip and Everything is all eight parts", RoofPushScopes.FromNames(RoofPushScopes.Names(RoofPushScope.All)) == RoofPushScope.All && RoofPushScopes.Names(RoofPushScope.All).Count == 8 &&
          RoofPushScopes.IsEverything(RoofPushScope.All) && !RoofPushScopes.IsEverything(RoofPushScope.Roof | RoofPushScope.Structure));
    Check("a scope is described in words for the dialog", RoofPushScopes.Describe(RoofPushScope.Roof | RoofPushScope.Entries) == "roof outline and size, entries (stairs, lifts, doors)", RoofPushScopes.Describe(RoofPushScope.Roof | RoofPushScope.Entries));

    // ---- a turned roof (30 x 12 m at 30 degrees) with beams, walls and equipment
    (double X, double Y) Rot(double x, double y, double deg) { var t = deg * Math.PI / 180; return (x * Math.Cos(t) - y * Math.Sin(t), x * Math.Sin(t) + y * Math.Cos(t)); }
    (double X, double Y) M(double a, double b) { var r = Rot(a, b, 30); return (r.X + 350.5, r.Y - 120.25); }
    var corners = new[] { M(0, 0), M(30, 0), M(30, 12), M(0, 12) };
    var frame = RoofFrame.Fit(corners.ToList(), corners.Min(q => q.X), corners.Min(q => q.Y), corners.Max(q => q.X), corners.Max(q => q.Y));

    var beams = new[]
    {
        new StructureGeometry.BeamRecord("IPE 300", M(0, 6).X, M(0, 6).Y, M(30, 6).X, M(30, 6).Y, 0.15, 0.30, 11.2),            // a beam along the roof, in the middle
        new StructureGeometry.BeamRecord("HEA 200", M(10, -3).X, M(10, -3).Y, M(10, 15).X, M(10, 15).Y, 0.2, 0.19, 11.2),      // across it, overhanging both edges by 3 m
        new StructureGeometry.BeamRecord("far", M(200, 200).X, M(200, 200).Y, M(230, 200).X, M(230, 200).Y, 0.2, 0.3, 11.2),   // nowhere near
    };
    var walls = new[]
    {
        new StructureGeometry.WallRecord("Concrete wall", M(20, 0).X, M(20, 0).Y, M(20, 12).X, M(20, 12).Y, 0.25, 3.2, true),
        new StructureGeometry.WallRecord("Partition", M(5, 3).X, M(5, 3).Y, M(5, 9).X, M(5, 9).Y, 0.1, 3.0, false),
    };
    var dto = StructureGeometry.ToRoofLocal(new List<StructureGeometry.GridSegment>(), new List<StructureGeometry.ColumnPoint>(), frame, beams, walls);
    var along = dto.Beams!.First(b => b.Name == "IPE 300");
    var across = dto.Beams!.First(b => b.Name == "HEA 200");
    Check("beams keep their section, are cut to the roof's box, and the far one is dropped", dto.Beams!.Count == 2 && Near(along.WidthM, 0.15) && Near(along.DepthM, 0.30) && Near(along.TopElevationM, 11.2) &&
          Near(Math.Min(across.StartM!.YM, across.EndM!.YM), 0, 0.26) && Near(Math.Max(across.StartM!.YM, across.EndM!.YM), 12, 0.26));
    Check("a beam along the turned roof is horizontal in the plan, half way down it; the one across is vertical, 10 m from the left", Near(along.StartM!.YM, 6, 0.005) && Near(along.EndM!.YM, 6, 0.005) &&
          Near(across.StartM!.XM, 10, 0.005) && Near(across.EndM!.XM, 10, 0.005));
    Check("walls keep thickness, height and whether they bear", dto.Walls!.Count == 2 && dto.Walls!.First(w => w.Name == "Concrete wall").Bearing && !dto.Walls!.First(w => w.Name == "Partition").Bearing &&
          Near(dto.Walls!.First(w => w.Name == "Concrete wall").ThicknessM, 0.25) && Near(dto.Walls!.First(w => w.Name == "Concrete wall").HeightM, 3.2));
    var noExtras = StructureGeometry.ToRoofLocal(new List<StructureGeometry.GridSegment>(), new List<StructureGeometry.ColumnPoint>(), frame);
    Check("without beams or walls the structure block has none (a roof whose model has none is not given empty ones)", noExtras.Beams == null && noExtras.Walls == null);

    var equipment = new[]
    {
        // a 4 x 2.5 m unit, 1.8 m high, 300 kg-force, standing square to the roof (its width runs at the roof's own 30 degrees)
        new RoofFeaturesGeometry.EquipmentRecord("mechanical", "Air handler", M(10, 5.25).X, M(10, 5.25).Y, 4.0, 2.5, 30 * Math.PI / 180, 1.8, 2.94, 7001),
        new RoofFeaturesGeometry.EquipmentRecord("electrical", "Switchboard", M(25, 2).X, M(25, 2).Y, 0.8, 0.8, 30 * Math.PI / 180, 2.0, null, 7002),
        new RoofFeaturesGeometry.EquipmentRecord("mechanical", "Not on this roof", M(90, 90).X, M(90, 90).Y, 2, 2, 0, 1.0, null, 7003),
        // the same unit standing at 90 degrees to the roof: its plan box swaps its sides
        new RoofFeaturesGeometry.EquipmentRecord("mechanical", "Turned unit", M(20, 9).X, M(20, 9).Y, 4.0, 2.5, 120 * Math.PI / 180, 1.5, null, 7004),
    };
    var outline = corners.Select(q => new RoofFeaturesGeometry.PointM(q.X, q.Y)).ToList();
    var features = RoofFeaturesGeometry.Build(frame, outline, Array.Empty<RoofFeaturesGeometry.OpeningLoop>(), Array.Empty<RoofFeaturesGeometry.EntryRecord>(), Array.Empty<RoofFeaturesGeometry.EdgeElement>(),
        Array.Empty<RoofFeaturesGeometry.DrainRecord>(), null, Array.Empty<RoofFeaturesGeometry.LevelRecord>(), 12, 0, null, null, null, equipment);
    Check("equipment on the roof is kept (the far one is not), with its kind, name, height and weight", features.Equipment.Count == 3 && features.Equipment.Any(e => e.Kind == "mechanical" && e.Name == "Air handler" && Near(e.HeightM, 1.8) && e.WeightKn == 2.94) &&
          features.Equipment.Any(e => e.Kind == "electrical" && e.WeightKn == null));
    var unit = features.Equipment.First(e => e.Name == "Air handler");
    Check("a unit square to the turned roof is 4 x 2.5 m in the plan, centred at (10, 6.75) (y is measured down: 12 - 5.25)", Near(unit.XM, 10, 0.01) && Near(unit.YM, 12 - 5.25, 0.01) && Near(unit.WidthM, 4, 0.01) && Near(unit.DepthM, 2.5, 0.01),
          $"(at {unit.XM:0.##}, {unit.YM:0.##}; {unit.WidthM:0.##} x {unit.DepthM:0.##})");
    var turned = features.Equipment.First(e => e.Name == "Turned unit");
    Check("the same unit turned 90 degrees to the roof swaps its sides in the plan (2.5 x 4)", Near(turned.WidthM, 2.5, 0.01) && Near(turned.DepthM, 4, 0.01), $"({turned.WidthM:0.##} x {turned.DepthM:0.##})");
    Check("equipment ids are numbered by kind", features.Equipment.Select(e => e.Id).OrderBy(i => i).SequenceEqual(new[] { "electrical_1", "mechanical_1", "mechanical_2" }));

    // ---- the merge: a part pushed alone is laid onto the roof pushed before, the app always gets the roof whole
    string Roof(double ox, string extra) => "{\"roof\":{\"length_m\":30,\"width_m\":12,\"rotation_deg\":30,\"origin_x_m\":" + ox.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"origin_y_m\":-120.25,\"boundary_m\":[{\"x_m\":0,\"y_m\":0},{\"x_m\":30,\"y_m\":0},{\"x_m\":30,\"y_m\":12},{\"x_m\":0,\"y_m\":12}],\"origin_z_m\":12,\"source_element_name\":\"Roof\"" + extra + "}}";
    var full = Roof(350.5, ",\"structure\":{\"source\":\"revit\",\"grid_lines\":[{\"name\":\"A\"}],\"columns\":[]},\"features\":{\"source\":\"revit\",\"notes\":[\"first\"],\"entries\":[{\"id\":\"stair_1\"}],\"openings\":[{\"id\":\"opening_1\"}],\"edges\":[],\"obstacles\":[],\"drains\":[{\"id\":\"drain_1\"}],\"equipment\":[],\"levels\":[]}");
    var fullMerged = RoofPushMerge.Merge(null, full, RoofPushScope.All, out var keptFull);
    var fullNode = System.Text.Json.Nodes.JsonNode.Parse(fullMerged)!["roof"]!;
    Check("Everything is sent as it is, with the list of what the roof now has", !keptFull && fullNode["structure"] != null && ((System.Text.Json.Nodes.JsonArray)fullNode["pushed_scope"]!).Count == 8);

    var entriesOnly = Roof(350.5, ",\"features\":{\"source\":\"revit\",\"notes\":[\"second\"],\"entries\":[{\"id\":\"stair_1\"},{\"id\":\"door_1\"}],\"openings\":[],\"edges\":[],\"obstacles\":[],\"drains\":[],\"equipment\":[],\"levels\":[]}");
    var m1 = RoofPushMerge.Merge(fullMerged, entriesOnly, RoofPushScope.Roof | RoofPushScope.Entries, out var kept1);
    var r1 = System.Text.Json.Nodes.JsonNode.Parse(m1)!["roof"]!;
    Check("the entries pushed alone replace the entries and keep the rest (structure, openings, drains)", kept1 && ((System.Text.Json.Nodes.JsonArray)r1["features"]!["entries"]!).Count == 2 &&
          ((System.Text.Json.Nodes.JsonArray)r1["features"]!["openings"]!).Count == 1 && ((System.Text.Json.Nodes.JsonArray)r1["features"]!["drains"]!).Count == 1 && r1["structure"] != null);
    Check("the notes of both pushes are kept, once each", string.Join("|", ((System.Text.Json.Nodes.JsonArray)r1["features"]!["notes"]!).Select(n => n!.ToString())) == "first|second");

    var structureOnly = Roof(350.5, ",\"structure\":{\"source\":\"revit\",\"grid_lines\":[{\"name\":\"B\"},{\"name\":\"C\"}],\"columns\":[],\"beams\":[{\"name\":\"IPE\"}]}");
    var m2 = RoofPushMerge.Merge(m1, structureOnly, RoofPushScope.Roof | RoofPushScope.Structure, out _);
    var r2 = System.Text.Json.Nodes.JsonNode.Parse(m2)!["roof"]!;
    Check("the structure pushed alone replaces the structure and leaves every feature alone", ((System.Text.Json.Nodes.JsonArray)r2["structure"]!["grid_lines"]!).Count == 2 && r2["structure"]!["beams"] != null &&
          ((System.Text.Json.Nodes.JsonArray)r2["features"]!["entries"]!).Count == 2 && ((System.Text.Json.Nodes.JsonArray)r2["features"]!["drains"]!).Count == 1);

    var outlineOnly = Roof(350.5, "");
    var m3 = RoofPushMerge.Merge(m2, outlineOnly, RoofPushScope.Roof, out var kept3);
    var r3 = System.Text.Json.Nodes.JsonNode.Parse(m3)!["roof"]!;
    Check("the outline pushed alone refreshes the outline and keeps everything else of the same roof", kept3 && r3["structure"] != null && r3["features"]!["entries"] != null && r3["boundary_m"] != null);
    Check("what the roof has so far is listed", ((System.Text.Json.Nodes.JsonArray)r3["pushed_scope"]!).Select(n => n!.ToString()).OrderBy(x => x).SequenceEqual(new[] { "entries", "roof", "structure", "openings", "drains", "edge", "equipment", "slab_levels" }.Where(x => x == "entries" || x == "roof" || x == "structure" || x == "openings" || x == "drains" || x == "edge" || x == "equipment" || x == "slab_levels").OrderBy(x => x)));

    var otherRoof = Roof(999.0, ",\"features\":{\"source\":\"revit\",\"entries\":[{\"id\":\"stair_9\"}],\"openings\":[],\"edges\":[],\"obstacles\":[],\"drains\":[],\"equipment\":[],\"levels\":[]}");
    var m4 = RoofPushMerge.Merge(m3, otherRoof, RoofPushScope.Roof | RoofPushScope.Entries, out var kept4);
    var r4 = System.Text.Json.Nodes.JsonNode.Parse(m4)!["roof"]!;
    Check("a push of ANOTHER roof starts again: the old roof's structure and features are gone", !kept4 && r4["structure"] == null && ((System.Text.Json.Nodes.JsonArray)r4["features"]!["entries"]!).Count == 1 &&
          ((System.Text.Json.Nodes.JsonArray)r4["pushed_scope"]!).Count == 2);
    Check("garbage from the server does not stop a push", RoofPushMerge.Merge("not json", entriesOnly, RoofPushScope.Roof | RoofPushScope.Entries, out var kept5).Length > 10 && !kept5);

    // ---- the contract: beams, walls and equipment are read from a layout
    var layout = System.Text.Json.JsonSerializer.Deserialize<SportifyLayout>("{\"roof_context\":{\"length_m\":30,\"width_m\":12,\"features\":{\"equipment\":[{\"id\":\"mechanical_1\",\"kind\":\"mechanical\",\"name\":\"AHU\",\"x_m\":10,\"y_m\":5,\"width_m\":4,\"depth_m\":2,\"height_m\":1.8,\"weight_kn\":3.5}]}},\"structure\":{\"beams\":[{\"name\":\"IPE\",\"start_m\":{\"x_m\":0,\"y_m\":6},\"end_m\":{\"x_m\":30,\"y_m\":6},\"width_m\":0.15,\"depth_m\":0.3,\"top_elevation_m\":11.2}],\"walls\":[{\"name\":\"W\",\"bearing\":true,\"thickness_m\":0.25}]}}")!;
    Check("equipment, beams and walls are read back from the layout", layout.RoofContext!.Features!.Equipment.Count == 1 && layout.RoofContext.Features.Equipment[0].WeightKn == 3.5 && layout.Structure!.Beams![0].DepthM == 0.3 && layout.Structure.Walls![0].Bearing);
}

// ---- the log and the family template search: what the import's honesty rests on
{
    Console.WriteLine("\n===== the add-in's log =====");
    var logDir = Path.Combine(Path.GetTempPath(), "sportify-log-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    SportifyLog.UseDirectory(logDir);
    SportifyLog.Info("check", "hello");
    SportifyLog.Warn("check", "careful");
    SportifyLog.Error("check", "it broke", new InvalidOperationException("the reason"));
    SportifyLog.Block("import", "report:", "line one\nline two\r\nline three");
    var text = File.ReadAllText(SportifyLog.CurrentFile);
    Check("the log is a file per day under the folder it was pointed at", SportifyLog.CurrentFile.StartsWith(logDir) && Path.GetFileName(SportifyLog.CurrentFile).StartsWith("addin-") && File.Exists(SportifyLog.CurrentFile));
    Check("every entry has a UTC time, a level and an area", System.Text.RegularExpressions.Regex.Matches(text, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z (INFO |WARN |ERROR) \[[a-z\-]+\] ", System.Text.RegularExpressions.RegexOptions.Multiline).Count == 4);
    Check("an exception is logged with its type, its message and its stack", text.Contains("InvalidOperationException: the reason"));
    Check("a report keeps its lines under one entry, indented", text.Contains("        line two") && text.Contains("        line three"));

    var old = Path.Combine(logDir, "addin-20200101.log");
    File.WriteAllText(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
    var fresh = Path.Combine(logDir, "addin-20260101.log");
    File.WriteAllText(fresh, "recent"); File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-3));
    SportifyLog.UseDirectory(logDir);               // forgets that it pruned today
    SportifyLog.Info("check", "prune");
    Check("files older than two weeks are removed, newer ones are kept", !File.Exists(old) && File.Exists(fresh));

    var blocker = Path.Combine(Path.GetTempPath(), "sportify-log-file-" + Guid.NewGuid().ToString("N").Substring(0, 8));
    File.WriteAllText(blocker, "a file where the log folder should be");
    SportifyLog.UseDirectory(Path.Combine(blocker, "logs"));
    var threw = false;
    try { SportifyLog.Error("check", "nowhere to write", new Exception("x")); } catch (Exception) { threw = true; }
    Check("a log that cannot be written never throws (it must not be what breaks an import)", !threw);
    SportifyLog.UseDirectory(null);
    try { Directory.Delete(logDir, true); File.Delete(blocker); } catch (IOException) { }

    Console.WriteLine("\n===== finding the Generic Model family template in any language =====");
    string[] Rank(params string[] names) => FamilyTemplateRanking.Rank(names.Select(n => @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\" + n)).Select(Path.GetFileName).ToArray()!;
    var en = Rank(@"English\Metric Generic Model wall based.rft", @"English\Metric Generic Model face based.rft", @"English\Metric Generic Model.rft", @"English\Metric Generic Model line based.rft",
                  @"English\Metric Generic Model work plane based.rft", @"English\Metric Generic Model Adaptive.rft", @"English\Metric Door.rft", @"English\Metric Furniture.rft");
    Check("English: the plain template first, the host-based variants and other categories never", en.Length >= 1 && en[0] == "Metric Generic Model.rft" && en.Length == 1, string.Join(" | ", en));
    var de = Rank(@"German\Allgemeines Modell wandbasiert.rft", @"German\Allgemeines Modell flächenbasiert.rft", @"German\Allgemeines Modell.rft", @"German\Allgemeines Modell arbeitsebenenbasiert.rft",
                  @"German\Allgemeines Modell deckenbasiert.rft", @"German\Tür.rft", @"German\Möbel.rft");
    Check("German (Allgemeines Modell): the plain one, none of the wand-/flächen-/decken-/arbeitsebenenbasiert ones", de.Length == 1 && de[0] == "Allgemeines Modell.rft", string.Join(" | ", de));
    var fr = Rank(@"French\Modèle générique métrique.rft", @"French\Modèle générique métrique basé sur un mur.rft", @"French\Modèle générique métrique basé sur une face.rft", @"French\Porte métrique.rft");
    Check("French (Modèle générique): the plain one, accents and all, not the 'basé sur' ones", fr.Length == 1 && fr[0] == "Modèle générique métrique.rft", string.Join(" | ", fr));
    var es = Rank(@"Spanish\Modelo genérico métrico.rft", @"Spanish\Modelo genérico métrico basado en pared.rft", @"Spanish\Puerta métrica.rft");
    var it = Rank(@"Italian\Modello generico metrico.rft", @"Italian\Modello generico metrico basato su parete.rft", @"Italian\Porta metrica.rft");
    Check("Spanish and Italian the same", es.Length == 1 && es[0] == "Modelo genérico métrico.rft" && it.Length == 1 && it[0] == "Modello generico metrico.rft", string.Join(" | ", es) + " / " + string.Join(" | ", it));
    var both = FamilyTemplateRanking.Rank(new[] { @"C:\x\English-Imperial\Generic Model.rft", @"C:\x\English\Metric Generic Model.rft" }).Select(Path.GetFileName).ToArray();
    Check("the metric template is preferred over the imperial one, but the imperial one is still a candidate", both.Length == 2 && both[0] == "Metric Generic Model.rft", string.Join(" | ", both));
    Check("a language nobody listed gives no candidates (Revit's category check is then the way, on every template)", Rank(@"Klingon\Tlhab.rft").Length == 0);
    Check("Normalise: accents and separators do not matter", FamilyTemplateRanking.Normalise("Modèle_générique-métrique") == "modele generique metrique");
}

{
    Console.WriteLine("\n===== the ribbon (RibbonLayout, RibbonIconData) and the rules of the BIM & Documentation panel (BimRules) =====");
    // the add-in's sources, found from here
    string? sources = null;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && sources == null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "SportfyRevit", "SportfyRevit");
        if (File.Exists(Path.Combine(candidate, "SportfyRevitApp.cs"))) sources = candidate;
    }
    Check("the add-in's sources are found from the check", sources != null);
    var allSources = sources == null ? new Dictionary<string, string>() : Directory.GetFiles(sources, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
        .ToDictionary(f => f, File.ReadAllText);
    var commandClasses = new Dictionary<string, string>();       // class name -> "public" or "other", for every class that implements IExternalCommand
    foreach (var kv in allSources)
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(kv.Value, @"(?<vis>public\s+|internal\s+)?(?:sealed\s+)?class\s+(?<name>\w+)\s*:\s*IExternalCommand\b"))
            commandClasses[m.Groups["name"].Value] = m.Groups["vis"].Value.Trim() == "public" ? "public" : "other";

    var panels = RibbonLayout.Panels;
    var everyButton = panels.SelectMany(pn => pn.Entries).SelectMany(e => e switch
    {
        RibbonButtonSpec b => new[] { b },
        RibbonPulldownSpec pd => pd.Items.ToArray(),
        _ => Array.Empty<RibbonButtonSpec>(),
    }).ToList();
    var everyName = panels.SelectMany(pn => pn.Entries).SelectMany(e => e switch
    {
        RibbonButtonSpec b => new[] { b.InternalName },
        RibbonPulldownSpec pd => pd.Items.Select(i => i.InternalName).Append(pd.InternalName).ToArray(),
        _ => Array.Empty<string>(),
    }).ToList();

    Check("the panels are, in order: App & Data Import, Algorithmic Analysis, Simulation & Analytics, Kinetics, BIM & Documentation, Data Export / Deliverables",
          panels.Select(pn => pn.Name).SequenceEqual(new[] { "App & Data Import", "Algorithmic Analysis", "Simulation & Analytics", "Kinetics", "BIM & Documentation", "Data Export / Deliverables" }));
    Check("every internal name (buttons, drop-downs, items) is unique: Revit refuses a second item of the same name in one tab", everyName.Distinct().Count() == everyName.Count,
          string.Join(", ", everyName.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key)));
    Check("every command class the ribbon names exists and implements IExternalCommand", everyButton.All(b => commandClasses.ContainsKey(b.CommandClass)),
          string.Join(", ", everyButton.Where(b => !commandClasses.ContainsKey(b.CommandClass)).Select(b => b.CommandClass)));
    Check("...and is public (Revit cannot load a command that is not)", everyButton.All(b => commandClasses.GetValueOrDefault(b.CommandClass) == "public"),
          string.Join(", ", everyButton.Where(b => commandClasses.GetValueOrDefault(b.CommandClass) != "public").Select(b => b.CommandClass)));
    Check("every button and drop-down has text and a tooltip, and every tooltip uses only the {APP_URL} placeholder",
          everyButton.All(b => b.Text.Length > 0 && b.Tooltip.Length > 10 && System.Text.RegularExpressions.Regex.Matches(b.Tooltip, @"\{[^}]*\}").All(m => m.Value == "{APP_URL}"))
          && panels.SelectMany(pn => pn.Entries).OfType<RibbonPulldownSpec>().All(pd => pd.Text.Length > 0 && pd.Tooltip.Length > 10));
    var icons = everyButton.Select(b => b.Icon).Concat(panels.SelectMany(pn => pn.Entries).OfType<RibbonPulldownSpec>().Select(pd => pd.Icon)).ToList();
    Check("every button, item and drop-down has an icon that exists", icons.All(i => RibbonIconData.Paths.ContainsKey(i)), string.Join(", ", icons.Where(i => !RibbonIconData.Paths.ContainsKey(i)).Distinct()));

    // the algorithmic analyses (rules and calculations, no simulation engine) are one group of their own, and the physical (Unity based) ones are not in it
    var algorithmic = panels.First(pn => pn.Name == "Algorithmic Analysis");
    var algorithmicClasses = algorithmic.Entries.OfType<RibbonButtonSpec>().Select(b => b.CommandClass).ToList();
    var physicalPanel = panels.First(pn => pn.Name == "Simulation & Analytics");
    var physicalClasses = physicalPanel.Entries.SelectMany(e => e switch { RibbonButtonSpec b => new[] { b.CommandClass }, RibbonPulldownSpec pd => pd.Items.Select(i => i.CommandClass).ToArray(), _ => Array.Empty<string>() }).ToList();
    Check("Algorithmic Analysis holds exactly the four rule-based analyses: fire safety, carbon impact, LCA, accessibility",
          algorithmicClasses.OrderBy(c => c).SequenceEqual(new[] { "AnalyzeAccessibilityCommand", "AnalyzeCarbonImpactCommand", "AnalyzeFireSafetyCommand", "AnalyzeLcaCommand" }));
    Check("...and the physical (Unity based) analyses stay independent of it: none of them in Algorithmic Analysis, none of the algorithmic ones in Simulation & Analytics",
          !algorithmicClasses.Intersect(physicalClasses).Any() && physicalClasses.ToHashSet().IsSupersetOf(new[] { "AnalyzeStructuralLoadsCommand", "AnalyzeDynamicLoadsCommand", "AnalyzeSunShadeCommand", "AnalyzeWindErosionRiskCommand", "SimulateSoilPercolationCommand", "SimulateBallTrajectoriesCommand", "SendPhysicalAnalysisToWebCommand" })
          && !physicalClasses.Any(c => algorithmicClasses.Contains(c)));

    // the layout of the request: Simulation & Analytics
    var sim = panels.First(pn => pn.Name == "Simulation & Analytics");
    var structural = sim.Entries.OfType<RibbonPulldownSpec>().FirstOrDefault(pd => pd.Text == "Structural");
    var environmental = sim.Entries.OfType<RibbonPulldownSpec>().FirstOrDefault(pd => pd.Text == "Environmental");
    Check("Simulation & Analytics has a Structural drop-down: Run Bay Utilization Check (AnalyzeStructuralLoadsCommand), Dynamic Frequency & Vibration (AnalyzeDynamicLoadsCommand)",
          structural != null && structural.Items.Select(i => (i.Text, i.CommandClass)).SequenceEqual(new[] { ("Run Bay Utilization Check", "AnalyzeStructuralLoadsCommand"), ("Dynamic Frequency & Vibration", "AnalyzeDynamicLoadsCommand") }));
    Check("...and an Environmental one that starts with Sun & Shade Analysis (AnalyzeSunShadeCommand) and the wind analysis (AnalyzeWindErosionRiskCommand)",
          environmental != null && environmental.Items.Count >= 2 && environmental.Items[0].CommandClass == "AnalyzeSunShadeCommand" && environmental.Items[0].Text == "Sun & Shade Analysis"
          && environmental.Items[1].CommandClass == "AnalyzeWindErosionRiskCommand");
    Check("the wind button is not called \"comfort\": the analysis checks uplift, overturning and erosion", environmental != null && !environmental.Items.Any(i => i.Text.Contains("Comfort", StringComparison.OrdinalIgnoreCase)));

    // BIM & Documentation
    var bim = panels.First(pn => pn.Name == "BIM & Documentation");
    var schedulesButton = bim.Entries.OfType<RibbonButtonSpec>().FirstOrDefault(b => b.CommandClass == "GenerateRevitSchedulesCommand");
    var filtersButton = bim.Entries.OfType<RibbonButtonSpec>().FirstOrDefault(b => b.CommandClass == "ApplyViewFiltersCommand");
    var phasing = bim.Entries.OfType<RibbonPulldownSpec>().FirstOrDefault(pd => pd.Text.Replace("\n", " ") == "Phasing & Worksets");
    Check("BIM & Documentation: Generate Schedules with the requested tooltip", schedulesButton != null && schedulesButton.Text.Replace("\n", " ") == "Generate Schedules"
          && schedulesButton.Tooltip == "Creates automated Equipment Takeoff and Green Roof Build-up schedules.");
    Check("...Apply View Filters", filtersButton != null && filtersButton.Text.Replace("\n", " ") == "Apply View Filters" && filtersButton.Tooltip.Contains("Zone Types"));
    Check("...and a Phasing & Worksets drop-down: Set Up Phases & Worksets, Batch Assign Phasing, Organize Multi-Worksets, Import Iterations as Design Options, Show Iteration",
          phasing != null && phasing.Items.Select(i => (i.Text, i.CommandClass)).SequenceEqual(new[]
          {
              ("Set Up Phases & Worksets", "SetUpSportifyPhasesCommand"), ("Batch Assign Phasing", "AssignPhasingCommand"), ("Organize Multi-Worksets", "AssignWorksetsCommand"),
              ("Import Iterations as Design Options", "ImportIterationsAsOptionsCommand"), ("Show Iteration", "SwitchIterationCommand"),
          }));

    // nothing that was on the ribbon before is gone, and the CSV schedule command keeps its name
    var before = new Dictionary<string, string>
    {
        ["OpenSportifyApp"] = "OpenSportifyAppCommand", ["ImportSportifyLayout"] = "ImportSportifyLayoutCommand", ["LoadFamilies"] = "LoadFamiliesCommand", ["ImportDxf"] = "ImportDxfCommand",
        ["SetSunAndLocation"] = "SetSunAndLocationCommand", ["ToggleAutoImport"] = "ToggleAutoImportCommand", ["AnalyzeFireSafety"] = "AnalyzeFireSafetyCommand",
        ["AnalyzeCarbonImpact"] = "AnalyzeCarbonImpactCommand", ["AnalyzeLca"] = "AnalyzeLcaCommand", ["AnalyzeAccessibility"] = "AnalyzeAccessibilityCommand",
        ["SendPhysicalAnalysisToWeb"] = "SendPhysicalAnalysisToWebCommand", ["SimulateBallTrajectories"] = "SimulateBallTrajectoriesCommand", ["AnalyzeStructuralLoads"] = "AnalyzeStructuralLoadsCommand",
        ["AnalyzeDynamicLoads"] = "AnalyzeDynamicLoadsCommand", ["AnalyzeSunShade"] = "AnalyzeSunShadeCommand", ["AnalyzeWindErosionRisk"] = "AnalyzeWindErosionRiskCommand",
        ["SimulateSoilPercolation"] = "SimulateSoilPercolationCommand", ["GenerateAnalysisReport"] = "GenerateAnalysisReportCommand", ["GenerateFunctionalDiagrams"] = "GenerateFunctionalDiagramsCommand",
        ["GenerateSchedules"] = "GenerateSchedulesCommand", ["OpenSportifyFolder"] = "OpenSportifyFolderCommand",
    };
    var missing = before.Where(kv => !everyButton.Any(b => b.InternalName == kv.Key && b.CommandClass == kv.Value)).Select(kv => kv.Key).ToList();
    Check("all 21 buttons the ribbon had before are still on it, with the same internal name and the same command class (only re-mounted)", missing.Count == 0, string.Join(", ", missing));
    Check("the CSV export keeps its class name (GenerateSchedulesCommand) and the native schedules have their own (GenerateRevitSchedulesCommand)",
          commandClasses.ContainsKey("GenerateSchedulesCommand") && commandClasses.ContainsKey("GenerateRevitSchedulesCommand"));

    // the four new commands, as requested
    var bimSource = allSources.FirstOrDefault(kv => kv.Key.EndsWith("BimCommands.cs")).Value ?? "";
    foreach (var cls in new[] { "GenerateRevitSchedulesCommand", "ApplyViewFiltersCommand", "AssignPhasingCommand", "AssignWorksetsCommand" })
        Check($"{cls} is in Commands/BimCommands.cs, [Transaction(TransactionMode.Manual)], and catches its own errors",
              System.Text.RegularExpressions.Regex.IsMatch(bimSource, @"\[Transaction\(TransactionMode\.Manual\)\][\s\S]{0,80}?class\s+" + cls + @"\s*:\s*IExternalCommand")
              && System.Text.RegularExpressions.Regex.IsMatch(bimSource, "class " + cls + @"[\s\S]*?catch \(Exception ex\)"));
    Check("GenerateRevitSchedulesCommand is also [Regeneration(RegenerationOption.Manual)] and calls BimScheduleBuilder.CreateSportifySchedules(doc, null) inside a transaction",
          bimSource.Contains("[Regeneration(RegenerationOption.Manual)]") && bimSource.Contains("BimScheduleBuilder.CreateSportifySchedules(doc, null)") && bimSource.Contains("new Transaction(doc, \"Sportify: generate schedules\")"));
    Check("ApplyViewFiltersCommand calls ViewFilterManager.ApplySportifyViewFilters(doc, doc.ActiveView) inside a transaction", bimSource.Contains("ViewFilterManager.ApplySportifyViewFilters(doc, doc.ActiveView)") && bimSource.Contains("new Transaction(doc, \"Sportify: apply view filters\")"));

    // OnStartup: the ribbon is built from the layout, entry by entry, and OnStartup still returns Succeeded
    var appSource = sources == null ? "" : File.ReadAllText(Path.Combine(sources, "SportfyRevitApp.cs"));
    Check("OnStartup builds the ribbon from RibbonLayout, each entry inside its own try/catch, and returns Result.Succeeded",
          appSource.Contains("BuildRibbon(application);") && appSource.Contains("try { AddEntry(panel, assembly, entry); }") && appSource.Contains("return Result.Succeeded;"));

    // the icons: every path is well formed SVG path data (commands with the right number of numbers)
    var arity = new Dictionary<char, int> { ['M'] = 2, ['L'] = 2, ['H'] = 1, ['V'] = 1, ['C'] = 6, ['S'] = 4, ['Q'] = 4, ['T'] = 2, ['A'] = 7, ['Z'] = 0 };
    string? Malformed(string d)
    {
        var tokens = System.Text.RegularExpressions.Regex.Matches(d, @"[MmLlHhVvCcSsQqTtAaZz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?");
        var rest = System.Text.RegularExpressions.Regex.Replace(d, @"[MmLlHhVvCcSsQqTtAaZz]|-?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?|[\s,]", "");
        if (rest.Length > 0) return "stray text \"" + rest + "\"";
        char cmd = ' '; int count = 0; bool first = true;
        foreach (System.Text.RegularExpressions.Match t in tokens)
        {
            if (char.IsLetter(t.Value[0]))
            {
                if (cmd != ' ' && count % Math.Max(1, arity[char.ToUpperInvariant(cmd)]) != 0) return "wrong number of numbers for " + cmd;
                if (cmd != ' ' && arity[char.ToUpperInvariant(cmd)] > 0 && count == 0) return cmd + " has no numbers";
                cmd = t.Value[0]; count = 0;
                if (first && char.ToUpperInvariant(cmd) != 'M') return "does not start with M";
                first = false;
            }
            else count++;
        }
        if (cmd != ' ' && arity[char.ToUpperInvariant(cmd)] > 0 && (count == 0 || count % arity[char.ToUpperInvariant(cmd)] != 0)) return "wrong number of numbers for " + cmd;
        return null;
    }
    var badIcons = RibbonIconData.Paths.Select(kv => (kv.Key, Problem: Malformed(kv.Value))).Where(x => x.Problem != null).ToList();
    Check($"all {RibbonIconData.Paths.Count} icon paths are well formed (known commands, the right number of numbers, start with M)", badIcons.Count == 0, string.Join("; ", badIcons.Select(x => x.Key + ": " + x.Problem)));
    Check("the checker itself catches a bad path", Malformed("M1 2 L3") != null && Malformed("L1 2") != null && Malformed("M1 2 x") != null && Malformed("M1 2 l3 4 h5") == null);

    // the rules
    Check("workset rules follow the import: floors and planting on Gardens, courts, activities and furniture on Sports, everything else (outline, paths, entries) on Combine",
          BimRules.WorksetFor(BimRules.ElementKind.Floor, null, false) == "Gardens" && BimRules.WorksetFor(BimRules.ElementKind.FamilyInstance, "field", false) == "Sports"
          && BimRules.WorksetFor(BimRules.ElementKind.FamilyInstance, "furniture", false) == "Sports" && BimRules.WorksetFor(BimRules.ElementKind.FamilyInstance, "activity", false) == "Sports"
          && BimRules.WorksetFor(BimRules.ElementKind.FamilyInstance, "vegetation", false) == "Gardens" && BimRules.WorksetFor(BimRules.ElementKind.FamilyInstance, "Garden", false) == "Gardens"
          && BimRules.WorksetFor(BimRules.ElementKind.FamilyInstance, null, true) == "Gardens" && BimRules.WorksetFor(BimRules.ElementKind.Other, null, false) == "Combine");
    Check("the three workset names are the ones SportifyLayoutBuilder makes", BimRules.WorksetNames.SequenceEqual(new[] { "Sports", "Gardens", "Combine" })
          && allSources.Any(kv => kv.Key.EndsWith("SportifyLayoutBuilder.cs") && kv.Value.Contains("new[] { \"Sports\", \"Gardens\", \"Combine\" }")));
    Check("a Sportify floor type is one whose name starts with \"Sportify - \" (as SportifyFloorTypeBuilder names them), and only that",
          BimRules.IsSportifyTypeName("Sportify - Gravel") && !BimRules.IsSportifyTypeName("Generic 150mm") && !BimRules.IsSportifyTypeName("sportify - x") && !BimRules.IsSportifyTypeName(null)
          && allSources.Any(kv => kv.Key.EndsWith("SportifyFloorTypeBuilder.cs") && kv.Value.Contains("Sportify - {assembly.Provider}")));
    var zoneColours = Enumerable.Range(0, BimRules.ZonePaletteSize).Select(BimRules.ColorForZoneType).Distinct().Count();
    Check("the zone-type palette has distinct colours and repeats after its last (and never fails on a negative index)", zoneColours == BimRules.ZonePaletteSize && BimRules.ColorForZoneType(BimRules.ZonePaletteSize) == BimRules.ColorForZoneType(0) && BimRules.ColorForZoneType(-1) == BimRules.ColorForZoneType(BimRules.ZonePaletteSize - 1));
    var kinds = new[] { "field", "activity", "garden", "vegetation", "furniture" }.Select(BimRules.ColorForCategory).Distinct().Count();
    Check("each kind of piece has its own colour, and an unknown one a grey", kinds == 5 && BimRules.ColorForCategory("nothing") == (150, 150, 150) && BimRules.ColorForCategory(null) == (150, 150, 150));
    Check("filter and schedule names lose the characters Revit refuses, and keep the words", BimRules.SafeName("Sportify Zone - Bauder {extensive}: 69|mm") == "Sportify Zone - Bauder extensive 69 mm");
    // the icons as logos: every one has a group colour, an outline and (mostly) a tint that are well formed, and each panel's icons are in the panel's own colour
    Check("every icon belongs to a known colour group and has a well-formed outline", RibbonIconData.Icons.All(kv => RibbonIconData.GroupColors.ContainsKey(kv.Value.Group) && Malformed(kv.Value.Stroke) == null),
          string.Join("; ", RibbonIconData.Icons.Where(kv => !RibbonIconData.GroupColors.ContainsKey(kv.Value.Group) || Malformed(kv.Value.Stroke) != null).Select(kv => kv.Key)));
    var badFill = RibbonIconData.Icons.Where(kv => kv.Value.Fill != null && Malformed(kv.Value.Fill) != null).Select(kv => kv.Key + ": " + Malformed(kv.Value.Fill!)).ToList();
    Check($"...and the tint shapes ({RibbonIconData.Icons.Count(kv => kv.Value.Fill != null)} of {RibbonIconData.Icons.Count} icons have one) are well formed too", badFill.Count == 0, string.Join("; ", badFill));
    Check("the five colour groups are five different colours", RibbonIconData.GroupColors.Values.Distinct().Count() == 5 && RibbonIconData.GroupColors.Count == 5);
    var panelGroup = new Dictionary<string, string> { ["App & Data Import"] = "setup", ["Algorithmic Analysis"] = "algorithmic", ["Simulation & Analytics"] = "physical", ["Kinetics"] = "physical", ["BIM & Documentation"] = "bim", ["Data Export / Deliverables"] = "export" };
    var offColour = new List<string>();
    foreach (var pn in panels)
        foreach (var e in pn.Entries)
        {
            var named = e switch { RibbonButtonSpec b => new[] { b.Icon }, RibbonPulldownSpec pd => pd.Items.Select(i => i.Icon).Append(pd.Icon).ToArray(), _ => Array.Empty<string>() };
            foreach (var icon in named)
                if (RibbonIconData.Icons.TryGetValue(icon, out var spec) && spec.Group != panelGroup[pn.Name]) offColour.Add(pn.Name + ": " + icon + " is " + spec.Group);
        }
    Check("every icon on a panel is in that panel's colour (setup slate, algorithmic blue, physical teal, BIM amber, export violet)", offColour.Count == 0, string.Join("; ", offColour));
    Check("the Sportify mark (Open Sportify App) is the one badge icon", panels.First().Entries.OfType<RibbonButtonSpec>().First().Icon == "app" && RibbonIconData.Icons["app"].Badge && RibbonIconData.Icons.Count(kv => kv.Value.Badge) == 1);

    // Push to Sportify: the drop-down and each of its nine items have an icon that exists
    var pushMap = System.Text.RegularExpressions.Regex.Matches(appSource, @"\[""(?<item>Push\w+)""\]\s*=\s*""(?<icon>\w+)""").Select(m => (Item: m.Groups["item"].Value, Icon: m.Groups["icon"].Value)).ToList();
    var pushItems = System.Text.RegularExpressions.Regex.Matches(appSource, @"Item\(""(?<name>Push\w+)"",").Select(m => m.Groups["name"].Value).Distinct().ToList();
    Check("every item of the Push to Sportify menu (" + pushItems.Count + ") has its own icon, and the icons exist", pushItems.Count == 9 && pushItems.All(n => pushMap.Any(x => x.Item == n)) && pushMap.All(x => RibbonIconData.Icons.ContainsKey(x.Icon)) && pushMap.Select(x => x.Icon).Distinct().Count() == pushMap.Count,
          string.Join(", ", pushItems.Where(n => !pushMap.Any(x => x.Item == n))));
    Check("...and the menu itself has the \"push\" icon", appSource.Contains("RibbonIcons.Large(\"push\")") && RibbonIconData.Icons.ContainsKey("push"));

    // the duplicate views that carry the filters
    Check("a duplicate view is named after the view it was made from: \"Level 1\" -> \"Level 1 - Sportify Zone Types\"", BimRules.SportifyViewName("Level 1", "Zone Types") == "Level 1 - Sportify Zone Types");
    Check("...never chained: a duplicate of a duplicate gets the same name as a duplicate of the original", BimRules.SportifyViewName("Level 1 - Sportify Piece Kinds", "Zone Types") == "Level 1 - Sportify Zone Types");
    Check("...and loses the characters Revit refuses in a view name (the default 3D view is \"{3D}\")", BimRules.SportifyViewName("{3D}", "Piece Kinds") == "3D - Sportify Piece Kinds");
    Check("Apply View Filters makes duplicates and leaves the view alone (ViewFilterManager duplicates, then filters the duplicate)", allSources.Any(kv => kv.Key.EndsWith("ViewFilterManager.cs") && kv.Value.Contains("source.Duplicate(") && kv.Value.Contains("var target = duplicate ? DuplicateFor(doc, view, group, notes) : view;")));
    // the pane's map: OpenStreetMap refuses tiles (and Nominatim searches) from a request with no Referer, so the add-in's web server must not send "no-referrer"
    var webServer = allSources.FirstOrDefault(kv => kv.Key.EndsWith("StaticWebServer.cs")).Value ?? "";
    Check("the add-in's web server lets the origin go to other sites (strict-origin-when-cross-origin), so OpenStreetMap's tiles and address search accept the app; never no-referrer",
          webServer.Contains("\"Referrer-Policy\", \"strict-origin-when-cross-origin\"") && !webServer.Contains("\"Referrer-Policy\", \"no-referrer\""));
    // Functional Diagrams and the report do not need worksets: what there is to draw is decided by what the import created
    var diagrams = allSources.FirstOrDefault(kv => kv.Key.EndsWith("GenerateFunctionalDiagramsCommand.cs")).Value ?? "";
    Check("Functional Diagrams works without worksets: what there is to draw is decided by the import's ledger, not doc.IsWorkshared, and every piece is styled (grey, labelled) and the circulation bold red the same way whether or not the project has worksets — nothing is hidden by view to fake that",
          diagrams.Contains("SportifyElementScan.Find(doc).IsEmpty") && !diagrams.Contains("HideAllButCombine") && diagrams.Contains("pieceOv.SetProjectionLineColor(gray)"));
    Check("Organize Multi-Worksets asks before turning worksharing on (a choice with a default of leaving the project alone) instead of only refusing",
          bimSource.Contains("Turn worksharing on and organize Sportify on worksets") && bimSource.Contains("ask.DefaultButton = TaskDialogResult.CommandLink2") && bimSource.Contains("doc.EnableWorksharing("));
    Check("worksharing is only offered where Revit allows it (Document.CanEnableWorksharing): by Organize Multi-Worksets and by the import's question, so a template file or read-only document gets an explanation, not an exception",
          bimSource.Contains("!doc.IsWorkshared && !doc.CanEnableWorksharing()") && allSources.Any(kv => kv.Key.EndsWith("WorksharingConsent.cs") && kv.Value.Contains("!doc.CanEnableWorksharing()")));
    Check("a command inside a drop-down is found by the hook with one more CustomCtrl_% level than a button on a panel", appSource.Contains("CustomCtrl_%CustomCtrl_%CustomCtrl_%") && appSource.Contains("CustomCtrl_%CustomCtrl_%\" + TabName"));

}

// ---------------------------------------------------------------------------------------------------------------- the ribbon follows the view and this computer
{
    Console.WriteLine("\n===== the ribbon follows the view and this computer (RibbonVisibility, SportifyCapabilities) =====");
    var found = new ToolStatus(true, @"C:\Tools\x.exe", "found");
    var all = new Capabilities(found, true, found, found);
    var noUnity = all with { Unity = new ToolStatus(false, null, "Unity was not found on this computer."), UnityProjectFree = false };
    var noSw = all with { SolidWorks = new ToolStatus(false, null, "SOLIDWORKS is not installed on this computer.") };
    var nothing = noUnity with { SolidWorks = noSw.SolidWorks, Chrome = new ToolStatus(false, null, "no Chrome") };

    var layoutPanels = RibbonLayout.Panels;
    var buttonNames = layoutPanels.SelectMany(p => p.Entries).SelectMany(e => e switch
    {
        RibbonButtonSpec b => new[] { b.InternalName },
        RibbonPulldownSpec pd => pd.Items.Select(i => i.InternalName),
        _ => Array.Empty<string>(),
    }).Append(RibbonVisibility.PushMenuName).ToList();
    var pulldownNames = layoutPanels.SelectMany(p => p.Entries).OfType<RibbonPulldownSpec>().Select(pd => pd.InternalName).ToList();
    string[] Shown(string? view, Capabilities c, bool showAll = false) => buttonNames.Where(n => RibbonVisibility.Plan(view, c, showAll)[n].Visible).ToArray();
    bool PanelShown(string? view, Capabilities c, string name) => RibbonVisibility.Plan(view, c)[RibbonVisibility.PanelKey(name)].Visible;

    Check("every name the rules mention is a real button of the layout (or the Push menu): a renamed button cannot silently stop being ruled",
          RibbonVisibility.Needs.Keys.Concat(RibbonVisibility.SimpleButtons).Concat(RibbonVisibility.AlwaysVisible).All(buttonNames.Contains),
          string.Join(", ", RibbonVisibility.Needs.Keys.Concat(RibbonVisibility.SimpleButtons).Concat(RibbonVisibility.AlwaysVisible).Where(n => !buttonNames.Contains(n))));
    Check("the plan has an entry for every button, drop-down item, drop-down and panel of the layout", buttonNames.Concat(pulldownNames).Concat(layoutPanels.Select(p => RibbonVisibility.PanelKey(p.Name))).All(n => RibbonVisibility.Plan("advanced", all).ContainsKey(n)));
    Check("the words on every button are found by its internal name (what the web app tells the person is hidden)", buttonNames.All(n => RibbonVisibility.TextOf(n) != n) && RibbonVisibility.TextOf("SimulateKinetics") == "Simulate (SOLIDWORKS)" && RibbonVisibility.TextOf("PushToSportify") == "Push to Sportify" && RibbonVisibility.TextOf("nope") == "nope");

    // the computer
    Check("Advanced view and every tool found: everything is shown, every panel too", Shown("advanced", all).Length == buttonNames.Count && layoutPanels.All(p => PanelShown("advanced", all, p.Name)) && RibbonVisibility.Hidden(RibbonVisibility.Plan("advanced", all)).Count == 0);
    var hiddenNoTools = buttonNames.Except(Shown("advanced", nothing)).OrderBy(n => n).ToArray();
    Check("with no Unity and no SOLIDWORKS exactly the buttons that cannot work are hidden: Ball Trajectory, Record Isolated Video and Simulate (SOLIDWORKS)", string.Join(",", hiddenNoTools) == "RecordKineticsVideo,SimulateBallTrajectories,SimulateKinetics", string.Join(",", hiddenNoTools));
    Check("the five physical analyses stay without Unity (they give a PDF), and so do their drop-downs and panels", new[] { "AnalyzeStructuralLoads", "AnalyzeDynamicLoads", "AnalyzeSunShade", "AnalyzeWindErosionRisk", "SimulateSoilPercolation" }.All(n => Shown("advanced", nothing).Contains(n))
          && pulldownNames.All(n => RibbonVisibility.Plan("advanced", nothing)[n].Visible) && layoutPanels.All(p => PanelShown("advanced", nothing, p.Name)));
    Check("Unity alone: only Simulate (SOLIDWORKS) is hidden; SOLIDWORKS alone: the two Unity buttons are", string.Join(",", buttonNames.Except(Shown("advanced", noSw))) == "SimulateKinetics" && string.Join(",", buttonNames.Except(Shown("advanced", noUnity)).OrderBy(n => n)) == "RecordKineticsVideo,SimulateBallTrajectories");
    Check("a hidden button's reason is the tool's own sentence (what to do about it)", RibbonVisibility.Plan("advanced", nothing)["SimulateKinetics"].Reason == "SOLIDWORKS is not installed on this computer." && RibbonVisibility.Plan("advanced", nothing)["RecordKineticsVideo"].Reason == "Unity was not found on this computer.");
    Check("Unity found but its Editor holding the project does not hide the buttons (that is asked at click time, with a way out)", Shown("advanced", all with { UnityProjectFree = false }).Length == buttonNames.Count);

    // the view
    var simple = Shown("simple", all);
    Check("the Simple view shows exactly the main path (" + RibbonVisibility.SimpleButtons.Count + " buttons), in every drop-down none", simple.OrderBy(n => n).SequenceEqual(RibbonVisibility.SimpleButtons.OrderBy(n => n)) && RibbonVisibility.SimpleButtons.Count <= 8 && pulldownNames.All(n => !RibbonVisibility.Plan("simple", all)[n].Visible), string.Join(",", simple));
    Check("the Simple view shows the panels that keep a button and hides the ones that keep none", PanelShown("simple", all, "App & Data Import") && PanelShown("simple", all, "Simulation & Analytics") && PanelShown("simple", all, "Data Export / Deliverables")
          && !PanelShown("simple", all, "Algorithmic Analysis") && !PanelShown("simple", all, "Kinetics") && !PanelShown("simple", all, "BIM & Documentation"));
    Check("Simple with no tools shows the same main path (nothing it keeps needs a tool)", Shown("simple", nothing).OrderBy(n => n).SequenceEqual(simple.OrderBy(n => n)));
    Check("an unknown or missing view is the Advanced view", new string?[] { null, "", "expert", "SIMPLEX" }.All(v => Shown(v, all).Length == buttonNames.Count) && RibbonVisibility.NormalizeView("Simple") == "simple" && RibbonVisibility.NormalizeView(null) == "advanced");

    // what the person added to the Simple view (the start-up quiz)
    string[] ShownWith(string? view, Capabilities c, params string[] extras) => buttonNames.Where(n => RibbonVisibility.Plan(view, c, false, extras)[n].Visible).ToArray();
    Check("every button an extra brings back is a real button of the layout, and every extra has some", RibbonVisibility.ExtraButtons.Values.All(b => b.Length > 0 && b.All(buttonNames.Contains)) && RibbonVisibility.ExtraButtons.Count == 6,
          string.Join(", ", RibbonVisibility.ExtraButtons.Values.SelectMany(b => b).Where(n => !buttonNames.Contains(n))));
    Check("each extra adds exactly its buttons to the Simple view (all tools found), and nothing else", RibbonVisibility.ExtraButtons.All(kv => ShownWith("simple", all, kv.Key).OrderBy(n => n).SequenceEqual(simple.Union(kv.Value).OrderBy(n => n))));
    Check("structure brings back the Structural drop-down (both items) and not the Environmental one", ShownWith("simple", all, "structure").Contains("AnalyzeDynamicLoads") && RibbonVisibility.Plan("simple", all, false, new[] { "structure" })["PullStructural"].Visible && !RibbonVisibility.Plan("simple", all, false, new[] { "structure" })["PullEnvironmental"].Visible);
    Check("the panels follow: post analysis shows the Kinetics panel, safety the Algorithmic Analysis panel, nothing else changes", PanelShown2("simple", "postAnalysis", "Kinetics") && !PanelShown2("simple", "structure", "Kinetics") && PanelShown2("simple", "safety", "Algorithmic Analysis") && !PanelShown2("simple", "structure", "Algorithmic Analysis") && !PanelShown2("simple", "carbon", "BIM & Documentation"));
    bool PanelShown2(string view, string extra, string panel) => RibbonVisibility.Plan(view, all, false, new[] { extra })[RibbonVisibility.PanelKey(panel)].Visible;
    Check("an extra whose tool is missing still does not show the button that cannot work: post analysis without Unity or SOLIDWORKS keeps Choose, Generate and Import, not Record or Simulate", string.Join(",", ShownWith("simple", nothing, "postAnalysis").Except(simple).OrderBy(n => n)) == "ChooseKineticFamily,GenerateKineticFamily,ImportKineticAdaptation");
    Check("safety without Unity keeps Fire Safety and Accessibility but not Ball Trajectory (which needs Unity)", string.Join(",", ShownWith("simple", noUnity, "safety").Except(simple).OrderBy(n => n)) == "AnalyzeAccessibility,AnalyzeFireSafety");
    Check("several extras add up, in any order, and the same extra twice is once", ShownWith("simple", all, "structure", "carbon").SequenceEqual(ShownWith("simple", all, "carbon", "structure", "structure")) && ShownWith("simple", all, "structure", "carbon").Except(simple).Count() == 4);
    Check("an extra that does not exist brings nothing, and none or null changes nothing", ShownWith("simple", all, "nope", "").OrderBy(n => n).SequenceEqual(simple.OrderBy(n => n)) && RibbonVisibility.Plan("simple", all, false, null)["AnalyzeSunShade"].Visible == false);
    Check("extras change nothing in the Advanced view (everything the computer can do is there)", RibbonVisibility.ExtraButtons.Keys.All(k => ShownWith("advanced", all, k).Length == buttonNames.Count) && ShownWith("advanced", nothing, "postAnalysis").SequenceEqual(Shown("advanced", nothing)));
    Check("a button an extra brought back is told apart from one nothing brought back: only the second says it is hidden in the Simple view", RibbonVisibility.Plan("simple", all, false, new[] { "conditions" })["AnalyzeSunShade"].Visible && RibbonVisibility.Plan("simple", all, false, new[] { "conditions" })["AnalyzeFireSafety"].Reason.Contains("Simple view") && RibbonVisibility.Plan("simple", all, false, new[] { "conditions" })["AnalyzeFireSafety"].Reason.Contains("quiz"));
    Check("the guard holds with extras too: the way into the web app and into the files is visible for every extra, in both views", RibbonVisibility.ExtraButtons.Keys.All(k => new[] { "simple", "advanced", null }.All(v => RibbonVisibility.AlwaysVisible.All(n => RibbonVisibility.Plan(v, nothing, false, new[] { k })[n].Visible))));

    // the guard
    var views = new string?[] { "simple", "advanced", null, "garbage" };
    Check("the way into the web app and into the person's files is visible in every view and on every computer", views.All(v => new[] { all, nothing, noUnity, noSw }.All(c => RibbonVisibility.AlwaysVisible.All(n => RibbonVisibility.Plan(v, c)[n].Visible))));
    Check("SPORTIFY_SHOW_ALL_BUTTONS turns both rules off: Simple with no tools still shows everything", Shown("simple", nothing, showAll: true).Length == buttonNames.Count && layoutPanels.All(p => RibbonVisibility.Plan("simple", nothing, true)[RibbonVisibility.PanelKey(p.Name)].Visible));
    Check("a drop-down is shown exactly when one of its items is, a panel exactly when one of its entries is", views.All(v => new[] { all, nothing }.All(c =>
    {
        var plan = RibbonVisibility.Plan(v, c);
        return layoutPanels.All(p => plan[RibbonVisibility.PanelKey(p.Name)].Visible == p.Entries.Any(e => e switch
        {
            RibbonButtonSpec b => plan[b.InternalName].Visible,
            RibbonPulldownSpec pd => plan[pd.InternalName].Visible && pd.Items.Any(i => plan[i.InternalName].Visible),
            RibbonPushMenuSpec => plan[RibbonVisibility.PushMenuName].Visible,
            _ => false,
        })) && layoutPanels.SelectMany(p => p.Entries).OfType<RibbonPulldownSpec>().All(pd => plan[pd.InternalName].Visible == pd.Items.Any(i => plan[i.InternalName].Visible));
    })));
    Check("the ribbon is never left with nothing: there is always something visible", views.All(v => new[] { all, nothing }.All(c => Shown(v, c).Length >= 2)));

    // what this computer has: one answer, kept for a while, safe when the look fails
    var looks = 0;
    SportifyCapabilities.UseProbe(() => { looks++; return all; });
    var first = SportifyCapabilities.Current();
    SportifyCapabilities.Current();
    Check("the computer is looked at once and the answer is reused; refresh looks again", looks == 1 && first.Unity.Found && SportifyCapabilities.Current(refresh: true) != null && looks == 2);
    SportifyCapabilities.UseProbe(() => throw new InvalidOperationException("registry unreadable"));
    var failed = SportifyCapabilities.Current();
    Check("a look that fails counts as 'nothing found' with the reason, and never throws", !failed.Unity.Found && failed.Unity.Note.Contains("registry unreadable") && !failed.SolidWorks.Found);
    SportifyCapabilities.UseProbe(() => Capabilities.None);
    Check("with no probe installed nothing is found (the safe answer: no button that can only fail is offered)", !SportifyCapabilities.Current(true).Unity.Found);
    var json = all.ToJson();
    Check("the answer as JSON has each tool's found/path/note and whether Unity's project is free", json["unity"]!["found"]!.GetValue<bool>() && json["solidworks"]!["path"]!.GetValue<string>() == @"C:\Tools\x.exe" && json["chrome"]!["note"]!.GetValue<string>() == "found" && json["unity_project_free"]!.GetValue<bool>());

    // the wiring in the add-in's sources
    string? src = null;
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && src == null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "SportfyRevit", "SportfyRevit");
        if (File.Exists(Path.Combine(candidate, "SportfyRevitApp.cs"))) src = candidate;
    }
    string Src(string file) => src == null ? "" : File.ReadAllText(Path.Combine(src, file));
    var app = Src("SportfyRevitApp.cs");
    Check("OnStartup installs the probe before the server starts, and the refresh bridge", app.IndexOf("SportifyCapabilities.UseProbe(CapabilityProbes.Detect)", StringComparison.Ordinal) is var at && at > 0 && at < app.IndexOf("RoofBoundaryServer.Start()", StringComparison.Ordinal) && app.Contains("RibbonRefreshBridge.Install()"));
    Check("BuildRibbon registers every panel, button, drop-down and drop-down item, and applies the plan after the last panel", app.Contains("RibbonApplier.RegisterPanel(spec.Name, panel)") && (app.Split("RibbonApplier.Register(").Length - 1) >= 4 && app.Contains("RibbonApplier.Apply()")
          && app.IndexOf("RibbonApplier.Apply()", StringComparison.Ordinal) > app.IndexOf("RibbonApplier.RegisterPanel", StringComparison.Ordinal));
    Check("a ribbon that cannot follow is logged and left as it was: Apply is inside its own try/catch, and each item's Visible is set inside one", app.Contains("the ribbon could not be made to follow the view and this computer: every button stays visible") && Src("RibbonApplier.cs").Contains("the visibility of \" + key + \" could not be set"));
    Check("the applier runs on Revit's own thread only (start-up, or the external event RibbonRefreshBridge), and SPORTIFY_SHOW_ALL_BUTTONS is honoured", Src("RibbonApplier.cs").Contains("IExternalEventHandler") && Src("RibbonApplier.cs").Contains("SPORTIFY_SHOW_ALL_BUTTONS") && Src("RibbonApplier.cs").Contains("ExternalEvent.Create(this)"));
    var media = Src("AnalysisMedia.cs");
    Check("the analysis dialogs no longer ask 'do you have Unity?': the video link is offered only where Unity is, the PDF always", !media.Contains("I have Unity") && !media.Contains("I don't have Unity") && media.Contains("if (!haveUnity)") && media.Contains("dialog.AddCommandLink(VideoLink, \"Render the 3D video with Unity\""));
    Check("SOLIDWORKS has one detector: the probe asks MechanicalTool, which the Simulate command uses too", Src("CapabilityProbes.cs").Contains("MechanicalTool.SolidWorksInstalled()") && Src("CapabilityProbes.cs").Contains("MechanicalTool.Locate(") && Src("CapabilityProbes.cs").Contains("UnityHeadlessRunner.TryLocate("));
}

Console.WriteLine(fails == 0 ? "\nALL ADD-IN CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;

// the request of --ribbon-matrix: one profile (the web app's view and the extras a person added to the Simple view)
record MatrixRequest(string Id, string? View, string[]? Extras);
