// The add-in publishes its results as one JSON document (GET :5679/analysis-results, shape: AnalysisResultPayload in SportfyRevit/AnalysisResultDto.cs); the web app's
// Analysis tab (analysisResults.js) lists the sections it knows how to show (RESULT_SECTIONS) and which sub-rail group each belongs to (ANALYSIS_SUBTABS).
// A section the add-in publishes that the web app does not list is a result nobody sees; a section the web lists that the add-in never publishes is a card that can
// never fill. This compares the two lists, both ways.
//
//   node Tools/ContractCheck/results-keys-parity.js <path to the web app's analysisResults.js>
const fs = require("fs");
const path = require("path");
const vm = require("vm");

const jsPath = process.argv[2];
if (!jsPath) { console.error("usage: node results-keys-parity.js <analysisResults.js>"); process.exit(2); }

const dto = fs.readFileSync(path.join(__dirname, "..", "..", "..", "SportfyRevit", "SportfyRevit", "AnalysisResultDto.cs"), "utf8");
const block = /class AnalysisResultPayload\s*\{([\s\S]*?)\n    \}/.exec(dto);
if (!block) { console.error("AnalysisResultPayload not found in AnalysisResultDto.cs"); process.exit(2); }
const published = [...block[1].matchAll(/JsonPropertyName\("([a-z_]+)"\)/g)].map(m => m[1]).sort();

const ctx = { localStorage: undefined };
vm.createContext(ctx);
vm.runInContext(fs.readFileSync(jsPath, "utf8") + "\n;this.__web = { sections: Object.keys(RESULT_SECTIONS), grouped: ANALYSIS_SUBTABS.flatMap(t => t.sections || []) };", ctx);
const known = [...ctx.__web.sections].sort();
const grouped = [...ctx.__web.grouped].sort();

let problems = 0;
const differ = m => { problems++; console.log("DIFFERENT " + m); };
for (const k of published) if (!known.includes(k)) differ(`the add-in publishes "${k}" but the web app's RESULT_SECTIONS does not list it`);
for (const k of known) if (!published.includes(k)) differ(`the web app lists "${k}" but the add-in never publishes it`);
for (const k of known) if (!grouped.includes(k)) differ(`"${k}" is in RESULT_SECTIONS but in no group of the Analysis rail (ANALYSIS_SUBTABS), so it is never shown`);
for (const k of grouped) if (!known.includes(k)) differ(`the Analysis rail shows "${k}", which RESULT_SECTIONS does not describe`);

console.log(problems === 0 ? `PARITY OK: ${published.length} published sections, all in the web app's RESULT_SECTIONS and its Analysis rail` : `${problems} DIFFERENCE(S): change AnalysisResultPayload and analysisResults.js together`);
process.exit(problems === 0 ? 0 : 1);
