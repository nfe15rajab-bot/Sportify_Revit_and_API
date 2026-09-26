// Runs every automated check the analysis code has, and exits non-zero if any fails: the one command for "did I break anything?" and what CI runs.
//
//   node Tools/run-checks.js                       everything that needs no Unity and no Revit (about 2 minutes)
//   node Tools/run-checks.js --web <folder>        where the web app is (default: $SPORTIFY_WEB, then a sibling folder called Sportify, sportify_frontend, sportfify_goldbeck or web)
//   node Tools/run-checks.js --require-web         fail, not skip, when the web app is not found (CI does this)
//   node Tools/run-checks.js --only sun            only the steps whose name contains this (the build of the tools always runs first; --no-build skips it)
//   node Tools/run-checks.js --local               also the checks that need an installed Unity / Revit: the Unity project still compiles, the add-in still compiles, the SOLIDWORKS tool works
//   node Tools/run-checks.js --unity               also re-run real Unity on every golden (about 6 minutes, uses a scratch copy of the project) and fail if a golden has gone stale
//   node Tools/run-checks.js --list                the steps, without running them
//
// What it covers (Tools/README.md has the picture): the fixtures still match their generators; every Revit-free analysis tool and its independent Node oracle on every
// fixture layout; the roof-shape and add-in checks; the Unity-to-add-in results contract; Unity's layout readers against the add-in's on every fixture; the circulation
// port against the web's rules.js; the assumptions register against the web's assumptions.js; the web's own syntax and wind-zone test.
// What it cannot cover: Revit itself (the collectors, the WPF windows, the ribbon: needs a live Revit), and Unity unless --local / --unity say so.
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawn } = require("child_process");

const sim = path.resolve(__dirname, "..");                       // Sportify.Simulation
const repo = path.resolve(sim, "..");                            // the repository root (Sportify_Revit_and_API)
const tools = path.join(sim, "Tools");
const fixtures = path.join(tools, "fixtures");
const isWin = process.platform === "win32";

// ---------------------------------------------------------------------------------------------------------------- options
const argv = process.argv.slice(2);
const flag = n => argv.includes(n);
const value = n => { const i = argv.indexOf(n); return i >= 0 ? argv[i + 1] : null; };
const only = value("--only");
const jobs = Math.max(1, Number(value("--jobs")) || Math.min(4, os.cpus().length));
const wantLocal = flag("--local"), wantUnity = flag("--unity");

function findWeb() {
  const given = value("--web") || process.env.SPORTIFY_WEB;
  if (given === "none") return null;
  const candidates = given ? [path.resolve(given)] : ["Sportify", "sportify_frontend", "sportfify_goldbeck", "web"].map(n => path.resolve(repo, "..", n));
  return candidates.find(d => fs.existsSync(path.join(d, "rules.js")) && fs.existsSync(path.join(d, "assumptions.js"))) || null;
}
const web = findWeb();

// ---------------------------------------------------------------------------------------------------------------- helpers
function run(cmd, args, opts = {}) {
  return new Promise(resolve => {
    const started = Date.now();
    const child = spawn(cmd, args, { cwd: opts.cwd || sim, env: { ...process.env, ...(opts.env || {}) }, windowsHide: true });
    let out = "", err = "";
    child.stdout.on("data", d => { out += d; });
    child.stderr.on("data", d => { err += d; });
    child.on("error", e => resolve({ code: -1, out, err: err + String(e), seconds: 0 }));
    child.on("close", code => resolve({ code, out, err, seconds: (Date.now() - started) / 1000 }));
  });
}
const dotnet = (args, opts) => run("dotnet", args, opts);
const node = (args, opts) => run(process.execPath, args, opts);
const tail = (text, n = 12) => text.trim().split(/\r?\n/).slice(-n).join("\n");

function dllOf(tool) {
  const bin = path.join(tools, tool, "bin", "Release");
  if (!fs.existsSync(bin)) return null;
  for (const tfm of fs.readdirSync(bin)) { const p = path.join(bin, tfm, tool + ".dll"); if (fs.existsSync(p)) return p; }
  return null;
}

