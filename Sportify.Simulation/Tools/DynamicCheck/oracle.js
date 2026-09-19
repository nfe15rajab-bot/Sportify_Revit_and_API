// Independent re-implementation of the closed-form parts of the dynamic model: the snow load, the dynamic load factors of jumping, the
// deck's natural frequency, and the acceleration each activity causes in each bay. It takes only each bay's inputs (span, area, mass,
// participants, force) from the C# report and recomputes what follows from them, from the specification in the header of
// DynamicLoadCore.cs. (The crowd simulation is checked by its invariants and by running it twice, not re-implemented here.)
//
//   dotnet run --project Tools/DynamicCheck -c Release -- <layout.json> --json > report.json
//   node Tools/DynamicCheck/oracle.js <layout.json> report.json
const fs = require("fs");
const [layoutPath, reportPath] = process.argv.slice(2);
const layout = JSON.parse(fs.readFileSync(layoutPath, "utf8"));
const report = JSON.parse(fs.readFileSync(reportPath, "utf8"));
const g = 9.81, zeta = 0.03, E = 30e9, band = [0.8, 1.25];

// ---- snow (DIN EN 1991-1-3/NA)
const sk = (zone, a) => {
  const q = ((a + 140) / 760) ** 2;
  return { "1": Math.max(0.65, 0.19 + 0.91 * q), "1a": 1.25 * Math.max(0.65, 0.19 + 0.91 * q), "2": Math.max(0.85, 0.25 + 1.91 * q),
           "2a": 1.25 * Math.max(0.85, 0.25 + 1.91 * q), "3": Math.max(1.10, 0.31 + 2.91 * q) }[zone];
};
const site = layout.site_conditions || {};
const zone = site.snow_zone ? String(site.snow_zone).trim().toLowerCase() : "2";
const alt = site.altitude_m != null && site.altitude_set !== false && site.altitude_m !== null ? site.altitude_m : 100;
const skWant = sk(zone, alt);
let worst = 0, n = 0; const bad = [];
const cmp = (name, a, b, tol) => { n++; const err = Math.abs(a - b) / Math.max(1e-3, Math.abs(a), Math.abs(b)); worst = Math.max(worst, err); if (err > tol) bad.push(`${name}: oracle ${a} vs C# ${b}`); };
cmp("sk", skWant, report.weather.snow.skKnM2, 2e-6);
cmp("roof snow", 0.8 * skWant, report.weather.snow.roofKnM2, 2e-6);

// ---- dynamic load factors: numerical Fourier analysis of the half-sine pulse train (period 1, contact ratio a, mean 1)
const alphaNumeric = (a, k) => {
  const N = 400000; let re = 0, im = 0; const kp = Math.PI / (2 * a);
  for (let i = 0; i < N; i++) { const t = (i + 0.5) / N; const f = t < a ? kp * Math.sin(Math.PI * t / a) : 0; re += f * Math.cos(2 * Math.PI * k * t); im += f * Math.sin(2 * Math.PI * k * t); }
  return 2 / N * Math.hypot(re, im);
};
const acts = report.resonance.activities;
for (const a of acts) if (a.contactRatio > 0) a.alpha.forEach((v, i) => cmp(`${a.name} alpha${i + 1}`, alphaNumeric(a.contactRatio, i + 1), v, 2e-3));

// ---- one bay's acceleration for one activity at rhythm fp and deck frequency fn
const accel = (a, force, mass, fn, fp) => {
  let s = 0;
  a.alpha.forEach((al, i) => { const k = i + 1, r = k * fp / fn; const d = 1 / Math.sqrt((1 - r * r) ** 2 + 4 * zeta * zeta * r * r); const acc = 4 / Math.PI * (force / mass) * al * r * r * d; s += acc * acc; });
  return Math.sqrt(s) / g;
};
const worstAcc = (a, force, mass, fn) => { let best = 0, at = a.fpLowHz; for (let fp = a.fpLowHz; fp <= a.fpHighHz + 1e-9; fp += 0.05) { const v = accel(a, force, mass, fn, fp); if (v > best) { best = v; at = fp; } } return { best, at }; };

const estimated = report.resonance.estimated;
for (const b of report.resonance.bays) {
  // frequency from span, depth, mass (estimated) and the depth rule
  const depth = Math.min(0.6, Math.max(0.2, b.spanM / 25));
  cmp(`${b.label} depth`, depth, b.depthM, 1e-5);
  if (estimated) cmp(`${b.label} frequency`, Math.PI / 2 * Math.sqrt(E * depth ** 3 / 12 / (b.massKgM2 * b.spanM ** 4)), b.frequencyHz, 1e-5);
  for (const r of b.activities) {
    const a = acts.find(x => x.name === r.activity);
    cmp(`${b.label} ${a.name} force`, r.participants < 1 ? r.participants * 882.9 / b.areaM2 : (a.sync * r.participants + (1 - a.sync) * Math.sqrt(r.participants)) * 882.9 / b.areaM2, r.forcePa, 2e-4);
    if (r.participants <= 0) { cmp(`${b.label} ${a.name} none`, 0, r.accelerationBandG, 1e-9); continue; }
    const m = b.massKgM2 + r.participants * 90 / b.areaM2;
    const fn = estimated ? b.frequencyHz * Math.sqrt(b.massKgM2 / m) : b.frequencyHz;
    const nominal = worstAcc(a, r.forcePa, m, fn).best;
    let bandMax = nominal;
    if (estimated) for (let s = band[0]; s <= band[1] + 1e-9; s += 0.025) bandMax = Math.max(bandMax, worstAcc(a, r.forcePa, m, fn * s).best);
    cmp(`${b.label} ${a.name} acceleration`, nominal, r.accelerationG, 2e-4);
    cmp(`${b.label} ${a.name} band acceleration`, bandMax, r.accelerationBandG, 2e-4);
    const ratio = bandMax / a.limitG;
    const status = ratio > 1 ? "exceeds" : ratio > 0.5 ? "marginal" : "ok";
    if (status !== r.status) bad.push(`${b.label} ${a.name} status: oracle ${status} vs C# ${r.status}`);
  }
}

// ---- the sweep of the worst bay
const rr = report.resonance;
if (rr.sweepG.length) {
  const wb = rr.bays.find(b => b.label === rr.worstBay), a = acts.find(x => x.name === rr.sweepActivity), resp = wb.activities.find(x => x.activity === a.name);
  const m = wb.massKgM2 + resp.participants * 90 / wb.areaM2, fn = estimated ? wb.frequencyHz * Math.sqrt(wb.massKgM2 / m) : wb.frequencyHz;
  rr.sweepG.forEach((v, i) => cmp(`sweep ${i}`, accel(a, resp.forcePa, m, fn, a.fpLowHz + i * 0.05), v, 2e-4));
}

console.log(`${n} values compared, worst relative difference ${worst.toExponential(2)}`);
if (bad.length) { console.log("MISMATCH\n  " + bad.slice(0, 15).join("\n  ")); process.exit(1); }
console.log("ORACLE MATCH");
