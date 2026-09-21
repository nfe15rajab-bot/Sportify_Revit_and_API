// Independent re-implementation of the structural load model's arithmetic, from the specification in the header of StructuralLoadCore.cs.
// It reads the layout JSON itself (geometry, roof outline, grid, columns, entries, paths) and takes only each piece's INTENSITIES (permanent and
// imposed load per m2, people, a tree's weight) from the C# report, so what it checks is the mapping from pieces to cells, bays, columns and
// balance, not the build-up weights (those are spot-checked against the providers' published figures).
//
// The roof need not be a rectangle and the grid need not be square to it. Every area that involves the outline or a slanted grid line (how much
// of a cell is roof, how much of a cell lies in a bay) is computed by SCANLINE INTEGRATION, not by clipping polygons as the C# does: the length
// of the part of a vertical line that lies inside the region is linear between a handful of breakpoints, so a 3-point Gauss rule per interval is
// exact. A slip in one method is not repeated in the other.
//
//   dotnet run --project Tools/StructuralCheck -c Release -- <layout.json> --json > report.json
//   node Tools/StructuralCheck/oracle.js <layout.json> report.json
const fs = require("fs");
const [layoutPath, reportPath] = process.argv.slice(2);
const layout = JSON.parse(fs.readFileSync(layoutPath, "utf8"));
const report = JSON.parse(fs.readFileSync(reportPath, "utf8"));

const CELL = 0.5, ROOF_FINISHES = 0.5, ROOF_LIVE = 0.75, ACCESSIBLE_LIVE = 3.0, ENTRY_RADIUS = 3.0, ENTRY_PERSONS = 10, ASSUMED_BAY = 8.4;
const MARGINAL_FROM = 0.8, MARGINAL_E = 0.05, HIGH_E = 0.10;
const AXIS_TOL_DEG = 0.05, MIN_BAY_AREA = 0.5;
const r4 = v => Math.round(v * 1e4) / 1e4;

const L = layout.roof_context.length_m, W = layout.roof_context.width_m;

// ---- the roof's outline in the plan's coordinates (y down), or null for the rectangle
function outlineOf() {
  const poly = layout.roof_context.source_boundary_polygon;
  if (!poly || poly.length < 3) return null;
  const pts = poly.map(p => [r4(p.x_m), r4(W - p.y_m)]);
  // a rectangle equal to the bounding box (however it is wound or repeated) is the rectangle
  const uniq = pts.filter((p, i) => i === 0 || Math.hypot(p[0] - pts[i - 1][0], p[1] - pts[i - 1][1]) > 1e-6);
  if (uniq.length > 1 && Math.hypot(uniq[0][0] - uniq[uniq.length - 1][0], uniq[0][1] - uniq[uniq.length - 1][1]) < 1e-6) uniq.pop();
  const onBox = uniq.every(p => (Math.abs(p[0]) < 0.05 || Math.abs(p[0] - L) < 0.05) && (Math.abs(p[1]) < 0.05 || Math.abs(p[1] - W) < 0.05));
  if (uniq.length === 4 && onBox) return null;
  // collinear vertices are not corners
  let poly2 = uniq, changed = true;
  while (changed && poly2.length >= 3) {
    changed = false;
    for (let i = 0; i < poly2.length; i++) {
      const a = poly2[(i + poly2.length - 1) % poly2.length], b = poly2[i], c = poly2[(i + 1) % poly2.length];
      const cross = (b[0] - a[0]) * (c[1] - b[1]) - (b[1] - a[1]) * (c[0] - b[0]), dot = (b[0] - a[0]) * (c[0] - b[0]) + (b[1] - a[1]) * (c[1] - b[1]);
      if (Math.abs(cross) < 1e-9 && dot > 0) { poly2 = poly2.filter((_, j) => j !== i); changed = true; break; }
    }
  }
  return poly2.length >= 3 ? poly2 : null;
}
const outline = outlineOf();
const roofPoly = outline || [[0, 0], [L, 0], [L, W], [0, W]];

