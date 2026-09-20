// An independent check of the sun and shade model, written from the specification in the header of SunShadeCore.cs and NOT from its code:
//   - the sun by the acos form of the bearing (the C# uses atan2),
//   - the roof outline by a winding number (the C# a ray-crossing count),
//   - every shadow by RAY CASTING from each cell centre toward the sun against the real solids (a wall as a box, a tree as a sphere, a plate at
//     its height), where the C# builds shadow shapes (a convex hull of a swept footprint, an ellipse, a shifted rectangle),
//   - the zones, the sun hours, the peak shade, the state after the recommended pieces,
//   - and the recommendation itself: every piece must obey the placement rules, and the FIRST piece must be the one the search rule picks.
//
//   dotnet run --project Tools/SunCheck -c Release -- <layout.json> --json > report.json
//   node Tools/SunCheck/oracle.js report.json
//
// It takes the model's INPUTS (as the tool dumped them: the layout already read) and the report's pieces; everything else it recomputes.
const fs = require("fs");
const dump = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
const I = dump.inputs, R = dump.report;
const Deg = Math.PI / 180;
const STEP = 0.5, MIN_EL = 2, WINDOW = [11, 16], MARGIN = 2, MIN_PLANT = 2;
const TREE_TAU = { jun: 0.25, mar: 0.5, dec: 0.7 };
const DAYS = [{ key: "jun", doy: 172 }, { key: "mar", doy: 80 }, { key: "dec", doy: 355 }];
const lat = I.latitude == null ? 51 : I.latitude;
const north = I.north == null ? 0 : I.north;
const target = I.shadeTarget == null ? 50 : I.shadeTarget;
const minSun = I.gardenMinSun == null ? 4 : I.gardenMinSun;
const allowed = I.equipment === "light" || I.equipment === "fixed" ? I.equipment : "all";

// ---- the sun
function decl(doy) {
  const b = 2 * Math.PI * (doy - 1) / 365;
  return 0.006918 - 0.399912 * Math.cos(b) + 0.070257 * Math.sin(b) - 0.006758 * Math.cos(2 * b) + 0.000907 * Math.sin(2 * b) - 0.002697 * Math.cos(3 * b) + 0.00148 * Math.sin(3 * b);
}
function sunAt(doy, t) {
  const d = decl(doy), phi = lat * Deg, w = 15 * (t - 12) * Deg;
  const sinAlt = Math.sin(phi) * Math.sin(d) + Math.cos(phi) * Math.cos(d) * Math.cos(w);
  const alt = Math.asin(Math.max(-1, Math.min(1, sinAlt)));
  // the acos form of the bearing, mirrored after solar noon
  let cosA = (Math.sin(d) - Math.sin(alt) * Math.sin(phi)) / (Math.cos(alt) * Math.cos(phi));
  cosA = Math.max(-1, Math.min(1, cosA));
  let bearing = Math.acos(cosA) / Deg;                 // 0..180, measured from north through east
  if (w > 0) bearing = 360 - bearing;                  // afternoon: west of south
  const el = alt / Deg;
  const rel = (bearing - north) * Deg;
  return { el, up: el >= MIN_EL, u: [Math.sin(rel), -Math.cos(rel)], tan: Math.tan(alt), sin: Math.sin(alt), cos: Math.cos(alt) };
}
const times = doy => { const out = []; for (let k = 0; k < 48; k++) { const t = 0.25 + STEP * k; if (sunAt(doy, t).up) out.push(t); } return out; };

// ---- the roof
const Nx = Math.max(1, Math.ceil(I.roofLength / 0.5 - 1e-9)), Ny = Math.max(1, Math.ceil(I.roofWidth / 0.5 - 1e-9));
const cw = I.roofLength / Nx, ch = I.roofWidth / Ny, N = Nx * Ny;
function winding(poly, x, y) {
  let wn = 0;
  for (let i = 0; i < poly.length; i++) {
    const a = poly[i], b = poly[(i + 1) % poly.length];
    const cross = (b[0] - a[0]) * (y - a[1]) - (x - a[0]) * (b[1] - a[1]);
    if (a[1] <= y) { if (b[1] > y && cross > 0) wn++; } else if (b[1] <= y && cross < 0) wn--;
  }
  return wn !== 0;
}
const active = new Array(N).fill(true);
if (I.outline && I.outline.length >= 3) for (let j = 0; j < Ny; j++) for (let i = 0; i < Nx; i++) active[j * Nx + i] = winding(I.outline, (i + 0.5) * cw, (j + 0.5) * ch);
const cx = c => ((c % Nx) + 0.5) * cw, cy = c => (Math.floor(c / Nx) + 0.5) * ch;