function layoutsOf(groups) {
  const list = [["sample", path.join(sim, "Assets", "StreamingAssets", "sample_layout_roofgarden.json")]];
  for (const g of fs.readdirSync(path.join(fixtures, "layouts")).sort()) {
    if (!groups.includes(g)) continue;
    for (const f of fs.readdirSync(path.join(fixtures, "layouts", g)).sort()) if (f.endsWith(".json")) list.push([g + "/" + f.replace(/\.json$/, ""), path.join(fixtures, "layouts", g, f)]);
  }
  return list;
}
const analysisGroups = ["struct", "dyn", "roof", "sun", "wind", "export"];        // "ball" is for real Unity runs only, "circ" for the circulation check

// A tiny pool: runs the async task functions, at most `n` at a time.
async function pool(tasks, n) {
  const results = new Array(tasks.length);
  let next = 0;
  await Promise.all(Array.from({ length: Math.min(n, tasks.length) }, async () => {
    while (next < tasks.length) { const i = next++; results[i] = await tasks[i](); }
  }));
  return results;
}

// ---------------------------------------------------------------------------------------------------------------- the steps
// a step returns { status: "pass" | "fail" | "skip", detail?: string, output?: string }
const steps = [];
const step = (name, fn, opts = {}) => steps.push({ name, fn, ...opts });

step("fixtures match their generators", async () => {
  const r = await node([path.join(fixtures, "make-fixtures.js"), "--check"]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1) } : { status: "fail", output: r.out + r.err };
});

const toolNames = ["StructuralCheck", "DynamicCheck", "PercolationCheck", "WindCoreCheck", "SunCheck", "RoofCheck", "AddinCheck", "ReferenceDbCheck", "CirculationCheck", "AnalysisParity", "InstallerCheck", "ReaderParity"];
if (isWin) toolNames.push("ContractCheck");

step("build the check tools", async () => {
  const bad = [];
  for (const t of toolNames) {
    const r = await dotnet(["build", path.join(tools, t), "-c", "Release", "-v", "q", "-nologo"]);
    if (r.code !== 0) bad.push(`${t}:\n${tail(r.out.split(/\r?\n/).filter(l => / error /.test(l)).join("\n") || r.out + r.err, 8)}`);
  }
  return bad.length ? { status: "fail", output: bad.join("\n") } : { status: "pass", detail: toolNames.length + " tools" };
}, { build: true });

// each Revit-free analysis tool with its independent oracle, on every fixture layout
const analysisTools = [
  { tool: "StructuralCheck", args: ["--json"], oracle: (l, rep) => [path.join(tools, "StructuralCheck", "oracle.js"), l, rep] },
  { tool: "DynamicCheck", args: ["--json"], oracle: (l, rep) => [path.join(tools, "DynamicCheck", "oracle.js"), l, rep] },
  { tool: "PercolationCheck", args: ["--json"], oracle: (l, rep) => [path.join(tools, "PercolationCheck", "oracle.js"), l, rep] },
  { tool: "WindCoreCheck", args: [], oracle: (l, rep) => [path.join(tools, "WindCoreCheck", "oracle.js"), l, rep] },
  { tool: "SunCheck", args: ["--json"], oracle: (l, rep) => [path.join(tools, "SunCheck", "oracle.js"), rep] },
];
for (const spec of analysisTools) {
  step(`${spec.tool} and its oracle on every fixture layout`, async () => {
    const dll = dllOf(spec.tool);
    if (!dll) return { status: "fail", output: "not built" };
    const layouts = layoutsOf(analysisGroups);
    const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-" + spec.tool + "-"));
    const failures = [];
    try {
      // the tool's own checks are its exit code; the oracle re-derives the numbers independently
      await pool(layouts.map(([label, file], i) => async () => {
        const r = await dotnet([dll, file, ...spec.args]);
        if (r.code !== 0) { failures.push(`${label}: ${spec.tool} exit ${r.code}\n${tail(r.out.split(/\r?\n/).filter(l => /FAIL/.test(l)).join("\n") || r.out + r.err, 6)}`); return; }
        const rep = path.join(tmp, i + ".json");
        fs.writeFileSync(rep, r.out);
        const o = await node(spec.oracle(file, rep));
        if (o.code !== 0) failures.push(`${label}: oracle\n${tail(o.out + o.err, 6)}`);
      }), jobs);
    } finally { fs.rmSync(tmp, { recursive: true, force: true }); }
    return failures.length ? { status: "fail", output: failures.sort().join("\n") } : { status: "pass", detail: `${layouts.length} layouts` };
  });
}

