# Next steps (written 2026-09-25)

## A. Before the first real release: the five open points, step by step

**Test package**: `dist\Sportify-0.1.0-beta.1-Revit2025.zip` (built by `BuildDistribution.ps1`, also kept as an artifact of the `release` workflow). It holds the installer, its checksum, the license,
`README-FIRST.txt` (the test steps) and `Remove-Developer-Sportify.ps1` (moves the developer side of Sportify out of Revit into a backup folder; `-DryRun` first).

1. **Click through the installer on the other machine** (about 30 minutes)
   1. Copy the ZIP over, extract it, read `README-FIRST.txt`.
   2. If that machine has the developer version: close Revit, `Remove-Developer-Sportify.ps1 -DryRun`, then without `-DryRun`. Start Revit once: no Sportify tab.
   3. Run the `.exe`; walk the pages: welcome, license, install folder, your Sportify folder, install, last page. Note every page that is unclear.
   4. Tick the checklist in `README-FIRST.txt` (Chrome opens the app at `http://localhost:5107/`; Revit shows the author; the tab and the docked app appear; the Library folder is there).
   5. Uninstall (Settings > Apps) and check that the tab and the install folder are gone and your Sportify folder is kept.
   6. Send back: screenshots of anything odd, `%TEMP%\Setup Log ....txt`, `%APPDATA%\Sportify\logs`.
2. **Upload the library assets** (10 minutes; the files are ready in `Documents\Sportify-library-assets`: `Sportify_DE.rte`, `Sportify_EN.rte`, `families.zip`)
   1. github.com/nfe15rajab-bot/Sportify_Revit_and_API > Releases > Draft a new release.
   2. Tag `library-assets` (create it), title "Library assets", tick "Set as a pre-release", drag the three files in, Publish.
   3. Re-run the workflow (Actions > release > Run workflow on `cd/inno-installer`): the warning about missing assets is gone and the installer carries the templates and the 61 families.
   4. Only when the templates or families change: upload the new files to the same release (replace them).
3. **Decide the license** (a conversation, then 30 minutes of work)
   1. Ask the supervisor at TH OWL and the partner (GOLDBECK) whether the text in `Sportify.Setup\LICENSE_AGREEMENT.txt` is acceptable and whether they want a named license.
   2. Choose: MIT or Apache-2.0 (true open source, commercial use allowed, including by GOLDBECK), or a non-commercial license such as PolyForm Noncommercial or CC BY-NC-SA (educational use only, but then it is *not* "open source" in the usual sense), or your own wording as now.
   3. Add a `LICENSE` file at the root of both repositories and change `LICENSE_AGREEMENT.txt` to say the same thing (`Tools/ReleaseCheck` keeps checking the phrases you asked for).
   4. Check the licenses of what Sportify uses: QuestPDF (the PDF report; it has its own community license with limits), WebView2, OpenStreetMap data (attribution), the icon and script libraries vendored in the web app, SOLIDWORKS' interop DLLs. A `THIRD_PARTY_NOTICES.txt` in the installer is the usual answer: ask me to generate it.
