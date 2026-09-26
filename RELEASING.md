# Releasing Sportify

For the people who cut a release. The people who use Sportify get `Sportify.Setup\docs\USER_GUIDE.html` (installed as a PDF).

## What a release is

**One file**: `Sportify-Setup-<version>-Revit2025.exe`, made with Inno Setup. Nothing else has to be copied. It carries

| Part | What | Where it comes from |
|---|---|---|
| the add-in | `SportfyRevit.dll` and its files | `BuildDistribution.ps1` builds it |
| the web app | `web\` (the bundled copy) | the `sportfify_goldbeck` repository (`-WebAppDir`) |
| the local API | `api\Sportify.Api.exe`, self-contained (no .NET needed); it also serves the web app on `http://localhost:5107/`; its database (`reference.db`) is made on first run | `BuildDistribution.ps1` publishes it |
| the SOLIDWORKS tool | `mechanical\` (only when SOLIDWORKS' interop is on the build machine) | `Sportify.Mechanical` |
| the library | `Library\Templates` (Sportify_DE.rte, Sportify_EN.rte), `Library\Families` (.rfa), `Library\Worksets`, `Library\Documentation` (user guide PDF, license, notes), `Library\Database` (notes) | `Sportify.Setup\Build-Installer.ps1` stages it |
| Microsoft components | the WebView2 bootstrapper and the Visual C++ runtime, used only when the computer lacks them | fetched at build time, signature checked |
| launchers | `tools\Open-Sportify-WebApp.ps1` (starts the API, opens Chrome), `Open-Sportify-InRevit.ps1`, `Stop-Sportify.ps1` | `Sportify.Setup\tools` |

What the person sees: license agreement (accept, Next) → install folder → "Your Sportify folder" (deliverables) → Install (files copied, add-in registered with Revit, missing Microsoft
components installed, the API started once so its database exists) → last page: **Open the Sportify web app** (Chrome) or **Open Revit with the web app docked inside it**.
The add-in loads by itself (the setup writes the `.addin` manifest, naming the DLL by its full path); Revit shows **Digital Tools and Methods - Group of Sports and Gardens** as the author.

## Version naming

**Semantic Versioning**, written once, in the `VERSION` file at the root: `MAJOR.MINOR.PATCH`, and `-beta.N` while Sportify is under testing.

| Version | Meaning |
|---|---|
| `0.1.0-beta.1` | the first beta; `-beta.2`, `-beta.3` ... for the next test builds |
| `0.1.0` | the first version the team calls tested |
| `0.2.0` | a new feature (a new panel, a new analysis) |
| `0.1.1` | a fix |
| `1.0.0` | when the team decides it is ready to be used without the word "beta" |

Everything is made from `VERSION`: the assemblies (`Directory.Build.props`), the installer's file name and version info, the settings file the installer writes, the user guide, the Git tag
(`v<VERSION>`) and the release title. A release is a **pre-release** on GitHub while the version has a `-`. `Tools/ReleaseCheck` (part of `node Tools/run-checks.js`) checks that the
license, the author, the Sportify folder's subfolders, the worksets and phases sheet and the API port agree between the files that make the release.

## To release

1. Change `VERSION` (say `0.1.0-beta.2`), commit and push. Update `Sportify.Setup\docs\USER_GUIDE.html` if what a person does has changed.
2. Make sure the release `library-assets` has the templates and the families (see below).
3. `git tag v0.1.0-beta.2` and `git push origin v0.1.0-beta.2`.
4. The workflow `release` builds the installer, runs `Sportify.Setup\Test-Installer.ps1` (a silent install into scratch folders, the installed API serving the web app, an uninstall), and creates the GitHub Release with the `.exe` and its SHA-256. If the tag and `VERSION` differ, or the library assets are missing, it stops.

A push to a branch named `cd/...` (or Actions > release > Run workflow) does the same without creating a release; the installer is kept as a build artifact for 30 days.

## The library assets (once, and when the templates or families change)

The Revit templates and the Revit families are made in Revit and are too big for Git. They live as the assets of a GitHub release named **`library-assets`**:

* `Sportify_DE.rte`, `Sportify_EN.rte` (from `SportfyRevit\SportfyRevit\Templates` on the machine that made them)
* `families.zip` (the `.rfa` files: the add-in's generated families in `%APPDATA%\Autodesk\Revit\Addins\2025\SportifyGeneratedFamilies`, without the `.0001.rfa` backups)

Upload them on the release page (Releases > Draft a new release, tag `library-assets`, mark it a pre-release) or with `gh release create library-assets Sportify_DE.rte Sportify_EN.rte families.zip --prerelease`.
On your own machine `BuildDistribution.ps1` finds them by itself in those two folders.

## Building on your machine

```
.\BuildDistribution.ps1 -WebAppDir ..\sportfify_goldbeck -RequireLibrary        # needs Revit 2025 (or the public reference packages), the .NET 8 SDK, internet, and Inno Setup 6 (winget install JRSoftware.InnoSetup)
.\Sportify.Setup\Test-Installer.ps1 -RunApi                                     # a silent install into scratch folders and back out
```

Useful switches: `-SkipPrerequisiteDownload` (the installer then downloads the Microsoft components on the person's computer if it needs them), `-NoInstaller` (the payload only),
`-LegacyInstaller` (the older console installer too), `-TemplatesDir`, `-FamiliesDir`. Test switches of the installer itself (harmless in normal use): `/ADDINSDIR`, `/SETTINGSDIR`,
`/DELIVERABLES`, `/NOSTARTAPI=1`, `/NOPREREQS=1`, `/DISABLELEGACY=1`, `/NOSHORTCUTS=1`.

## Signing

The installer, the add-in DLL and the API are signed when the repository secrets `CODESIGN_PFX_BASE64` and `CODESIGN_PFX_PASSWORD` are set (or `-PfxFile` / `-CertificateThumbprint` locally).
Until a certificate exists the release is **unsigned**: Windows SmartScreen shows a blue screen once ("More info", "Run anyway") and Revit asks whether to load the add-in ("Always Load").
The user guide says so.

## Known limits of the installer

* Per user, no administrator rights: the install folder must be one the person can write to (not `Program Files`); the Visual C++ runtime installer, when needed, asks for elevation itself.
* Revit 2025 only (the file name says so). Another year needs another build of the add-in.
* WebView2 is downloaded by Microsoft's bootstrapper: without internet the docked pane inside Revit stays empty (Chrome still works).
* The SOLIDWORKS tool needs the .NET 8 desktop runtime and SOLIDWORKS; the installer does not install either.
* An older `Sportify.addin` (June, `Sportify\Sportify.dll`) is switched off only when the person says yes.
