// The web app talks to the add-in's local server (workspaceBridge.js: the Sportify folder, the analyses, the charts, the PDFs) and the add-in answers in
// WorkspaceEndpoints.cs (and RoofBoundaryServer.cs for the roof and the layout). A path the web app calls that the add-in does not serve is a button that can never
// work; a chart section the add-in draws that the web app has no title for is a card without a name. This compares the two, and the two lists of analysis keys.
//
//   node Tools/ContractCheck/endpoints-parity.js <path to the web app's workspaceBridge.js>
const fs = require("fs");
const path = require("path");

const jsPath = process.argv[2];
if (!jsPath) { console.error("usage: node endpoints-parity.js <workspaceBridge.js>"); process.exit(2); }
const addin = path.join(__dirname, "..", "..", "..", "SportfyRevit", "SportfyRevit");
const read = f => fs.readFileSync(path.join(addin, f), "utf8");

const web = fs.readFileSync(jsPath, "utf8");
const called = [...new Set([...web.matchAll(/localApi\(\s*[`"'](\/[a-z-]+)/g)].map(m => m[1]))].sort();

const served = new Set([
  ...[...read("WorkspaceEndpoints.cs").matchAll(/case "(\/[a-z-]+)"/g)].map(m => m[1]),
  ...[...read("RoofBoundaryServer.cs").matchAll(/path == "(\/[a-z-]+)"/g)].map(m => m[1]),
]);

let problems = 0;
const differ = m => { problems++; console.log("DIFFERENT " + m); };
for (const p of called) if (!served.has(p)) differ(`the web app calls "${p}" but the add-in's server does not answer it`);

// the analyses the charts endpoint draws, and the titles the web app gives them
const keys = /AllKeys = \{([^}]*)\}/.exec(read("PhysicalAnalysisPdf.cs"));
if (!keys) differ("PhysicalAnalysisPdf.AllKeys not found");
else {
  const drawn = [...keys[1].matchAll(/"([a-z_]+)"/g)].map(m => m[1]);
  const titles = /const WS_TITLES = \{([^}]*)\}/.exec(web);
  const named = titles ? [...titles[1].matchAll(/([a-z_]+):/g)].map(m => m[1]) : [];
  for (const k of drawn) if (!named.includes(k)) differ(`the add-in draws charts for "${k}" but WS_TITLES in workspaceBridge.js has no title for it`);
  for (const k of named) if (!drawn.includes(k)) differ(`WS_TITLES names "${k}" but the add-in draws no charts for it`);
}

// the workspace subfolders the web app saves into
const kinds = [...read("SportifyWorkspace.cs").matchAll(/new\("([a-z]+)", "/g)].map(m => m[1]);
for (const m of web.matchAll(/deliverFile\("([a-z]+)"/g)) if (!kinds.includes(m[1])) differ(`the web app saves into "${m[1]}", which is not a workspace kind (${kinds.join(", ")})`);
for (const f of ["sportController.js", "gardenController.js", "combineController.js", "analysisController.js"]) {
  const file = path.join(path.dirname(jsPath), f);
  if (!fs.existsSync(file)) continue;
  for (const m of fs.readFileSync(file, "utf8").matchAll(/deliverFile\("([a-z]+)"/g)) if (!kinds.includes(m[1])) differ(`${f} saves into "${m[1]}", which is not a workspace kind (${kinds.join(", ")})`);
}

console.log(problems === 0 ? `PARITY OK: ${called.length} endpoints the web app calls are all served; the chart sections and the workspace kinds agree` : `${problems} DIFFERENCE(S): change workspaceBridge.js and the add-in's endpoints together`);
process.exit(problems === 0 ? 0 : 1);