4. **Signing** (needs money or an institution; until then keep the release unsigned)
   1. Ask TH OWL IT whether the university can issue a code-signing certificate (often free for staff and students).
   2. Otherwise: an OV code-signing certificate (about 100-300 EUR a year), or Azure Trusted Signing (about 10 USD a month, needs identity validation), or SignPath Foundation (free for open source projects, but wants an OSI license: it depends on point 3).
   3. With a `.pfx`: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))` and put the result and the password into the repository secrets `CODESIGN_PFX_BASE64` and `CODESIGN_PFX_PASSWORD`. The next tagged release is signed (add-in, API and installer); SmartScreen stops warning after some downloads, Revit's "unknown publisher" prompt goes away.
5. **Merge into master** (I do it; 15 minutes)
   1. After the package test passes and the library assets are up.
   2. Web repository: merge `kinetics-hardening` into `master`, push first (the Revit CI checks out the web `master`).
   3. Revit repository: merge `cd/inno-installer` (it contains `kinetics-hardening`) into `master`, run `node Tools/run-checks.js --local`, push.
   4. `git tag v0.1.0-beta.1` and push it: the `release` workflow builds, tests and publishes the pre-release with the installer.

## B. The next phase: my opinion and how to proceed

**Order.** Do the Goldbeck BIM model *before* the big UX redesign, or in parallel with it, not after. It is the first complete, real use of everything (IFC, worksets, phases, design options, two iterations, deliverables), and what confuses you during that day is exactly what a quiz and a simple mode should fix. A quiz designed on paper first will guess wrong. Two tracks work: you and your friends do the BIM day when they are free; while they are not, I build the UX in slices that can be tested without Revit.

**UX slices (in this order).**
1. *Simple / Advanced as a view, not a lock.* One switch in the web app and in the Revit ribbon (Revit can show and hide ribbon buttons and panels at run time). Simple = the eight buttons and four tabs of the main path; Advanced = everything. Stored in `%APPDATA%\Sportify\settings.json` so Revit and the web app agree. **Started (branch `ux/profile`, both repositories):** the web app's *Profile* tab next to New Session keeps the view (Simple = Overview, Site, Sport, Combine, Analysis + Deliverables, Save Session, New Session, Profile; Advanced = everything), role, theme and the person's name and photo; it is saved at once in the browser, in `settings.json` under `"profile"` (newer copy wins) and in the *Profile* folder of the Sportify folder. **Not yet:** the Revit ribbon following the view (needs a live-Revit test), and the choices marked as defaults in `profileCore.js` awaiting review.
2. *Capability detection instead of questions.* The add-in already knows whether Unity, SOLIDWORKS and Chrome are there: hide "Simulate" without SOLIDWORKS, offer PDF charts without Unity.
3. *The quiz (five questions, re-runnable, sets defaults only):* what do you want to do (design a layout / check an existing design / make documents), which analyses, which site data you already have (location, orientation, roof outline or IFC, structural grid, wind and snow values, none), how experienced you are, and only if detection failed, which tools you have. The answers set which tabs and buttons are shown, which inputs are pre-filled or skipped, and the "next step" bar. "All tools" is always one click away.
4. *The rundgang:* a guided tour that points at real elements of the page (no library: the web app's content policy forbids outside scripts, so a small overlay of our own), and a "Getting started" checklist in Revit. Completion is remembered.
5. *Tests:* my walk-through harness can assert "profile X shows exactly these tabs and buttons" and re-run the Goldbeck flow after every change.

**Checking the deliverables and the Revit UI.** Write down what "renders correctly and makes sense" means before looking: numbers equal the analyses, units and labels present, PRELIMINARY marked, no blank charts, one report per iteration with the iteration's name in the file name. I can add automatic checks (page count, text present, no NaN, images not blank); the "makes sense" part needs a person. For the UI: two people who have not seen it do the rundgang while you watch and count clicks to the first result.

**The Goldbeck BIM model (one day).**
- *Before the day (I can prepare, about half a day, once I have the IFC and the two sessions):* a dry run of the IFC import in Revit; a small command that assigns worksets by IFC class from a mapping table (what "classified as per worksets" needs; Organize Multi-Worksets does it by hand); everything of GOLDBECK in the phase Existing; deliverable names that carry the session and iteration names.
- *The day:* (1) open the IFC as a project, save it workshared, run Set Up Phases & Worksets, classify, check counts (about 2 h); (2) Push to Sportify the roof, open the web app, load the session (name it "DIGITAL TOOLS AND METHODS 2 - Roof and Sports": the message had "DITIGAL", and the name should be spelled once and used everywhere) with the algorithmic and the manual iteration (1 h); (3) Import Iterations as Design Options: two options, check the worksets and the phase Design and analysis per option (2 h); (4) run the analyses and produce the deliverables per iteration into separate folders (2 h); (5) review sheet and defect log, then save and back up the model (1 h).
- *Risks to look at first:* an IFC roof arrives as a generic element, and Push to Sportify looks for roofs and slabs; the IFC's coordinates and true north against the roof frame Sportify uses; the size of the IFC (worksets and phases on very many elements are slow); deliverables are made for the current layout, so per-iteration deliverables need small changes before the day.
- *What I need from you:* the IFC (and which IFC version), the two sessions, the naming you want for the options and the folders, and confirmation that everything from GOLDBECK is one phase (Existing).

**Keep a defect log during the BIM day** (step, what you expected, what happened, how bad). It becomes the UX backlog, and I turn the steps into an automatic regression walk-through so that the Goldbeck model keeps working while we improve the UX.
