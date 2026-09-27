// Regenerates the layout fixtures every check runs on, from the bundled sample layout (Assets/StreamingAssets/sample_layout_roofgarden.json):
//
//   node Tools/fixtures/make-fixtures.js            rewrites layouts/struct, dyn, roof, sun, ball, circ
//   node Tools/fixtures/make-fixtures.js --check    regenerates in a throw-away copy and FAILS if a committed fixture no longer matches its generator (touches nothing in the tree)
//
// layouts/wind holds three hand-made layouts (no generator): a variant with two more zones, one with no garden at all, and an old export.
// layouts/export holds a REAL export of the web app (build 2026-09-20-14, after the merge of moamen/design-panel) with what that merge added to it: specified basketball, volleyball
// and padel courts with their own payloads, site furniture, a tree, a polygon zone, a roof finish. It cannot be generated (it needs the browser): when the export format
// changes on purpose, build such a layout in the web app again and replace the file.
// Order matters: dyn and roof are derived from struct, sun from the sample.
//
// --check used to regenerate the fixtures in place and put them back afterwards. The suite runs its checks side by side, and the others read layouts/ at that very moment: on Windows a
// file that another process has open cannot be rewritten (EBUSY), so the check failed now and then for no reason a change had given. It now works on a copy and leaves the tree alone.
const fs = require("fs");
const os = require("os");
const path = require("path");
const { execFileSync } = require("child_process");

const groups = ["struct", "dyn", "roof", "sun", "ball", "circ"];
const order = [["struct", "make/struct.js"], ["dyn", "make/dyn.js"], ["roof", "make/roof.js"], ["sun", "make/sun.js"], ["ball", "make/ball.js"], ["circ", "make/circ.js"]];
const sample = path.join("Assets", "StreamingAssets", "sample_layout_roofgarden.json");
const simulation = path.join(__dirname, "..", "..");      // Sportify.Simulation

function snapshot(layoutsDir) {
  const snap = new Map();
  for (const g of groups) {
    const dir = path.join(layoutsDir, g);
    if (!fs.existsSync(dir)) continue;
    for (const f of fs.readdirSync(dir)) if (f.endsWith(".json")) snap.set(path.join(g, f), fs.readFileSync(path.join(dir, f), "utf8"));
  }
  return snap;
}

/** Runs the generators that sit in <fixtures>/make; they write to <fixtures>/layouts and read the sample layout three folders above make/. */
function generate(fixtures) {
  for (const [, script] of order) execFileSync(process.execPath, [path.join(fixtures, script)], { stdio: ["ignore", "ignore", "inherit"] });
}

if (!process.argv.includes("--check")) {
  generate(__dirname);
  console.log("wrote " + snapshot(path.join(__dirname, "layouts")).size + " layouts");
} else {
  const committed = snapshot(path.join(__dirname, "layouts"));
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-fixtures-"));
  let after;
  try {
    // the same shape as the tree, so the generators' relative paths hold: <tmp>/Sportify.Simulation/Tools/fixtures/{make,layouts} and <tmp>/Sportify.Simulation/Assets/StreamingAssets/<sample>
    const fixtures = path.join(tmp, "Sportify.Simulation", "Tools", "fixtures");
    fs.cpSync(path.join(__dirname, "make"), path.join(fixtures, "make"), { recursive: true });
    for (const g of groups) {
      const from = path.join(__dirname, "layouts", g), to = path.join(fixtures, "layouts", g);
      if (fs.existsSync(from)) fs.cpSync(from, to, { recursive: true }); else fs.mkdirSync(to, { recursive: true });
    }
    fs.mkdirSync(path.dirname(path.join(tmp, "Sportify.Simulation", sample)), { recursive: true });
    fs.copyFileSync(path.join(simulation, sample), path.join(tmp, "Sportify.Simulation", sample));
    generate(fixtures);
    after = snapshot(path.join(fixtures, "layouts"));
  } finally {
    fs.rmSync(tmp, { recursive: true, force: true });
  }
  const bad = [];
  for (const [k, v] of after) if (committed.get(k) !== v) bad.push((committed.has(k) ? "changed: " : "new (not committed): ") + k);
  for (const k of committed.keys()) if (!after.has(k)) bad.push("no longer generated: " + k);
  if (bad.length) { console.log("FIXTURES OUT OF DATE\n  " + bad.join("\n  ") + "\nRun: node Tools/fixtures/make-fixtures.js"); process.exit(1); }
  console.log("FIXTURES MATCH THEIR GENERATORS (" + after.size + " layouts)");
}