// ---- ray casting against the solids
// the ray from (x, y, 0): horizontal direction u, rising as s * tan; a solid is hit when the ray is inside it at some s >= 0
function hitBox(x, y, u, tan, x0, y0, x1, y1, hmax) {      // a wall as a box: rectangle footprint, from the ground to hmax
  // slab method along the ray parameter s (horizontal distance)
  let s0 = 0, s1 = hmax / tan;
  for (const [p, d, lo, hi] of [[x, u[0], x0, x1], [y, u[1], y0, y1]]) {
    if (Math.abs(d) < 1e-12) { if (p < lo || p > hi) return false; continue; }
    let a = (lo - p) / d, b = (hi - p) / d;
    if (a > b) [a, b] = [b, a];
    s0 = Math.max(s0, a); s1 = Math.min(s1, b);
    if (s0 > s1) return false;
  }
  return s0 <= s1;
}
function wallRect(o) {                                      // the wall's rotated rectangle -> test in its own frame
  const ex = o.X1 - o.X0, ey = o.Y1 - o.Y0, len = Math.hypot(ex, ey);
  return { ex: ex / len, ey: ey / len, len, half: Math.max(o.ThicknessM, 0.05) / 2, x0: o.X0, y0: o.Y0, h: o.HeightM };
}
function hitWall(W, x, y, u, tan) {
  // rotate the ray into the wall's frame: a = along the wall, b = across it
  const px = x - W.x0, py = y - W.y0;
  const p = [px * W.ex + py * W.ey, -px * W.ey + py * W.ex];
  const d = [u[0] * W.ex + u[1] * W.ey, -u[0] * W.ey + u[1] * W.ex];
  let s0 = 0, s1 = W.h / tan;
  for (const [pp, dd, lo, hi] of [[p[0], d[0], 0, W.len], [p[1], d[1], -W.half, W.half]]) {
    if (Math.abs(dd) < 1e-12) { if (pp < lo || pp > hi) return false; continue; }
    let a = (lo - pp) / dd, b = (hi - pp) / dd;
    if (a > b) [a, b] = [b, a];
    s0 = Math.max(s0, a); s1 = Math.min(s1, b);
    if (s0 > s1) return false;
  }
  return true;
}
function hitSphere(x, y, u, sun, sx, sy, sz, r) {           // distance from the sphere's centre to the ray
  const d3 = [u[0] * sun.cos, u[1] * sun.cos, sun.sin];
  const v = [sx - x, sy - y, sz];
  const t = v[0] * d3[0] + v[1] * d3[1] + v[2] * d3[2];
  const px = v[0] - t * d3[0], py = v[1] - t * d3[1], pz = v[2] - t * d3[2];
  return px * px + py * py + pz * pz <= r * r;
}
function hitPlate(piece, x, y, u, sun) {                    // a thin horizontal plate at its height (a rectangle or a disc)
  const s = piece.heightM / sun.tan;
  const qx = x + s * u[0], qy = y + s * u[1];
  if (piece.shape === "disc") { const r = piece.widthM / 2; return (qx - (piece.x + r)) ** 2 + (qy - (piece.y + piece.depthM / 2)) ** 2 <= r * r; }
  return qx >= piece.x && qx <= piece.x + piece.widthM && qy >= piece.y && qy <= piece.y + piece.depthM;
}
const walls = I.obstacles.map(o => wallRect({ X0: o.X0 ?? o.x0, Y0: o.Y0 ?? o.y0, X1: o.X1 ?? o.x1, Y1: o.Y1 ?? o.y1, HeightM: o.HeightM ?? o.heightM, ThicknessM: o.ThicknessM ?? o.thicknessM })).filter(w => w.len > 1e-6 && w.h > 0);
const trees = I.plants.filter(p => p.height >= MIN_PLANT).map(p => { const r = Math.max(0.25, p.crown * 0.5); return { x: p.x, y: p.y, r, zc: Math.max(r, p.height - r) }; });

