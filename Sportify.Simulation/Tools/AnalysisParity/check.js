// Embodied carbon (LCA), fire safety and accessibility: the web app's own functions against the add-in's, on every layout given.
//
//   node Tools/AnalysisParity/check.js <path to the web app> <layout.json> [<layout.json> ...]
//
// The add-in's LCA, Fire Safety and Accessibility commands are ports of the web app's analyzeLCA(), analyzeFireSafety() and analyzeAccessibility(): the same decisions on
// the same layout, so that a designer who sees "over the limit" in the browser does not see "within" in Revit. The circulation engine underneath them has its own check
// (Tools/CirculationCheck); this compares what is built on it: the embodied-carbon sum and which material a piece is made of, the longest walk against the reference distance,
// the walkway width against the reference width, every piece reachable.
//
// It does not re-implement anything. carbon.js and analysisController.js (with rules.js, data.js) are loaded as they are into a vm with the DOM stubbed out and given the layout
// as the Combine tab would hold it; the add-in's side is the real EmbodiedCarbon.cs / SafetyAnalysis.cs (Tools/AnalysisParity/Program.cs). To exercise the tier fallback and the
// "missing" counts, every second field piece has its picked material removed (it must then be the material its quality tier means) and every fourth material has no carbon
// figure; both sides get the same modified layout and the same material list.
const fs = require("fs");
const os = require("os");
const path = require("path");
const vm = require("vm");
const { spawnSync } = require("child_process");

const [webDir, ...layouts] = process.argv.slice(2);
if (!webDir || !layouts.length) { console.error("usage: node check.js <web app folder> <layout.json> ..."); process.exit(2); }
const tools = path.join(__dirname, "..");
const dll = (() => {
  const bin = path.join(__dirname, "bin", "Release");
  for (const tfm of fs.existsSync(bin) ? fs.readdirSync(bin) : []) { const p = path.join(bin, tfm, "AnalysisParity.dll"); if (fs.existsSync(p)) return p; }
  return null;
})();
if (!dll) { console.error("AnalysisParity is not built: dotnet build Tools/AnalysisParity -c Release"); process.exit(2); }

// ── the web app's scripts, as the page loads them, with a DOM that accepts everything ──
function nothing() {
  return new Proxy(function () {}, {
    get(_, p) { return p === Symbol.toPrimitive ? () => "" : p === Symbol.iterator || p === "then" ? undefined : p === "length" ? 0 : nothing(); },
    apply() { return nothing(); }, construct() { return nothing(); }, set() { return true; }
  });
}
const sandbox = {
  console: { log() {}, warn() {}, error() {}, info() {} }, Math, Set, Map, Int16Array, Int32Array,
  document: { getElementById: () => nothing(), querySelector: () => nothing(), querySelectorAll: () => [], addEventListener() {}, createElement: () => nothing(), body: nothing(), readyState: "complete" },
  localStorage: { getItem: () => null, setItem() {} },
  setTimeout, clearTimeout, setInterval: () => 0,
  fetch: async () => { throw new Error("no network in this check"); },
};
sandbox.window = sandbox;
const ctx = vm.createContext(sandbox);
const load = f => vm.runInContext(fs.readFileSync(path.join(webDir, f), "utf8"), ctx, { filename: f });
// combineField.js draws and is DOM all the way down: getFootprint is its definition (a piece's box, turned by its rotation)
vm.runInContext("function getFootprint(obj) { const rotated = (obj.rotation % 180) !== 0; return { w: rotated ? obj.width_m : obj.length_m, h: rotated ? obj.length_m : obj.width_m }; }", ctx);
for (const f of ["data.js", "rules.js", "carbon.js", "analysisController.js", "designPanel.js"]) load(f);
const get = expr => vm.runInContext(expr, ctx);

let problems = 0;
const differ = m => { problems++; console.log("DIFFERENT " + m); };
const near = (a, b) => Math.abs(a - b) <= 1e-9 * Math.max(1, Math.abs(a), Math.abs(b));

