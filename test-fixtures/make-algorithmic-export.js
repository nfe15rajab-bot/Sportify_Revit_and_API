// Makes a real export of the merged web app in Algorithmic placement mode, for the live Revit import test:
//
//   node test-fixtures/make-algorithmic-export.js <web app folder> <api url> <out.json>
//
// It loads the web app's own scripts (all of them but main.js) into a sandbox with a DOM that accepts everything, gives it the API's real catalogue (build-ups, plants, furniture, court options
// from a running Sportify.Api: run one on a COPY of reference.db), lets the real packing core lay out a roof the size of the one the first live import used (67.6 x 21 m, four entries),
// runs Apply as the button does, adds a few pieces of furniture and a tree the way the panels do, and writes buildCombinedPayload(): what "Export Combined JSON" gives.
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const [webDir, apiUrl, outFile] = process.argv.slice(2);
if (!webDir || !apiUrl || !outFile) { console.error("usage: node make-algorithmic-export.js <web app folder> <api url> <out.json>"); process.exit(2); }

function nothing() {
  return new Proxy(function () {}, {
    get(_, p) { return p === Symbol.toPrimitive ? () => "" : p === Symbol.iterator || p === "then" ? undefined : p === "length" ? 0 : nothing(); },
    apply() { return nothing(); }, construct() { return nothing(); }, set() { return true; }
  });
}
const sandbox = {
  console: { log() {}, warn() {}, error() {}, info() {} },
  document: { getElementById: () => nothing(), querySelector: () => nothing(), querySelectorAll: () => [], addEventListener() {}, createElement: () => nothing(), body: nothing(), documentElement: nothing(), readyState: "complete" },
  localStorage: { getItem: () => null, setItem() {} },
  sessionStorage: { getItem: () => null, setItem() {} },
  setTimeout, clearTimeout, setInterval: () => 0, clearInterval() {}, performance: { now: () => Date.now() },
  ResizeObserver: class { observe() {} disconnect() {} }, MutationObserver: class { observe() {} disconnect() {} },
  Math, Set, Map, Int16Array, Int32Array, Headers, TextEncoder, crypto: require("crypto").webcrypto, encodeURIComponent, URL,
  location: { origin: "http://localhost:8123" },
  // the web app's addresses answered by the API at apiUrl; everything else (the add-in, Nominatim) is not there
  fetch: async (url, init) => {
    if (typeof url === "string" && url.startsWith("http://localhost:5107")) return fetch(apiUrl + url.slice("http://localhost:5107".length), { headers: { Accept: "application/json" } });
    throw new Error("no network in this script: " + url);
  },
};
sandbox.window = sandbox;
const ctx = vm.createContext(sandbox);
const get = expr => vm.runInContext(expr, ctx);

const order = [...fs.readFileSync(path.join(webDir, "index.html"), "utf8").matchAll(/<script src="([A-Za-z0-9_.\/]+\.js)/g)].map(m => m[1]).filter(f => !/^vendor\//.test(f) && f !== "main.js");
const skipped = [];
for (const f of order) {
  try { vm.runInContext(fs.readFileSync(path.join(webDir, f), "utf8"), ctx, { filename: f }); }
  catch (e) { skipped.push(f + ": " + e.message); }
}
if (skipped.length) console.error("could not load: " + skipped.join(" | "));
// drawing needs a browser: what draws is replaced by nothing (the state it would draw is what the export reads)
vm.runInContext("drawCombineCanvas = function () {}; refreshSuggestions = function () {}; resetCombineView = function () {}; showToast = function () {};", ctx);

(async () => {
  // what main.js and the controllers set up that the payload reads
  await Promise.all([get("loadVegetation()"), get("loadFurniture()"), get("loadAssemblies()")]);
  vm.runInContext("try { initAnalysisReferenceData && 0 } catch (e) {}", ctx);
  await get("initAnalysisReferenceData()");

  const roof = { length: 67.6, width: 21, boundary: null, originXm: 0, originYm: 0, rotationDeg: 0 };
  const CS = get("combineState");
  CS.roof = Object.assign(CS.roof || {}, roof);
  CS.entryPoints = [
    { id: "e1", x_m: 0, y_m: 10.5, edge: "left" }, { id: "e2", x_m: 67.6, y_m: 10.5, edge: "right" },
    { id: "e3", x_m: 33.8, y_m: 0, edge: "top" }, { id: "e4", x_m: 33.8, y_m: 21, edge: "bottom" },
  ];

  const A = get("AlgoPlacement"), S = get("algoState");
  get("algoAdoptSpecifiedSizes()");
  const rect = (w, h) => [[0, 0], [w, 0], [w, h], [0, h]];
  const site = A.makeSite({ foot: rect(67.6, 21), setback: 1, entries: [[0, 9, 2, 12], [65.6, 9, 67.6, 12]] });
  const sport = n => A.SPORTS.find(s => s.name === n);
  const qty = { "Basketball Court": 1, "Volleyball": 1, "Multi Sport Court": 1, "Badminton": 1 };
  const requests = Object.entries(qty).flatMap(([n, k]) => Array.from({ length: k }, () => ({ name: n, w: sport(n).long, h: sport(n).short })));
  const plan = await A.planLayout(site, requests, { timeLimit: 2, seed: 7 });
  S.plan = plan; S.site = site; S.busy = false; S.blocks = [];
  await get("algoApply()");
  console.error(`placed ${CS.items.length} pieces, ${CS.zones.length} zones`);

  // furniture and a tree, the way the panels put them on the roof
  let x = 6;
  for (const key of Object.keys(get("FURNITURE")).slice(0, 5)) {
    get("furnitureState").key = key; get("pushFurnitureToCombine()");
  }
  for (const k of Object.keys(get("VEGETATION_TYPES")).slice(0, 2)) { get("vegetationState").typeKey = k; get("vegetationState").crown_m = get("VEGETATION_TYPES")[k].crown_m; get("pushVegetationToCombine()"); }
  for (const it of CS.tray.slice()) { it.x_m = x; it.y_m = 1.5; CS.items.push(it); x += 3.2; }
  CS.tray = [];

  const payload = get("buildCombinedPayload()");
  fs.writeFileSync(outFile, JSON.stringify(payload, null, 2));
  const cats = {};
  for (const p of payload.placements) cats[p.category] = (cats[p.category] || 0) + 1;
  console.error(`wrote ${outFile}: ${payload.placements.length} placements ${JSON.stringify(cats)}, ${(payload.zones || []).length} zones, ${(payload.assemblies || []).length} build-ups, roof finish ${payload.roof_finish ? payload.roof_finish.assembly_key : "none"}`);
})().catch(e => { console.error("FAILED: " + (e && e.stack || e)); process.exit(1); });