// The --json runs above only dump numbers for the oracles: the tool returns before its own checks (the hand-calculated values, the kinetic units' geometry and linkage, the
// mechanics). Those run here, once each, in plain mode on the sample layout. (They did not run in the suite at all until 2026-09-25; two SunCheck ones had gone stale.)
step("the analysis tools' own checks (Structural, Dynamic, Percolation, Sun: hand-calculated values, the kinetic units' geometry and linkage)", async () => {
  const sample = path.join(sim, "Assets", "StreamingAssets", "sample_layout_roofgarden.json");
  const failures = [];
  for (const t of ["StructuralCheck", "DynamicCheck", "PercolationCheck", "SunCheck"]) {
    const dll = dllOf(t);
    if (!dll) { failures.push(t + ": not built"); continue; }
    const r = await dotnet([dll, sample]);
    if (r.code !== 0) failures.push(t + ": exit " + r.code + "\n" + tail(r.out.split(/\r?\n/).filter(l => /FAIL/.test(l)).join("\n") || r.out + r.err, 8));
  }
  return failures.length ? { status: "fail", output: failures.join("\n") } : { status: "pass", detail: "4 tools" };
});

// what makes a release (VERSION, the license the installer shows, the author Revit shows, the Sportify folder's subfolders, the worksets and phases sheet, the API port): the same
// everywhere. Reads files only.
step("the release: the version, the license, the author, the Sportify folder, the worksets and phases, the API port and the installer agree (ReleaseCheck)", async () => {
  const r = await node([path.join(tools, "ReleaseCheck", "check.js"), ...(web ? [web] : [])]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^RELEASE OK: /, "") } : { status: "fail", output: tail(r.out + r.err, 30) };
});

step("roof shapes (RoofCheck)", () => runTool("RoofCheck"));
step("the installer's record and uninstaller on scratch folders (InstallerCheck)", () => runTool("InstallerCheck"));
step("the Revit-free add-in parts (AddinCheck)", () => runTool("AddinCheck"));
step("the API's reference.db guard on throw-away databases (ReferenceDbCheck)", () => runTool("ReferenceDbCheck"));
step("Unity results contract (ContractCheck)", () => runTool("ContractCheck"), { needsWindows: true });
step("Unity's layout readers against the add-in's (ReaderParity)", () => runTool("ReaderParity"));

step("circulation: the add-in's engine against the web's rules.js", async () => {
  if (!web) return noWeb();
  const dll = dllOf("CirculationCheck");
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-circ-"));
  const failures = [];
  const layouts = layoutsOf(["circ"]);
  try {
    await pool(layouts.map(([label, file], i) => async () => {
      const r = await dotnet([dll, file]);
      if (r.code !== 0) { failures.push(`${label}: CirculationCheck exit ${r.code}\n${tail(r.out + r.err, 4)}`); return; }
      const rep = path.join(tmp, i + ".json");
      fs.writeFileSync(rep, r.out);
      const o = await node([path.join(tools, "CirculationCheck", "oracle.js"), file, rep, path.join(web, "rules.js")]);
      if (o.code !== 0) failures.push(`${label}:\n${tail(o.out + o.err, 6)}`);
    }), jobs);
  } finally { fs.rmSync(tmp, { recursive: true, force: true }); }
  return failures.length ? { status: "fail", output: failures.sort().join("\n") } : { status: "pass", detail: `${layouts.length} layouts` };
}, { needsWeb: true });

step("embodied carbon, fire safety, accessibility: the web's carbon.js / analysisController.js against the add-in (AnalysisParity)", async () => {
  if (!web) return noWeb();
  const layouts = layoutsOf(["circ", "export"]);
  const r = await node([path.join(tools, "AnalysisParity", "check.js"), web, ...layouts.map(l => l[1])]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^PARITY OK: /, "") } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("one source: the API's seed, the web app's tables and the add-in's fallbacks (SourceParity, runs the real API)", async () => {
  if (!web) return noWeb();
  const r = await node([path.join(tools, "SourceParity", "check.js"), web]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^PARITY OK: /, "") } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: sport dimensions come from the database once the API answers (data.js)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "field-source-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/field-source-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "the Data tab's sizes reach the Sport tab" } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("assumptions register: the C# list against the web's assumptions.js", async () => {
  if (!web) return noWeb();
  const r = await node([path.join(tools, "StructuralCheck", "assumptions-parity.js"), path.join(web, "assumptions.js")]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^PARITY OK: /, "") } : { status: "fail", output: r.out + r.err };
}, { needsWeb: true });

