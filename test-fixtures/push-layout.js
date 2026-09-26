// Pushes a layout to the running add-in the way the web app does (GET /session with the app's Origin, then POST /combined-layout with the token) and waits for the import's report
// in the add-in's log. Auto Import must be ON in Revit (the journal test/import-journal.txt turns it on).
//
//   node test-fixtures/push-layout.js <layout.json> [--wait <seconds>] [--draft]
const fs = require("fs");
const http = require("http");
const path = require("path");

const file = process.argv[2];
const wait = Number((process.argv.indexOf("--wait") > 0 && process.argv[process.argv.indexOf("--wait") + 1]) || 120);
const draft = process.argv.includes("--draft");
if (!file) { console.error("usage: node push-layout.js <layout.json> [--wait <seconds>] [--draft]"); process.exit(2); }

const logDir = process.env.SPORTIFY_LOG_DIR || path.join(process.env.APPDATA, "Sportify", "logs");
const today = new Date(); const pad = n => String(n).padStart(2, "0");
const logFile = path.join(logDir, `addin-${today.getFullYear()}${pad(today.getMonth() + 1)}${pad(today.getDate())}.log`);

function request(method, url, headers, body) {
  return new Promise((resolve, reject) => {
    const req = http.request({ host: "localhost", port: 5679, method, path: url, headers: Object.assign({ Host: "localhost:5679" }, headers || {}) }, res => {
      const chunks = []; res.on("data", c => chunks.push(c)); res.on("end", () => resolve({ status: res.statusCode, text: Buffer.concat(chunks).toString("utf8") }));
    });
    req.on("error", reject);
    if (body) { req.setHeader("Content-Length", Buffer.byteLength(body)); req.setHeader("Content-Type", "application/json"); req.write(body); }
    req.end();
  });
}

(async () => {
  const session = await request("GET", "/session", { Origin: "http://localhost:8123" });
  if (session.status !== 200) { console.error("no session: " + session.status + " " + session.text); process.exit(1); }
  const token = JSON.parse(session.text).token;
  const sizeBefore = fs.existsSync(logFile) ? fs.statSync(logFile).size : 0;
  const body = fs.readFileSync(file, "utf8");
  const r = await request("POST", "/combined-layout" + (draft ? "?draft=1" : ""), { Origin: "http://localhost:8123", "X-Sportify-Token": token }, body);
  console.log("POST /combined-layout -> " + r.status + " " + r.text);
  if (draft) return;

  // the import runs on Revit's Idling: its report ("... replaced N element(s) of the previous import; report:") lands in the log
  const started = Date.now();
  let seen = "";
  while (Date.now() - started < wait * 1000) {
    await new Promise(res => setTimeout(res, 1500));
    const text = fs.existsSync(logFile) ? fs.readFileSync(logFile, "utf8").slice(sizeBefore) : "";
    seen = text;
    if (/\[import\][^\n]*(report:|building the geometry failed|the import stopped|cancelled)/.test(text) && Date.now() - started > 4000) {
      await new Promise(res => setTimeout(res, 2500));      // the report block is written in one go; let a following line land
      seen = fs.readFileSync(logFile, "utf8").slice(sizeBefore);
      break;
    }
  }
  console.log("\n----- what the add-in logged since the push -----\n" + (seen || "(nothing: is Auto Import ON, and is a project open?)"));
})().catch(e => { console.error("FAILED: " + e.message); process.exit(1); });
