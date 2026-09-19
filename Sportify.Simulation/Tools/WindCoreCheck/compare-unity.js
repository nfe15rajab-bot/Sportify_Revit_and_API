// Compares the numbers Unity wrote (wind_results.json -> analysis) with the add-in-side harness report for the same layout.
const fs = require("fs");
const u = JSON.parse(fs.readFileSync(process.argv[2], "utf8")).analysis;
const h = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));
let bad = 0, n = 0;
function walk(a, b, path) {
  if (typeof a === "number" || typeof b === "number") {
    n++;
    if (typeof a !== "number" || typeof b !== "number" || Math.abs(a - b) > 1e-4 * Math.max(1, Math.abs(b))) { bad++; if (bad < 15) console.log("  DIFF", path, "unity", a, "harness", b); }
    return;
  }
  if (Array.isArray(a) || Array.isArray(b)) {
    if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length) { bad++; console.log("  DIFF length", path, a && a.length, b && b.length); return; }
    a.forEach((x, i) => walk(x, b[i], path + "[" + i + "]")); return;
  }
  if (a && typeof a === "object") { for (const k of Object.keys(b)) walk(a[k], b[k], path + "." + k); return; }
  n++;
  // Unity's JsonUtility writes a string that was null as "": the same thing.
  if ((a === undefined || a === null ? "" : a) !== (b === undefined || b === null ? "" : b)) { bad++; if (bad < 15) console.log("  DIFF", path, JSON.stringify(a), JSON.stringify(b)); }
}
walk(u, h, "analysis");
console.log(bad === 0 ? "UNITY == ADD-IN SIDE: " + n + " values identical" : "MISMATCHES: " + bad + " of " + n);
process.exit(bad ? 1 : 0);
