// Circulation, the web's rules.js against the add-in's CirculationEngine.
//
//   dotnet run --project Tools/CirculationCheck -c Release -- <layout.json> > report.json
//   node Tools/CirculationCheck/oracle.js <layout.json> report.json <path to the web app's rules.js>
//
// CirculationEngine.cs says it is "a C# port of rules.js's computeCirculation, kept numerically identical on purpose so Fire Safety and Accessibility never disagree
// with the web app's own Combine rules checklist". This is the check that says so. It does NOT re-implement the algorithm: it loads the REAL rules.js into a vm
// context, feeds it the layout as the Combine tab would hold it (a piece at its bounding box, entries on the edges, the design rules), and takes each piece's walking
// distance from the path rules.js drew (the sum of its segments; the C# side sums the same grid steps). Same distances, same unreachable pieces, or it fails.
//
// What it does not cover: how the web turns a piece's rotation into its bounding box on export (that is combineController.js's exporter, which needs a browser); the
// bounding box is taken as given here, as the add-in takes it.
const fs = require("fs");
const vm = require("vm");

const [layoutPath, reportPath, rulesPath] = process.argv.slice(2);
if (!layoutPath || !reportPath || !rulesPath) { console.error("usage: node oracle.js <layout.json> <report.json> <rules.js>"); process.exit(2); }

const layout = JSON.parse(fs.readFileSync(layoutPath, "utf8"));
const csharp = JSON.parse(fs.readFileSync(reportPath, "utf8"));

// rules.js is browser script: top-level consts and functions, no DOM. getFootprint lives in combineField.js (which draws); this is its definition.
const ctx = { Math, Set, Int16Array, Int32Array, console };
vm.createContext(ctx);
vm.runInContext(
  "function getFootprint(obj) { const rotated = (obj.rotation % 180) !== 0; return { w: rotated ? obj.width_m : obj.length_m, h: rotated ? obj.length_m : obj.width_m }; }\n" +
  fs.readFileSync(rulesPath, "utf8") + "\n;globalThis.__rules = { computeCirculation, DESIGN_RULES };", ctx);
const { computeCirculation, DESIGN_RULES } = ctx.__rules;

const roof = { length: layout.roof_context.length_m, width: layout.roof_context.width_m };
const items = (layout.placements || []).map(p => ({
  id: p.id, x_m: p.bounding_box.top_left_x_m, y_m: p.bounding_box.top_left_y_m,
  rotation: 0, length_m: p.bounding_box.width_m, width_m: p.bounding_box.height_m,     // the box is final: no rotation swap
}));
const rules = { ...DESIGN_RULES };
if (layout.design_rules && typeof layout.design_rules.circulation_width_m === "number") rules.circulationWidth_m = layout.design_rules.circulation_width_m;
const entries = (layout.entry_points || []).map(e => ({ x_m: e.x_m, y_m: e.y_m, edge: e.edge }));

const web = computeCirculation({ roof, items, entryPoints: entries }, rules);

const webDistances = {};
for (const p of web.paths) {
  let len = 0;
  for (let i = 1; i < p.points.length; i++) len += Math.hypot(p.points[i].x - p.points[i - 1].x, p.points[i].y - p.points[i - 1].y);
  webDistances[p.itemId] = len;
}
const webUnreachable = [...web.unreachable].sort();

let problems = 0;
const differ = msg => { problems++; console.log("DIFFERENT " + msg); };
const cUnreach = [...csharp.unreachable].sort();
if (JSON.stringify(cUnreach) !== JSON.stringify(webUnreachable)) differ(`unreachable pieces: C# [${cUnreach}] vs web [${webUnreachable}]`);
const ids = new Set([...Object.keys(csharp.distances), ...Object.keys(webDistances)]);
for (const id of ids) {
  const a = csharp.distances[id], b = webDistances[id];
  if (a === undefined || b === undefined) { differ(`${id}: distance C# ${a} vs web ${b}`); continue; }
  if (Math.abs(a - b) > 1e-6) differ(`${id}: distance C# ${a} vs web ${b}`);
}

const reached = Object.keys(webDistances).length;
console.log(problems === 0
  ? `ORACLE MATCH: ${reached} piece(s) reached, ${webUnreachable.length} unreachable, ${entries.length} entr${entries.length === 1 ? "y" : "ies"}, walkway ${rules.circulationWidth_m} m`
  : `${problems} DIFFERENCE(S) between the add-in's CirculationEngine and ${rulesPath}: change them together`);
process.exit(problems === 0 ? 0 : 1);
