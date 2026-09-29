# Sportify: Revit add-in, local API and analyses

Sportify helps you design **sports and gardens on a roof**. You lay out the roof in a web app, send it to **Autodesk Revit 2025**, where it becomes real model elements, and check it with analyses: structure, wind, rain, sun and shade, dynamic loads, fire, carbon, accessibility and ball paths. From the results, *Kinetics* places moving shading elements.

A student project of the **Digital Tools and Methods – Group of Sports and Gardens** (TH OWL, School of Architecture, MID project), in partnership with **GOLDBECK**. Led by Nada, Moamen, Sukriti and Ali, and developed with the main assistance of Claude (Anthropic). **Beta: results are preliminary**, and nothing here replaces the checks of a qualified architect or engineer.

This repository holds the Revit add-in, the local API and database, the analyses and the installer. The web app is in [sportfify_goldbeck](https://github.com/nfe15rajab-bot/sportfify_goldbeck); the installer bundles it, so you do not need to download it separately.

## Install (about 5 minutes)

**You need** Windows 10/11 (64-bit), **Revit 2025**, about 600 MB of disk space and an internet connection during setup. Google Chrome is recommended. Unity and SOLIDWORKS are optional.

1. Close Revit and run **`Sportify-Setup-0.1.0-beta.2-Revit2025.exe`**.
2. Windows may show a blue *"Windows protected your PC"* screen, because the installer is not signed by a certificate Windows trusts. Click **More info → Run anyway**.
3. Accept the license and keep the suggested folders. Click **Install**, then **Finish**, leaving *Open the Sportify web app* ticked.
4. Chrome opens the web app at `http://localhost:5107/`. Later, open it again from the Start menu: **Sportify web app**.
5. Start Revit 2025. When it asks about the Sportify add-in, choose **Always Load**. A **Sportify** tab appears in the ribbon, with the web app docked inside Revit.

Setup installs everything else it needs: the add-in, the web app, its local API and database, the Revit templates and families, and Microsoft WebView2 and the Visual C++ runtime if they are missing. You never copy a DLL by hand. To uninstall, use **Settings → Apps → Sportify**.

## See it working in two minutes (no Revit project needed)

In the web app, click **Next → I've used Sportify before → Load Goldbeck IFC Roof – Prebuilt Session**. Pick one of two real roofs from the GOLDBECK model:

* **Low Roof, Sports**: eight courts packed by the algorithmic placement.
* **High Roof, Garden**: a garden preset, with three saved iterations (planted, social, quiet) ready in **Compare**.

Then open **Results** and **Compare**. With Revit open, **Import Configuration** builds the layout as Revit elements.

The full walkthrough is in the user guide, installed as a PDF (Start menu → *Sportify library → Documentation*). Its source is [`Sportify.Setup/docs/USER_GUIDE.html`](Sportify.Setup/docs/USER_GUIDE.html).

## What is in this repository

| Folder | What it is |
|---|---|
| `SportfyRevit/` | The Revit 2025 add-in (C#, .NET 8): the ribbon tab, Push to Sportify, Import Configuration, the analyses, Kinetics, BIM & documentation, and the local server the web app talks to (port 5679) |
| `Sportify.Api/` | The local API (ASP.NET Core + SQLite `reference.db`): sport fields, build-ups, materials, plants and prices. When installed, it also serves the web app on port 5107 |
| `Sportify.Simulation/` | The Unity project for the analysis films, the Unity-free analysis cores they share with the add-in, and **`Tools/`**: the automated check suite (`node Tools/run-checks.js`, 45 checks) |
| `Sportify.Mechanical/` | The SOLIDWORKS bridge for Kinetics (assembly + STEP) |
| `Sportify.Setup/` | The Inno Setup installer, the library (templates, families, worksets), the user guide and the installer test |
| `BuildDistribution.ps1` | Builds the add-in, publishes the API, signs, and makes the installer (see [RELEASING.md](RELEASING.md)) |

Other notes: [WORKSPACE.md](WORKSPACE.md) (the Sportify folder), [ROOF_FEATURES.md](ROOF_FEATURES.md) (what a roof push carries) and [SECURITY.md](SECURITY.md).

## Build from source (developers)

```
.\BuildDistribution.ps1 -WebAppDir ..\sportfify_goldbeck -RequireLibrary
.\Sportify.Setup\Test-Installer.ps1 -RunApi
node Sportify.Simulation\Tools\run-checks.js --web ..\sportfify_goldbeck
```

This needs the .NET 8 SDK, Revit 2025 (or its reference packages), Node 20 and Inno Setup 6. A plain `dotnet build` of the add-in **deploys into your real Revit Addins folder**, so close Revit first. CI runs the check suite on every push (`.github/workflows/checks.yml`).

## License

[MIT](LICENSE). The Revit templates and families in the installer's library are Revit-authored assets and are not covered by the MIT grant.
