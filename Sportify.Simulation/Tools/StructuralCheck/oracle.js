// Independent re-implementation of the structural load model's arithmetic, from the specification in the header of StructuralLoadCore.cs.
// It reads the layout JSON itself (geometry, grid, columns, entries, paths) and takes only each piece's INTENSITIES (permanent and imposed
// load per m2, people, a tree's weight) from the C# report, so what it checks is the mapping from pieces to cells, bays, columns and balance,
// not the build-up weights (those are spot-checked against the providers' published figures).
//
//   dotnet run --project Tools/StructuralCheck -c Release -- <layout.json> --json > report.json
//   node Tools/StructuralCheck/oracle.js <layout.json> report.json
const fs = require("fs");
const [layoutPath, reportPath] = process.argv.slice(2);
const layout = JSON.parse(fs.readFileSync(layoutPath, "utf8"));
const report = JSON.parse(fs.readFileSync(reportPath, "utf8"));

const CELL = 0.5, ROOF_FINISHES = 0.5, ROOF_LIVE = 0.75, ACCESSIBLE_LIVE = 3.0, ENTRY_RADIUS = 3.0, ENTRY_PERSONS = 10, ASSUMED_BAY = 8.4;
const G = 9.81, PERSON_KG = 90, MARGINAL_FROM = 0.8, COLUMN_HIGH = 1.25, MARGINAL_E = 0.05, HIGH_E = 0.10;

const L = layout.roof_context.length_m, W = layout.roof_context.width_m;

// ---- geometry of every piece the C# report lists, by id
const byId = new Map();
for (const z of layout.zones || []) byId.set(z.id, { x: z.bounding_box.top_left_x_m, y: z.bounding_box.top_left_y_m, w: z.bounding_box.width_m, h: z.bounding_box.height_m });
for (const p of layout.placements || []) {
  const bb = p.bounding_box;
  if (!bb) continue;
  if (p.category === "vegetation" && p.parameters && p.parameters.vegetation) {
    const crown = p.parameters.vegetation.crown_m > 0 ? p.parameters.vegetation.crown_m : Math.max(bb.width_m, bb.height_m);
    const cx = bb.top_left_x_m + bb.width_m / 2, cy = bb.top_left_y_m + bb.height_m / 2;
    byId.set(p.id, { x: cx - crown / 2, y: cy - crown / 2, w: crown, h: crown });
  } else byId.set(p.id, { x: bb.top_left_x_m, y: bb.top_left_y_m, w: bb.width_m, h: bb.height_m });
}
const items = report.items.map(i => {
  const g = byId.get(i.id);
  if (!g) throw new Error("no geometry for " + i.id);
  const isTree = i.kind === "Tree";
  return { ...g, dead: isTree ? 0 : i.deadKnM2, point: isTree ? i.deadKn : 0, live: i.liveKnM2, persons: i.persons };
});

const Nx = Math.max(1, Math.round(L / CELL)), Ny = Math.max(1, Math.round(W / CELL));
const cw = L / Nx, ch = W / Ny, cellArea = cw * ch, n = Nx * Ny;
const dead = new Float64Array(n).fill(ROOF_FINISHES * cellArea), live = new Float64Array(n), persons = new Float64Array(n);
const idx = (ix, iy) => iy * Nx + ix;
const clampCell = (v, size, count) => Math.min(count - 1, Math.max(0, Math.floor(v / size)));

