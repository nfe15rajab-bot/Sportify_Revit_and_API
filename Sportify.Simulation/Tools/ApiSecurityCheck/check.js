// Runs the real Sportify.Api twice on a scratch copy (a fresh reference.db in a temp folder: the team's file is never touched) and asks it what a hostile page, a script
// with no key, and a wrong host get. Run: node Tools/ApiSecurityCheck/check.js   (the suite does: run-checks.js)
//
//   run 1  as deployed by default: Production, nothing configured  -> writes need the key the API made for the run, the app gets it from GET /api/session (only from an
//          origin of the app), SQL import is off, accounts are off, another origin's CORS preflight is refused, the Host header and the body size are checked
//   run 2  configured: Api:WriteKey chosen, Admin:AllowSqlImport, a real Jwt:Key -> the chosen key is not handed out, SQL import works but not ATTACH/PRAGMA/VACUUM,
//          accounts still refuse without a database
const fs = require("fs");
let fails = 0;
const check = (name, ok, extra = "") => { if (!ok) fails++; console.log((ok ? "PASS  " : "FAIL  ") + name + (extra ? "  " + extra : "")); };

const { buildApi, startApi, request, freePort } = require("../lib/runApi.js");

(async () => {
  let out;
  try { out = buildApi(); } catch (e) { console.log(String(e.message)); console.log("FAIL  the API builds"); process.exit(1); }
  console.log("built the API into " + out);

  const app = "http://localhost:8123", evil = "https://evil.example";
  let first, second;
  try {
    // ------------------------------------------------------------------ run 1: nothing configured
    const p1 = await freePort();
    first = await startApi(out, p1, {});
    const H = extra => Object.assign({ Host: `localhost:${p1}` }, extra || {});
    const get = (u, h) => request(p1, "GET", u, { headers: H(h) });
    console.log("\n===== nothing configured (how the API runs by default) =====");
    check("reading the catalogue is open", (await get("/api/Furniture")).status === 200 && (await get("/api/Plants")).status === 200);

    const noKey = await request(p1, "POST", "/api/Furniture", { headers: H(), body: { key: "canary", manufacturer: "x", productName: "x", category: "bench" } });
    check("a write with no key is refused (401)", noKey.status === 401, noKey.text.slice(0, 80));
    const wrongKey = await request(p1, "POST", "/api/Furniture", { headers: H({ "X-Sportify-Key": "nope" }), body: { key: "canary" } });
    check("...and with a wrong key", wrongKey.status === 401);
    for (const [m, u] of [["PUT", "/api/Furniture/1"], ["DELETE", "/api/Furniture/1"], ["PATCH", "/api/Admin/records/Norm/1"], ["POST", "/api/Admin/records"], ["POST", "/api/Admin/import-sql"], ["POST", "/api/RoofAssemblies"], ["DELETE", "/api/RoofAssemblies/1"], ["POST", "/api/SportOptions"], ["PUT", "/api/SportOptions/1"], ["DELETE", "/api/SportOptions/1"]]) {
      const r = await request(p1, m, u, { headers: H(), body: m === "DELETE" ? null : {} });
      check(`${m} ${u} needs the key`, r.status === 401);
    }

    const session0 = await get("/api/session");
    check("the session is not given to a request with no Origin", session0.status === 403 && !/[0-9a-f]{48}/.test(session0.text));
    const sessionEvil = await get("/api/session", { Origin: evil });
    check("...nor to another origin", sessionEvil.status === 403 && !/[0-9a-f]{48}/.test(sessionEvil.text) && !sessionEvil.headers["access-control-allow-origin"]);
    const session = await get("/api/session", { Origin: app });
    const key = session.json && session.json.key;
    check("the app's origin gets the key the API made for this run, and the CORS header names that origin", session.status === 200 && /^[0-9a-f]{48}$/.test(key || "") && session.headers["access-control-allow-origin"] === app);

    const created = await request(p1, "POST", "/api/Furniture", { headers: H({ "X-Sportify-Key": key, Origin: app }), body: { key: "canary_bench", manufacturer: "Canary", productName: "Bench", category: "bench", lengthM: 1, widthM: 0.5, heightM: 0.5 } });
    check("with the key the write goes through", created.status === 201 || created.status === 200, created.status + " " + created.text.slice(0, 100));
    const id = created.json && created.json.id;
    const removed = await request(p1, "DELETE", `/api/Furniture/${id}`, { headers: H({ "X-Sportify-Key": key }) });
    check("...and so does the delete", removed.status === 204 || removed.status === 200, String(removed.status));

    const pre = await request(p1, "OPTIONS", "/api/Furniture", { headers: H({ Origin: app, "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "content-type,x-sportify-key" }) });
    check("the preflight of the app allows the key header", pre.status < 300 && pre.headers["access-control-allow-origin"] === app && /x-sportify-key/i.test(pre.headers["access-control-allow-headers"] || ""));
    const preEvil = await request(p1, "OPTIONS", "/api/Furniture", { headers: H({ Origin: evil, "Access-Control-Request-Method": "POST", "Access-Control-Request-Headers": "content-type" }) });
    check("the preflight of another origin gets no CORS permission", !preEvil.headers["access-control-allow-origin"]);
    const readEvil = await get("/api/Furniture", { Origin: evil });
    check("a read from another origin carries no CORS permission (the browser hides it from that page)", !readEvil.headers["access-control-allow-origin"]);

    const sql = await request(p1, "POST", "/api/Admin/import-sql", { headers: H({ "X-Sportify-Key": key }), body: { sql: "SELECT 1;" } });
    check("SQL import is off in Production, even with the key", sql.status === 404 && /turned off/.test(sql.text), sql.status + " " + sql.text.slice(0, 90));
    const cap = await get("/api/Admin/capabilities");
    check("the API says so, for the Data tab", cap.status === 200 && cap.json.sqlImport === false);

    const badHost = await request(p1, "GET", "/api/Norms", { headers: { Host: "evil.example" } });
    check("a Host that is not localhost is refused", badHost.status === 400 || badHost.status === 404, String(badHost.status));

    const big = await request(p1, "POST", "/api/Admin/records", { headers: H({ "X-Sportify-Key": key }), body: Buffer.alloc(17 * 1024 * 1024, 65) });
    check("a body over 16 MB is refused (413)", big.status === 413 || big.status === 0, String(big.status) + (big.error ? " " + big.error : ""));

    const login = await request(p1, "POST", "/api/Auth/login", { headers: H(), body: { email: "a@b.c", password: "x" } });
    check("accounts are off without a real Jwt:Key: login answers 503, not a 500 from a missing database", login.status === 503, login.status + " " + login.text.slice(0, 80));
    check("the log says what is on and what is off", /Jwt:Key is not a real key/.test(first.log()) && /SQL import is off/.test(first.log()) && /Api:WriteKey is not set/.test(first.log()));
    const anon = await get("/swagger/index.html");
    check("the swagger page is not there in Production", anon.status === 404);
    first.child.kill(); first = null;

    // ------------------------------------------------------------------ run 2: configured
    console.log("\n===== configured: a chosen key, SQL import allowed, a real Jwt:Key =====");
    const chosen = "a-long-chosen-key-for-this-check-0123456789";
    const p2 = await freePort();
    second = await startApi(out, p2, { Api__WriteKey: chosen, Admin__AllowSqlImport: "true", Jwt__Key: "0123456789abcdef0123456789abcdef0123456789" });
    const H2 = extra => Object.assign({ Host: `localhost:${p2}` }, extra || {});
    const get2 = (u, h) => request(p2, "GET", u, { headers: H2(h) });
    const s2 = await get2("/api/session", { Origin: app });
    check("a chosen key is not handed out by the session", s2.status === 200 && s2.json.mode === "configured" && !s2.text.includes(chosen));
    const withGenerated = await request(p2, "POST", "/api/Admin/import-sql", { headers: H2({ "X-Sportify-Key": key }), body: { sql: "SELECT 1;" } });
    check("the key of the other run does not work", withGenerated.status === 401);
    const sqlOk = await request(p2, "POST", "/api/Admin/import-sql", { headers: H2({ "X-Sportify-Key": chosen }), body: { sql: "CREATE TABLE IF NOT EXISTS canary_import (x TEXT); INSERT INTO canary_import VALUES ('vacuum drainage, attach the seat -- pragma');" } });
    check("SQL import works with the chosen key when it is allowed, and words inside string literals are only data", sqlOk.status === 200, sqlOk.status + " " + sqlOk.text.slice(0, 120));
    for (const [what, script] of [["ATTACH", "ATTACH DATABASE 'x.db' AS x;"], ["PRAGMA", "PRAGMA writable_schema = 1;"], ["VACUUM", "VACUUM INTO 'copy.db';"], ["load_extension", "SELECT load_extension('x');"], ["ATTACH in lower case after a comment", "-- hi\nattach database 'x' as y;"]]) {
      const r = await request(p2, "POST", "/api/Admin/import-sql", { headers: H2({ "X-Sportify-Key": chosen }), body: { sql: script } });
      check(`${what} is refused inside an import`, r.status === 400, r.status + " " + r.text.slice(0, 80));
    }
    const cap2 = await get2("/api/Admin/capabilities");
    check("the API says the import is on", cap2.json && cap2.json.sqlImport === true);
    const reg = await request(p2, "POST", "/api/Auth/register", { headers: H2({ "X-Sportify-Key": chosen }), body: { email: "a@b.c", password: "x" } });
    check("accounts with a real Jwt:Key but no database still answer 503, not 500", reg.status === 503, reg.status + " " + reg.text.slice(0, 80));
    check("the log says the accounts key was accepted and the chosen write key is in use", !/Jwt:Key is not a real key/.test(second.log()) && /configured Api:WriteKey/.test(second.log()) && !second.log().includes(chosen));
    second.child.kill(); second = null;
  } catch (e) {
    console.log("FAIL  the check itself stopped: " + (e && e.message || e));
    fails++;
  } finally {
    for (const x of [first, second]) if (x) x.child.kill();
    await new Promise(r => setTimeout(r, 1500));
    try { fs.rmSync(out, { recursive: true, force: true }); } catch (e) { /* the dll is still released: a temp folder */ }
  }
  console.log(fails === 0 ? "\nALL API SECURITY CHECKS PASSED" : "\n" + fails + " CHECK(S) FAILED");
  process.exit(fails ? 1 : 0);
})();