function fraction(x, y, sun, doy, pieces) {
  let f = 1;
  for (const W of walls) if (hitWall(W, x, y, sun.u, sun.tan)) f *= 0;
  const tau = TREE_TAU[DAYS.find(d => d.doy === doy).key];
  for (const T of trees) if (hitSphere(x, y, sun.u, sun, T.x, T.y, T.zc, T.r)) f *= tau;
  for (const P of pieces) {
    if (P.shape === "tree") { if (hitSphere(x, y, sun.u, sun, P.x + P.widthM / 2, P.y + P.depthM / 2, Math.max(2.5, P.heightM - 2.5), 2.5)) f *= tau; }
    else if (hitPlate(P, x, y, sun.u, sun)) f *= P.transmission;
  }
  return f;
}

// ---- the zones
const items = I.items;
const inRect = (x, y, rx, ry, rw, rh) => x >= rx && x <= rx + rw && y >= ry && y <= ry + rh;
const courts = items.filter(i => i.kind === "Court");
const zones = [];
for (const it of items) {
  if (it.kind === "Tree") continue;
  const kind = it.kind === "Court" ? "court" : it.kind === "Activity" ? "people" : "garden";
  const z = { id: it.id, label: it.name || it.label, kind, cells: [], x: it.x, y: it.y, w: it.w, h: it.h };
  for (let c = 0; c < N; c++) if (active[c] && inRect(cx(c), cy(c), it.x, it.y, it.w, it.h)) z.cells.push(c);
  zones.push(z);
  if (it.kind === "Court" && it.seats > 0) {
    const b = { id: it.id + "_seats", label: z.label + " spectators", kind: "spectators", cells: [], x: it.x - MARGIN, y: it.y - MARGIN, w: it.w + 2 * MARGIN, h: it.h + 2 * MARGIN };
    for (let c = 0; c < N; c++) {
      if (!active[c] || !inRect(cx(c), cy(c), b.x, b.y, b.w, b.h)) continue;
      if (courts.some(k => inRect(cx(c), cy(c), k.x, k.y, k.w, k.h))) continue;
      b.cells.push(c);
    }
    zones.push(b);
  }
}
const zoneList = zones.slice(0, 60);

// ---- the day, cell by cell
function dayFractions(doy, pieces) {
  return times(doy).map(t => {
    const sun = sunAt(doy, t), f = new Float64Array(N);
    for (let c = 0; c < N; c++) if (active[c]) f[c] = fraction(cx(c), cy(c), sun, doy, pieces);
    return { t, sun, f, window: doy === 172 && t >= WINDOW[0] && t <= WINDOW[1] };
  });
}
function measures(days) {
  const sunHours = days.map(d => { const h = new Float64Array(N); for (const s of d) for (let c = 0; c < N; c++) h[c] += s.f[c] * STEP; return h; });
  const peak = new Float64Array(N); let nw = 0;
  for (const s of days[0]) if (s.window) { nw++; for (let c = 0; c < N; c++) peak[c] += 1 - s.f[c]; }
  if (nw) for (let c = 0; c < N; c++) peak[c] /= nw;
  return { sunHours, peak };
}
const mean = (cells, v) => cells.length ? cells.reduce((s, c) => s + v[c], 0) / cells.length : 0;

const pieces = R.equipment;
const before = measures(DAYS.map(d => dayFractions(d.doy, [])));
const after = measures(DAYS.map(d => dayFractions(d.doy, pieces)));