step("results: the sections the add-in publishes against the web's Analysis rail", async () => {
  if (!web) return noWeb();
  const r = await node([path.join(tools, "ContractCheck", "results-keys-parity.js"), path.join(web, "analysisResults.js")]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^PARITY OK: /, "") } : { status: "fail", output: r.out + r.err };
}, { needsWeb: true });

step("workspace: the endpoints the web app calls against the ones the add-in serves", async () => {
  if (!web) return noWeb();
  // every web script that calls the add-in through localApi(): workspaceBridge.js, and profile.js (the PROFILE: GET/POST /profile) once the web app has it
  const callers = ["workspaceBridge.js", "profile.js"].map(f => path.join(web, f)).filter(f => fs.existsSync(f));
  const r = await node([path.join(tools, "ContractCheck", "endpoints-parity.js"), ...callers]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^PARITY OK: /, "") } : { status: "fail", output: r.out + r.err };
}, { needsWeb: true });

step("the web app: every script parses, and its wind-zone test", async () => {
  if (!web) return noWeb();
  const bad = [];
  const files = fs.readdirSync(web).filter(f => f.endsWith(".js"));
  for (const f of files) {
    const r = await node(["--check", path.join(web, f)]);
    if (r.code !== 0) bad.push(f + ":\n" + tail(r.err, 4));
  }
  const test = path.join(web, "tools", "windzones-test.js");
  if (!fs.existsSync(test)) bad.push("tools/windzones-test.js not found in the web app");
  else {
    const r = await node([test], { cwd: web });
    if (r.code !== 0) bad.push("windzones-test.js:\n" + tail(r.out + r.err, 8));
  }
  return bad.length ? { status: "fail", output: bad.join("\n") } : { status: "pass", detail: `${files.length} scripts` };
}, { needsWeb: true });

step("algorithmic placement: the packing core against the Rhino tool's results", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "algoplacement-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/algoplacement-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^ALL ALGORITHMIC PLACEMENT CHECKS PASSED /, "") } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("algorithmic placement: how it meets the rest of Combine (zones, specified courts, build-up)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "algoplacement-ui-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/algoplacement-ui-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^ALL ALGORITHMIC PLACEMENT UI CHECKS PASSED/, "the real scripts, the real packer, Apply") } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("results: the Analysis tab badges what is about an earlier layout (the id rule, the comparison, the card)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "results-freshness-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/results-freshness-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1).replace(/^ALL RESULT FRESHNESS CHECKS PASSED/, "the real scripts, the same SHA-256 rule as the add-in") } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app reaches the add-in only through localSession.js (the session token, a new one after a restart, links)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "local-session-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/local-session-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "no script calls the add-in with a bare fetch" } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: the PROFILE (Simple/Advanced view against the real page, name and photo rules, role and theme, the sync with Revit's copy)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "profile-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/profile-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "the real profile.js against a stand-in page and a stand-in add-in" } : { status: "fail", output: tail(r.out.split(/\r?\n/).filter(l => /^FAIL|Error/.test(l)).join("\n") || r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: the start-up quiz (questions, what the answers set, only for a new session, the address search and what goes to the Site tab, the Overview)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "quiz-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/quiz-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "the real quiz.js, siteField.js and siteController.js against a stand-in page, map and geocoder" } : { status: "fail", output: tail(r.out.split(/\r?\n/).filter(l => /^FAIL|Error/.test(l)).join("\n") || r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: the rundgang (which steps a profile sees, where the card goes, every target still in the page, a whole tour with the buttons and the keyboard)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "tour-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/tour-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "the real tour.js against a stand-in page" } : { status: "fail", output: tail(r.out.split(/\r?\n/).filter(l => /^FAIL|Error/.test(l)).join("\n") || r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: text goes into markup escaped (the lint over every script, the attack strings)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "escape-audit-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/escape-audit-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "no name, label, description, note, source or id reaches a template unescaped" } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: its Content-Security-Policy, and that the page obeys it (no inline script, no CDN, vendored assets)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "csp-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/csp-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "script-src 'self', nothing from another site" } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("the web app: catalogue writes carry the API's write key (apiSession.js)", async () => {
  if (!web) return noWeb();
  const test = path.join(web, "tools", "api-session-test.js");
  if (!fs.existsSync(test)) return { status: "fail", output: "tools/api-session-test.js not found in the web app" };
  const r = await node([test], { cwd: web });
  return r.code === 0 ? { status: "pass", detail: "handshake, typed key, retry after a restart, no bare fetch writes" } : { status: "fail", output: tail(r.out + r.err, 30) };
}, { needsWeb: true });

