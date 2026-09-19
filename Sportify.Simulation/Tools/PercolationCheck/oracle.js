// Independent re-implementation of the rainfall / percolation model, written from its specification (the header of
// PercolationCore.cs), reading the layout JSON itself. Compared against the C# report.
//   dotnet run --project Tools/PercolationCheck -c Release -- <layout.json> --json > report.json
//   node Tools/PercolationCheck/oracle.js <layout.json> report.json
const fs = require("fs");
const layout = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
const csharp = JSON.parse(fs.readFileSync(process.argv[3], "utf8").replace(/^﻿/, ""));

const K = { cell: 10, surface: 2, wetStart: 1.0, fcOfSat: 0.7, resOfSat: 0.2, exponent: 2.5, cupsPerMm: 0.2, overflow: 6, lag: 90, bareLag: 30, film: 1, series: 30, tail: 60 };
const scenarios = [[10, 180], [40, 30], [108, 10]];   // mm/h, minutes

const assemblies = {};
for (const a of layout.assemblies) assemblies[a.key] = a;
const isGravel = n => /gravel|kies|split|grit|sand/.test((n || "").toLowerCase());

function specFor(a) {
  const layers = a.layers.map(l => ({ fn: l.function, name: l.name, mm: l.thickness_m * 1000 }));
  const sub = layers.filter(l => l.fn === "substrate").reduce((s, l) => s + l.mm, 0);
  const drain = layers.filter(l => l.fn === "drainage").reduce((s, l) => s + l.mm, 0);
  const paved = a.category === "walkway" || layers[0].fn === "wearing";
  const spec = { sub, drain, has: sub >= K.cell && !paved, film: K.film };
  spec.intercept = paved ? 0 : (isGravel(layers[0].name) ? 0.3 : 1.0);
  if (!spec.has) return spec;
  const intensive = a.category === "intensive";
  let ts = intensive ? 0.45 : 0.50;
  spec.ks = intensive ? 0.2 : 0.5;
  if (a.saturated_kg_m2 != null && a.water_storage_l_m2 != null && a.water_storage_l_m2 > 0) {
    const cal = (a.water_storage_l_m2 - drain * K.cupsPerMm - (drain > 0 ? K.overflow : 0)) / sub;
    if (cal >= 0.30 && cal <= 0.65) ts = cal;
  }
  spec.ts = ts; spec.tfc = K.fcOfSat * ts; spec.tr = K.resOfSat * ts;
  return spec;
}

function simulate(spec, mmH, minutes) {
  const n = spec.has ? Math.round(spec.sub / K.cell) : 0, dz = n ? spec.sub / n : 0;
  const start = spec.has ? spec.tr + K.wetStart * (spec.tfc - spec.tr) : 0;
  const th = new Array(n).fill(start);
  const cups = spec.drain * K.cupsPerMm;
  let surf = 0, icpt = 0, store = 0, rain = 0, runoff = 0, first = -1, peak = 0, maxFill = 0, sat = false, sample = 0, surfRunoff = 0;
  const rainS = minutes * 60, total = rainS + K.tail * 60, rate = mmH / 3600;
  const drainRate = t => spec.ks * Math.pow(Math.max(0, t - spec.tfc) / Math.max(1e-9, spec.ts - spec.tfc), K.exponent);
  for (let t = 0; t < total; t++) {
    const r = t < rainS ? rate : 0;
    rain += r;
    const held = Math.min(r, Math.max(0, spec.intercept - icpt)); icpt += held; surf += r - held;
    let out;
    if (n === 0) {
      const over = Math.max(0, surf - spec.film); surf -= over; store += over;
      const above = Math.max(0, store - cups); const rel = Math.min(above, above / K.lag); store -= rel; out = rel;
    } else {
      const f = new Array(n + 1).fill(0);
      f[0] = Math.min(Math.min(surf, spec.ks), Math.max(0, (spec.ts - th[0]) * dz));
      for (let i = 1; i < n; i++) {
        const ex = Math.max(0, th[i - 1] - spec.tfc);
        f[i] = Math.max(0, Math.min(Math.min(drainRate(th[i - 1]), ex * dz), (spec.ts - th[i]) * dz));
      }
      { const ex = Math.max(0, th[n - 1] - spec.tfc); f[n] = Math.max(0, Math.min(drainRate(th[n - 1]), ex * dz)); }
      for (let i = 0; i < n; i++) th[i] += (f[i] - f[i + 1]) / dz;
      surf -= f[0];
      const so = Math.max(0, surf - K.surface); surf -= so; surfRunoff += so;
      store += f[n];
      const above = Math.max(0, store - cups); const rel = Math.min(above, above / K.lag); store -= rel;
      out = rel + so;
    }
    runoff += out;
    if (out * 3600 > 0.1 && first < 0) first = t / 60;
    for (let i = 0; i < n; i++) { maxFill = Math.max(maxFill, Math.max(0, Math.min(1, (th[i] - spec.tr) / (spec.ts - spec.tr)))); if (th[i] >= spec.ts - 0.005) sat = true; }
    sample += out;
    if ((t + 1) % K.series === 0) { peak = Math.max(peak, sample / K.series * 3600); sample = 0; }
  }
  return { rain, runoff, first, peak, maxFill, sat, surfRunoff };
}

function bare(mmH, minutes) {
  let film = 0, q = 0, rain = 0, runoff = 0, sample = 0, peak = 0, first = -1;
  const rainS = minutes * 60, total = rainS + K.tail * 60, rate = mmH / 3600;
  for (let t = 0; t < total; t++) {
    const r = t < rainS ? rate : 0; rain += r; film += r;
    const over = Math.max(0, film - K.film); film -= over; q += over;
    const rel = Math.min(q, q / K.bareLag); q -= rel; runoff += rel;
    if (rel * 3600 > 0.1 && first < 0) first = t / 60;
    sample += rel; if ((t + 1) % K.series === 0) { peak = Math.max(peak, sample / K.series * 3600); sample = 0; }
  }
  return { rain, runoff, peak, first };
}

let bad = 0, n = 0;
const near = (a, b, tol, what) => { n++; if (Math.abs(a - b) > tol * Math.max(1, Math.abs(b))) { bad++; if (bad < 15) console.log("  MISMATCH", what, "oracle", a, "csharp", b); } };

layout.zones.forEach((z, zi) => {
  const spec = specFor(assemblies[z.assembly_key]);
  const c = csharp.zones[zi];
  scenarios.forEach(([mmH, min], k) => {
    const run = simulate(spec, mmH, min), b = bare(mmH, min), r = c.scenarios[k];
    const tag = "zone " + (zi + 1) + " " + r.scenario;
    near(run.rain, r.rainMm, 1e-4, tag + " rain");
    near(run.runoff, r.runoffMm, 1e-4, tag + " runoff");
    near(100 * (1 - run.runoff / b.runoff), r.retainedPercent, 1e-4, tag + " retained %");
    near(run.peak, r.peakRunoffMmH, 1e-4, tag + " peak");
    near(run.first, r.firstRunoffMin, 1e-4, tag + " first runoff");
    near(100 * run.maxFill, r.maxSubstrateFillPercent, 1e-4, tag + " fill %");
    if (run.sat !== r.saturated) { bad++; console.log("  MISMATCH", tag, "saturated", run.sat, r.saturated); }
  });
});
console.log(bad === 0 ? "ORACLE MATCH: " + layout.zones.length + " zones x 3 scenarios, " + n + " values" : "ORACLE MISMATCHES: " + bad + " of " + n);
process.exit(bad ? 1 : 0);
