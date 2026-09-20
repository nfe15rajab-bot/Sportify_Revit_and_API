// Independent re-implementation of the wind + erosion screening model, written from the model's specification
// (not translated from the C#), reading the layout JSON itself. Compared against the C# report.
//   node oracle.js <layout.json> <csharp-report.json>
const fs = require("fs");
const layout = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
const csharp = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));

const rho = 1.25, g = 9.81, kappa = 0.4;
const roof = layout.roof_context;
const Lr = roof.length_m, Wr = roof.width_m;
const zg = roof.height_above_ground_m || 0, zo = roof.world_origin_z_m || 0;
const h = zg > 0.5 && zg <= 300 ? zg : (zo > 0.5 && zo <= 60 ? zo : 12);   // a given height wins, else the elevation, else 12 m
const sc = layout.site_conditions || {};

// ---- site (wind zone from the layout, else 2; terrain III)
const vb = ({ 1: 22.5, 2: 25, 3: 27.5, 4: 30 })[sc.wind_zone] || 25, z0 = 0.3, zmin = 5;
const kr = 0.19 * Math.pow(z0 / 0.05, 0.07);
const qp = z => {
  const zz = Math.max(z, zmin);
  const vm = vb * kr * Math.log(zz / z0);
  const iv = 1 / Math.log(zz / z0);
  return (1 + 7 * iv) * 0.5 * rho * vm * vm;
};
const qRoof = qp(h);

// ---- zoning
const cpe = { F: -1.8, G: -1.2, H: -0.7, I: -0.2 };
const rank = { I: 0, H: 1, G: 2, F: 3 };
function zoneFor(dist, along, e) {
  if (dist <= e / 10) return along <= e / 4 ? "F" : "G";
  if (dist <= e / 2) return "H";
  return "I";
}
// the roof's outline in the plan's coordinates (y down), or null for the rectangle (the same reading as the structural oracle's)
const r4 = v => Math.round(v * 1e4) / 1e4;
function outlineOf() {
  const poly = roof.source_boundary_polygon;
  if (!poly || poly.length < 3) return null;
  const pts = poly.map(p => [r4(p.x_m), r4(Wr - p.y_m)]);
  const uniq = pts.filter((p, i) => i === 0 || Math.hypot(p[0] - pts[i - 1][0], p[1] - pts[i - 1][1]) > 1e-6);
  if (uniq.length > 1 && Math.hypot(uniq[0][0] - uniq[uniq.length - 1][0], uniq[0][1] - uniq[uniq.length - 1][1]) < 1e-6) uniq.pop();
  const onBox = uniq.every(p => (Math.abs(p[0]) < 0.05 || Math.abs(p[0] - Lr) < 0.05) && (Math.abs(p[1]) < 0.05 || Math.abs(p[1] - Wr) < 0.05));
  if (uniq.length === 4 && onBox) return null;
  let q = uniq, changed = true;
  while (changed && q.length >= 3) {
    changed = false;
    for (let i = 0; i < q.length; i++) {
      const a = q[(i + q.length - 1) % q.length], b = q[i], c = q[(i + 1) % q.length];
      const cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]), dot = (b[0] - a[0]) * (c[0] - b[0]) + (b[1] - a[1]) * (c[1] - b[1]);
      if (Math.abs(cross) < 1e-9 && dot > 0) { q = q.filter((_, j) => j !== i); changed = true; break; }
    }
  }
  return q.length >= 3 ? q : null;
}
const outline = outlineOf();
const inPoly = (x, y) => {
  let ins = false;
  for (let i = 0, j = outline.length - 1; i < outline.length; j = i++) {
    const [xi, yi] = outline[i], [xj, yj] = outline[j];
    const dx = xj - xi, dy = yj - yi, l2 = dx * dx + dy * dy, t = l2 < 1e-18 ? 0 : Math.max(0, Math.min(1, ((x - xi) * dx + (y - yi) * dy) / l2));
    if (Math.hypot(xi + t * dx - x, yi + t * dy - y) < 1e-6) return true;        // on the boundary
    if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) ins = !ins;
  }
  return ins;
};
// edges with their outward normals (the shoelace sign says which side the inside is on)
const outlineEdges = (() => {
  if (!outline) return [];
  let area2 = 0;
  outline.forEach((p, i) => { const q = outline[(i + 1) % outline.length]; area2 += p[0] * q[1] - q[0] * p[1]; });
  return outline.map((p, i) => {
    const q = outline[(i + 1) % outline.length], dx = q[0] - p[0], dy = q[1] - p[1], len = Math.hypot(dx, dy);
    return { p, q, len, tx: dx / len, ty: dy / len, nx: (area2 > 0 ? dy : -dy) / len, ny: (area2 > 0 ? -dx : dx) / len };
  });
})();
function classifyOutline(x, y, dirDeg) {
  const a = dirDeg * Math.PI / 180, ux = Math.cos(a), uy = Math.sin(a);
  let best = "I";
  for (const e of outlineEdges) {
    if (-(e.nx * ux + e.ny * uy) <= 0.01) continue;                                // the wind does not blow in across this stretch
    const t = (x - e.p[0]) * e.tx + (y - e.p[1]) * e.ty, d = -((x - e.p[0]) * e.nx + (y - e.p[1]) * e.ny);
    if (t < 0 || t > e.len || d < -1e-9) continue;                                 // not behind it
    let visible = true;                                                             // seen from the point without leaving the roof: sampled along the line to the foot
    for (let k = 1; k < 200 && visible; k++) { const f = k / 200; if (!inPoly(x + (e.p[0] + t * e.tx - x) * f, y + (e.p[1] + t * e.ty - y) * f)) visible = false; }
    if (!visible) continue;
    const proj = outline.map(v => v[0] * e.tx + v[1] * e.ty), b = Math.max(...proj) - Math.min(...proj);
    const z = zoneFor(Math.max(0, d), Math.min(t, e.len - t), Math.min(b, 2 * h));
    if (rank[z] > rank[best]) best = z;
  }
  return best;
}
function classify(x, y, dirDeg) {
  if (outline) return classifyOutline(x, y, dirDeg);
  const a = dirDeg * Math.PI / 180, ux = Math.cos(a), uy = Math.sin(a);
  let best = "I";
  const consider = z => { if (rank[z] > rank[best]) best = z; };
  if (Math.abs(ux) > 0.01) {
    const dist = ux > 0 ? x : Lr - x;
    const along = Math.min(y, Wr - y);
    consider(zoneFor(Math.max(0, dist), along, Math.min(Wr, 2 * h)));
  }
  if (Math.abs(uy) > 0.01) {
    const dist = uy > 0 ? y : Wr - y;
    const along = Math.min(x, Lr - x);
    consider(zoneFor(Math.max(0, dist), along, Math.min(Lr, 2 * h)));
  }
  return best;
}
const speedUp = z => Math.sqrt(1 - cpe[z]) / Math.sqrt(1 - cpe.I);
const dirs = [0, 45, 90, 135, 180, 225, 270, 315];

