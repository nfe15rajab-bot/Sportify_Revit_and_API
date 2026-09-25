# The Revit ↔ SOLIDWORKS bridge: what it is, what it proves, what it does not

This is a **proof of concept**. It was built because nothing in the course connects the two programs: Revit knows where a building element is and what the analyses
asked for; SOLIDWORKS knows how a mechanism is put together and whether its parts move without hitting each other. The bridge lets a result of the Sportify
analyses (say, "the sun on this garden needs a louvre pergola whose blades turn to follow it") become a real assembly, checked for interferences and recorded as a film,
without anyone drawing it twice. The team's mechanical engineer has not had time to review it, so this document says plainly what has been checked by machine, what has
not been checked by anyone, and what a mechanical engineer would still have to do.

## The chain

```
web app (design, Combine)
   │  layout JSON                                             (localhost server of the add-in)
   ▼
Revit add-in ── analyses (sun, wind, ball paths) ── Kinetics: decides WHAT to make and HOW it moves
   │
   │  one request file per unit: bars (a line, a section, a direction) and membranes, in the unit's own
   │  frame, at every state; plus the design inputs                          (KineticsRender.cs)
   │
   ├──► Revit        the same bars as adaptive components (2 families: bar, membrane), on the
   │                 "Sportify Dynamic Furniture" / "Sportify Structure" worksets, Post analysis phase
   ├──► Unity        an isolated film of the unit through its states               (KineticsRunner.cs)
   └──► SOLIDWORKS   Sportify.Mechanical: parts → assembly → states → interference check → film + SLDASM + STEP
                                                                                (this folder)
```

The three consumers read **the same numbers**, so they cannot disagree on how many blades there are or how far a mast has run. That agreement is the point of the
bridge, and it is also its limit: the film shows that the model is consistent with itself, not that the model is right.

## The contract (the request file)

* Metres and kilograms everywhere. The unit's own frame is right-handed: **x** along the unit, **y** outward (across it), **z** up. SOLIDWORKS is y-up, so the tool
  maps (x, y, z) → (x, z, −y), a proper rotation.
* A **bar** is `{role, p0, p1, u, sizeU, sizeV, dynamic, detail, cadOnly}`: a straight member from p0 to p1 with a rectangular or (for masts, rods, pistons) round section
  of sizeU × sizeV whose sizeU axis points along `u`. `dynamic` = it moves between states. `detail` = mechanism hardware (drawn by Unity and SOLIDWORKS, not placed in Revit).
  `cadOnly` = only SOLIDWORKS draws it (a fence curtain's slats). Bar *i* of one state is bar *i* of the next.
* A **membrane** is four corners (a sail's fabric, a fence's curtain).
* A **state** is `{label, sun, openDeg, bars[], surfaces[]}`; `inputs[]` are the design inputs the sections and densities come from.
* The add-in writes it (`KineticsRender.For`), Unity reads it with `JsonUtility`, the tool with `System.Text.Json`.

## What the tool does

One part per distinct bar (an extrusion of its real section, hollow where the mechanics assume a wall), the assembly at the first state, every state posed by setting each
component's transform, an interference check at each state, every frame rendered by SOLIDWORKS itself (hidden window, its own graphics), the state written on it, the
MP4 encoded with Windows Media Foundation. It keeps the `.SLDPRT`, `.SLDASM` and `.step` files in the Mechanical folder of the Sportify folder, for anyone who wants to
open the model.

## What was checked, and by what

| Claim | How it was checked | By |
|---|---|---|
| The parts stand where the plan puts them | Revit self-test: every instance's solid geometry against the plan, all six test units, worst deviation 0.0 mm | machine (live Revit 2025) |
| The unit moves without parts hitting each other | SOLIDWORKS interference detection at every state, all six test units: none. Expected contacts (a blade end in its rail, a crank on its pin, a carriage on its track) are listed in `UnitAssembly.cs` and are the only ones ignored | machine (SOLIDWORKS 2026) |
| The blade weighs what the mechanics say | Hollow 150 × 30 × 2 mm section: 704 mm², 5.702 kg over 3 m in SOLIDWORKS, equal to the closed-form value the deflection check uses | machine |
| The linkage is a linkage | `SunCheck`: from −60° to 75°, every crank keeps its length, every pin lies on the rod, the rod ends where the piston ends, the cylinder is hinged at a point that never moves and stays in line with its piston, and the piston never runs back into its cylinder | machine |
| The masts telescope in a way a mast can | `SunCheck`: three nested stages, 16 mm narrower each, 0.15 m inserted at full extension, the top stage on the fabric's corner; a storm cannot lower the sail below the collapsed mast | machine |
| The path Revit → tool → film works | The deployed add-in ran the tool from Revit and got a film, an assembly and a STEP file | machine (live) |
| A hung SOLIDWORKS does not hang the run | `watchdog-test` (exit code 4 in seconds), and `smoke` in `run-checks.js --local` | machine |
| The whole path on a small unit still works | `smoke`: a 1.2 m louvre planned by the real cores, built, moved, recorded, saved | machine (found a real problem the first time: see below) |

The smoke test earned its keep at once. On the day it was written, SOLIDWORKS stopped extruding hollow sections on this machine: the setting that lets it infer
relations between sketch entities had changed and it snapped the inner outline to the outer one. The tool now adds sketch entities without inference, so it no longer
depends on that setting.

## What is simplified (know these before you trust a number)

* **No motion study, no mates.** SOLIDWORKS cannot create a motion study in a hidden session, so the tool sets each component's pose itself, frame by frame. The
  interference check therefore tests the poses the plan prescribes; it does not solve the mechanism. The linkage test above is what stands in for solving it, and it is
  a test of the plan's geometry, not of forces.
* **A louvre's linkage** is a parallelogram: cranks of equal length on the blades, one rod, one hinged linear actuator. The rod runs through the end post (in a real part
  that is a slot in the post); the interference check counts that as a contact. Actuator force and torque come from the mechanics model (wind on the blades, safety factor,
  linkage efficiency), with inputs marked *assumed*.
