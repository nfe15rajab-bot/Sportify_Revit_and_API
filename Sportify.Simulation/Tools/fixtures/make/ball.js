const fs = require("fs");
const path = require("path");
const out = process.argv[2] || path.join(__dirname, "..", "layouts", "ball");

const DIMS = {                       // straight from the web app's FIELDS table (data.js)
  handball_mini:    { sport: "handball",   variant: "mini",     norm: "IHF / DIN 18032",    d: { length_m: 30,   width_m: 16,  runoff_m: 1.5, min_height_m: 7 } },
  football_mini:    { sport: "football",   variant: "mini",     norm: "DFB / DIN 18032",    d: { length_m: 25,   width_m: 16,  runoff_m: 2,   min_height_m: 5 } },
  basketball_mini:  { sport: "basketball", variant: "mini",     norm: "FIBA / DIN 18032",   d: { length_m: 22,   width_m: 13,  runoff_m: 2,   min_height_m: 7 } },
  volleyball_mini:  { sport: "volleyball", variant: "mini",     norm: "FIVB / DIN 18032",   d: { length_m: 16,   width_m: 8,   runoff_m: 3,   min_height_m: 7 } },
  polyvalent_mini:  { sport: "polyvalent", variant: "mini",     norm: "DIN 18032",          d: { length_m: 20,   width_m: 12,  runoff_m: 1.5, min_height_m: 5.5 } },
  badminton_std:    { sport: "badminton",  variant: "standard", norm: "BWF / DIN 18032",    d: { length_m: 13.4, width_m: 6.1, runoff_m: 2,   min_height_m: 9 } },
};

function field(id, key, x, y, rotated) {
  const f = DIMS[key];
  const w = rotated ? f.d.width_m : f.d.length_m;      // bounding_box is the POST-rotation footprint
  const h = rotated ? f.d.length_m : f.d.width_m;
  return {
    id, category: "field", label: f.sport,
    insertion_point: { center_x_m: x + w / 2, center_y_m: y + h / 2 },
    bounding_box: { top_left_x_m: x, top_left_y_m: y, width_m: w, height_m: h },
    transform: { rotation_deg: rotated ? 90 : 0 },
    parameters: { field: { sport: f.sport, variant: f.variant, norm: f.norm, dimensions: f.d } },
  };
}
const garden = (id, x, y, w, h) => ({ id, category: "garden", label: "Garden bed", bounding_box: { top_left_x_m: x, top_left_y_m: y, width_m: w, height_m: h }, transform: { rotation_deg: 0 }, parameters: {} });

const layout = (roofL, roofW, placements) => ({
  version: "1.3", generator: "Sportify-Combine",
  roof_context: { length_m: roofL, width_m: roofW, source_boundary_polygon: null, world_origin_x_m: 0, world_origin_y_m: 0 },
  design_rules: { clearance_m: 1.0, boundary_setback_m: 1.5, circulation_width_m: 1.0, min_entry_points: 4, quiet_buffer_m: 3.0 },
  entry_points: [ { x_m: roofL / 2, y_m: 0, edge: "top" }, { x_m: roofL / 2, y_m: roofW, edge: "bottom" }, { x_m: 0, y_m: roofW / 2, edge: "left" }, { x_m: roofL, y_m: roofW / 2, edge: "right" } ],
  circulation_paths: [], site_location: null, placements,
});

// A: tight roof, big-throwing sports next to each other -> crossings of every kind
fs.writeFileSync(out + "/layout_A_crossings.json", JSON.stringify(layout(58, 20, [
  garden("g0", 0.5, 0.2, 10, 1.6),
  field("f0", "handball_mini", 0.5, 2, false),
  field("f1", "football_mini", 31.5, 2, false),
]), null, 1));

// B: rotated courts + mixed sports (long axis along y for the rotated ones)
fs.writeFileSync(out + "/layout_B_rotated_mixed.json", JSON.stringify(layout(46, 36, [
  garden("g0", 2, 28, 20, 4),
  field("f0", "basketball_mini", 2, 2, true),
  field("f1", "volleyball_mini", 17, 2, false),
  field("f2", "polyvalent_mini", 17, 14, false),
  field("f3", "badminton_std", 38, 2, true),
]), null, 1));

// C: no fields at all -> nothing to simulate
fs.writeFileSync(out + "/layout_C_empty.json", JSON.stringify(layout(40, 20, [ garden("g0", 2, 2, 10, 3) ]), null, 1));

// D: not a Sportify layout at all
fs.writeFileSync(out + "/layout_D_invalid.json", JSON.stringify({ hello: "world" }));
console.log("wrote", fs.readdirSync(out).join(", "));