// ---- build-ups
const assemblies = {};
layout.zones = layout.zones || []; layout.assemblies = layout.assemblies || [];      // an old export has neither: empty lists, as the readers have it
for (const a of layout.assemblies) assemblies[a.key] = a;
const isGravel = n => /gravel|kies|split|grit|sand/.test((n || "").toLowerCase());
const density = (fn, intensive, name) => isGravel(name) ? 1700 : ({ vegetation: 150, substrate: intensive ? 1200 : 1000, filter: 300, drainage: 250, protection: 400, root_barrier: 1000, waterproofing: 1000, wearing: 2300, bedding: 150 }[fn] ?? 500);
function dryWeight(a) {
  if (a.saturated_kg_m2 != null && a.water_storage_l_m2 != null && a.saturated_kg_m2 > 0) return a.saturated_kg_m2 - a.water_storage_l_m2;
  return a.layers.reduce((s, l) => s + l.thickness_m * density(l.function, a.category === "intensive", l.name), 0);
}
const substrateMm = a => a.layers.filter(l => l.function === "substrate").reduce((s, l) => s + l.thickness_m * 1000, 0);

// ---- erosion
const zRefE = h + 2;
const terrainRatio = Math.log(zRefE / z0) / Math.log(10 / z0);
function onset(grainMm, rhoP, s) {
  const d = grainMm / 1000;
  const ustar = 0.1 * Math.sqrt((rhoP - rho) / rho * g * d);
  return ustar * Math.log(2 / (d / 30)) / (kappa * s * terrainRatio);
}
function coverOf(a) {
  if (a.category === "walkway") return "paved";
  const top = a.layers[0];
  if (top.function === "wearing") return "paved";
  const n = top.name.toLowerCase();
  if (isGravel(n)) return "gravel";
  return "veg";   // any planted bed: on the layout the oracle reads, a vegetation top layer or a substrate one
}