// ---- pieces onto cells, by overlap
const claims = [];
for (const it of items) {
  const claim = { q: it.live, cells: [] };
  const area = it.w * it.h;
  if (area > 1e-12) {
    for (let iy = clampCell(it.y, ch, Ny); iy <= clampCell(it.y + it.h, ch, Ny); iy++) {
      const oy = Math.min(it.y + it.h, (iy + 1) * ch) - Math.max(it.y, iy * ch);
      if (oy <= 0) continue;
      for (let ix = clampCell(it.x, cw, Nx); ix <= clampCell(it.x + it.w, cw, Nx); ix++) {
        const ox = Math.min(it.x + it.w, (ix + 1) * cw) - Math.max(it.x, ix * cw);
        if (ox <= 0) continue;
        const k = idx(ix, iy), a = ox * oy;
        dead[k] += it.dead * a; persons[k] += it.persons * a / area; claim.cells.push([k, a / cellArea]);
      }
    }
  }
  claims.push(claim);
  if (it.point > 0) {           // shared between the four cell centres around it
    const gx = (it.x + it.w / 2) / cw - 0.5, gy = (it.y + it.h / 2) / ch - 0.5;
    const ix = Math.floor(gx), iy = Math.floor(gy), tx = gx - ix, ty = gy - iy;
    for (const [dx, wx] of [[0, 1 - tx], [1, tx]]) for (const [dy, wy] of [[0, 1 - ty], [1, ty]]) {
      const w = wx * wy;
      if (w <= 0) continue;
      dead[idx(Math.min(Nx - 1, Math.max(0, ix + dx)), Math.min(Ny - 1, Math.max(0, iy + dy)))] += it.point * w;
    }
  }
}
// circulation
const pathWidth = layout.design_rules && layout.design_rules.circulation_width_m > 0 ? layout.design_rules.circulation_width_m : 1.0;
for (const c of layout.circulation_paths || []) {
  if (!c.points_m || c.points_m.length < 2) continue;
  const claim = { q: ACCESSIBLE_LIVE, cells: [] };
  for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
    const x = (ix + 0.5) * cw, y = (iy + 0.5) * ch;
    let best = Infinity;
    for (let i = 0; i + 1 < c.points_m.length; i++) {
      const a = c.points_m[i], b = c.points_m[i + 1], dx = b.x_m - a.x_m, dy = b.y_m - a.y_m, l2 = dx * dx + dy * dy;
      const t = l2 < 1e-12 ? 0 : Math.max(0, Math.min(1, ((x - a.x_m) * dx + (y - a.y_m) * dy) / l2));
      best = Math.min(best, Math.hypot(a.x_m + t * dx - x, a.y_m + t * dy - y));
    }
    if (best <= pathWidth / 2) claim.cells.push([idx(ix, iy), 1]);
  }
  claims.push(claim);
}
// imposed load: highest intensity first, weighted by the share of the cell each covers; the rest carries the roof's own
const claimed = new Float64Array(n);
[...claims].sort((a, b) => b.q - a.q).forEach(c => { for (const [k, f] of c.cells) { const take = Math.min(f, 1 - claimed[k]); if (take > 0) { live[k] += c.q * take * cellArea; claimed[k] += take; } } });
for (let k = 0; k < n; k++) live[k] += ROOF_LIVE * (1 - claimed[k]) * cellArea;
// arrivals at the entries
for (const e of layout.entry_points || []) {
  const near = []; let nearest = 0, nd = Infinity;
  for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
    const d = Math.hypot((ix + 0.5) * cw - e.x_m, (iy + 0.5) * ch - e.y_m);
    if (d <= ENTRY_RADIUS) near.push(idx(ix, iy));
    if (d < nd) { nd = d; nearest = idx(ix, iy); }
  }
  if (!near.length) near.push(nearest);
  for (const k of near) persons[k] += ENTRY_PERSONS / near.length;
}