* **The sail's fabric** is a thin flat plate drawn between the four mast tops (a plane fitted to a slightly twisted quadrilateral). A real tensile fabric needs
  prestress and double curvature to carry wind; none of that is modelled. The plate stands in for the fabric's outline and area, which is what the shade analysis uses.
* **The masts** are telescopes (3 stages by default: `mast_stages`, `mast_overlap_m`). The bending check treats a mast as **one tube of the base diameter**; the thinner
  upper stages and the joints are **not** checked. This is the largest engineering gap in the sail.
* **The roller fence's curtain** is drawn as slats that follow the bottom bar; a real curtain is fabric or a rolled slat shutter.
* **No FEA.** Stresses and deflections are closed-form (bending of a beam, a cantilever mast), in `LouvreMechanicsModel.cs` and `SailMechanicsModel.cs`.
* **Inputs**: sections, wall thicknesses, densities, safety factors are defaults marked *standard*, *literature*, *assumed* or *placeholder* in the register (`SportifyKineticsInputs.json`
  next to the add-in). Results are marked PRELIMINARY until the *assumed* ones are entered by someone who owns them.

## What a mechanical engineer would still have to do

1. Decide whether the telescope is the right way to lower a 3.5 m mast (a fold-down or a winched mast are alternatives) and check the joints and the upper stages in bending.
2. Size the linkage's bearings and pins, and give the rod its slot (or move the actuator to the frame's inside).
3. Turn the poses into mates and a real motion study, and read the actuator's force from it.
4. Replace the flat fabric plate with a form-found membrane and check the fabric, cables and anchors for prestress and wind.
5. Confirm or replace every input marked *assumed*.

## How to run it

```
Sportify.Mechanical simulate --request unit.json --out DIR --name NAME [--stills] [--no-detail] [--stall-min 5] [--timeout-min 45]
Sportify.Mechanical smoke                 the whole path on a small unit (about a minute)
Sportify.Mechanical watchdog-test         a hang must end with exit code 4
node Sportify.Simulation/Tools/run-checks.js --local     everything, including the two above (skipped where SOLIDWORKS is not installed)
```

From Revit: Sportify ribbon, Kinetics panel, **Simulate (SOLIDWORKS)**. If SOLIDWORKS stops answering (usually a dialog of its own that a hidden session cannot show) the
tool stops it after five minutes without a sign of life and says so; Revit's progress window has Cancel and its own backstop.
