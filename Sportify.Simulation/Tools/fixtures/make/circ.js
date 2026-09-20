// Layouts for the circulation parity check (Tools/CirculationCheck): the web's rules.js computeCirculation against the add-in's CirculationEngine.
// The other checks never see these (they need entry points and walkways in different states: pieces walling each other in, an entry buried under a piece, no entries,
// roofs that make the grid coarser, a wide walkway that closes the gaps).
const fs = require("fs");
const path = require("path");
const root = path.join(__dirname, "..", "..", "..");                 // Sportify.Simulation
const out = path.join(__dirname, "..", "layouts", "circ");
fs.mkdirSync(out, { recursive: true });
const base = () => JSON.parse(fs.readFileSync(root + "/Assets/StreamingAssets/sample_layout_roofgarden.json", "utf8"));
const write = (name, l) => fs.writeFileSync(out + "/" + name + ".json", JSON.stringify(l, null, 2));
const box = (id, x, y, w, h) => ({ id, category: "field", name: id, bounding_box: { top_left_x_m: x, top_left_y_m: y, width_m: w, height_m: h }, parameters: {} });

write("c1-sample", base());
{ const l = base(); l.design_rules.circulation_width_m = 2.6; write("c2-wide-walkway", l); }                     // gaps narrower than this close: pieces become unreachable
{ const l = base(); l.entry_points = [l.entry_points[2], l.entry_points[3]]; write("c3-two-entries", l); }
{ const l = base(); l.entry_points = []; write("c4-no-entries", l); }
{                                                                                                                  // a big roof: the cell size grows until the grid is small enough
  const l = base(); l.roof_context.length_m = 160; l.roof_context.width_m = 80;
  l.entry_points = [{ x_m: 80, y_m: 0, edge: "top" }, { x_m: 0, y_m: 40, edge: "left" }, { x_m: 160, y_m: 30, edge: "right" }];
  write("c5-large-roof", l);
}
{                                                                                                                  // a small roof: the cell size floors at 0.3 m
  const l = base(); l.roof_context.length_m = 12; l.roof_context.width_m = 9; l.design_rules.circulation_width_m = 1.2;
  l.entry_points = [{ x_m: 6, y_m: 0, edge: "top" }, { x_m: 0, y_m: 4.5, edge: "left" }];
  l.placements = [box("a", 2, 2, 3, 2), box("b", 7, 5, 3, 2.5), box("c", 4.6, 4.6, 1.5, 1.5)];
  write("c6-small-roof", l);
}
{                                                                                                                  // an entry under a piece: the start walks inward to free ground; another piece is walled in
  const l = base(); l.roof_context.length_m = 30; l.roof_context.width_m = 20; l.design_rules.circulation_width_m = 1.0;
  l.entry_points = [{ x_m: 15, y_m: 0, edge: "top" }, { x_m: 30, y_m: 10, edge: "right" }];
  l.placements = [box("over-entry", 12, 0, 6, 3), box("island", 20, 6, 6, 6), box("ring-1", 19, 5, 8, 1), box("ring-2", 19, 12, 8, 1), box("ring-3", 19, 5, 1, 8), box("ring-4", 26, 5, 1, 8)];
  write("c7-buried-entry-and-island", l);
}
{ const l = base(); l.placements = []; write("c8-no-placements", l); }
{ const l = base(); l.roof_context.length_m = 40; l.roof_context.width_m = 40; l.entry_points = [{ x_m: 20, y_m: 0, edge: "top" }];       // a square roof, one entry, a wall of pieces across it
  l.placements = [box("wall-1", 0, 18, 18, 4), box("wall-2", 21, 18, 19, 4), box("behind", 15, 30, 8, 4), box("front", 15, 8, 8, 4)];
  write("c9-partial-wall", l);
}
console.log("wrote circulation layouts to", out);
