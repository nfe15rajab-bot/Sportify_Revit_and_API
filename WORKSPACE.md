# The Sportify folder, and the loop between the web app and Revit

Nothing is imported or exported by hand. Everything Sportify makes goes into one folder on the user's machine, and the web app and Revit both read and write it.

## The folder

The installer asks where it should be (Enter takes the default). It is made at install time with a subfolder for every kind of deliverable, and the choice is written to `%APPDATA%\Sportify\settings.json` (`workspace_folder`), which the add-in reads.

```
Sportify.Installer.exe                       asks: "Press Enter for Documents\Sportify Workspace, or type another folder"
Sportify.Installer.exe --default-workspace   silent, the default
Sportify.Installer.exe --workspace "D:\Projects\Sportify"
```

| Subfolder | What goes in it | Made by |
|---|---|---|
| `Layouts` | the Combine layout (JSON), the roof image (PNG) | the web app's exports |
| `Sport fields` | field data (JSON), CAD outline (DXF) | the web app's exports |
| `Garden` | garden parcels (JSON) | the web app's exports |
| `Physical analysis` | the charts of the five physical analyses as PDF; the sun-path chart | Run analysis / Charts as PDF; the analysis buttons when there is no Unity |
| `Videos` | the Unity recordings | the analysis buttons (with Unity) |
| `Analysis reports` | the analysis report (PDF) | the report button in Revit or in the web app |
| `Schedules` | component schedules (CSV) | the Schedules button in Revit or in the web app |
| `Diagrams` | functional diagrams (PNG) | Revit's views, from the ribbon or from the web app |

Files are never overwritten: a name that exists gets a number. `SportifyWorkspace.cs` is the one description of all this; the installer, the add-in and `Tools/ContractCheck` compile the same file.

## The loop

1. **Select in Revit, push.** The ribbon's *Push to Sportify* drop-down pushes the roof and any part of what the model says about it: structure (grid, columns, beams, bearing walls), entries (stairs, lifts, doors, ramps), openings, edge, drains, equipment, slab and levels. What is **selected** in Revit is pushed instead of everything of that kind (selected grid lines, columns, beams, walls, stairs, lifts, doors, ramps); with nothing of a kind selected the model is searched as before.
2. **Layers in Combine.** The web app takes them in as they arrive. Combine's *Revit layers* switch (off by default) draws them layer by layer over the roof; the structural grid is opt-in even when the switch is on. The analyses use the layers whether they are drawn or not.
3. **Results and configuration.** The layout on screen is sent to the add-in as a draft as it changes, so the add-in always works on what the designer sees. What was entered or accepted in Revit's assumptions window comes back into the app's Structure and Site conditions tabs (only where the app has no value of its own). Results and recordings come back through `/analysis-results` as before.
4. **Run.** *Run analysis* (Analysis tab, Combine's layers panel, Deliverables tab) runs the five physical analyses in the add-in (no Unity, a few seconds), and the numbers and **charts** appear in the Analysis groups. *Send All to Web App* in Revit does the same from the other side.
5. **Deliverables.** The Deliverables tab lists the folder by kind with links, and makes the charts PDF, the analysis report, the schedule and (through Revit) the functional diagrams. *Open Sportify Folder* is on the Revit ribbon.

Not connected is a normal state: exports download through the browser as before; the buttons that need Revit say so.

## The local server (`:5679`, `RoofBoundaryServer` + `WorkspaceEndpoints`)

| Path | |
|---|---|
| `GET /workspace`, `GET /deliverables` | the folder, its kinds, the files with a link each |
| `GET /deliverable?kind=&name=` | a file (ranges for video); only files really in the workspace |
| `POST /deliverable?kind=&name=` | the web app's own exports, into the kind's folder |
| `POST /combined-layout?draft=1` | the layout as it changes (no Auto Import); without `draft`, the explicit export |
| `POST /run-analysis`, `GET /charts`, `POST /analysis-pdf`, `POST /analysis-report`, `POST /schedule` | the analyses and what they make |
| `GET /analysis-config` | what was decided in Revit's assumptions window for the project on screen |
| `POST /revit-command?name=diagrams` | asks Revit (which owns its API thread) to draw the diagrams; 202, 501 outside Revit |
| `POST /open-folder?kind=` | opens the folder in Explorer |

### Who may ask (`LocalRequestGuard`)

The server answers only the web app, and only so much:

- **Host** must be `localhost:5679` (or `127.0.0.1`/`[::1]` with the port): a page that reaches the server through a name it controls (DNS rebinding) is refused.
- **Origin**, when the request has one (every browser fetch has), must be one of the app's: `http://localhost:8123` and `:8124` (the add-in's web server and the presentation copy), also as `127.0.0.1`. `SPORTIFY_ALLOWED_ORIGINS` (semicolon-separated) adds more for a dev server elsewhere; `*` and `null` are never accepted. The CORS header names the asking origin, never `*`.
- **Token**: every request except `GET /session` needs `X-Sportify-Token` (or `?token=` for a video's `src` and download links, which cannot send a header). The token is 256 random bits made when the add-in starts. `GET /session` gives it only to a request whose Origin is one of the app's, so a page from any other origin cannot get it, and cannot read the answer of a request it has no token for. It stops the browser's other pages; it is not a defence against software already running as the user.
- **Size**: a body is counted while it is read: a layout and a saved file 64 MB, anything else 1 MB (`413`; a `Content-Length` beyond the cap is refused before a byte is read). Header and body time limits close a connection that sends at a trickle.
- The static web server (`:8123`) answers `GET`/`HEAD` only, and a file only inside the web folder (a sibling folder whose name begins with it, an encoded `..` and a directory are `404`).

The web app does all this in `localSession.js` (`localFetch`, `localUrl`); no other script calls the add-in directly (`tools/local-session-test.js` fails if one does). Refusals are logged (the first 20, then one in a hundred) in `%APPDATA%\Sportify\logs`.

`Tools/ContractCheck` calls the endpoints as plain functions (no listener) and `ContractCheck/endpoints-parity.js` checks that every path the web app calls is served. `ContractCheck serve-workspace <folder>` starts the real server on a scratch folder to try the web app against.

## What is not proven

Not run in a live Revit: the selection-first collection and the ramps (`StructureCollector`, `RoofFeatureCollector`), the command bridge that draws the diagrams for the web app (`RevitCommandBridge`), the *Open Sportify Folder* button, the progress window and the result dialogs' links. `run-checks.js --local` proves they compile against the Revit 2025 API. A cloud alternative to the Unity video was not built: without Unity the charts come as PDF.