step("the API: writes need a key, SQL import is gated, no placeholder JWT (the real API, run twice)", async () => {
  const r = await node([path.join(tools, "ApiSecurityCheck", "check.js")], { timeout: 280000 });
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1) } : { status: "fail", output: tail(r.out + r.err, 40) };
});

// ---- local only: they need an installed Unity / Revit (CI has neither)
step("Unity project still compiles (needs a Unity install)", async () => {
  const managed = process.env.UNITY_MANAGED || "C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.2f1\\Editor\\Data\\Managed\\UnityEngine";
  if (!fs.existsSync(path.join(managed, "UnityEngine.CoreModule.dll"))) return { status: "skip", detail: "no Unity found at " + managed + " (set UNITY_MANAGED)" };
  const r = await dotnet(["build", path.join(tools, "UnityCompileCheck"), "-c", "Release", "-v", "q", "-nologo", "-p:UnityManaged=" + managed]);
  return r.code === 0 ? { status: "pass" } : { status: "fail", output: tail(r.out.split(/\r?\n/).filter(l => / error /.test(l)).join("\n") || r.out + r.err, 20) };
}, { local: true });

step("Revit add-in still compiles (needs Revit 2025; scratch copy, nothing is deployed)", async () => {
  if (!fs.existsSync("C:\\Program Files\\Autodesk\\Revit 2025\\RevitAPI.dll")) return { status: "skip", detail: "Revit 2025 is not installed here" };
  // The add-in's build copies itself into %APPDATA%\Autodesk\Revit\Addins: a scratch copy with a fake APPDATA keeps that away from the real Revit.
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-revit-"));
  try {
    const skip = p => !/[\\/](bin|obj|web)([\\/]|$)/.test(p);
    fs.cpSync(path.join(repo, "SportfyRevit"), path.join(tmp, "SportfyRevit"), { recursive: true, filter: skip });
    fs.cpSync(path.join(sim, "Assets", "Scripts"), path.join(tmp, "Sportify.Simulation", "Assets", "Scripts"), { recursive: true });
    fs.mkdirSync(path.join(tmp, "appdata"), { recursive: true });
    const r = await dotnet(["build", path.join(tmp, "SportfyRevit", "SportfyRevit", "SportfyRevit.csproj"), "-c", "Release", "-v", "q", "-nologo"], { env: { APPDATA: path.join(tmp, "appdata") } });
    return r.code === 0 ? { status: "pass" } : { status: "fail", output: tail(r.out.split(/\r?\n/).filter(l => / error /.test(l)).join("\n") || r.out + r.err, 20) };
  } finally { fs.rmSync(tmp, { recursive: true, force: true }); }
}, { local: true });