// ---- the structural grid
const st = layout.structure || {};
const vLines = [], hLines = [];
for (const g of st.grid_lines || []) {
  const dx = g.end_m.x_m - g.start_m.x_m, dy = g.end_m.y_m - g.start_m.y_m;
  if (Math.hypot(dx, dy) < 1e-6) continue;
  const vertical = Math.abs(dy) >= Math.abs(dx);
  const off = Math.atan2(vertical ? Math.abs(dx) : Math.abs(dy), vertical ? Math.abs(dy) : Math.abs(dx)) * 180 / Math.PI;
  if (off > 5) continue;
  (vertical ? vLines : hLines).push({ name: g.name || "", pos: vertical ? (g.start_m.x_m + g.end_m.x_m) / 2 : (g.start_m.y_m + g.end_m.y_m) / 2 });
}
const gridAssumed = vLines.length === 0 && hLines.length === 0;
const regular = len => { const m = Math.max(1, Math.round(len / ASSUMED_BAY)); return Array.from({ length: m - 1 }, (_, i) => ({ name: "", pos: len * (i + 1) / m })); };
const vl = gridAssumed ? regular(L) : vLines, hl = gridAssumed ? regular(W) : hLines;
const bounds = (lines, len) => {
  const all = [0, len, ...lines.filter(l => l.pos > CELL && l.pos < len - CELL).map(l => l.pos)].sort((a, b) => a - b);
  const out = [];
  for (const v of all) if (!out.length || v - out[out.length - 1] > CELL) out.push(v);
  out[out.length - 1] = len;
  return out;
};
const xs = bounds(vl, L), ys = bounds(hl, W);
const nbx = xs.length - 1, nby = ys.length - 1;
const bayDead = new Float64Array(nbx * nby), bayLive = new Float64Array(nbx * nby), bayPeople = new Float64Array(nbx * nby);
const overlapShares = (b, lo, hi) => { const out = []; for (let i = 0; i + 1 < b.length; i++) { const o = Math.min(hi, b[i + 1]) - Math.max(lo, b[i]); if (o > 1e-12) out.push([i, o / (hi - lo)]); } return out; };
for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
  const k = idx(ix, iy);
  for (const [r, fy] of overlapShares(ys, iy * ch, (iy + 1) * ch)) for (const [c, fx] of overlapShares(xs, ix * cw, (ix + 1) * cw)) {
    const b = r * nbx + c, w = fx * fy;
    bayDead[b] += dead[k] * w; bayLive[b] += live[k] * w; bayPeople[b] += persons[k] * w;
  }
}
const capacity = st.deck_capacity_kn_m2 > 0 ? st.deck_capacity_kn_m2 : 8.0;

// ---- columns, balance
let cols = (st.columns || []).filter(c => c.x_m >= -0.5 && c.x_m <= L + 0.5 && c.y_m >= -0.5 && c.y_m <= W + 0.5).map(c => [c.x_m, c.y_m]);
if (!cols.length) for (const x of vl.filter(l => l.pos >= -0.3 && l.pos <= L + 0.3)) for (const y of hl.filter(l => l.pos >= -0.3 && l.pos <= W + 0.3)) cols.push([x.pos, y.pos]);
const colLoad = new Float64Array(cols.length);
if (cols.length) for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
  const x = (ix + 0.5) * cw, y = (iy + 0.5) * ch;
  let best = 0, bd = Infinity;
  cols.forEach((c, i) => { const d = (c[0] - x) ** 2 + (c[1] - y) ** 2; if (d < bd) { bd = d; best = i; } });
  colLoad[best] += dead[idx(ix, iy)] + live[idx(ix, iy)];
}
const centreX = cols.length ? cols.reduce((s, c) => s + c[0], 0) / cols.length : L / 2;
const centreY = cols.length ? cols.reduce((s, c) => s + c[1], 0) / cols.length : W / 2;
let dS = 0, dX = 0, dY = 0, tS = 0, tX = 0, tY = 0, left = 0, top = 0;
for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
  const k = idx(ix, iy), x = (ix + 0.5) * cw, y = (iy + 0.5) * ch, d = dead[k], t = dead[k] + live[k];
  dS += d; dX += d * x; dY += d * y; tS += t; tX += t * x; tY += t * y;
  left += t * Math.max(0, Math.min(1, (centreX - ix * cw) / cw));
  top += t * Math.max(0, Math.min(1, (centreY - iy * ch) / ch));
}
const oracle = {
  totalDead: dS, totalLive: live.reduce((a, b) => a + b, 0), people: persons.reduce((a, b) => a + b, 0),
  eccX: (tX / tS - centreX) / L, eccY: (tY / tS - centreY) / W, deadEccX: (dX / dS - centreX) / L, deadEccY: (dY / dS - centreY) / W,
  left: 100 * left / tS, top: 100 * top / tS, centreX, centreY,
};