// ---- scanline integration: area and moments of {x in [X0,X1], y in [Y0,Y1], inside the roof, every half-plane a*x + b*y + c >= 0}
function scan(poly, x) {
  const ys = [];
  for (let i = 0; i < poly.length; i++) {
    const [xa, ya] = poly[i], [xb, yb] = poly[(i + 1) % poly.length];
    if ((xa <= x && x < xb) || (xb <= x && x < xa)) ys.push(ya + (x - xa) * (yb - ya) / (xb - xa));
  }
  ys.sort((p, q) => p - q);
  const out = [];
  for (let i = 0; i + 1 < ys.length; i += 2) out.push([ys[i], ys[i + 1]]);
  return out;
}
function region(rect, half, poly = roofPoly) {
  const [X0, Y0, X1, Y1] = rect;
  const xs = [X0, X1];
  const cutY = (xa, ya, xb, yb, y) => (ya - y) * (yb - y) < 0 ? xa + (y - ya) * (xb - xa) / (yb - ya) : null;
  const lines = half.map(h => ({ a: h[0], b: h[1], c: h[2] }));
  const segs = poly.map((p, i) => [p, poly[(i + 1) % poly.length]]);
  for (const p of poly) xs.push(p[0]);
  for (const [p, q] of segs) for (const y of [Y0, Y1]) { const x = cutY(p[0], p[1], q[0], q[1], y); if (x !== null) xs.push(x); }
  for (const h of lines) {
    for (const y of [Y0, Y1]) if (Math.abs(h.a) > 1e-12) xs.push(-(h.b * y + h.c) / h.a);
    for (const [p, q] of segs) {                          // the half-plane's line against a polygon edge
      const fa = h.a * p[0] + h.b * p[1] + h.c, fb = h.a * q[0] + h.b * q[1] + h.c;
      if (fa * fb < 0) xs.push(p[0] + (q[0] - p[0]) * fa / (fa - fb));
    }
  }
  for (let i = 0; i < lines.length; i++) for (let j = i + 1; j < lines.length; j++) {    // two half-planes' lines against each other
    const p = lines[i], q = lines[j], det = p.a * q.b - q.a * p.b;
    if (Math.abs(det) > 1e-12) xs.push((p.b * q.c - q.b * p.c) / det);
  }
  const bp = [...new Set(xs.filter(x => x >= X0 - 1e-12 && x <= X1 + 1e-12).map(x => Math.min(X1, Math.max(X0, x))))].sort((p, q) => p - q);
  const nodes = [[0.5 - Math.sqrt(0.6) / 2, 5 / 18], [0.5, 8 / 18], [0.5 + Math.sqrt(0.6) / 2, 5 / 18]];
  let area = 0, mx = 0, my = 0;
  for (let i = 0; i + 1 < bp.length; i++) {
    const a = bp[i], b = bp[i + 1], span = b - a;
    if (span < 1e-13) continue;
    for (const [t, w] of nodes) {
      const x = a + t * span;
      let iv = scan(poly, x).map(([lo, hi]) => [Math.max(lo, Y0), Math.min(hi, Y1)]).filter(([lo, hi]) => hi > lo);
      for (const h of lines) {
        iv = iv.map(([lo, hi]) => {
          if (Math.abs(h.b) < 1e-12) return h.a * x + h.c >= 0 ? [lo, hi] : [1, 0];
          const bound = -(h.a * x + h.c) / h.b;
          return h.b > 0 ? [Math.max(lo, bound), hi] : [lo, Math.min(hi, bound)];
        }).filter(([lo, hi]) => hi > lo);
      }
      let len = 0, mom = 0;
      for (const [lo, hi] of iv) { len += hi - lo; mom += (hi * hi - lo * lo) / 2; }
      area += w * span * len; mx += w * span * x * len; my += w * span * mom;
    }
  }
  return { area, cx: area > 1e-12 ? mx / area : 0, cy: area > 1e-12 ? my / area : 0 };
}

// ---- geometry of every piece the C# report lists, by id
const byId = new Map();
for (const z of layout.zones || []) byId.set(z.id, { x: z.bounding_box.top_left_x_m, y: z.bounding_box.top_left_y_m, w: z.bounding_box.width_m, h: z.bounding_box.height_m,
  poly: z.points && z.points.length >= 3 ? z.points.map(p => [p.x_m, p.y_m]) : null });
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
  const isTree = i.kind === "Tree" || i.kind === "Furniture";       // a concentrated load: the piece's weight at its centre
  return { ...g, dead: isTree ? 0 : i.deadKnM2, point: isTree ? i.deadKn : 0, live: i.liveKnM2, persons: i.persons };
});

