// What each profile shows: the web app's tabs and the Revit ribbon's buttons, for every profile a person can end up with, against a reviewed list.
//
//   node Tools/ProfileMatrix/check.js <path of the web app (sportfify_goldbeck)> [--update]
//
// A PROFILE is a view (Simple or Advanced) and, in Simple, the extras a person added (what the start-up quiz brings back for the analyses they care about). The web app decides which tabs a
// profile shows (profileCore.js); the add-in decides which buttons of the ribbon (RibbonVisibility.cs, asked through `AddinCheck --ribbon-matrix`). The two were written and tested apart, so
// this puts them side by side for every profile: Advanced, and Simple with each of the 2^6 = 64 sets of extras.
//
// profile-matrix.json is the reviewed answer: for each profile the exact tabs, ribbon buttons and panels, and which buttons a computer with no Unity, SOLIDWORKS or Chrome would lose. A change
// to what any profile shows (a tab or button added to a view, an extra that brings something else) fails here until the change is looked at and the list is renewed with --update: the diff of
// profile-matrix.json in git is what a reviewer reads. Besides the list, rules that hold for every profile are checked on their own (the ways in and out are always there, an extra brings
// exactly its tabs and its buttons and takes nothing away, Advanced ignores extras, every outcome of the quiz is a profile of the list and opens on a tab it shows).
const fs = require("fs");
const path = require("path");
const { spawnSync } = require("child_process");

const args = process.argv.slice(2);
const update = args.includes("--update");
const webDir = args.find(a => !a.startsWith("--"));
if (!webDir || !fs.existsSync(path.join(webDir, "profileCore.js")) || !fs.existsSync(path.join(webDir, "quizCore.js"))) {
  console.error("usage: node check.js <web app folder with profileCore.js and quizCore.js> [--update]");
  process.exit(2);
}
const goldenFile = path.join(__dirname, "profile-matrix.json");
const dll = (() => {
  const bin = path.join(__dirname, "..", "AddinCheck", "bin", "Release");
  for (const tfm of fs.existsSync(bin) ? fs.readdirSync(bin) : []) { const p = path.join(bin, tfm, "AddinCheck.dll"); if (fs.existsSync(p)) return p; }
  return null;
})();
if (!dll) { console.error("AddinCheck is not built: dotnet build Tools/AddinCheck -c Release"); process.exit(2); }

const core = require(path.resolve(webDir, "profileCore.js"));
const quiz = require(path.resolve(webDir, "quizCore.js"));
const MODES = core.PROFILE_MODES, EXTRAS = core.PROFILE_EXTRAS, extraKeys = Object.keys(EXTRAS);

let problems = 0;
const problem = m => { problems++; if (problems <= 40) console.log("DIFFERENT " + m); else if (problems === 41) console.log("DIFFERENT ... and more (only the first 40 are printed)"); };

// ---- the profiles: Advanced, Simple with each set of extras (in the order of PROFILE_EXTRAS), and Advanced with every extra (which must be the same as Advanced)
const idOf = (view, extras) => view === "advanced" ? "advanced" : "simple" + (extras.length ? " + " + extras.join(", ") : "");
const profiles = [{ id: "advanced", view: "advanced", extras: [] }];
for (let mask = 0; mask < (1 << extraKeys.length); mask++) {
  const extras = extraKeys.filter((_, i) => mask & (1 << i));
  profiles.push({ id: idOf("simple", extras), view: "simple", extras });
}
profiles.push({ id: "advanced with every extra", view: "advanced", extras: extraKeys });

