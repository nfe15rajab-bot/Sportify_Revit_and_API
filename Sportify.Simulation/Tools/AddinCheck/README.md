# AddinCheck

Checks for the Revit-free parts of the Revit add-in, compiled from the same source files the add-in builds. Not part of the Unity project or the add-in.

```
dotnet run --project Tools/AddinCheck -c Release -- Assets/StreamingAssets/sample_layout_roofgarden.json [<folder to write a sample roof push to>]
```

- **Assumptions dialog logic** (`AnalysisAssumptionsPatcher`): reads what a layout has entered or accepted, writes the designer's decisions back, validates what was typed (comma decimals, ranges, choices), and keeps the session's decisions for the project they were made for (a different roof forgets them). The result is read back through the add-in's own layout reader and the analyses, which must report each input as entered, accepted or unconfirmed, and mark the results PRELIMINARY while any is unconfirmed.
- **Roof features** (`RoofFeaturesGeometry`): openings, entries, edge protection, drains, slab and levels in the roof's own plan coordinates (the y flip, de-duplication, edge coverage by parapets and railings, an L-shaped outline), and the JSON round trip through `SportifyLayout`. See `ROOF_FEATURES.md` at the repository root.

What needs Revit itself is not here: `RoofFeatureCollector` (the Revit calls; compiled against the Revit 2025 API but never run outside Revit) and the WPF window (`AnalysisAssumptionsDialog`).

The web app's copy of the assumptions register is checked by `node Tools/StructuralCheck/assumptions-parity.js <path to assumptions.js>`.
