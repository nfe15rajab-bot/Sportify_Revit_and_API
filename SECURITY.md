# Security: what is protected, what is configured, what is not covered

Written for the black-box test (2026-09-21). Every rule below is pinned by a check in `Sportify.Simulation/Tools/run-checks.js`; the check is named in brackets.

## The three doors

| Door | Who may use it | How | Check |
|---|---|---|---|
| **Add-in server** `localhost:5679` (`RoofBoundaryServer`, `LocalRequestGuard`) | the web app, on `localhost`/`127.0.0.1` ports `8123`/`8124` | `Host` must be this machine's; `Origin` (when present) must be the app's; every request except `GET /session` carries `X-Sportify-Token` (or `?token=` for a video/download link); bodies are capped while read (64 MB a layout or saved file, 1 MB otherwise); header/body time limits | `AddinCheck` (live listener), `tools/local-session-test.js` |
| **Add-in web server** `localhost:8123` (`StaticWebServer`) | anyone on this machine, read-only | `GET`/`HEAD` only; a file only inside the web folder (a sibling folder with a longer name is a 404); `frame-ancestors 'none'` | `AddinCheck` |
| **API** `localhost:5107` (`Sportify.Api`, `ApiSecurity`) | reads: anyone on this machine; writes: whoever has the key | `POST/PUT/PATCH/DELETE` need `X-Sportify-Key`; CORS names only the app's origins; `AllowedHosts` is localhost; 16 MB request cap; swagger only in Development | `Tools/ApiSecurityCheck` (runs the real API twice) |

## Configuration

| Setting | Where | Effect |
|---|---|---|
| `SPORTIFY_ALLOWED_ORIGINS` | environment of Revit | more origins for the add-in server (semicolon-separated); `*` and `null` are never accepted |
| `Api:WriteKey` (`Api__WriteKey`) | API configuration | the write key. Not set: the API makes a random key per run and gives it, through `GET /api/session`, only to a request whose `Origin` is one of the app's. Set: it is never handed out, and the Data tab asks for it once per browser tab |
| `Api:AllowedOrigins` | API configuration | more origins for the API's CORS and session handshake |
| `Admin:AllowSqlImport` | API configuration | `true` turns on `POST /api/Admin/import-sql` outside Development. It always needs the key and refuses `ATTACH`, `DETACH`, `PRAGMA`, `VACUUM`, `load_extension` |
| `Jwt:Key` | API configuration / user secrets | 32+ characters turns on the account endpoints. There is no key in the repository; the old placeholder is refused |
| `SPORTIFY_LOG_DIR` | environment of Revit | where the add-in logs (default `%APPDATA%\Sportify\logs`) |

## The web app

- Text goes into markup through `escapeHtml` / `safeUrl` (`escape.js`); the lint `tools/escape-audit.js` fails the suite when a name, label, description, note, source, provider, id or message reaches a template without it. `tools/xss-canary-proxy.js` poisons the API's answers so a real browser can show what the lint cannot.
- `index.html` carries a Content-Security-Policy (`script-src 'self'`, no inline script, nothing from another site except map tiles and photographs as images and the address lookup as a `connect`). Leaflet, SunCalc, the icon font and Titillium Web are in `vendor/` (`tools/csp-test.js`).
- `style-src` still allows inline styles: the app sets `style` attributes throughout. A style cannot run script; it can restyle a page.

## What this does not cover

- **Software already running as the user.** The session token and the write key stop the browser's *other pages*; a local process can forge an `Origin` header, read the add-in's memory or the API's log line that shows a generated key. That is outside what a localhost service can defend.
- **Transport**: everything is plain `http` on the loopback interface. Nothing is exposed on the network unless someone changes the URLs; if they do, put it behind TLS and set `Api:WriteKey`.
- **Nominatim**: the site's coordinates are sent to `nominatim.openstreetmap.org` for the address lookup and the wind zone. That is by design and is what the CSP's `connect-src` names.
- **Signing**: the installer and the add-in DLL are signed only when a certificate is supplied (`BuildDistribution.ps1 -CertificateThumbprint` / `-PfxFile`); an unsigned build shows SmartScreen and Revit's "unsigned add-in" prompt. There is no certificate in the repository and there cannot be.
- **Accounts** (`AuthController`, Postgres): off unless `Jwt:Key` and a connection string are both set; nothing in the app uses them.