// ---- zones
const out = { zones: [], plants: [] };
layout.zones.forEach((z, i) => {
  const a = assemblies[z.assembly_key];
  const dry = dryWeight(a), cover = coverOf(a);
  const bb = z.bounding_box;
  const paved = cover === "paved";
  const nx = Math.max(1, Math.round(bb.width_m / 0.5)), ny = Math.max(1, Math.round(bb.height_m / 0.5));
  let maxU = 0, maxExtra = 0, flagged = 0, minBare = Infinity, minEst = Infinity, bareFlag = 0, estFlag = 0;
  for (let iy = 0; iy < ny; iy++) for (let ix = 0; ix < nx; ix++) {
    const x = bb.top_left_x_m + (ix + 0.5) * bb.width_m / nx, y = bb.top_left_y_m + (iy + 0.5) * bb.height_m / ny;
    let cu = 0, cb = Infinity, ce = Infinity;
    for (const d of dirs) {
      const zn = classify(x, y, d), s = speedUp(zn);
      const suction = -cpe[zn] * qRoof;
      const u = 1.5 * suction / (0.9 * dry * g);
      cu = Math.max(cu, u);
      maxExtra = Math.max(maxExtra, 1.5 * suction / 0.9 / g - dry);
      if (cover !== "paved") {
        const bare = cover === "gravel" ? onset(8, 2600, s) : onset(3, 1200, s);
        const est = cover === "veg" ? bare * 2 : bare;
        cb = Math.min(cb, bare); ce = Math.min(ce, est);
      }
    }
    maxU = Math.max(maxU, cu);
    if (cu > 1) flagged++;
    if (cb < 10.8) bareFlag++;
    if (ce < 17.2) estFlag++;
    minBare = Math.min(minBare, cb); minEst = Math.min(minEst, ce);
  }
  out.zones.push({ label: "Green roof " + (i + 1), dry, upliftMax: maxU, flaggedPct: 100 * flagged / (nx * ny), ballastMm: Math.ceil(Math.max(0, maxExtra) / 1.8 / 5 - 1e-9) * 5, minBare: paved ? 0 : minBare, minEst: paved ? 0 : minEst, barePct: 100 * bareFlag / (nx * ny), estPct: 100 * estFlag / (nx * ny) });
});

