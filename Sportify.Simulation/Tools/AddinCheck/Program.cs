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

Console.WriteLine(fails == 0 ? "\nALL ADD-IN CHECKS PASSED" : $"\n{fails} CHECK(S) FAILED");
return fails;
