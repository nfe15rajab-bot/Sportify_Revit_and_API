// Runs the real Sportify.Api on a scratch content root: for the checks that need the API as it is (its answers, its database as a fresh install seeds it), never the team's reference.db.
const { spawn, spawnSync } = require("child_process");
const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");
const net = require("net");

const apiProject = path.join(__dirname, "..", "..", "..", "Sportify.Api", "Sportify.Api", "Sportify.Api.csproj");

/** Builds the API into a new temp folder (its content root: a fresh reference.db is seeded there on first start). Returns the folder, or throws with the build output. */
function buildApi() {
  const out = fs.mkdtempSync(path.join(os.tmpdir(), "sportify-api-check-"));
  // Two checks build the API at the same time (each is its own process): both write the project's obj\ folder (the static web assets cache), and on GitHub's runner one of them lost
  // the file ("Microsoft.NET.Sdk.StaticWebAssets.targets ... File.OpenWrite"). So the builds queue up; the second one is an incremental build and takes seconds.
  return withBuildLock(() => {
    const build = spawnSync("dotnet", ["build", apiProject, "-c", "Release", "-o", out, "--nologo", "-v", "q"], { encoding: "utf8" });
    if (build.status !== 0) throw new Error("the API does not build: " + build.stdout + build.stderr);
    return out;
  });
}

/** Runs fn while holding a lock every process on this machine shares (a folder: creating it is atomic). A lock left behind by a run that died is taken over after ten minutes. */
function withBuildLock(fn) {
  const lock = path.join(os.tmpdir(), "sportify-api-build.lock");
  const started = Date.now();
  for (;;) {
    try { fs.mkdirSync(lock); break; }
    catch (e) {
      if (e.code !== "EEXIST") throw e;
      try { if (Date.now() - fs.statSync(lock).mtimeMs > 600000) { fs.rmdirSync(lock); continue; } } catch (e2) { /* released in the meantime */ }
      if (Date.now() - started > 600000) throw new Error("waited ten minutes for another build of the API to finish");
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500);
    }
  }
  try { return fn(); } finally { try { fs.rmdirSync(lock); } catch (e) { /* already gone */ } }
}

function freePort() {
  return new Promise(resolve => { const s = net.createServer(); s.listen(0, "127.0.0.1", () => { const p = s.address().port; s.close(() => resolve(p)); }); });
}

function request(port, method, url, opts) {
  const o = opts || {};
  return new Promise(resolve => {
    const data = o.body == null ? null : Buffer.isBuffer(o.body) ? o.body : Buffer.from(typeof o.body === "string" ? o.body : JSON.stringify(o.body));
    const headers = Object.assign({}, o.headers || {});
    if (data) { headers["Content-Length"] = data.length; if (!headers["Content-Type"]) headers["Content-Type"] = "application/json"; }
    const req = http.request({ host: "127.0.0.1", port, method, path: url, headers }, res => {
      const chunks = [];
      res.on("data", c => chunks.push(c));
      res.on("end", () => { const text = Buffer.concat(chunks).toString("utf8"); let json = null; try { json = JSON.parse(text); } catch (e) { /* not JSON */ } resolve({ status: res.statusCode, headers: res.headers, text, json }); });
    });
    req.on("error", e => resolve({ status: 0, error: e.message, headers: {}, text: "", json: null }));
    req.setTimeout(30000, () => req.destroy(new Error("timeout")));
    req.end(data);
  });
}

async function startApi(out, port, env) {
  const child = spawn("dotnet", [path.join(out, "Sportify.Api.dll"), "--urls", `http://localhost:${port}`, "--contentRoot", out], { env: Object.assign({}, process.env, { ASPNETCORE_ENVIRONMENT: "Production" }, env), stdio: ["ignore", "pipe", "pipe"] });
  let log = "";
  child.stdout.on("data", d => { log += d; });
  child.stderr.on("data", d => { log += d; });
  for (let i = 0; i < 120; i++) {
    await new Promise(r => setTimeout(r, 500));
    const r = await request(port, "GET", "/api/Norms", { headers: { Host: `localhost:${port}` } });
    if (r.status === 200) return { child, log: () => log };
    if (child.exitCode != null) throw new Error("the API exited: " + log.slice(-800));
  }
  throw new Error("the API did not start: " + log.slice(-800));
}


module.exports = { buildApi, startApi, request, freePort };