// ---- plants (trees)
const zoneAt = (x, y) => layout.zones.find(z => { const b = z.bounding_box; return x >= b.top_left_x_m && x <= b.top_left_x_m + b.width_m && y >= b.top_left_y_m && y <= b.top_left_y_m + b.height_m; });
for (const p of layout.placements) {
  if (p.category !== "vegetation") continue;
  const v = p.parameters.vegetation;
  if (!(v.form === "tree" || v.height_m >= 2)) continue;
  const bb = p.bounding_box, cx = bb.top_left_x_m + bb.width_m / 2, cy = bb.top_left_y_m + bb.height_m / 2;
  const zone = zoneAt(cx, cy);
  const sub = zone ? substrateMm(assemblies[zone.assembly_key]) : 0;
  const H = v.height_m, C = v.crown_m;
  const area = Math.PI / 4 * C * 0.65 * H, lever = 0.675 * H, q = qp(h + 0.7 * H);
  const R = Math.min(0.25 * C, 2.5), plate = Math.PI * R * R;
  const treeN = 100 * (H / 6) ** 2 * g, soilN = 1650 * g * plate * sub / 1000;
  const resist = (soilN + treeN) * R;
  let worst = 0, worstM = 0, worstZone = "I";
  for (const d of dirs) {
    let zn = "I";
    for (const [ox, oy] of [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const c = classify(cx + ox * C / 2, cy + oy * C / 2, d);
      if (rank[c] > rank[zn]) zn = c;
    }
    const s = speedUp(zn);
    const m = 0.5 * q * s * s * area * lever;
    const u = 1.5 * m / (0.9 * resist);
    if (u > worst) { worst = u; worstM = m; worstZone = zn; }
  }
  out.plants.push({ species: v.botanical_name, util: worst, momentKNm: worstM / 1000, resistKNm: resist / 1000, zone: worstZone });
}

// ---- compass: rotate the plan into north/east components instead of using the C# angle formula
const compass = [];
if (sc.north_set && typeof sc.north_deg === "number") {
  const B = sc.north_deg * Math.PI / 180;
  const up = [Math.sin(B), Math.cos(B)], right = [Math.sin(B + Math.PI / 2), Math.cos(B + Math.PI / 2)];   // [east, north]
  for (const d of dirs) {
    const t = d * Math.PI / 180;
    const E = right[0] * Math.cos(t) - up[0] * Math.sin(t), N = right[1] * Math.cos(t) - up[1] * Math.sin(t);
    compass.push(((Math.atan2(-E, -N) * 180 / Math.PI) + 360) % 360);
  }
}

// ---- compare
let bad = 0;
const near = (a, b, tol, what) => {
  const ok = Math.abs(a - b) <= tol * Math.max(1, Math.abs(b));
  if (!ok) { bad++; console.log("  MISMATCH", what, "oracle", a, "csharp", b); }
  return ok;
};
console.log("q roof: oracle", qRoof.toFixed(3), "csharp", csharp.site.peakPressureAtRoofPa.toFixed(3));
near(qRoof, csharp.site.peakPressureAtRoofPa, 1e-4, "qRoof");
if (compass.length) {
  compass.forEach((b, i) => near(b, csharp.directions[i].fromBearingDeg, 1e-4, "direction " + i + " bearing"));
  const prev = compass.map((b, i) => [Math.abs(((b - 250 + 540) % 360) - 180), i]).sort((a, b) => a[0] - b[0])[0][1];
  if (csharp.site.prevailingDirectionIndex !== prev) { bad++; console.log("  MISMATCH prevailing", prev, csharp.site.prevailingDirectionIndex); }
  const names = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
  compass.forEach((b, i) => { const want = "from the " + names[Math.round(b / 45) % 8]; if (csharp.directions[i].compassLabel !== want) { bad++; console.log("  MISMATCH label", i, want, csharp.directions[i].compassLabel); } });
} else if (csharp.site.hasNorth) { bad++; console.log("  MISMATCH: the C# report has an orientation the layout does not"); }
out.zones.forEach((z, i) => {
  const c = csharp.zones[i];
  near(z.dry, c.dryWeightKgM2, 1e-4, z.label + " dry");
  near(z.upliftMax, c.upliftUtilisationMax, 1e-4, z.label + " upliftMax");
  near(z.flaggedPct, c.upliftFlaggedAreaPercent, 1e-4, z.label + " flagged%");
  near(z.ballastMm, c.requiredBallastMm, 1e-6, z.label + " ballast");
  near(z.minBare, c.erosionOnsetBareMs, 1e-4, z.label + " bare onset");
  near(z.minEst, c.erosionOnsetEstablishedMs, 1e-4, z.label + " est onset");
  near(z.barePct, c.erosionBareAreaPercent, 1e-4, z.label + " bare%");
  near(z.estPct, c.erosionAreaPercent, 1e-4, z.label + " est%");
});
const trees = csharp.plants.filter(p => p.status !== "not-checked");
if (trees.length !== out.plants.length) { bad++; console.log("  MISMATCH tree count", out.plants.length, trees.length); }
out.plants.forEach((p, i) => {
  const c = trees[i];
  near(p.util, c.utilisation, 1e-4, p.species + " util");
  near(p.momentKNm, c.overturningMomentKNm, 1e-4, p.species + " M");
  near(p.resistKNm, c.resistingMomentKNm, 1e-4, p.species + " R");
  if (p.zone !== c.roofZone) { bad++; console.log("  MISMATCH", p.species, "zone", p.zone, c.roofZone); }
});
console.log(bad === 0 ? "ORACLE MATCH: " + out.zones.length + " zones, " + out.plants.length + " trees" : "ORACLE MISMATCHES: " + bad);
process.exit(bad === 0 ? 0 : 1);
