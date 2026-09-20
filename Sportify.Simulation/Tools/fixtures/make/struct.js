// Variants of the bundled sample layout for the structural checks.
const fs = require("fs");
const path = require("path");
const root = path.join(__dirname, "..", "..", "..");                 // Sportify.Simulation
const out = path.join(__dirname, "..", "layouts", "struct");
fs.mkdirSync(out, { recursive: true });
const base = () => JSON.parse(fs.readFileSync(root + "/Assets/StreamingAssets/sample_layout_roofgarden.json", "utf8"));
const write = (name, l) => fs.writeFileSync(out + "/" + name + ".json", JSON.stringify(l, null, 2));

write("v1-sample", base());

{ const l = base(); delete l.structure; write("v2-no-structure", l); }

{ // capacity given, grid lines only (no columns), one skewed line that must be ignored
  const l = base();
  l.structure.deck_capacity_kn_m2 = 12.0;
  l.structure.columns = [];
  l.structure.grid_lines.push({ name: "X", start_m: { x_m: 0, y_m: 0 }, end_m: { x_m: 60, y_m: 21 } });
  write("v3-capacity-lines-only", l);
}

{ // no tree bed: what is left is courts + light roofs
  const l = base();
  l.zones = l.zones.filter(z => z.id !== "zone_sample_4");
  l.placements = l.placements.filter(p => !(p.category === "vegetation" && p.bounding_box.top_left_x_m > 52));
  write("v4-no-tree-bed", l);
}

{ // both courts pushed to the right edge, with seats: the load sits right, and one court can move
  const l = base();
  l.zones = l.zones.filter(z => z.id !== "zone_sample_4");
  l.placements = l.placements.filter(p => p.category !== "vegetation");
  const courts = l.placements.filter(p => p.category === "field");
  courts[0].bounding_box.top_left_x_m = 40; courts[1].bounding_box.top_left_x_m = 53.6;
  courts[0].parameters.field.capacity.seats = 40;
  l.structure.deck_capacity_kn_m2 = 9.0;
  write("v5-courts-right", l);
}

{ // gardens only in the top-left corner, no grid, an activity placement
  const l = base();
  l.structure = undefined; delete l.structure;
  l.placements = l.placements.filter(p => p.category !== "field" && p.category !== "vegetation");
  l.placements.push({ id: "act1", category: "activity", label: "Play area", bounding_box: { top_left_x_m: 2, top_left_y_m: 2, width_m: 12, height_m: 8 }, parameters: { activity: { category: "play" } } });
  l.circulation_paths = [{ item_id: "x", source: "auto", points_m: [{ x_m: 0, y_m: 10.5 }, { x_m: 30, y_m: 10.5 }, { x_m: 30, y_m: 0 }] }];
  write("v6-activity-path", l);
}
console.log("variants written to", out);

{ // only two courts, both right of centre with seats: the natural answer is to slide the free court toward the middle
  const l = base();
  l.zones = []; l.placements = l.placements.filter(p => p.category === "field");
  l.placements[0].bounding_box.top_left_x_m = 40; l.placements[1].bounding_box.top_left_x_m = 53.6;
  l.placements[0].parameters.field.capacity.seats = 60; l.placements[1].parameters.field.capacity.seats = 60;
  l.structure.deck_capacity_kn_m2 = 9.0;
  write("v7-courts-only", l);
}
