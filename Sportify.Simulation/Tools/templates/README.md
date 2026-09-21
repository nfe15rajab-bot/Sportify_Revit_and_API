# Sportify templates for Revit

Sportify ships Revit templates that control the visuals (view templates and filters), the sheets and the schedules, after the German norms. They are adapted from Revit's own
German BIM template (`Templates\German\BIM_Architektur_und_Ingenieurbau.rte`) and come in two languages with exactly the same structure:

| | German (`Sportify_DE.rte`) | English (`Sportify_EN.rte`) |
|---|---|---|
| Made from | Revit's German BIM template | Revit's English multi-discipline template |
| View templates | `S1-Vorplanung-Dachaufsicht`, `S2-Entwurf-Nutzungen`, ... | `S1-Preliminary-Roof Plan`, `S2-Design-Uses`, ... |
| Schedules | `S AVA - DACHAUFBAUTEN`, `S FLÄCHEN - DIN 277`, `S KOSTENGRUPPEN - DIN 276`, ... | `S QTO - ROOF BUILD-UPS`, `S AREAS - DIN 277`, `S COST GROUPS - DIN 276`, ... |
| Sheets | `S2-01` ... `S2-13` on the German Plankopf (A1 plans, A3 lists) | the same numbers, English names |
| Norms | HOAI Leistungsphasen 1-4, DIN 1356-1 sheets, DIN 277 areas, DIN 276 cost groups | the same norms, English words; the DIN numbers do not change |

The structure (phases, scales, detail levels, view templates, views, schedules, sheets) is defined once in `SportfyRevit/SportfyRevit/SportifyTemplateSpec.cs`; the German and English
sets are made from one table so they cannot drift apart (`Tools/AddinCheck` checks it, and every name against Revit's own).

## Three ways to get the template

1. **Ribbon, BIM & Documentation, "Apply Sportify Template"**: makes the templates in the open project (asks German or English). A project in imperial units is converted to the German
   template's units first (meters, m², m³, degrees; only the display units change, nothing moves). "More Templates" has the German and the English one directly, and Hide / Show Revit
   Templates.
2. **File > New > Browse** to `Templates\Sportify_DE.rte` or `Sportify_EN.rte` in the add-in's folder: a project that starts with only the Sportify templates. Revit's own view templates,
   views, sheets, schedules and filters are *absent until requested*: "Show Revit Templates" copies them back from Revit's template, "Hide Revit Templates" removes the unused ones again.
3. **After an import**, run Apply again: with a Sportify layout in the project it also makes the views (roof plan, pieces by kind, zones by build-up, circulation, axonometric) with the
   templates applied, their sheets, and writes the DIN 277 / DIN 276 classes onto the elements (`Sportify_DIN277`, `Sportify_KG`, never overwriting a value that is there).

The seven Sportify schedules (`S00_PLANLISTE`, `S AVA - ...`, `S FLÄCHEN - DIN 277`, ...) are assigned to a schedule view template of their own (`S-Liste-Standard` / `S-Schedule-Standard`), like
"Schedule Typical" in Revit's English template, so they read as Sportify's in the Project Browser. The template controls none of a schedule's fields, filters or grouping, and the
assignment is undone for a schedule that would lose any.

The DIN assignments are **proposals for the team to review** (`GermanNorms.cs` states each in one place).

## Rebuilding the .rte files

The `.rte` files are made by Revit, not by a script. Close Revit, build the add-in, then start Revit with an environment variable and click through the unsigned add-in prompt:

```
set SPORTIFY_BUILD_TEMPLATE=<repo>\SportfyRevit\SportfyRevit\Templates
"C:\Program Files\Autodesk\Revit 2025\Revit.exe"
```

The add-in builds `Sportify_DE.rte` and `Sportify_EN.rte` there (a path ending in `.rte` builds the German one only), and checks each: a new project from the file, Show, Hide, with the
counts in `%APPDATA%\Sportify\logs\addin-<date>.log` (`verify De: ...`, `verify En: ...`). `SPORTIFY_VERIFY_IMPERIAL=1` also applies the template to a project from Revit's imperial
template and logs the units before and after.

## Regenerating the lists of Revit's own items

`BuiltInGermanTemplate.cs` and `BuiltInEnglishTemplate.cs` list what Revit's templates bring into a project (so Hide knows what is Revit's and Show where to copy it from). They are
generated from the inspection of the templates (`fixtures/templates/*.inspection.json`, written by `SPORTIFY_INSPECT_TEMPLATES=<files;...>`):

```
node Sportify.Simulation/Tools/templates/generate-builtin-set.js
```
