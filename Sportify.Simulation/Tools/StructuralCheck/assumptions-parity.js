// Checks that the web app's copy of the analysis assumptions (assumptions.js) is the same list as the C# register
// (Assets/Scripts/Simulation/Structure/AnalysisAssumptions.cs): same keys, order, texts, defaults, ranges, choices, references.
//
//   node Tools/StructuralCheck/assumptions-parity.js <path to the web app's assumptions.js>
//
// Run from Sportify.Simulation. It asks the C# side for its register with `dotnet run --project Tools/StructuralCheck -- --assumptions-json`.
const fs = require("fs");
const vm = require("vm");
const { execFileSync } = require("child_process");

const jsPath = process.argv[2];
if (!jsPath) { console.error("usage: node assumptions-parity.js <assumptions.js>"); process.exit(2); }

const ctx = {};
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(jsPath, "utf8") + "\nthis.REGISTER = ANALYSIS_ASSUMPTIONS; this.GROUPS = typeof ASSUMPTION_GROUPS === 'undefined' ? null : ASSUMPTION_GROUPS;", ctx);
const web = JSON.parse(JSON.stringify(ctx.REGISTER));

const out = execFileSync("dotnet", ["run", "--project", "Tools/StructuralCheck", "-c", "Release", "--", "--assumptions-json"], { encoding: "utf8" });
const cs = JSON.parse(out.trim().split("\n").pop());

let problems = 0;
function differ(where, a, b) { problems++; console.log(`DIFFERENT ${where}\n   C#:  ${JSON.stringify(a)}\n   web: ${JSON.stringify(b)}`); }

for (const list of ["editable", "fixed"]) {
  if (cs[list].length !== web[list].length) differ(`${list}: number of entries`, cs[list].length, web[list].length);
  const n = Math.min(cs[list].length, web[list].length);
  for (let i = 0; i < n; i++) {
    const a = cs[list][i], b = web[list][i];
    const keys = new Set([...Object.keys(a), ...Object.keys(b)]);
    for (const k of keys) if (JSON.stringify(a[k]) !== JSON.stringify(b[k])) differ(`${list}[${i}] ${a.key}.${k}`, a[k], b[k]);
  }
}

// The web app shows the inputs in two tabs (ASSUMPTION_GROUPS): every input and every constant must be in exactly one of them, or a tab silently drops it.
if (!ctx.GROUPS) differ("ASSUMPTION_GROUPS", "a group list", null);
else {
  const groups = Object.values(JSON.parse(JSON.stringify(ctx.GROUPS)));
  const inputs = groups.flatMap(g => g.sections.flatMap(sec => sec.keys));
  const constants = groups.flatMap(g => g.fixed);
  for (const [label, listed, all] of [["input", inputs, web.editable.map(d => d.key)], ["constant", constants, web.fixed.map(d => d.key)]]) {
    for (const key of all) { const n = listed.filter(k => k === key).length; if (n !== 1) differ(`${label} "${key}" is in ${n} tab group(s), not exactly one`, 1, n); }
    for (const key of listed) if (!all.includes(key)) differ(`a tab group lists ${label} "${key}", which is not in the register`, null, key);
  }
}

console.log(problems === 0
  ? `PARITY OK: ${cs.editable.length} inputs and ${cs.fixed.length} constants identical in the C# register and ${jsPath}`
  : `${problems} DIFFERENCE(S): change AnalysisAssumptions.cs and assumptions.js together`);
process.exit(problems === 0 ? 0 : 1);