const Nx = Math.max(1, Math.round(L / CELL)), Ny = Math.max(1, Math.round(W / CELL));
const cw = L / Nx, ch = W / Ny, cellArea = cw * ch, n = Nx * Ny;
const dead = new Float64Array(n).fill(ROOF_FINISHES * cellArea), live = new Float64Array(n), persons = new Float64Array(n);
const idx = (ix, iy) => iy * Nx + ix;
const clampCell = (v, size, count) => Math.min(count - 1, Math.max(0, Math.floor(v / size)));

// how much of each cell is roof
const cover = new Float64Array(n).fill(1);
if (outline) for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) cover[idx(ix, iy)] = Math.min(1, region([ix * cw, iy * ch, (ix + 1) * cw, (iy + 1) * ch], []).area / cellArea);

// ---- pieces onto cells, by overlap
const claims = [];
for (const it of items) {
  const claim = { q: it.live, cells: [] };
  const area = it.poly ? region([-1e4, -1e4, 1e4, 1e4], [], it.poly).area : it.w * it.h;
  if (area > 1e-12) {
    for (let iy = clampCell(it.y, ch, Ny); iy <= clampCell(it.y + it.h, ch, Ny); iy++) {
      const oy = Math.min(it.y + it.h, (iy + 1) * ch) - Math.max(it.y, iy * ch);
      if (oy <= 0) continue;
      for (let ix = clampCell(it.x, cw, Nx); ix <= clampCell(it.x + it.w, cw, Nx); ix++) {
        const ox = Math.min(it.x + it.w, (ix + 1) * cw) - Math.max(it.x, ix * cw);
        if (ox <= 0) continue;
        const k = idx(ix, iy), a = it.poly ? region([ix * cw, iy * ch, (ix + 1) * cw, (iy + 1) * ch], [], it.poly).area : ox * oy;
        if (a <= 1e-12) continue;
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
// arrivals at the entries: shared by how much of each cell is roof (cells that are at least half roof)
for (const e of layout.entry_points || []) {
  const near = []; let nearest = 0, nd = Infinity;
  for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
    if (cover[idx(ix, iy)] < 0.5) continue;
    const d = Math.hypot((ix + 0.5) * cw - e.x_m, (iy + 0.5) * ch - e.y_m);
    if (d <= ENTRY_RADIUS) near.push(idx(ix, iy));
    if (d < nd) { nd = d; nearest = idx(ix, iy); }
  }
  if (!near.length) near.push(nearest);
  const cs = near.reduce((s, k) => s + cover[k], 0);
  if (cs <= 1e-9) continue;
  for (const k of near) persons[k] += ENTRY_PERSONS / cs;
}
// a cell carries only what is on its roof part
for (let k = 0; k < n; k++) { dead[k] *= cover[k]; live[k] *= cover[k]; persons[k] *= cover[k]; }

// ---- the structural grid: a line square to the roof's axes needs only where it crosses; a slanted one keeps its two points
const st = layout.structure || {};
const vLines = [], hLines = [];
for (const g of st.grid_lines || []) {
  const x0 = r4(g.start_m.x_m), y0 = r4(g.start_m.y_m), x1 = r4(g.end_m.x_m), y1 = r4(g.end_m.y_m);
  const dx = x1 - x0, dy = y1 - y0;
  if (Math.hypot(dx, dy) < 1e-6) continue;
  const vertical = Math.abs(dy) >= Math.abs(dx);
  const off = Math.atan2(vertical ? Math.abs(dx) : Math.abs(dy), vertical ? Math.abs(dy) : Math.abs(dx)) * 180 / Math.PI;
  const line = { name: g.name || "", vertical };
  if (off <= AXIS_TOL_DEG) line.pos = vertical ? (x0 + x1) / 2 : (y0 + y1) / 2;
  else {
    line.geom = true;
    // vertical: x = x0 + m (y - y0) with m = dx/dy; horizontal: y = y0 + k (x - x0) with k = dy/dx
    if (vertical) { line.m = dx / dy; line.x0 = x0; line.y0 = y0; line.pos = x0 + (W / 2 - y0) * dx / dy; }
    else { line.k = dy / dx; line.x0 = x0; line.y0 = y0; line.pos = y0 + (L / 2 - x0) * dy / dx; }
  }
  (vertical ? vLines : hLines).push(line);
}
const gridAssumed = vLines.length === 0 && hLines.length === 0;
const regular = len => { const m = Math.max(1, Math.round(len / ASSUMED_BAY)); return Array.from({ length: m - 1 }, (_, i) => ({ name: "", vertical: false, pos: len * (i + 1) / m })); };
const vl = gridAssumed ? regular(L).map(l => ({ ...l, vertical: true })) : vLines, hl = gridAssumed ? regular(W) : hLines;
// lines of one direction that meet on the roof cannot cut it into strips: leave out the line that meets the most (the more slanted, then the later, on a tie)
const meet = (p, q) => {                       // where two extended lines cross, or null when parallel
  const pt = l => l.geom ? (l.vertical ? [l.x0, l.y0, l.x0 + l.m * 1, l.y0 + 1] : [l.x0, l.y0, l.x0 + 1, l.y0 + l.k * 1]) : (l.vertical ? [l.pos, 0, l.pos, 1] : [0, l.pos, 1, l.pos]);
  const [a0, a1, a2, a3] = pt(p), [b0, b1, b2, b3] = pt(q);
  const d1x = a2 - a0, d1y = a3 - a1, d2x = b2 - b0, d2y = b3 - b1, den = d1x * d2y - d1y * d2x;
  if (Math.abs(den) < 1e-12) return null;
  const t = ((b0 - a0) * d2y - (b1 - a1) * d2x) / den;
  return [a0 + t * d1x, a1 + t * d1y];
};
const deviation = l => l.geom ? Math.atan(Math.abs(l.vertical ? l.m : l.k)) * 180 / Math.PI : 0;
const droppedLines = [];
const withoutCrossings = lines => {
  const list = [...lines];
  for (;;) {
    const counts = list.map(() => 0);
    for (let i = 0; i < list.length; i++) for (let j = i + 1; j < list.length; j++) {
      if (!list[i].geom && !list[j].geom) continue;
      const at = meet(list[i], list[j]);
      if (!at || at[0] <= 0.05 || at[0] >= L - 0.05 || at[1] <= 0.05 || at[1] >= W - 0.05) continue;
      counts[i]++; counts[j]++;
    }
    let worst = -1;
    for (let i = 0; i < list.length; i++) {
      if (!counts[i]) continue;
      if (worst < 0 || counts[i] > counts[worst] || (counts[i] === counts[worst] && (deviation(list[i]) > deviation(list[worst]) + 1e-9 || (Math.abs(deviation(list[i]) - deviation(list[worst])) <= 1e-9 && list[i].pos > list[worst].pos)))) worst = i;
    }
    if (worst < 0) return list;
    droppedLines.push(list[worst].name || "(unnamed)");
    list.splice(worst, 1);
  }
};
const inside = (lines, len) => {
  const sorted = withoutCrossings(lines.filter(l => l.pos > CELL && l.pos < len - CELL)).sort((a, b) => a.pos - b.pos);
  const kept = []; let last = 0;
  for (const l of sorted) { if (l.pos - last <= CELL) continue; kept.push(l); last = l.pos; }
  return kept;
};
const vk = inside(vl, L), hk = inside(hl, W);
const xs = [0, ...vk.map(l => l.pos), L], ys = [0, ...hk.map(l => l.pos), W];
const nbx = xs.length - 1, nby = ys.length - 1;

// half-plane (a, b, c): a x + b y + c >= 0 for the side of a line: greater = larger x (vertical) / larger y (horizontal)
function side(line, greater) {
  let a, b, c;
  if (line.vertical) {                      // F = x - x_line(y)
    if (line.geom) { a = 1; b = -line.m; c = line.m * line.y0 - line.x0; } else { a = 1; b = 0; c = -line.pos; }
  } else {                                  // G = y - y_line(x)
    if (line.geom) { a = -line.k; b = 1; c = line.k * line.x0 - line.y0; } else { a = 0; b = 1; c = -line.pos; }
  }
  return greater ? [a, b, c] : [-a, -b, -c];
}
const plain = !outline && ![...vk, ...hk].some(l => l.geom);
const parts = [];
for (let r = 0; r < nby; r++) for (let c = 0; c < nbx; c++) {
  const half = [];
  if (c > 0) half.push(side(vk[c - 1], true));
  if (c < nbx - 1) half.push(side(vk[c], false));
  if (r > 0) half.push(side(hk[r - 1], true));
  if (r < nby - 1) half.push(side(hk[r], false));
  if (plain) { parts.push({ c, r, half, area: (xs[c + 1] - xs[c]) * (ys[r + 1] - ys[r]), cx: (xs[c] + xs[c + 1]) / 2, cy: (ys[r] + ys[r + 1]) / 2, reported: parts.length }); continue; }
  const reg = region([0, 0, L, W], half);
  if (reg.area < 1e-9) continue;
  parts.push({ c, r, half, area: reg.area, cx: reg.cx, cy: reg.cy, reported: -1 });
}
let bays = parts;
if (!plain) {
  let real = parts.filter(p => p.area >= MIN_BAY_AREA);
  if (!real.length) real = [...parts].sort((a, b) => b.area - a.area).slice(0, 1);
  real.forEach((p, i) => { p.reported = i; });
  for (const p of parts.filter(q => q.reported < 0)) {
    let best = null, bd = Infinity;
    for (const q of real) { const d = (q.cx - p.cx) ** 2 + (q.cy - p.cy) ** 2; if (d < bd) { bd = d; best = q; } }
    p.reported = best.reported;
  }
  bays = real;
}
const nb = bays.length;
const bayDead = new Float64Array(nb), bayLive = new Float64Array(nb), bayPeople = new Float64Array(nb), bayArea = new Float64Array(nb);
if (plain) bays.forEach((p, i) => { bayArea[i] = p.area; });
else parts.forEach(p => { bayArea[p.reported] += p.area; });
for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
  const k = idx(ix, iy);
  if (cover[k] <= 1e-12) continue;
  const cell = [ix * cw, iy * ch, (ix + 1) * cw, (iy + 1) * ch];
  const w = new Map(); let total = 0;
  for (const p of parts) {
    const a = region(cell, p.half).area;
    if (a <= 1e-14) continue;
    w.set(p.reported, (w.get(p.reported) || 0) + a); total += a;
  }
  if (total <= 1e-14) continue;
  for (const [b, a] of w) { const s = a / total; bayDead[b] += dead[k] * s; bayLive[b] += live[k] * s; bayPeople[b] += persons[k] * s; }
}
const capacity = st.deck_capacity_kn_m2 > 0 ? st.deck_capacity_kn_m2 : 8.0;

// ---- columns, balance
const inPoly = (x, y) => { let ins = false; for (let i = 0, j = roofPoly.length - 1; i < roofPoly.length; j = i++) { const [xi, yi] = roofPoly[i], [xj, yj] = roofPoly[j]; if ((yi > y) !== (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) ins = !ins; } return ins; };
const distEdge = (x, y) => { let best = Infinity; for (let i = 0; i < roofPoly.length; i++) { const [xa, ya] = roofPoly[i], [xb, yb] = roofPoly[(i + 1) % roofPoly.length]; const dx = xb - xa, dy = yb - ya, l2 = dx * dx + dy * dy, t = l2 < 1e-18 ? 0 : Math.max(0, Math.min(1, ((x - xa) * dx + (y - ya) * dy) / l2)); best = Math.min(best, Math.hypot(xa + t * dx - x, ya + t * dy - y)); } return best; };
const cols = (st.columns || []).filter(c => c.x_m >= -0.5 && c.x_m <= L + 0.5 && c.y_m >= -0.5 && c.y_m <= W + 0.5)
  .filter(c => !outline || inPoly(c.x_m, c.y_m) || distEdge(c.x_m, c.y_m) <= 0.5).map(c => [c.x_m, c.y_m]);
if (!cols.length) {
  for (const x of vl.filter(l => l.pos >= -0.3 && l.pos <= L + 0.3)) for (const y of hl.filter(l => l.pos >= -0.3 && l.pos <= W + 0.3)) {
    let p;
    if (!x.geom && !y.geom) p = [x.pos, y.pos];
    else {                                      // a vertical line x = xv0 + mv y with a horizontal one y = yh0 + kh x
      const mv = x.geom ? x.m : 0, xv0 = x.geom ? x.x0 - x.m * x.y0 : x.pos;
      const kh = y.geom ? y.k : 0, yh0 = y.geom ? y.y0 - y.k * y.x0 : y.pos;
      const yy = (yh0 + kh * xv0) / (1 - kh * mv);
      p = [xv0 + mv * yy, yy];
    }
    if (p[0] < -0.3 || p[0] > L + 0.3 || p[1] < -0.3 || p[1] > W + 0.3) continue;
    if (outline && !inPoly(p[0], p[1]) && distEdge(p[0], p[1]) > 0.3) continue;
    cols.push(p);
  }
}
const colLoad = new Float64Array(cols.length), colArea = new Float64Array(cols.length);
if (cols.length) for (let iy = 0; iy < Ny; iy++) for (let ix = 0; ix < Nx; ix++) {
  const k = idx(ix, iy);
  if (cover[k] <= 0) continue;
  const x = (ix + 0.5) * cw, y = (iy + 0.5) * ch;
  let best = 0, bd = Infinity;
  cols.forEach((c, i) => { const d = (c[0] - x) ** 2 + (c[1] - y) ** 2; if (d < bd) { bd = d; best = i; } });
  colLoad[best] += dead[k] + live[k]; colArea[best] += cellArea * cover[k];
}
const roofRegion = region([0, 0, L, W], []);
const centreX = cols.length ? cols.reduce((s, c) => s + c[0], 0) / cols.length : (outline ? roofRegion.cx : L / 2);
const centreY = cols.length ? cols.reduce((s, c) => s + c[1], 0) / cols.length : (outline ? roofRegion.cy : W / 2);
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
let worst = 0, compared = 0;
const bad = [];
const cmp = (name, a, b, tol) => {
  compared++;
  const err = Math.abs(a - b) / Math.max(1, Math.abs(b), Math.abs(a));
  worst = Math.max(worst, err);
  if (err > tol) bad.push(`${name}: oracle ${a} vs C# ${b}`);
};
const s = report.summary, bl = report.balance;
cmp("roof area", roofRegion.area, s.roofAreaM2, 2e-6);
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
if (report.bays.length !== nb) bad.push(`bay count: oracle ${nb} vs C# ${report.bays.length}`);
else report.bays.forEach((b, i) => {
  if (b.label !== `Bay ${bays[i].c + 1}.${bays[i].r + 1}`) bad.push(`bay ${i} label: oracle Bay ${bays[i].c + 1}.${bays[i].r + 1} vs C# ${b.label}`);
  cmp(`${b.label} permanent`, bayDead[i], b.deadKn, 2e-6);
  cmp(`${b.label} imposed`, bayLive[i], b.liveKn, 2e-6);
  cmp(`${b.label} people`, bayPeople[i], b.persons, 2e-6);
  cmp(`${b.label} area`, bayArea[i], b.areaM2, 2e-6);
  cmp(`${b.label} utilisation`, (bayDead[i] + bayLive[i]) / bayArea[i] / capacity, b.utilisation, 2e-5);
  const status = (bayDead[i] + bayLive[i]) / bayArea[i] / capacity > 1 ? "over" : ((bayDead[i] + bayLive[i]) / bayArea[i] / capacity > MARGINAL_FROM ? "marginal" : "ok");
  if (status !== b.status) bad.push(`${b.label} status: oracle ${status} vs C# ${b.status}`);
  if (!plain && (!b.polygon || b.polygon.length < 6)) bad.push(`${b.label}: a bay of a roof with an outline or a slanted grid must carry its polygon`);
});
if (report.columns.length !== cols.length) bad.push(`column count: oracle ${cols.length} vs C# ${report.columns.length}`);
else report.columns.forEach((c, i) => { cmp(`${c.label} load`, colLoad[i], c.loadKn, 2e-6); cmp(`${c.label} tributary area`, colArea[i], c.tributaryM2, 2e-6); });
const status = Math.max(Math.abs(oracle.eccX), Math.abs(oracle.eccY)) > HIGH_E ? "unbalanced" : (Math.max(Math.abs(oracle.eccX), Math.abs(oracle.eccY)) > MARGINAL_E ? "marginal" : "balanced");
if (status !== bl.status) bad.push(`balance status: oracle ${status} vs C# ${bl.status}`);

console.log(`${compared} values compared, worst relative difference ${worst.toExponential(2)}${plain ? "" : "  (" + (outline ? "outline" : "rectangle") + ", " + [...vk, ...hk].filter(l => l.geom).length + " slanted lines)"}`);
if (bad.length) { console.log("MISMATCH\n  " + bad.slice(0, 15).join("\n  ")); process.exit(1); }
console.log("ORACLE MATCH");