step("the SOLIDWORKS tool: a hung SOLIDWORKS is stopped (watchdog), and a small louvre is built, moved, recorded and saved (needs SOLIDWORKS; about a minute)", async () => {
  const interop = "C:\\Program Files\\SOLIDWORKS Corp\\SOLIDWORKS\\api\\redist\\SolidWorks.Interop.sldworks.dll";
  if (!fs.existsSync(interop)) return { status: "skip", detail: "SOLIDWORKS is not installed here" };
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-mechanical-"));
  const lines = text => text.split(/\r?\n/);
  try {
    const built = await dotnet(["build", path.join(repo, "Sportify.Mechanical", "Sportify.Mechanical.csproj"), "-c", "Release", "-v", "q", "-nologo", "-o", path.join(tmp, "tool")]);
    if (built.code !== 0) return { status: "fail", output: tail(lines(built.out).filter(l => / error /.test(l)).join("\n") || built.out + built.err, 20) };
    const exe = path.join(tmp, "tool", "Sportify.Mechanical.exe");
    // a run that hangs must end by itself with exit code 4 (the watchdog), without needing SOLIDWORKS
    const hang = await run(exe, ["watchdog-test"]);
    if (hang.code !== 4 || !/STALLED/.test(hang.out)) return { status: "fail", output: "watchdog-test: exit code " + hang.code + " (4 expected)\n" + tail(hang.out + hang.err, 8) };
    // the whole path on a small unit: the real cores plan it, SOLIDWORKS builds, moves and records it
    const smoke = await run(exe, ["smoke", "--out", path.join(tmp, "smoke")]);
    if (smoke.code === 2) return { status: "skip", detail: tail(smoke.out, 1) };
    if (smoke.code !== 0) return { status: "fail", output: tail(lines(smoke.out).filter(l => !l.startsWith("PROGRESS")).join("\n") + smoke.err, 20) };
    return { status: "pass", detail: lines(smoke.out).filter(l => l.startsWith("smoke unit")).join(" ") };
  } finally { try { fs.rmSync(tmp, { recursive: true, force: true }); } catch (e) { /* SOLIDWORKS may still hold a file for a moment */ } }
}, { local: true });