let n = 0, bad = 0;
function cmp(name, got, want, tol) {
  n++;
  if (!(Math.abs(got - want) <= tol * Math.max(1, Math.abs(want)))) { bad++; if (bad < 20) console.log("  DIFF", name, "model", got, "oracle", want); }
}
const active_ = Array.from({ length: N }, (_, c) => c).filter(c => active[c]);
R.days.forEach((d, i) => {
  cmp(`days[${i}].roofMeanSunHours`, d.roofMeanSunHours, mean(active_, before.sunHours[i]), 2e-6);
  cmp(`days[${i}].roofMeanSunHoursAfter`, d.roofMeanSunHoursAfter, mean(active_, after.sunHours[i]), 2e-6);
  cmp(`days[${i}].noonElevationDeg`, d.noonElevationDeg, sunAt(DAYS[i].doy, 12).el, 2e-6);
});
cmp("zones", R.zones.length, zoneList.length, 0);
R.zones.forEach((z, i) => {
  const o = zoneList[i];
  cmp(`zones[${i}].kind`, z.kind === o.kind ? 0 : 1, 0, 0);
  cmp(`zones[${i}].areaM2`, z.areaM2, o.cells.length * cw * ch, 2e-6);
  cmp(`zones[${i}].sunHoursJune`, z.sunHoursJune, mean(o.cells, before.sunHours[0]), 2e-6);
  cmp(`zones[${i}].sunHoursMarch`, z.sunHoursMarch, mean(o.cells, before.sunHours[1]), 2e-6);
  cmp(`zones[${i}].sunHoursDecember`, z.sunHoursDecember, mean(o.cells, before.sunHours[2]), 2e-6);
  cmp(`zones[${i}].peakShadePercent`, z.peakShadePercent, 100 * mean(o.cells, before.peak), 2e-6);
  cmp(`zones[${i}].afterSunHoursJune`, z.afterSunHoursJune, mean(o.cells, after.sunHours[0]), 2e-6);
  cmp(`zones[${i}].afterPeakShadePercent`, z.afterPeakShadePercent, 100 * mean(o.cells, after.peak), 2e-6);
  const sh = 100 * mean(o.cells, before.peak), hrs = mean(o.cells, before.sunHours[0]);
  const status = !o.cells.length ? "n/a" : o.kind === "people" || o.kind === "spectators" ? (sh + 1e-9 >= target ? "ok" : "too-sunny") : o.kind === "garden" ? (hrs + 1e-9 >= minSun ? "ok" : "too-shaded") : "n/a";
  if (status !== z.status && !(o.kind === "court")) { bad++; n++; console.log("  DIFF status", z.label, z.status, status); } else n++;
});