// ── the tier table and the offline thresholds are the same in both ──
const defaults = JSON.parse(spawnSync("dotnet", [dll, "--defaults"], { encoding: "utf8" }).stdout);
const webTier = get("QUALITY_REFERENCE_MATERIAL");
if (JSON.stringify(Object.entries(webTier).sort()) !== JSON.stringify(Object.entries(defaults.quality_reference_material).sort()))
  differ(`the quality tier -> material table: web ${JSON.stringify(webTier)} vs add-in ${JSON.stringify(defaults.quality_reference_material)}`);

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-analysis-parity-"));
let checked = 0, withCarbon = 0, withFire = 0;
try {
  for (const file of layouts) {
    const label = path.basename(file);
    const layout = JSON.parse(fs.readFileSync(file, "utf8"));

    // every second field piece loses its picked material: it must then be the material its tier means, in both
    let n = 0;
    for (const p of layout.placements || []) {
      const m = p.category === "garden" ? p.parameters?.garden?.materials : p.parameters?.materials;
      if (m && m.reference_material && (n++ % 2) === 1) m.reference_material = null;
    }
    const modified = path.join(tmp, label);
    fs.writeFileSync(modified, JSON.stringify(layout));

    // the web side holds the layout as combineState: a piece at its bounding box, its materials in sourceJson
    const items = (layout.placements || []).map(p => ({
      id: p.id, kind: p.category, x_m: p.bounding_box.top_left_x_m, y_m: p.bounding_box.top_left_y_m, rotation: 0,
      length_m: p.bounding_box.width_m, width_m: p.bounding_box.height_m,
      sourceJson: p.category === "garden" ? { garden: { materials: p.parameters?.garden?.materials } } : { materials: p.parameters?.materials },
    }));
    const names = [...new Set(items.map(it => get("referenceMaterialName")(it)).filter(Boolean))].sort();
    const materials = names.map((name, i) => ({ name, embodiedCarbonValue: i % 4 === 3 ? null : Math.round((4.25 + 3.125 * i) * 1000) / 1000 }));
    const materialsFile = path.join(tmp, label + ".materials.json");
    fs.writeFileSync(materialsFile, JSON.stringify(materials));

    sandbox.combineState = {
      roof: { length: layout.roof_context.length_m, width: layout.roof_context.width_m },
      items,
      entryPoints: (layout.entry_points || []).map(e => ({ x_m: e.x_m, y_m: e.y_m, edge: e.edge })),
    };
    vm.runInContext("analysisMaterialsCache = " + JSON.stringify(materials) + "; analysisParametersCache = [];", ctx);
    if (layout.design_rules && typeof layout.design_rules.circulation_width_m === "number") get("DESIGN_RULES").circulationWidth_m = layout.design_rules.circulation_width_m;
    const webLca = get("analyzeLCA")(), webFire = get("analyzeFireSafety")(), webAccess = get("analyzeAccessibility")();
    const webMetric = get("computeCarbonMetric")();

    const run = spawnSync("dotnet", [dll, modified, materialsFile], { encoding: "utf8" });
    if (run.status !== 0) { differ(`${label}: AnalysisParity exit ${run.status}: ${run.stderr || run.stdout}`); continue; }
    const cs = JSON.parse(run.stdout);
    checked++;

    // LCA
    if (webLca.status === "empty") { if (cs.lca.total !== 0) differ(`${label}: LCA, web empty but add-in counts ${cs.lca.total} pieces`); }
    else {
      withCarbon += webLca.coveredCount > 0 ? 1 : 0;
      if (!near(webLca.totalKg, cs.lca.total_kg)) differ(`${label}: LCA total kg: web ${webLca.totalKg} vs add-in ${cs.lca.total_kg}`);
      if (webLca.coveredCount !== cs.lca.covered || webLca.missingCount !== cs.lca.missing || webLca.totalCount !== cs.lca.total)
        differ(`${label}: LCA counts covered/missing/total: web ${webLca.coveredCount}/${webLca.missingCount}/${webLca.totalCount} vs add-in ${cs.lca.covered}/${cs.lca.missing}/${cs.lca.total}`);
      // the Design panel's CO2 metric is the same sum (null instead of 0 when nothing could be counted)
      const metric = webMetric.totalKg == null ? 0 : webMetric.totalKg;
      if (!near(metric, webLca.totalKg) || webMetric.covered !== webLca.coveredCount) differ(`${label}: the web's own two carbon figures differ: Analysis ${webLca.totalKg} (${webLca.coveredCount}) vs Design panel ${metric} (${webMetric.covered})`);
    }

    // fire safety
    if (webFire.status === "empty" || webFire.status === "no-entries") {
      if (cs.fire.status !== webFire.status) differ(`${label}: fire status web ${webFire.status} vs add-in ${cs.fire.status}`);
    } else {
      withFire++;
      if (cs.fire.status !== webFire.status) differ(`${label}: fire status web ${webFire.status} vs add-in ${cs.fire.status}`);
      else if (webFire.status === "fail") { if (cs.fire.unreachable !== webFire.unreachableCount) differ(`${label}: fire unreachable web ${webFire.unreachableCount} vs add-in ${cs.fire.unreachable}`); }
      else {
        if (!near(webFire.maxDist, cs.fire.max_dist_m)) differ(`${label}: fire longest walk web ${webFire.maxDist} vs add-in ${cs.fire.max_dist_m}`);
        if (webFire.maxTravelDistance !== cs.fire.max_travel_distance_m) differ(`${label}: fire reference distance web ${webFire.maxTravelDistance} vs add-in ${cs.fire.max_travel_distance_m}`);
        if (webFire.withinLimit !== cs.fire.within_limit) differ(`${label}: fire within limit web ${webFire.withinLimit} vs add-in ${cs.fire.within_limit}`);
      }
    }

    // accessibility
    if (webAccess.status === "empty") { if (cs.access.status !== "empty") differ(`${label}: accessibility status web empty vs add-in ${cs.access.status}`); }
    else {
      if (webAccess.widthOk !== cs.access.width_ok || webAccess.reachOk !== cs.access.reach_ok) differ(`${label}: accessibility width/reach ok: web ${webAccess.widthOk}/${webAccess.reachOk} vs add-in ${cs.access.width_ok}/${cs.access.reach_ok}`);
      if (webAccess.currentWidth !== cs.access.current_width_m || webAccess.minWidth !== cs.access.min_width_m) differ(`${label}: accessibility widths: web ${webAccess.currentWidth}/${webAccess.minWidth} vs add-in ${cs.access.current_width_m}/${cs.access.min_width_m}`);
    }
  }
} finally { fs.rmSync(tmp, { recursive: true, force: true }); }

console.log(problems === 0
  ? `PARITY OK: ${checked} layouts (${withCarbon} with carbon counted, ${withFire} with a fire-safety walk); LCA, fire safety and accessibility agree between carbon.js / analysisController.js and the add-in`
  : `${problems} DIFFERENCE(S): change carbon.js / analysisController.js and EmbodiedCarbon.cs / SafetyAnalysis.cs together`);
process.exit(problems === 0 ? 0 : 1);
