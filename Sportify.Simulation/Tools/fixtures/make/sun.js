// Variants of the roof-garden sample for the sun and shade analysis: people zones (activities, court seats), walls that cast shadows,
// an outline with a notch, drains, an opening, other sites and orientations.
const fs = require("fs");
const path = require("path");
const src = path.join(__dirname, "..", "..", "..", "Assets", "StreamingAssets", "sample_layout_roofgarden.json");
const out = (process.argv[2] || path.join(__dirname, "..", "layouts", "sun")) + path.sep;
fs.mkdirSync(out, { recursive: true });
const base = JSON.parse(fs.readFileSync(src, "utf8"));
const clone = o => JSON.parse(JSON.stringify(o));
const L = base.roof_context.length_m, W = base.roof_context.width_m;

function activity(id, label, x, y, w, h) {
  return { id, category: "activity", label, bounding_box: { top_left_x_m: x, top_left_y_m: y, width_m: w, height_m: h }, transform: { rotation_deg: 0 }, parameters: {} };
}
function wall(name, x0, y0, x1, y1, h, t) { return { id: name, name, start_m: { x_m: x0, y_m: y0 }, end_m: { x_m: x1, y_m: y1 }, height_m: h, thickness_m: t }; }

function withFeatures(j) {
  j.roof_context.source_boundary_polygon = [{ x_m: 0, y_m: 0 }, { x_m: L, y_m: 0 }, { x_m: L, y_m: W - 3 }, { x_m: L - 7, y_m: W - 3 }, { x_m: L - 7, y_m: W }, { x_m: 0, y_m: W }];
  const parapet = (h) => [
    wall("Parapet S", 0, W - 0.125, L, W - 0.125, h, 0.25), wall("Parapet W", 0.125, 0, 0.125, W, h, 0.25), wall("Parapet N", 0, 0.125, L - 7, 0.125, h, 0.25),
  ];
  j.roof_context.features = {
    source: "revit", notes: [], openings: [{ id: "opening_1", polygon_m: [{ x_m: 10, y_m: 9 }, { x_m: 12, y_m: 9 }, { x_m: 12, y_m: 11 }, { x_m: 10, y_m: 11 }], area_m2: 4, x_m: 10, y_m: 9, width_m: 2, height_m: 2 }],
    entries: [], edges: [], drains: [{ id: "drain_1", kind: "drain", name: "Roof drain", x_m: 52.8, y_m: 10.5 }, { id: "drain_2", kind: "drain", name: "Roof drain", x_m: 2, y_m: 2 }],
    obstacles: [...parapet(0.8),
      wall("Stair house N", 61, 9, 64, 9, 3, 0.3), wall("Stair house E", 64, 9, 64, 12, 3, 0.3), wall("Stair house S", 64, 12, 61, 12, 3, 0.3), wall("Stair house W", 61, 12, 61, 9, 3, 0.3)],
    slab: null, levels: []
  };
  return j;
}

function people(j) {
  j.placements.push(activity("act_yoga", "Yoga", 5, 6, 8, 6));
  j.placements.push(activity("act_play", "Play area", 16, 13, 6, 5));
  j.placements[0].parameters.field.capacity.seats = 40;       // spectators at the first badminton court
  return j;
}

const A = withFeatures(people(clone(base)));
A.analysis_assumptions = { accepted: [], comfort_limit_walking_g: null, comfort_limit_rhythmic_g: null };
fs.writeFileSync(out + "sunA_people.json", JSON.stringify(A));

const B = clone(A); delete B.site_location; B.site_conditions.north_set = false; B.site_conditions.north_deg = null; delete B.analysis_assumptions;
fs.writeFileSync(out + "sunB_defaults.json", JSON.stringify(B));

const C = clone(A); C.site_conditions.north_deg = 180; C.analysis_assumptions.shade_target_percent = 70;
fs.writeFileSync(out + "sunC_north180_target70.json", JSON.stringify(C));

const D = clone(A); D.analysis_assumptions.garden_min_sun_hours = 8;
fs.writeFileSync(out + "sunD_garden8.json", JSON.stringify(D));

const E = clone(A); delete E.site_location; E.analysis_assumptions.site_latitude_deg = 60; E.analysis_assumptions.shade_equipment = "light";
fs.writeFileSync(out + "sunE_light_lat60.json", JSON.stringify(E));

const F = clone(A); F.site_conditions.north_deg = 90; F.site_location.latitude_deg = 47; F.analysis_assumptions.shade_equipment = "fixed"; F.analysis_assumptions.accepted = ["site_latitude", "roof_north", "shade_target", "garden_min_sun", "shade_equipment", "deck_capacity"];
fs.writeFileSync(out + "sunF_fixed_east.json", JSON.stringify(F));

fs.writeFileSync(out + "sunG_sample.json", JSON.stringify(clone(base)));
// H: plant on the roof (it shades like a wall) and the slab's structural thickness (the deck's depth in the resonance estimate)
const H = clone(A);
H.roof_context.features = H.roof_context.features || {};
H.roof_context.features.equipment = [
  { id: "mechanical_1", kind: "mechanical", name: "Air handler", x_m: 14, y_m: 5, width_m: 4, depth_m: 2.5, height_m: 2.2 },
  { id: "electrical_1", kind: "electrical", name: "Switchboard", x_m: 30, y_m: 19, width_m: 0.8, depth_m: 1.6, height_m: 2.0 },
];
H.roof_context.features.slab = { type_name: "Flat roof", thickness_m: 0.45, structural_thickness_m: 0.28, layers: [] };
fs.writeFileSync(out + "sunH_equipment.json", JSON.stringify(H));
// I, J: the orientation is by the flag. Unity's JsonUtility cannot read a null, so it goes by site_conditions.north_set alone: a value with the flag off, or with no flag at all
// (an export from before the flag), is "not set" to it, and the add-in's reader has to agree.
const I = clone(A); I.site_conditions.north_deg = 135; I.site_conditions.north_set = false;
fs.writeFileSync(out + "sunI_north_flag_off.json", JSON.stringify(I));
const J = clone(A); J.site_conditions.north_deg = 135; delete J.site_conditions.north_set;
fs.writeFileSync(out + "sunJ_north_no_flag.json", JSON.stringify(J));
console.log("wrote 10 variants to", out);
