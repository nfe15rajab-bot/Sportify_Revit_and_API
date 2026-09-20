# Tools: the checks that keep the copies of the analysis logic honest

Each analysis exists in several copies. The numbers must be the same in all of them, and a person cannot keep six copies in step by eye.

- **The core** (`Assets/Scripts/Simulation/<Area>/…Core.cs`): plain C#, no Unity. It is compiled into the Unity project (which films it), into the Revit add-in (which computes the same numbers with no Unity installed) and into every tool here.
- **Two readers of the layout JSON**: Unity reads it with `JsonUtility` (floats, no nulls), the add-in with `System.Text.Json` (doubles); each has its own `*LayoutAdapter.cs`.
- **JavaScript copies** in the web app (`rules.js`, `assumptions.js`, the exporter).

So there are three kinds of drift, and one kind of check for each:

| Drift | What catches it |
|---|---|
| the **core** is wrong (a slip in the model) | an **independent oracle** (`oracle.js` next to each tool): a second implementation written from the specification, not from the C#, that recomputes the numbers; `SunCheck` casts rays where the model builds shadow shapes, the structural oracle integrates by scanline |
| the two **readers** of the layout disagree (Unity's JsonUtility vs the add-in's System.Text.Json) | `ReaderParity`: compiles both readers, runs them on every fixture, emulates JsonUtility's quirks, checks the emulation against **real Unity results** (`fixtures/golden-unity`), and checks that no field Unity reads is missing from the add-in's DTOs |
| Unity's **results** no longer fit the add-in (a renamed field, a changed text) | `ContractCheck`: every real Unity result parses into the add-in's DTOs, agrees with the add-in's own numbers, publishes and builds its PDF rows |
| a **JavaScript copy** differs from the C# | `CirculationCheck` runs the web's real `rules.js` against the add-in's `CirculationEngine`; `StructuralCheck/assumptions-parity.js` compares the assumptions register; the web's own wind-zone test runs |
| the web app calls something the add-in does not serve | `ContractCheck/endpoints-parity.js`: every path `workspaceBridge.js` calls is answered by `WorkspaceEndpoints.cs` / `RoofBoundaryServer.cs`; the chart sections and the workspace subfolders agree both ways |

## Run everything

```
node Sportify.Simulation/Tools/run-checks.js
```

About two minutes, no Unity, no Revit. Exits 0 only if everything passed. This is what CI runs (`.github/workflows/checks.yml`, in this repo and in the web repo).

| Option | |
|---|---|
| `--web <folder>` | where the web app is; default `$SPORTIFY_WEB`, then a sibling folder named `Sportify`, `sportify_frontend`, `sportfify_goldbeck` or `web`. Without it the web-side checks are **skipped and said so** |
| `--require-web` | fail instead of skipping (CI) |
| `--only <text>` | just the steps whose name contains it (`--only circulation`) |
| `--local` | also: the Unity project still compiles (needs a Unity install, `UNITY_MANAGED` to point elsewhere), and the add-in still compiles against the Revit 2025 API (on a scratch copy with a fake `APPDATA`: nothing is deployed) |
| `--unity` | also: re-run **real Unity** on every golden (about 6 minutes, on a scratch copy of the project) and fail if a golden has gone stale |
| `--list` | the steps |

A pre-push hook is there if you want it: `git config core.hooksPath .githooks` (skip once with `SKIP_CHECKS=1 git push`).

## The pieces

