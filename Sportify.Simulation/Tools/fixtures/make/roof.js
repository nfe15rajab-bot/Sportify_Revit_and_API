// Variants of the structural sample for roofs that are not axis-aligned rectangles: an L-shaped roof, a skewed grid, both.
// The roof outline travels in roof_context.source_boundary_polygon in Revit's convention (y UP); the grid lines and columns in the plan's (y DOWN).
const fs = require("fs");
const path = require("path");
const FIX = path.join(__dirname, "..", "layouts");
const base = JSON.parse(fs.readFileSync(path.join(FIX, "struct", "v1-sample.json"), "utf8"));
const L = base.roof_context.length_m, W = base.roof_context.width_m;
const clone = o => JSON.parse(JSON.stringify(o));
const outDir = path.join(FIX, "roof") + path.sep;
fs.mkdirSync(outDir, { recursive: true });

// canvas (y down) outline -> the payload's y-up polygon
const up = pts => pts.map(([x, y]) => ({ x_m: x, y_m: +(W - y).toFixed(4) }));

// an L: the bottom-right corner (12 x 7 m in the canvas) is missing
const notch = [[0, 0], [L, 0], [L, W - 7], [L - 12, W - 7], [L - 12, W], [0, W]];
// a second shape: a stepped roof (two notches)
const stepped = [[0, 0], [L - 10, 0], [L - 10, 6], [L, 6], [L, W], [14, W], [14, W - 5], [0, W - 5]];

function skewGrid(l, deg) {
  const t = Math.tan(deg * Math.PI / 180);
  const s = l.structure;
  const hs = s.grid_lines.filter(g => g.start_m.y_m === g.end_m.y_m);
  const vs = s.grid_lines.filter(g => g.start_m.x_m === g.end_m.x_m);
  const moved = vs.map(g => ({ name: g.name, start_m: { x_m: +(g.start_m.x_m + (0 - W / 2) * t).toFixed(4), y_m: 0 }, end_m: { x_m: +(g.start_m.x_m + (W - W / 2) * t).toFixed(4), y_m: W } }));
  s.grid_lines = [...hs, ...moved];
  // columns at the crossings (kept only where they fall on the roof)
  const cols = [];
  for (const v of moved) for (const h of hs) {
    const y = h.start_m.y_m;
    const x = v.start_m.x_m + (v.end_m.x_m - v.start_m.x_m) * (y - v.start_m.y_m) / (v.end_m.y_m - v.start_m.y_m);
    if (x >= -0.3 && x <= L + 0.3) cols.push({ label: v.name + h.name, x_m: +x.toFixed(4), y_m: y });
  }
  s.columns = cols;
}

const variants = {
  "r0-rect": l => l,
  "r1-notch": l => { l.roof_context.source_boundary_polygon = up(notch); return l; },
  "r2-skew": l => { skewGrid(l, 6); return l; },
  "r3-notch-skew": l => { l.roof_context.source_boundary_polygon = up(notch); skewGrid(l, 6); return l; },
  "r4-stepped": l => { l.roof_context.source_boundary_polygon = up(stepped); return l; },
  "r5-stepped-skew": l => { l.roof_context.source_boundary_polygon = up(stepped); skewGrid(l, -9); return l; },
};
for (const [name, f] of Object.entries(variants)) {
  const l = f(clone(base));
  fs.writeFileSync(outDir + name + ".json", JSON.stringify(l));
  console.log(name, "->", (l.structure.grid_lines || []).length, "lines,", (l.structure.columns || []).length, "columns", l.roof_context.source_boundary_polygon ? "outline" : "no outline");
}

// the slab's structural thickness is the deck's depth in the resonance estimate (a thickness under 0.10 m is ignored as a modelling slip)
for (const [n, t] of [["s30", 0.30], ["s50", 0.50], ["s08", 0.08]]) {
  const c = clone(variants["r0-rect"](clone(base)));
  c.roof_context.features = { source: "revit", slab: { type_name: "Flat roof", thickness_m: t + 0.2, structural_thickness_m: t, layers: [] } };
  fs.writeFileSync(outDir + "slab-" + n + ".json", JSON.stringify(c));
  console.log("slab-" + n, "-> slab", t, "m");
}
