// Re-records the real-Unity results in golden-unity/, which Tools/ReaderParity compares its Unity-reader emulation against.
//
//   node Tools/fixtures/regenerate-goldens.js                      re-runs Unity headless once per golden file (about 30 s each, on a scratch copy of the project)
//   node Tools/fixtures/regenerate-goldens.js --only sun__          only the goldens whose file name contains this
//   node Tools/fixtures/regenerate-goldens.js --check               runs Unity but writes nothing: exit 1 if a committed golden is not what Unity writes now (Tools/run-checks.js --unity)
//   node Tools/fixtures/regenerate-goldens.js --work D:\scratch\unity-proj      the scratch project to use (default: <temp>/sportify-golden-project)
//   set UNITY_EXE=...  or  --unity <path to Unity.exe>              default: Unity Hub's 6000.4.2f1
//
// The set of goldens is the set of files already in golden-unity/, named <analysis>__<group>-<layout>.json: analysis is structure | dynamic | wind | percolation |
// sun | collision, and layouts/<group>/<layout>.json is the layout it is run on. To add a golden, create an empty file with the right name and run this.
//
// WHEN: after any change to Unity's readers (GoldbeckLayoutData.cs, the *LayoutAdapter.cs), the cores, or the fixtures a golden is made from. ReaderParity says
// "equals what Unity wrote" fails for a golden that has gone stale; the fix is to run this and look at what changed (git diff), not to edit the file.
//
// The project is NEVER run in place: a second Unity on a project the Editor has open exits silently, and the Editor must not be disturbed. The scratch copy
// gets Assets/, Packages/ and ProjectSettings/ from the real one on every run; its Library/ is kept between runs so imports are incremental (the first run
// builds it, a few minutes).
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");

const sim = path.resolve(__dirname, "..", "..");                       // Sportify.Simulation
const goldenDir = path.join(__dirname, "golden-unity");
const layoutsDir = path.join(__dirname, "layouts");

const arg = name => { const i = process.argv.indexOf(name); return i >= 0 ? process.argv[i + 1] : null; };
const only = arg("--only");
const checkOnly = process.argv.includes("--check");
const work = path.resolve(arg("--work") || path.join(os.tmpdir(), "sportify-golden-project"));
const unity = arg("--unity") || process.env.UNITY_EXE || "C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.2f1\\Editor\\Unity.exe";

const methods = {
  structure: ["RunStructuralAnalysis", "structure_results.json"],
  dynamic: ["RunDynamicAnalysis", "dynamic_results.json"],
  wind: ["RunWindAnalysis", "wind_results.json"],
  percolation: ["RunPercolationAnalysis", "percolation_results.json"],
  sun: ["RunSunAnalysis", "sun_results.json"],
  collision: ["RunCollisionAnalysis", "collision_results.json"],
};

if (!fs.existsSync(unity)) { console.error("Unity not found at " + unity + " (set UNITY_EXE or pass --unity)"); process.exit(2); }
if (path.resolve(work) === sim || work.startsWith(sim + path.sep)) { console.error("refusing to use the real project as the scratch copy"); process.exit(2); }

const files = fs.readdirSync(goldenDir).filter(f => f.endsWith(".json") && (!only || f.includes(only))).sort();
if (!files.length) { console.error("no golden files match"); process.exit(2); }

// ---- the scratch project
fs.mkdirSync(work, { recursive: true });
for (const dir of ["Assets", "Packages", "ProjectSettings"]) {
  fs.rmSync(path.join(work, dir), { recursive: true, force: true });
  fs.cpSync(path.join(sim, dir), path.join(work, dir), { recursive: true });
}
fs.mkdirSync(path.join(work, "Recordings"), { recursive: true });
console.log("scratch project: " + work + (fs.existsSync(path.join(work, "Library")) ? "" : "  (no Library yet: the first run imports everything)"));

// A path in a Unity result is the machine's, not a fact about the analysis: keep the file name, so goldens do not carry the user's folders.
function scrub(node) {
  if (typeof node === "string") return /^[A-Za-z]:[\\/]/.test(node) ? node.split(/[\\/]/).pop() : node;
  if (Array.isArray(node)) return node.map(scrub);
  if (node && typeof node === "object") { const o = {}; for (const k of Object.keys(node)) o[k] = scrub(node[k]); return o; }
  return node;
}

let failed = 0;
for (const file of files) {
  const name = file.replace(/\.json$/, "");
  const [analysis, rest] = name.split("__");
  const dash = rest.indexOf("-");
  const layout = path.join(layoutsDir, rest.slice(0, dash), rest.slice(dash + 1) + ".json");
  const method = methods[analysis];
  if (!method || !fs.existsSync(layout)) { console.log("SKIP  " + name + ": no analysis '" + analysis + "' or no layout " + layout); failed++; continue; }

  const results = path.join(work, "Recordings", method[1]);
  fs.rmSync(results, { force: true });
  const out = path.join(work, "out-" + name);
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  const started = Date.now();
  const run = spawnSync(unity, ["-batchmode", "-projectPath", work, "-executeMethod", "Sportify.Simulation.Editor.BatchRunner." + method[0],
    "-layoutFile=" + layout, "-videoFile=" + path.join(out, "video.mp4"), "-logFile", path.join(out, "unity.log")], { timeout: 15 * 60 * 1000, stdio: "ignore" });
  const seconds = Math.round((Date.now() - started) / 1000);
  if (!fs.existsSync(results)) {
    console.log("FAIL  " + name + ": Unity exit " + run.status + ", " + seconds + " s, no " + method[1] + " written (see " + path.join(out, "unity.log") + ")");
    failed++;
    continue;
  }
  const json = JSON.parse(fs.readFileSync(results, "utf8"));
  const target = path.join(goldenDir, file);
  const fresh = scrub(json);
  // compared as data, not as text: the formatting is ours, the numbers are Unity's
  const changed = !fs.existsSync(target) || JSON.stringify(JSON.parse(fs.readFileSync(target, "utf8"))) !== JSON.stringify(fresh);
  if (checkOnly) { if (changed) failed++; console.log((changed ? "STALE " : "same  ") + name + "  (" + seconds + " s)"); continue; }
  fs.writeFileSync(target, JSON.stringify(fresh, null, 4));
  console.log((changed ? "NEW   " : "same  ") + name + "  (" + seconds + " s)");
}
console.log(checkOnly
  ? (failed ? failed + " golden(s) stale or not run: node Tools/fixtures/regenerate-goldens.js, then look at git diff" : "every golden is what Unity writes now")
  : (failed ? failed + " golden(s) not regenerated" : "goldens regenerated; run: dotnet run --project Tools/ReaderParity -c Release"));
process.exit(failed ? 1 : 0);
