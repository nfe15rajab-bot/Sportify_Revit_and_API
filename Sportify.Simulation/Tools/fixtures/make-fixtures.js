// Regenerates the layout fixtures every check runs on, from the bundled sample layout (Assets/StreamingAssets/sample_layout_roofgarden.json):
//
//   node Tools/fixtures/make-fixtures.js            rewrites layouts/struct, dyn, roof, sun, ball, circ
//   node Tools/fixtures/make-fixtures.js --check    regenerates in memory-safe fashion and FAILS if a committed fixture no longer matches its generator
//
// layouts/wind holds three hand-made layouts (no generator): a variant with two more zones, one with no garden at all, and an old export.
// layouts/export holds a REAL export of the web app (build 2026-09-20-14, after the merge of moamen/design-panel) with what that merge added to it: specified basketball, volleyball
// and padel courts with their own payloads, site furniture, a tree, a polygon zone, a roof finish. It cannot be generated (it needs the browser): when the export format
// changes on purpose, build such a layout in the web app again and replace the file.
// Order matters: dyn and roof are derived from struct, sun from the sample.
const fs = require("fs");
const path = require("path");
const { execFileSync } = require("child_process");

const layouts = path.join(__dirname, "layouts");
const groups = ["struct", "dyn", "roof", "sun", "ball", "circ"];
const order = [["struct", "make/struct.js"], ["dyn", "make/dyn.js"], ["roof", "make/roof.js"], ["sun", "make/sun.js"], ["ball", "make/ball.js"], ["circ", "make/circ.js"]];

function snapshot() {
  const snap = new Map();
  for (const g of groups) {
    const dir = path.join(layouts, g);
    if (!fs.existsSync(dir)) continue;
    for (const f of fs.readdirSync(dir)) if (f.endsWith(".json")) snap.set(path.join(g, f), fs.readFileSync(path.join(dir, f), "utf8"));
  }
  return snap;
}

const before = snapshot();
for (const [, script] of order) execFileSync(process.execPath, [path.join(__dirname, script)], { stdio: ["ignore", "ignore", "inherit"] });
const after = snapshot();

if (process.argv.includes("--check")) {
  const bad = [];
  for (const [k, v] of after) if (before.get(k) !== v) bad.push((before.has(k) ? "changed: " : "new (not committed): ") + k);
  for (const k of before.keys()) if (!after.has(k)) bad.push("no longer generated: " + k);
  // leave the working tree as it was
  for (const [k, v] of before) fs.writeFileSync(path.join(layouts, k), v);
  for (const k of after.keys()) if (!before.has(k)) fs.unlinkSync(path.join(layouts, k));
  if (bad.length) { console.log("FIXTURES OUT OF DATE\n  " + bad.join("\n  ") + "\nRun: node Tools/fixtures/make-fixtures.js"); process.exit(1); }
  console.log("FIXTURES MATCH THEIR GENERATORS (" + after.size + " layouts)");
} else {
  console.log("wrote " + after.size + " layouts");
}