| Tool | Runs | Against |
|---|---|---|
| `StructuralCheck`, `DynamicCheck`, `PercolationCheck`, `WindCoreCheck`, `SunCheck` | the analysis on a layout, its own invariants, `--json` for the oracle | `oracle.js` |
| `RoofCheck` | roof shapes: rotated, L-shaped, stepped, skewed | hand-worked cases |
| `AddinCheck` | the Revit-free add-in parts: assumptions patcher, roof features geometry, the roof's plan frame, push scopes, beams / walls / equipment | its own assertions |
| `ContractCheck` | Unity results → add-in (parse, compare, publish, PDF rows), the roof height rule; the "Send All to Web App" batch; the headless Unity run (a stand-in program plays `Unity.exe`: finishes, Cancel, timeout, project open in the Editor) and its progress window; the **PDF of charts** for people without Unity (every chart well-formed SVG, the PDF written to its own folder). `SPORTIFY_PDF_PNG_DIR=<folder>` also writes the pages as images to look at; the **Sportify folder** (`SportifyWorkspace`: default, the installer's choice, its subfolders, files never overwritten) and the **endpoints the web app uses** (`WorkspaceEndpoints`: list, save, run the analyses, charts, PDFs, schedule, the settings decided in Revit, diagrams) called as plain functions, no listener. `ContractCheck serve-workspace <folder> [seconds] [roof.json]` starts the add-in's real local server on `:5679` on a scratch folder, for trying the web app against it | `fixtures/golden-unity` |
| `ReaderParity` | Unity's readers vs the add-in's on every fixture | each other, and real Unity |
| `CirculationCheck` | the add-in's `CirculationEngine` | the web's `rules.js` |
| `UnityCompileCheck` | compiles `Assets/Scripts` (Editor code included) with the .NET SDK against the installed Unity's modules | (only `--local`) |
| `fixtures/` | the layouts everything runs on, made by `make-fixtures.js` from the bundled sample; `golden-unity/` are real Unity results | |

Every one of these is a console project that compiles the same source files the product builds. None of them is part of the Unity project or the add-in.

## When you change something

- **A core** (`…Core.cs`): run `run-checks.js`. If the model changed on purpose, change the oracle to match *in the same commit*: the point is that a slip is not repeated in both. Then re-record the goldens (below).
- **A reader** (`GoldbeckLayoutData.cs`, an adapter, a DTO): change **both** sides; `ReaderParity` fails until you have. A field only Unity's scene builder reads goes in `ReaderParity/drift-allowlist.txt` with the reason; one an analysis reads gets a property in `SportifyLayoutDto.cs`. Rules that two readers must share go through `InputQuantiser` (`Q` rounds numbers to six digits so a float and a double agree; `Or` is "text, or this if it is missing", since JsonUtility never gives null).
- **A layout field**: add a fixture that has it, in `fixtures/make/*.js` (`node Tools/fixtures/make-fixtures.js`). `--check` fails when a committed fixture no longer matches its generator.
- **The web's `rules.js` / `assumptions.js`**, or the C# they mirror: both change together; the parity checks say which side is behind.
- **Anything Unity shows** (a video, a card): `fixtures/regenerate-goldens.js` runs real Unity headless on a scratch copy of the project and rewrites `golden-unity/`; look at `git diff` before you commit it. `run-checks.js --unity` does the same without writing, and fails if a golden is stale.

## What is not automated

- **Revit itself.** The collectors (`StructureCollector`, `RoofFeatureCollector`, including "what is selected in Revit is pushed instead of everything" and the ramps), the command that asks Revit for the functional diagrams from the web app (`RevitCommandBridge`), the "Open Sportify Folder" button, the WPF windows, the ribbon and its dropdown, the push commands' Revit calls, and which link of each result dialog does what ("I have Unity" / "I don't have Unity" / review) need a running Revit. What sits behind the links (the Unity run, the progress window, the PDF) is tested. `--local` proves the add-in *compiles* against the Revit API; nothing proves it *behaves* until it is run in Revit.
- **Unity, on CI.** GitHub's runners have no Unity licence. Locally, `--local` compiles the project and `--unity` re-runs the goldens; on CI the goldens are trusted and the Unity-free half of the contract is what runs.
- **Ball trajectories and the web's Combine-tab exporter.** The ball simulation is Unity-only physics (its independent check is in the notes, not here), and the exporter that turns a piece's rotation into its bounding box needs a browser. `fixtures/layouts/ball` is for real Unity runs.
- **The CI files themselves** have never run on GitHub: the first push will show whether the workflow needs adjusting (a private sibling repository needs its token secret).