step("the installer: a silent install into scratch folders registers the add-in, the installed API serves the web app, and it uninstalls cleanly (needs a built dist\\Sportify-Setup-*.exe)", async () => {
  const dist = path.join(repo, "dist");
  const built = fs.existsSync(dist) ? fs.readdirSync(dist).filter(f => /^Sportify-Setup-.*\.exe$/.test(f)) : [];
  if (built.length === 0) return { status: "skip", detail: "no installer built here: run BuildDistribution.ps1 first" };
  const busy = await new Promise(resolve => { require("http").get("http://localhost:5107/api/AnalysisParameters", () => resolve(true)).on("error", () => resolve(false)); });
  if (busy) return { status: "skip", detail: "port 5107 is in use (the API is running): stop it first" };
  const r = await run("powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(repo, "Sportify.Setup", "Test-Installer.ps1"), "-RunApi"]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out.split(/\r?\n/).filter(l => /^\s+ok /.test(l)).join("\n"), 1).trim() + " (" + r.out.split(/\r?\n/).filter(l => /^\s+ok /.test(l)).length + " checks)" } : { status: r.code === 2 ? "skip" : "fail", detail: r.code === 2 ? tail(r.out, 1) : undefined, output: tail(r.out + r.err, 30) };
}, { local: true });

step("real Unity: every golden is still what Unity writes", async () => {
  const r = await node([path.join(fixtures, "regenerate-goldens.js"), "--check"]);
  return r.code === 0 ? { status: "pass", detail: tail(r.out, 1) } : { status: r.code === 2 ? "skip" : "fail", detail: r.code === 2 ? tail(r.err || r.out, 1) : undefined, output: r.out + r.err };
}, { unity: true });

async function runTool(name) {
  const dll = dllOf(name);
  return dll ? simple(await dotnet([dll])) : { status: "fail", output: name + " is not built" };
}
function simple(r) {
  const last = tail(r.out, 1);
  return r.code === 0 ? { status: "pass", detail: (last.match(/\((\d+ checks)\)/) || [])[1] || last } : { status: "fail", output: tail(r.out.split(/\r?\n/).filter(l => /FAIL|error|Exception/i.test(l)).join("\n") || r.out + r.err, 30) };
}
function noWeb() {
  return flag("--require-web")
    ? { status: "fail", output: "the web app was not found (looked for rules.js and assumptions.js): pass --web <folder> or set SPORTIFY_WEB" }
    : { status: "skip", detail: "the web app was not found: pass --web <folder> or set SPORTIFY_WEB (CI passes --require-web)" };
}

// ---------------------------------------------------------------------------------------------------------------- run
(async () => {
  // The build always runs (a check on a stale build says nothing) unless --no-build; --only picks among the rest.
  const selected = steps.filter(s => (s.build ? !flag("--no-build") : (!only || s.name.toLowerCase().includes(only.toLowerCase()))) && (!s.local || wantLocal) && (!s.unity || wantUnity));
  if (flag("--list")) { for (const s of steps) console.log((s.local ? "[--local] " : s.unity ? "[--unity] " : "") + s.name); return; }

  console.log(`Sportify checks   repo ${repo}\n                  web  ${web || "(not found)"}   jobs ${jobs}\n`);
  const started = Date.now();
  const results = [];
  // The build must finish first; everything after it is independent and runs together (each step is itself parallel inside, so a few at a time is plenty).
  const build = selected.filter(s => s.build), rest = selected.filter(s => !s.build);
  const runOne = async s => {
    const t = Date.now();
    let r;
    try { r = (s.needsWindows && !isWin) ? { status: "skip", detail: "Windows only (WPF)" } : await s.fn(); } catch (e) { r = { status: "fail", output: String(e && e.stack || e) }; }
    r.seconds = (Date.now() - t) / 1000; r.name = s.name;
    console.log(`${{ pass: "PASS", fail: "FAIL", skip: "SKIP" }[r.status]}  ${s.name}${r.detail ? "  (" + r.detail.split(/\r?\n/)[0] + ")" : ""}   ${r.seconds.toFixed(1)} s`);
    results.push(r);
    return r;
  };
  for (const s of build) { const r = await runOne(s); if (r.status === "fail") { report(results, started); return; } }
  await pool(rest.map(s => () => runOne(s)), 3);
  report(results, started);
})();

function report(results, started) {
  const failed = results.filter(r => r.status === "fail"), skipped = results.filter(r => r.status === "skip");
  for (const r of failed) console.log(`\n----- ${r.name} -----\n${(r.output || "").trim()}`);
  for (const r of skipped) console.log(`\nSKIPPED  ${r.name}: ${r.detail}`);
  const passed = results.filter(r => r.status === "pass").length;
  console.log(`\n${failed.length ? "CHECKS FAILED" : "ALL CHECKS PASSED"}: ${passed} passed, ${failed.length} failed, ${skipped.length} skipped   ${((Date.now() - started) / 1000).toFixed(0)} s`);
  process.exitCode = failed.length ? 1 : 0;
  if (process.env.GITHUB_ACTIONS) githubReport(results, passed, failed, skipped, started);
}

// On GitHub Actions the log of a failed step is behind a login. So each failed check also becomes an annotation (shown on the run's page and in the pull request, and readable through
// the API without a login) and the whole list goes into the run's job summary: what ran, what failed and why, without opening the log.
function githubReport(results, passed, failed, skipped, started) {
  const esc = t => String(t).replace(/%/g, "%25").replace(/\r/g, "%0D").replace(/\n/g, "%0A");
  for (const r of failed) console.log(`::error title=${esc("check failed: " + r.name).replace(/,/g, "%2C").replace(/:/g, "%3A")}::${esc((r.output || "(no output)").trim().slice(-1800))}`);
  const file = process.env.GITHUB_STEP_SUMMARY;
  if (!file) return;
  const cell = t => String(t || "").split(/\r?\n/)[0].replace(/\|/g, "\\|").slice(0, 160);
  const rows = results.slice().sort((a, b) => (a.status === "fail" ? 0 : 1) - (b.status === "fail" ? 0 : 1)).map(r => `| ${{ pass: "pass", fail: "**FAIL**", skip: "skipped" }[r.status]} | ${cell(r.name)} | ${cell(r.detail)} | ${r.seconds.toFixed(1)} s |`);
  let md = `## ${failed.length ? "Checks failed" : "All checks passed"}: ${passed} passed, ${failed.length} failed, ${skipped.length} skipped (${((Date.now() - started) / 1000).toFixed(0)} s)\n\n| Result | Check | Detail | Time |\n|---|---|---|---|\n${rows.join("\n")}\n`;
  for (const r of failed) md += `\n### ${cell(r.name)}\n\n\`\`\`\n${(r.output || "(no output)").trim().slice(-3000)}\n\`\`\`\n`;
  try { require("fs").appendFileSync(file, md); } catch (e) { /* the summary is a courtesy */ }
}
