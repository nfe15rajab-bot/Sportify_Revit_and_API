# SunCheck

Checks for the sun and shade analysis (`Assets/Scripts/Simulation/Sun/SunShadeCore.cs`). None of this is part of the Unity project; Unity only imports `Assets/`.

The model is plain C# with no Unity types, so three programs compile the same file: the Unity run (which films it), the Revit add-in (which computes the numbers with no Unity installed, `SportfyRevit/AnalyzeSunShadeCommand.cs`) and this tool. The layout is read twice, on purpose: by `SunLayoutAdapter` in Unity (`JsonUtility`) and by the add-in's `SunLayoutAdapter` (`System.Text.Json`, the one this tool uses). The comparison at the end confirms they agree.

## What the analysis does

Direct sun only, in solar time, on the roof's plan (0.5 m cells): the sun's position on 21 June, 21 March and 21 December from a declination series and the hour angle; the shadows of the walls standing on the roof (`roof_context.features.obstacles`, see `ROOF_FEATURES.md`), of trees (a sphere for the crown, letting some sun through) and of any equipment; the sun hours of every zone; the shade of every people zone at midday in the heat window (11 to 16 h) against the shade target; whether every garden still gets the sun it needs. Where a people zone is too sunny it searches for shading equipment (sails, parasols, pergolas, canopies, a tree in a deep bed) that fixes it without taking the sun from a garden, and puts what it weighs on the deck through the structural analysis. It is a **screening model**: the shade target, the garden's sun need, the equipment catalogue and the tree densities are the author's choices, listed in the assumptions register (`AnalysisAssumptions.cs`) and marked PRELIMINARY until the designer enters or accepts them.

## Run the model on a layout

```
dotnet run --project Tools/SunCheck -c Release -- layout.json
```

Prints the case study, the three design days, every zone, every piece of equipment and the deck effect, then runs the checks that must hold whatever the constants (the sun's geometry, shadows against hand-worked cases, mirror symmetry, "equipment only takes sun away", pieces inside the roof and off courts / drains / entries / each other, gardens kept, determinism, edge cases such as a 0% or 100% target, another latitude, the polar circle). It must end with every line `PASS`.

`--json` writes the inputs the adapter made of the file and the whole report, for the oracle and the comparison below.

## Check it against an independent implementation

`oracle.js` re-implements the model from its specification without reading the C#: the sun by another formula, the roof outline by a winding number, and every shadow by **ray casting** from each cell toward the sun against the real solids (where the C# builds shadow shapes). It recomputes the zones, the sun hours, the peak shade and the state after the pieces, checks that every piece obeys the placement rules and that the first piece is the one the search rule picks.

```
dotnet run --project Tools/SunCheck -c Release -- layout.json --json > sun.json
node Tools/SunCheck/oracle.js sun.json
```

It must print `ORACLE MATCH`. Change the oracle deliberately whenever the model itself changes: the point is that a slip in one is not repeated in the other.

## Check Unity's run against the add-in's

After a Unity run (`BatchRunner.RunSunAnalysis`, `-layoutFile=...`) has written `Recordings/sun_results.json`, compare its `analysis` with the report of the same layout. `compare-unity.js` (in `Tools/WindCoreCheck`) walks the report the tool writes, so hand it the `report` part of `--json`:

```
node -e "const fs=require('fs');fs.writeFileSync('report.json',JSON.stringify(JSON.parse(fs.readFileSync('sun.json','utf8')).report))"
node Tools/WindCoreCheck/compare-unity.js Recordings/sun_results.json report.json
```

It must print `UNITY == ADD-IN SIDE`. Recommendations rank pieces by their gain rounded to 1e-9 with a stable order, so a tie cannot be broken differently by the two readers' last-bit differences.

## Trying variants

The sample layout of the roof garden has no sports; the sun analysis is easiest to see on a layout with a court, a play area, a yoga area and gardens, in a few orientations, latitudes and targets (the checks above were run on seven such variants). In Revit the designer's own choices come from the window that opens before the analysis (`AnalysisAssumptionsDialog`) or from the Site tab (Analysis assumptions), and travel in `analysis_assumptions` (`shade_target_percent`, `garden_min_sun_hours`, `shade_equipment`, `site_latitude_deg`) and `site_conditions.north_deg`.
