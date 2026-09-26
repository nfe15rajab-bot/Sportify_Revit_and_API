// One source for the sports' dimensions and the analysis thresholds: the database's seed against the web app's table and the add-in's offline fallbacks.
//
//   node Tools/SourceParity/check.js <path to the web app>
//
// At run time the database is the source (the web app reads AnalysisParameters and FieldVariants from the API, the add-in reads AnalysisParameters). Each also keeps a copy to
// work with when the API cannot be reached: data.js's FIELDS and analysisController.js's ANALYSIS_PARAM_DEFAULTS in the web app, AnalysisReferenceData.Defaults in the add-in.
// A copy that has drifted from the seed means a fresh install (seeded) and an offline session (copies) give different numbers for the same layout. This starts the REAL API on
// a fresh database (never the team's reference.db), asks it for what it seeds, and compares all three, both ways: nothing in a copy that the seed lacks, nothing in the seed
// that a copy lacks, and the same value everywhere.
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const { spawnSync } = require("child_process");
const { buildApi, startApi, request, freePort } = require("../lib/runApi.js");

const webDir = process.argv[2];
if (!webDir) { console.error("usage: node check.js <web app folder>"); process.exit(2); }

let problems = 0;
const differ = m => { problems++; console.log("DIFFERENT " + m); };

// ── the web app's copies, evaluated as the page would ──
const ctx = vm.createContext({ console: { log() {} } });
vm.runInContext(fs.readFileSync(path.join(webDir, "data.js"), "utf8"), ctx, { filename: "data.js" });
const fields = JSON.parse(JSON.stringify(vm.runInContext("FIELDS", ctx)));
const analysisSrc = fs.readFileSync(path.join(webDir, "analysisController.js"), "utf8");
const m = /const ANALYSIS_PARAM_DEFAULTS = (\{[\s\S]*?\n\});/.exec(analysisSrc);
if (!m) { console.log("FAIL  ANALYSIS_PARAM_DEFAULTS not found in analysisController.js"); process.exit(1); }
const webDefaults = vm.runInNewContext("(" + m[1] + ")");

// ── the add-in's copies, from the real C# ──
const binDir = path.join(__dirname, "..", "AnalysisParity", "bin", "Release");
let dll = null;
for (const tfm of fs.existsSync(binDir) ? fs.readdirSync(binDir) : []) { const p = path.join(binDir, tfm, "AnalysisParity.dll"); if (fs.existsSync(p)) dll = p; }
if (!dll) { console.log("FAIL  AnalysisParity is not built"); process.exit(1); }
const addin = JSON.parse(spawnSync("dotnet", [dll, "--defaults"], { encoding: "utf8" }).stdout).parameters;

(async () => {
  const out = buildApi();
  const port = await freePort();
  const api = await startApi(out, port, {});
  try {
    const get = u => request(port, "GET", u, { headers: { Host: `localhost:${port}` } });
    const params = (await get("/api/AnalysisParameters")).json;
    const sports = (await get("/api/Sports")).json;
    if (!Array.isArray(params) || !Array.isArray(sports)) { console.log("FAIL  the API did not answer"); process.exit(1); }

    // ── thresholds ──
    const seeded = {};
    for (const p of params) (seeded[p.category] = seeded[p.category] || {})[p.key] = p.value;
    const flat = o => Object.entries(o).flatMap(([c, kv]) => Object.entries(kv).map(([k, v]) => [c + " / " + k, v])).sort((a, b) => a[0].localeCompare(b[0]));
    const seedFlat = flat(seeded);
    for (const [name, copy] of [["the web app (analysisController.js ANALYSIS_PARAM_DEFAULTS)", webDefaults], ["the add-in (AnalysisReferenceData.Defaults)", addin]]) {
      const c = new Map(flat(copy));
      for (const [k, v] of seedFlat) {
        if (!c.has(k)) differ(`${k} = ${v} is seeded by the API but ${name} has no fallback for it`);
        else if (c.get(k) !== v) differ(`${k}: the API seeds ${v}, ${name} says ${c.get(k)}`);
      }
      for (const [k] of c) if (!seeded[k.split(" / ")[0]] || seeded[k.split(" / ")[0]][k.split(" / ")[1]] === undefined) differ(`${k} is in ${name} but the API does not seed it`);
    }

    // ── sport dimensions ──
    const keyOf = name => String(name).trim().toLowerCase().split(/[\s(]/)[0];
    const seededFields = {};
    for (const s of sports) for (const v of s.variants || []) (seededFields[keyOf(s.name)] = seededFields[keyOf(s.name)] || {})[v.variant] = { l: v.lengthM, w: v.widthM, runoff: v.runoffM, h: v.heightMinM, norm: v.norm };
    for (const [sport, variants] of Object.entries(fields)) {
      for (const [variant, d] of Object.entries(variants)) {
        const s = seededFields[sport] && seededFields[sport][variant];
        if (!s) { differ(`FIELDS.${sport}.${variant} is in data.js but the API does not seed it`); continue; }
        for (const k of ["l", "w", "runoff", "h", "norm"]) if (s[k] !== d[k]) differ(`FIELDS.${sport}.${variant}.${k}: data.js says ${d[k]}, the API seeds ${s[k]}`);
      }
    }
    for (const [sport, variants] of Object.entries(seededFields)) for (const variant of Object.keys(variants)) if (!fields[sport] || !fields[sport][variant]) differ(`the API seeds ${sport}/${variant} but data.js has no FIELDS.${sport}.${variant}`);

    const nFields = Object.values(fields).reduce((n, v) => n + Object.keys(v).length, 0);
    console.log(problems === 0
      ? `PARITY OK: ${seedFlat.length} analysis parameters and ${nFields} field variants are the same in the API's seed, the web app's fallbacks and the add-in's fallbacks`
      : `${problems} DIFFERENCE(S): the seed (Sportify.Api ReferenceDataSeeder), data.js, analysisController.js and AnalysisReferenceData.cs have to change together`);
  } finally {
    api.child.kill();
    await new Promise(r => setTimeout(r, 1500));
    try { fs.rmSync(out, { recursive: true, force: true }); } catch (e) { /* a temp folder */ }
  }
  process.exit(problems === 0 ? 0 : 1);
})().catch(e => { console.log("FAIL  the check itself stopped: " + (e && e.message || e)); process.exit(1); });