// ---- the recommendation: rules, then the first piece
const CATALOGUE = [
  { key: "pergola", shape: "rect", group: "fixed", sizes: [[4, 3], [6, 4]], h: 2.6, tau: 0.15, w: 0.35, cp: 1.2 },
  { key: "canopy", shape: "rect", group: "fixed", sizes: [[4, 3], [6, 3]], h: 3.0, tau: 0.0, w: 0.5, cp: 1.2 },
  { key: "sail", shape: "rect", group: "light", sizes: [[5, 4], [6, 5]], h: 3.5, tau: 0.10, w: 0.03, cp: 1.5 },
  { key: "parasol", shape: "disc", group: "light", sizes: [[3.5, 3.5], [4.5, 4.5]], h: 2.8, tau: 0.10, w: 0.05, cp: 1.3 },
  { key: "tree", shape: "tree", group: "tree", sizes: [[3, 3]], h: 6.0, tau: 0.25, w: 13.5, cp: 0 },
];
const overlap = (a, b) => a[0] < b[2] && a[2] > b[0] && a[1] < b[3] && a[3] > b[1];
function segHitsRect(s0, s1, r) {
  let t0 = 0, t1 = 1; const dx = s1[0] - s0[0], dy = s1[1] - s0[1];
  const P = [-dx, dx, -dy, dy], Q = [s0[0] - r[0], r[2] - s0[0], s0[1] - r[1], r[3] - s0[1]];
  for (let i = 0; i < 4; i++) {
    if (Math.abs(P[i]) < 1e-12) { if (Q[i] < 0) return false; continue; }
    const q = Q[i] / P[i];
    if (P[i] < 0) { if (q > t1) return false; if (q > t0) t0 = q; } else { if (q < t0) return false; if (q < t1) t1 = q; }
  }
  return true;
}
function distPolyline(pts, x, y) {
  let best = Infinity;
  for (let i = 0; i + 1 < pts.length; i++) {
    const [ax, ay] = pts[i], [bx, by] = pts[i + 1], vx = bx - ax, vy = by - ay, l2 = vx * vx + vy * vy;
    const u = l2 < 1e-12 ? 0 : Math.max(0, Math.min(1, ((x - ax) * vx + (y - ay) * vy) / l2));
    best = Math.min(best, Math.hypot(x - (ax + u * vx), y - (ay + u * vy)));
  }
  return best;
}
function fits(t, w, d, x, y, placed) {
  const x1 = x + w, y1 = y + d, box = [x, y, x1, y1];
  for (const c of [[x, y], [x1, y], [x1, y1], [x, y1]]) {
    if (c[0] < 0.3 || c[0] > I.roofLength - 0.3 || c[1] < 0.3 || c[1] > I.roofWidth - 0.3) return false;
    if (I.outline && I.outline.length >= 3 && !winding(I.outline, c[0], c[1])) return false;
  }
  for (const k of courts) if (overlap(box, [k.x, k.y, k.x + k.w, k.y + k.h])) return false;
  for (const p of placed) if (overlap(box, [p.x, p.y, p.x + p.widthM, p.y + p.depthM])) return false;
  for (const dr of I.drains) if (dr[0] >= x - 0.3 && dr[0] <= x1 + 0.3 && dr[1] >= y - 0.3 && dr[1] <= y1 + 0.3) return false;
  for (const e of I.entries) if (e[0] >= x - 2 && e[0] <= x1 + 2 && e[1] >= y - 2 && e[1] <= y1 + 2) return false;
  for (const op of I.openings) {
    const xs = op.map(q => q[0]), ys = op.map(q => q[1]);
    if (overlap(box, [Math.min(...xs) - 0.3, Math.min(...ys) - 0.3, Math.max(...xs) + 0.3, Math.max(...ys) + 0.3])) return false;
  }
  for (const o of I.obstacles) {
    const h = o.HeightM ?? o.heightM;
    if (h < t.h - 0.3) continue;
    if (segHitsRect([o.X0 ?? o.x0, o.Y0 ?? o.y0], [o.X1 ?? o.x1, o.Y1 ?? o.y1], [x - 0.2, y - 0.2, x1 + 0.2, y1 + 0.2])) return false;
  }
  const posts = t.shape === "rect" ? [[x, y], [x1, y], [x1, y1], [x, y1]] : [[x + w / 2, y + d / 2]];
  for (const path of I.paths) for (const p of posts) if (distPolyline(path.points, p[0], p[1]) < path.width / 2 + 0.3) return false;
  return true;
}
// every piece the model placed obeys the rules
{
  const placedSoFar = [];
  pieces.forEach((p, i) => {
    const t = CATALOGUE.find(c => c.key === p.key);
    n++;
    if (!t || !fits(t, p.widthM, p.depthM, p.x, p.y, placedSoFar)) { bad++; console.log("  DIFF piece breaks a placement rule:", p.name, p.x, p.y); }
    placedSoFar.push(p);
  });
}
// the first piece is the one the search rule picks
if (pieces.length > 0) {
  const peopleZ = zoneList.filter(z => (z.kind === "people" || z.kind === "spectators") && z.cells.length > 0);
  const worst = peopleZ.map(z => ({ z, shade: 100 * mean(z.cells, before.peak) })).filter(o => o.shade + 1e-9 < target)
    .sort((a, b) => Math.round((target - b.shade) * 1e6) - Math.round((target - a.shade) * 1e6))[0].z;
  const jun = DAYS[0], junDay = dayFractions(jun.doy, []), window = junDay.filter(s => s.window);
  const member = new Uint8Array(N); worst.cells.forEach(c => member[c] = 1);
  const gardens = zoneList.filter(z => z.kind === "garden" && z.cells.length > 0);
  const hoursNow = gardens.map(g => mean(g.cells, before.sunHours[0]));
  const cands = [];
  let order = 0;
  for (const t of CATALOGUE.filter(c => allowed === "all" || c.group === allowed)) {
    for (const size of t.sizes) {
      const orients = t.shape === "rect" && Math.abs(size[0] - size[1]) > 1e-9 ? 2 : 1;
      for (let o = 0; o < orients; o++) {
        const w = o === 0 ? size[0] : size[1], d = o === 0 ? size[1] : size[0];
        const x0 = Math.floor(worst.x - 4), x1 = Math.ceil(worst.x + worst.w + 4 - w), y0 = Math.floor(worst.y - 4), y1 = Math.ceil(worst.y + worst.h + 4 - d);
        for (let x = x0; x <= x1 + 1e-9; x += 1) for (let y = y0; y <= y1 + 1e-9; y += 1) {
          order++;
          if (!fits(t, w, d, x, y, [])) continue;
          const piece = { key: t.key, shape: t.shape, x, y, widthM: w, depthM: d, heightM: t.h, transmission: t.tau };
          let sum = 0;
          for (const s of window) for (let c = 0; c < N; c++) if (member[c] && active[c]) {
            const px = cx(c), py = cy(c);
            const hit = piece.shape === "tree" ? hitSphere(px, py, s.sun.u, s.sun, x + w / 2, y + d / 2, Math.max(2.5, t.h - 2.5), 2.5) : hitPlate(piece, px, py, s.sun.u, s.sun);
            if (hit) sum += s.f[c] * (1 - (piece.shape === "tree" ? 0.25 : t.tau));
          }
          const gain = sum / (worst.cells.length * window.length);
          if (gain >= 0.03) {
            const area = t.shape === "disc" ? Math.PI * w * w / 4 : w * d;
            cands.push({ t, w, d, x, y, gain: Math.round(gain * 1e9) / 1e9, order, load: Math.round((t.shape === "tree" ? t.w * w * d : t.w * area) * 1000) / 1000 });
          }
        }
      }
    }
  }
  cands.sort((a, b) => b.gain - a.gain || a.order - b.order);
  let ranked = cands.slice(0, 48);
  if (ranked.length) {
    const top = ranked[0].gain;
    const near = ranked.filter(c => c.gain >= 0.9 * top - 1e-12).sort((a, b) => a.load - b.load || b.gain - a.gain || a.order - b.order);
    ranked = near.concat(ranked.filter(c => !near.includes(c)));
  }
  function keepsGardens(c) {
    const piece = { key: c.t.key, shape: c.t.shape, x: c.x, y: c.y, widthM: c.w, depthM: c.d, heightM: c.t.h, transmission: c.t.tau };
    const loss = gardens.map(() => 0);
    for (const s of junDay) for (let gi = 0; gi < gardens.length; gi++) for (const cell of gardens[gi].cells) {
      const px = cx(cell), py = cy(cell);
      const hit = piece.shape === "tree" ? hitSphere(px, py, s.sun.u, s.sun, c.x + c.w / 2, c.y + c.d / 2, Math.max(2.5, c.t.h - 2.5), 2.5) : hitPlate(piece, px, py, s.sun.u, s.sun);
      if (hit) loss[gi] += s.f[cell] * (1 - (piece.shape === "tree" ? 0.25 : c.t.tau)) * STEP;
    }
    return gardens.every((g, gi) => { const a = hoursNow[gi] - loss[gi] / g.cells.length; return hoursNow[gi] + 1e-9 >= minSun ? a + 1e-9 >= minSun : a + 1e-9 >= hoursNow[gi] - 0.25; });
  }
  const chosen = ranked.find(keepsGardens);
  const first = pieces[0];
  n++;
  if (!chosen || chosen.t.key !== first.key || Math.abs(chosen.x - first.x) > 1e-6 || Math.abs(chosen.y - first.y) > 1e-6 || Math.abs(chosen.w - first.widthM) > 1e-6 || Math.abs(chosen.d - first.depthM) > 1e-6) {
    bad++; console.log("  DIFF first piece: model", first.key, first.x, first.y, first.widthM + "x" + first.depthM, "oracle", chosen && [chosen.t.key, chosen.x, chosen.y, chosen.w + "x" + chosen.d]);
  }
  console.log(`  first piece checked against ${ranked.length} ranked of ${cands.length} feasible candidates for "${worst.label}"`);
}
console.log(bad === 0 ? `ORACLE MATCH: ${n} values, ${pieces.length} pieces` : `MISMATCHES: ${bad} of ${n}`);
process.exit(bad ? 1 : 0);
