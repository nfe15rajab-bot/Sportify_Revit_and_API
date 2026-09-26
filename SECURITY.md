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
- The person's profile (name, photo, view, role, theme; `profileCore.js`, `SportifyProfile.cs`) is rebuilt from the fields it knows on both sides before it is kept: unknown fields are dropped, the name is one line of at most 60 characters and is only ever put on the page as text, and a photo is only a small JPEG, PNG or WebP data URL of at most 120,000 characters (an SVG can carry script, an address is a request to somewhere else, so neither is kept). `POST /profile` needs the add-in's session token like every other write and is limited to 1 MB by the guard. `tools/profile-test.js` and `Tools/ContractCheck` test the same hostile inputs.

## What this does not cover

- **Software already running as the user.** The session token and the write key stop the browser's *other pages*; a local process can forge an `Origin` header, read the add-in's memory or the API's log line that shows a generated key. That is outside what a localhost service can defend.
- **Transport**: everything is plain `http` on the loopback interface. Nothing is exposed on the network unless someone changes the URLs; if they do, put it behind TLS and set `Api:WriteKey`.
- **Nominatim**: the site's coordinates are sent to `nominatim.openstreetmap.org` for the address lookup and the wind zone. That is by design and is what the CSP's `connect-src` names.
- **Install and uninstall**: `Sportify-Setup-<version>-Revit2025.exe` (Inno Setup, `Sportify.Setup\`) installs per user, with no administrator rights: the files go to the folder the person chooses, the Revit manifest (`SportfyRevit.addin`, naming the DLL by its full path) to `%APPDATA%\Autodesk\Revit\Addins\2025`, the choice of the deliverables folder to `%APPDATA%\Sportify\settings.json`. It registers *Sportify* under Windows' Installed apps; the uninstaller stops the local API and removes the add-in, the web app, the API, the library and the manifest, never another add-in's files and never the person's Sportify folder of layouts and reports, and offers to give back an older Sportify add-in that setup switched off. The only thing that may run with elevation is Microsoft's own Visual C++ runtime installer, and only when it is missing. `Sportify.Setup\Test-Installer.ps1` installs and uninstalls silently into scratch folders and checks the result (the release workflow runs it). The older console installer (`Sportify_Revit.exe`, `Tools/InstallerCheck`) is kept for `BuildDistribution.ps1 -LegacyInstaller`.
- **The local API serves the web app**: the installed `Sportify.Api.exe` also serves the bundled web app on `http://localhost:5107/` so that Chrome can show it with Revit closed. Its session handshake gives the write key to a page it served itself only when the browser proves that the request is same-origin (`Sec-Fetch-Site`, which a page cannot forge) and the request came to `localhost` or `127.0.0.1`; the add-in's server accepts that page as one of the app's own origins.
- **Signing**: the installer, the add-in DLL and the API are signed only when a certificate is supplied (`BuildDistribution.ps1 -CertificateThumbprint` / `-PfxFile`, or the repository secrets in the release workflow); an unsigned build shows SmartScreen and Revit's "unsigned add-in" prompt. There is no certificate in the repository and there cannot be. The Microsoft components the installer carries (the WebView2 bootstrapper, the Visual C++ runtime) are fetched at build time and checked for Microsoft's signature before they are embedded.
- **Accounts** (`AuthController`, Postgres): off unless `Jwt:Key` and a connection string are both set; nothing in the app uses them.
