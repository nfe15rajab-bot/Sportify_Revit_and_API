# DynamicCheck

Checks for the dynamic structural analysis (`Assets/Scripts/Simulation/Dynamics/DynamicLoadCore.cs`): a day of crowds, weather load cases, and the resonance of the deck under rhythmic crowd movement. Not part of the Unity project; Unity only imports `Assets/`.

Like the other analyses the model is plain C# with no Unity types: the Unity run films it, the Revit add-in computes the numbers with no Unity installed, and this tool runs it on a layout.

## Run the model and its checks

```
dotnet run --project Tools/DynamicCheck -c Release -- Assets/StreamingAssets/sample_layout_roofgarden.json
```

Prints the crowd through the day, the snow, rain and wind figures, every load case, each bay's natural frequency and response, the advice; then checks what must hold whatever the constants:

- **Crowds:** the same layout gives the identical report twice; every second arrivals - departures = people on the roof, everybody inside the roof, settled + leaving = all; half an hour into every hour each piece holds what the schedule asks; nobody exceeds the busiest hour; the roof is empty at the end.
- **Weather:** snow ground loads against DIN EN 1991-1-3/NA (zones 1, 1a, 2, 3 and their minimums); dry <= field capacity <= saturated build-up weight; the cloudburst lies between field capacity and saturation; the gale lifts; every case's utilisation is its load per m2 over the capacity; each bay's governing case is the highest.
- **Resonance:** magnification 1/(2 zeta) at resonance; the dynamic load factors of jumping (1.8, 1.29, 0.67 for contact ratio 1/3) agree with a numerical Fourier analysis of a half-sine pulse train; acceleration is linear in the force; frequency scales as it should with span, depth and mass; the spectrum peaks where a harmonic can reach the deck.
- **Inputs:** a given natural frequency replaces the estimate in every bay; a given snow zone, altitude and another day schedule are read.

Add `--json` to print the whole report instead.

## Check it against an independent implementation

`oracle.js` re-implements the closed-form parts (snow load, the harmonics of jumping by numerical Fourier analysis, the deck's frequency, the acceleration of every bay under every activity including the search over the crowd's rhythm and the frequency band, the sweep) from the specification in the header of `DynamicLoadCore.cs`. It takes only each bay's inputs (span, area, mass, participants, force) from the C# report:

```
dotnet run --project Tools/DynamicCheck -c Release -- <layout.json> --json > report.json
node Tools/DynamicCheck/oracle.js <layout.json> report.json
```

It must print `ORACLE MATCH`. The crowd simulation is not re-implemented: it is checked by the invariants above and by running it twice. After a Unity run (`BatchRunner.RunDynamicAnalysis`), `node Tools/WindCoreCheck/compare-unity.js Recordings/dynamic_results.json report.json` confirms Unity's reading of the layout gives the same report, value for value (about 2,450 values).

## What the numbers are and are not

A screening model, not a structural verification or a vibration design. The natural frequency is **estimated** from the spans (a simply supported strip, deck depth span/25, E = 30 GPa, the load's mass; good to about 25%) unless the structural engineer's first natural frequency is given (`structure.natural_frequency_hz`); the snow zone is **assumed** (zone 2 at 100 m) unless set (`site_conditions.snow_zone`, `altitude_m`); the day is a generic schedule of the author's (`site_conditions.day_schedule`); the acceleration limits (0.02 g walking, 0.05 g rhythmic), the coordination of participants and the damping (3%) are the author's. What is from the literature: the snow-load formulas (DIN EN 1991-1-3/NA), the half-sine pulse model of jumping (Bachmann and Ammann), the 1.5 to 2.8 Hz range of a group's rhythm and 0.25 people per m2 of a rhythmic crowd, the EN 1990 combination values 0.7 (assembly) and 0.5 (snow), which were recalled, not checked against the standards.