// ---- the ribbon, asked of the add-in's own rules
const run = spawnSync("dotnet", [dll, "--ribbon-matrix"], { input: JSON.stringify(profiles), encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
if (run.status !== 0) { console.error("AddinCheck --ribbon-matrix failed: " + (run.stderr || run.stdout)); process.exit(2); }
const ribbon = JSON.parse(run.stdout);
const ribbonOf = Object.fromEntries(ribbon.profiles.map(p => [p.id, p]));

// ---- the web app's tabs
const tabsOf = (view, extras) => Object.keys(MODES).filter(m => core.profileModeVisible(view, m, extras)).map(m => MODES[m].label);
const entryOf = p => {
  const r = ribbonOf[p.id];
  const withTools = r.with_every_tool.buttons, without = new Set(r.with_no_tool.buttons);
  return {
    view: p.view, extras: p.extras,
    tabs: tabsOf(p.view, p.extras),
    ribbon_panels: r.with_every_tool.panels,
    ribbon_buttons: withTools,
    hidden_without_unity_solidworks_chrome: withTools.filter(b => !without.has(b)),
  };
};
const actual = {};
profiles.filter(p => p.id !== "advanced with every extra").forEach(p => { actual[p.id] = entryOf(p); });

// ---- rules that hold for every profile (independent of the list)
const has = (arr, x) => arr.includes(x);
const alwaysTabs = ["guide", "deliverables", "session", "profile"].map(m => MODES[m].label);
for (const [id, e] of Object.entries(actual)) {
  for (const t of alwaysTabs) if (!has(e.tabs, t)) problem(`${id}: the tab "${t}" is not shown: the ways around the app must always be there`);
  for (const b of ribbon.always_visible) {
    if (!has(e.ribbon_buttons, b)) problem(`${id}: the ribbon button ${b} is hidden: the way into the web app and into the files must always be there`);
    if (has(e.hidden_without_unity_solidworks_chrome, b)) problem(`${id}: ${b} disappears on a computer without Unity, SOLIDWORKS or Chrome`);
  }
}
const advanced = actual["advanced"];
{
  if (advanced.tabs.length !== Object.keys(MODES).length) problem("advanced does not show every tab of the app");
  if (advanced.ribbon_buttons.length !== ribbon.buttons.length || advanced.ribbon_panels.length !== ribbon.panels.length) problem("advanced does not show every button and panel of the ribbon (with every tool found)");
  const withEvery = entryOf(profiles.at(-1));
  if (JSON.stringify(withEvery.tabs) !== JSON.stringify(advanced.tabs) || JSON.stringify(withEvery.ribbon_buttons) !== JSON.stringify(advanced.ribbon_buttons)) problem("extras change what Advanced shows: they should change nothing");
}
const simple = actual["simple"];
if (JSON.stringify([...simple.ribbon_buttons].sort()) !== JSON.stringify([...ribbon.simple_buttons].sort())) problem("simple with no extras does not show exactly the ribbon's SimpleButtons");
const diff = (a, b) => a.filter(x => !b.includes(x));
for (const key of extraKeys) {
  const e = actual[idOf("simple", [key])];
  const addedTabs = diff(e.tabs, simple.tabs), addedButtons = diff(e.ribbon_buttons, simple.ribbon_buttons);
  const wantTabs = EXTRAS[key].modes.map(m => MODES[m].label);
  if (JSON.stringify([...addedTabs].sort()) !== JSON.stringify([...wantTabs].sort())) problem(`the extra "${key}" brings the tabs [${addedTabs}] but PROFILE_EXTRAS says [${wantTabs}]`);
  const theirs = (ribbon.extra_buttons[key] || []).filter(b => !has(simple.ribbon_buttons, b));
  if (JSON.stringify([...addedButtons].sort()) !== JSON.stringify([...theirs].sort())) problem(`the extra "${key}" brings the ribbon buttons [${addedButtons}] but RibbonVisibility.ExtraButtons says [${theirs}]`);
  if (diff(simple.tabs, e.tabs).length || diff(simple.ribbon_buttons, e.ribbon_buttons).length) problem(`the extra "${key}" takes something away from the Simple view`);
}
// adding an extra never takes anything away, whichever others are there
for (const p of profiles.filter(p => p.view === "simple")) for (const key of extraKeys) {
  if (p.extras.includes(key)) continue;
  const more = actual[idOf("simple", extraKeys.filter(k => p.extras.includes(k) || k === key))], less = actual[p.id];
  if (diff(less.tabs, more.tabs).length || diff(less.ribbon_buttons, more.ribbon_buttons).length || diff(less.ribbon_panels, more.ribbon_panels).length) problem(`adding "${key}" to [${p.extras}] takes something away`);
}
// the ribbon always shows something
for (const [id, e] of Object.entries(actual)) if (e.ribbon_panels.length === 0) problem(`${id}: the ribbon shows no panel at all`);

// ---- every outcome of the quiz is a profile of the list, and opens on a tab it shows
const goals = ["design", "check", "documents", null], experiences = ["first", "some", "expert"];
const analysisValues = quiz.QUIZ_QUESTIONS.find(q => q.id === "analyses").options.map(o => o.value);
const analysesSets = []; for (let m = 0; m < (1 << analysisValues.length); m++) analysesSets.push(analysisValues.filter((_, i) => m & (1 << i)));
const sites = [[], ["none"], ["location"], ["roof_outline"], ["roof_outline", "location", "structure_grid", "wind_snow", "orientation"]];
let outcomes = 0; const seen = new Set();
for (const goal of goals) for (const experience of experiences) for (const analyses of analysesSets) for (const site_data of sites) {
  const o = quiz.quizOutcome({ goal, analyses, site_data, experience }); outcomes++;
  const extras = extraKeys.filter(k => o.extras.includes(k));
  const id = idOf(o.view, extras);
  seen.add(id);
  const e = actual[id];
  if (!e) { problem(`a quiz outcome (view ${o.view}, extras [${o.extras}]) is not a profile of the list`); continue; }
  const label = m => MODES[m] ? MODES[m].label : null;
  if (!has(e.tabs, label(o.landing))) problem(`${id}: the quiz opens on "${o.landing}", which this profile does not show`);
  if (!has(e.tabs, label(o.next.goto))) problem(`${id}: the quiz's next step goes to "${o.next.goto}", which this profile does not show`);
}

// ---- the reviewed list
const doc = {
  _about: "What each profile shows: the web app's tabs (profileCore.js) and the Revit ribbon's buttons and panels (RibbonVisibility.cs, on a computer with every tool). Written by Tools/ProfileMatrix/check.js --update; read the diff when it changes.",
  _tabs_in_order: Object.keys(MODES).map(m => MODES[m].label),
  _ribbon_buttons_in_order: ribbon.buttons,
  profiles: actual,
};
if (update) {
  fs.writeFileSync(goldenFile, JSON.stringify(doc, null, 1) + "\n");
  console.log(`profile-matrix.json written: ${Object.keys(actual).length} profiles`);
} else if (!fs.existsSync(goldenFile)) {
  problem("profile-matrix.json does not exist: run this with --update and commit it");
} else {
  const golden = JSON.parse(fs.readFileSync(goldenFile, "utf8"));
  if (JSON.stringify(golden._tabs_in_order) !== JSON.stringify(doc._tabs_in_order)) problem(`the tabs of the app changed: ${golden._tabs_in_order} -> ${doc._tabs_in_order}`);
  if (JSON.stringify(golden._ribbon_buttons_in_order) !== JSON.stringify(doc._ribbon_buttons_in_order)) problem(`the buttons of the ribbon changed: +[${diff(doc._ribbon_buttons_in_order, golden._ribbon_buttons_in_order)}] -[${diff(golden._ribbon_buttons_in_order, doc._ribbon_buttons_in_order)}]`);
  for (const id of new Set([...Object.keys(golden.profiles), ...Object.keys(actual)])) {
    const g = golden.profiles[id], a = actual[id];
    if (!g) { problem(`a profile that is not in the list: ${id}`); continue; }
    if (!a) { problem(`a profile of the list that no longer exists: ${id}`); continue; }
    for (const field of ["tabs", "ribbon_panels", "ribbon_buttons", "hidden_without_unity_solidworks_chrome"]) {
      if (JSON.stringify(g[field]) === JSON.stringify(a[field])) continue;
      problem(`${id}: ${field} is [${a[field]}] and the list says [${g[field]}] (now shows +[${diff(a[field], g[field])}] -[${diff(g[field], a[field])}])`);
    }
  }
}

console.log(problems === 0
  ? `PROFILE MATRIX OK: ${Object.keys(actual).length} profiles (Advanced and Simple with each of the ${1 << extraKeys.length} sets of extras); the ${outcomes} answer combinations of the quiz are ${seen.size} of them, each opening on a tab it shows; the web app's ${Object.keys(MODES).length} tabs and the ribbon's ${ribbon.buttons.length} buttons`
  : `${problems} DIFFERENCE(S) in what the profiles show (if the change is meant: node Tools/ProfileMatrix/check.js <web app> --update, and read the diff of profile-matrix.json)`);
process.exit(problems === 0 ? 0 : 1);
