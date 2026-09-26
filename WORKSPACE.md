# The Sportify folder, and the loop between the web app and Revit

Nothing is imported or exported by hand. Everything Sportify makes goes into one folder on the user's machine, and the web app and Revit both read and write it.

## The folder

The installer (`Sportify-Setup-<version>-Revit2025.exe`) has a page for it, "Your Sportify folder" (the suggestion is `Documents\Sportify Workspace`). It is made at install time with a subfolder for every kind of deliverable, and the choice is written to `%APPDATA%\Sportify\settings.json` (`workspace_folder`), which the add-in reads.

```
Sportify-Setup-<version>-Revit2025.exe                                      the wizard: license, install folder, "Your Sportify folder", install
Sportify-Setup-<version>-Revit2025.exe /VERYSILENT /DELIVERABLES="D:\Projects\Sportify"     silent, with the folder chosen
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
| `Mechanical` | the SOLIDWORKS assemblies (SLDASM), STEP files and films of the Kinetics dynamic units | the Kinetics panel's Simulate button in Revit (needs SOLIDWORKS) |
| `Profile` | your PROFILE: `Sportify-PROFILE.json` (name, view, role, theme) and your photo as a picture | the web app's Profile tab, at every change (and its Export button, which adds a dated copy) |

Files are never overwritten: a name that exists gets a number. The one exception is the profile: `Sportify-PROFILE.json` and `Sportify-PROFILE-photo.*` are always the profile in force. `SportifyWorkspace.cs` is the one description of all this; the installer, the add-in and `Tools/ContractCheck` compile the same file.

## The profile (name, photo, Simple or Advanced view)

The web app's **Profile** tab (next to New Session) keeps who you are (a name and a photo) and how Sportify looks for you: the **view** (Simple shows the main path, Advanced everything: a view, not a lock), your role (planner or client) and the theme. Every change is saved at once in the browser and sent to the add-in (`POST /profile`), which keeps it in `%APPDATA%\Sportify\settings.json` under `"profile"` (so the Revit ribbon and the web app read one copy; whichever copy is newer wins) and writes it into the `Profile` folder above. The add-in rebuilds the profile from the fields it knows before it keeps it: a photo is only ever a small JPEG, PNG or WebP (no SVG, no address), a name is one line of at most 60 characters. `Tools/ContractCheck` and the web app's `tools/profile-test.js` test both sides against the same hostile inputs.

**The quiz** (four questions on the first new session, and from the Profile tab any time: what you want to do, which analyses matter, what you already have about the site, how well you know Sportify) sets defaults and keeps its answers in the profile (`quiz`): the view (Advanced for someone who knows Sportify well, else Simple), the **extras** added to the Simple view (`structure`, `conditions`, `compare`, `postAnalysis`, `safety`, `carbon`: each brings back the web app's tab, where it has one, and its Revit buttons, `RibbonVisibility.ExtraButtons`), the workspace to start in (`landing`) and `onboarded` (the quiz was taken or skipped, so it is not offered again). It is offered only for a **new session** (the welcome screen's *Start a New Session* with nothing placed), never when a session is resumed or loaded. On the site question a person can type the address (a list of up to five places from Nominatim, searched when they press Enter or Search: its usage policy does not allow a search on every keystroke) and the orientation; both go to the Site tab, region and wind zone included. Those two are session data, not profile: they are not kept in the profile. `Tools/ReleaseCheck` compares the extras and landings of the web app with the add-in's.

**What runs where** (web app, `whereCore.js` / `where.js`): the one rule shown everywhere is *decide in the web app, build in Revit; Unity and SOLIDWORKS are engines Revit calls, never places you go*, and results always come back to the Results tab. A status pill in the top bar says whether Revit is open (`Revit 2025 connected`, from `GET /workspace`'s `revit_version`, or `Revit not open` in grey: 2D work goes on; it gives way to the tab bar on narrow screens), an action that needs Revit (or Unity, or SOLIDWORKS) carries a small badge that greys out with the reason when what it needs is missing (the Deliverables actions, the Run button, Send to Revit as Design Options), and the Overview has a card with the three columns and the live status of Revit, Unity and SOLIDWORKS. `tools/where-test.js` checks every claim of the card against the app (for example that the web app really computes the quick estimates it names).

**One store of results** (web app, `resultsStoreCore.js` / `resultsStore.js`): every analysis is told by one function, whichever program computed it, and three places read it. The Results tab opens on an **overview** of one icon tile per analysis (the whole layout at a glance): Revit's full analysis when it has run (the sections of `GET /analysis-results`, with the `layout_id` stamp deciding *this layout* or *out of date*), otherwise the web app's own quick estimate (fire safety, accessibility, rain, wind and LCA have one), otherwise "not run yet" with where to run it in Revit. Each tile says which of the two it is (`Full analysis · Revit` or `Quick estimate · this app · live`), and a tile opens the card of the same analysis in its group; the tile and the card call the same function, so they cannot say two things. In **Combine**, a selected piece shows an icon per analysis that applies to it in the inspector beside the roof (rain for garden pieces; fire safety, accessibility, wind and LCA for all), live while the piece is dragged. Those are the quick estimates: Revit's zones carry a label, not a piece id, and nothing of Revit's is guessed onto a piece (a stable piece id on each zone of a section would let Revit's own figures appear there too). The catalogue's keys are the add-in's section names; `tools/results-store-test.js` keeps them equal to the web app's `RESULT_SECTIONS` (`kinetics` aside: it lives in Improve).

**What the tabs are called** (web app, `profileCore.js` `PROFILE_MODES` is the table; `tools/names-test.js` checks the web app's page and texts against it, and `Tools/ReleaseCheck` the add-in's dialogs, tooltips and documents): *Results* (was Analysis), *Improve* (was Post Analysis: what to change first, and the moving shading), *Catalogue* (was Data), *Revit families* (was Families), *Documents* (was Deliverables; its page is headed *Documents & files*), *Structure inputs* (was Structure) and *Site conditions* (was Conditions). The ids (`analysis`, `postAnalysis`, `data`, `families`, `deliverables`, `structure`, `conditions`) did not change, so saved profiles, the add-in's settings and the ribbon's extras (`structure`, `conditions`, `postAnalysis` ...) work as before. The Revit ribbon's own panel names (*Algorithmic Analysis*, *Simulation & Analytics*, *Data Export / Deliverables* ...) were not renamed: a panel's name is part of its control id.

**What each profile shows** is pinned by `Tools/ProfileMatrix` (`node Tools/run-checks.js --only profiles`): the web app's tabs (`profileCore.js`) and the ribbon's buttons and panels (`RibbonVisibility`, asked through `AddinCheck --ribbon-matrix`) side by side for every profile a person can end up with: Advanced, and Simple with each of the 64 sets of extras. `Tools/ProfileMatrix/profile-matrix.json` is the reviewed list: a change to what any profile shows fails the check until the list is renewed with `--update` and its git diff read. Rules that hold for every profile are checked apart from the list: the ways in and out (Overview, Documents, Save Session, Profile; Open Sportify App, Open Sportify Folder) are always there, an extra brings exactly its tabs and its buttons and takes nothing away, Advanced ignores extras, and every answer of the quiz (7,680 combinations) is one of the listed profiles and opens on a tab it shows.

## What this computer has (Unity, SOLIDWORKS, Chrome) and what the ribbon shows

Nobody is asked "do you have Unity?": the add-in looks (`CapabilityProbes`: `UnityHeadlessRunner.TryLocate` for Unity's Editor and the Sportify.Simulation project, `MechanicalTool` for SOLIDWORKS and the Sportify SOLIDWORKS tool, the App Paths entry for Chrome) and keeps the answer for 30 seconds (`SportifyCapabilities`). The web app's Profile tab shows it (`GET /capabilities`, `?refresh=1` looks again). It decides three things:

- **The result dialogs of the physical analyses** offer "Render the 3D video with Unity" and "Charts as a PDF" when Unity is found, and only the PDF, with the reason, when it is not.
- **The ribbon hides the buttons that cannot work** (`RibbonVisibility.Needs`): *Ball Trajectory Simulation* and *Record Isolated Video* need Unity (their physics makes the numbers: there is no PDF alternative), *Simulate (SOLIDWORKS)* needs SOLIDWORKS and its tool. The five physical analyses stay: without Unity they give a PDF. SOLIDWORKS's tool is only part of a build made on a machine with SOLIDWORKS, so an installation from the released installer hides Simulate even where SOLIDWORKS is installed.
- **The Simple view (Profile tab) also hides the deeper part of the ribbon**: it keeps eight buttons (*Open Sportify App, Push to Sportify, Import Configuration, Send All to Web App, Analysis Report (PDF), Functional Diagrams, Schedules (CSV), Open Sportify Folder*) and the panels that keep one. Advanced shows everything the computer can do. The list is `RibbonVisibility.SimpleButtons`; `Tools/AddinCheck` tests every combination of view and tools.

The way into the web app (*Open Sportify App*) and into your files (*Open Sportify Folder*) are never hidden, and `SPORTIFY_SHOW_ALL_BUTTONS=1` (an environment variable of Revit) turns both rules off. The ribbon follows at start-up, when the profile is saved, and when the Profile tab asks for a fresh look; Revit's ribbon may only be touched from its own thread, so the server asks Revit through an external event (`RibbonRefreshBridge`).

## The loop

1. **Select in Revit, push.** The ribbon's *Push to Sportify* drop-down pushes the roof and any part of what the model says about it: structure (grid, columns, beams, bearing walls), entries (stairs, lifts, doors, ramps), openings, edge, drains, equipment, slab and levels. What is **selected** in Revit is pushed instead of everything of that kind (selected grid lines, columns, beams, walls, stairs, lifts, doors, ramps); with nothing of a kind selected the model is searched as before.
2. **Layers in Combine.** The web app takes them in as they arrive. Combine's *Revit layers* switch (off by default) draws them layer by layer over the roof; the structural grid is opt-in even when the switch is on. The analyses use the layers whether they are drawn or not.
3. **Results and configuration.** The layout on screen is sent to the add-in as a draft as it changes, so the add-in always works on what the designer sees. What was entered or accepted in Revit's assumptions window comes back into the app's Structure inputs and Site conditions tabs (only where the app has no value of its own). Results and recordings come back through `/analysis-results` as before.
4. **Run.** *Run analysis* (Results tab, Combine's layers panel, Documents tab) runs the five physical analyses in the add-in (no Unity, a few seconds), and the numbers and **charts** appear in the Results groups. *Send All to Web App* in Revit does the same from the other side.
5. **Documents.** The Documents tab lists the folder by kind with links, and makes the charts PDF, the analysis report, the schedule and (through Revit) the functional diagrams. *Open Sportify Folder* is on the Revit ribbon.

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