// ---- compare with the C# report
let worst = 0, compared = 0, bad = [];
const cmp = (name, a, b, tol) => {
  compared++;
  const err = Math.abs(a - b) / Math.max(1, Math.abs(b), Math.abs(a));
  worst = Math.max(worst, err);
  if (err > tol) bad.push(`${name}: oracle ${a} vs C# ${b}`);
};
const s = report.summary, bl = report.balance;
cmp("total permanent", oracle.totalDead, s.deadKn, 2e-6);
cmp("total imposed", oracle.totalLive, s.liveKn, 2e-6);
cmp("people", oracle.people, s.expectedPersons, 2e-6);
cmp("eccentricity x", oracle.eccX, bl.totalEccentricityX, 2e-5);
cmp("eccentricity y", oracle.eccY, bl.totalEccentricityY, 2e-5);
cmp("permanent eccentricity x", oracle.deadEccX, bl.deadEccentricityX, 2e-5);
cmp("permanent eccentricity y", oracle.deadEccY, bl.deadEccentricityY, 2e-5);
cmp("left share", oracle.left, bl.leftSharePercent, 2e-5);
cmp("top share", oracle.top, bl.topSharePercent, 2e-5);
cmp("centre x", oracle.centreX, bl.centreX, 2e-5);
cmp("centre y", oracle.centreY, bl.centreY, 2e-5);
if (report.bays.length !== nbx * nby) bad.push(`bay count: oracle ${nbx * nby} vs C# ${report.bays.length}`);
else report.bays.forEach((b, i) => {
  cmp(`${b.label} permanent`, bayDead[i], b.deadKn, 2e-6);
  cmp(`${b.label} imposed`, bayLive[i], b.liveKn, 2e-6);
  cmp(`${b.label} people`, bayPeople[i], b.persons, 2e-6);
  const area = (xs[i % nbx + 1] - xs[i % nbx]) * (ys[Math.floor(i / nbx) + 1] - ys[Math.floor(i / nbx)]);
  cmp(`${b.label} utilisation`, (bayDead[i] + bayLive[i]) / area / capacity, b.utilisation, 2e-5);
  const status = (bayDead[i] + bayLive[i]) / area / capacity > 1 ? "over" : ((bayDead[i] + bayLive[i]) / area / capacity > MARGINAL_FROM ? "marginal" : "ok");
  if (status !== b.status) bad.push(`${b.label} status: oracle ${status} vs C# ${b.status}`);
});
if (report.columns.length !== cols.length) bad.push(`column count: oracle ${cols.length} vs C# ${report.columns.length}`);
else report.columns.forEach((c, i) => cmp(`${c.label} load`, colLoad[i], c.loadKn, 2e-6));
const status = Math.max(Math.abs(oracle.eccX), Math.abs(oracle.eccY)) > HIGH_E ? "unbalanced" : (Math.max(Math.abs(oracle.eccX), Math.abs(oracle.eccY)) > MARGINAL_E ? "marginal" : "balanced");
if (status !== bl.status) bad.push(`balance status: oracle ${status} vs C# ${bl.status}`);

console.log(`${compared} values compared, worst relative difference ${worst.toExponential(2)}`);
if (bad.length) { console.log("MISMATCH\n  " + bad.slice(0, 15).join("\n  ")); process.exit(1); }
console.log("ORACLE MATCH");
