# Sportify.Mechanical

The SOLIDWORKS side of Kinetics: it builds a dynamic unit (louvre pergola, slat or fin screen, sail on ground rails, roller fence) as a real assembly, moves it through the
states the Sportify analysis worked out, checks it for interferences and records the motion as an MP4. Everything comes from the same numbers as the Revit model and the Unity
video (the add-in writes one request file for all three), so the three agree on how many blades there are, how far apart, and how they move.

Needs SOLIDWORKS (tested on 2026, version 34, the student licence) and the .NET 8 runtime. It starts SOLIDWORKS hidden, or uses the one that is already open (then the work is visible
in it and the session is left open).

## From Revit

Sportify ribbon, Kinetics panel, **Simulate (SOLIDWORKS)**: pick what to make, and a progress window (with Cancel) runs this tool. The film is published to the web app's Post
Results tab; the assembly and its STEP file are in the **Mechanical** folder of your Sportify folder. The add-in finds the tool in a `mechanical` folder next to itself
(`SPORTIFY_MECHANICAL_EXE` overrides it); building the add-in on a machine with SOLIDWORKS publishes it there.

## Commands

```
Sportify.Mechanical simulate --request unit.json --out DIR --name NAME [--fps 15] [--width 1280] [--height 720] [--stills] [--no-video] [--no-detail] [--cancel-file FILE] [--visible]
Sportify.Mechanical thumb    --mp4 film.mp4 --out frame.png [--at 1.5]      a frame read back out of a finished video
Sportify.Mechanical blade    [--span 3] [--write-inputs]                     the louvre blade alone, its mass and area moment against the closed-form section
Sportify.Mechanical encode-test / probe / close                              development aids
```

`simulate` prints `PROGRESS <percent> <text>` lines and, at the end, `RESULT <path of NAME_simulation.json>`. `--stills` writes one composed picture per state instead of a film (to look at
the camera and the poses; `NAME_still_detail.png` is the mechanism close-up). `--no-detail` leaves the close-up out of the film.

## What it makes

* one **part** per distinct bar (an extrusion of its real section: hollow where the mechanics assume a wall, round for masts, rods and pistons), named `NAME_<role>_<n>.SLDPRT`;
* the **assembly** `NAME_assembly.SLDASM` at the first state, and `NAME_assembly.step`;
* the **film** `NAME_simulation.mp4`, every frame rendered by SOLIDWORKS with the state written on it: the unit through its states, then a **close-up of the mechanism** (piston, housing and the nearest cranks) through the same states, ending on a card with what it found;
* `NAME_simulation.json`: components, parts, mass by role, interferences per state, and notes.

The mechanism is in the model: a crank arm on each blade, a push-rod hanging from the arms' pins (it moves on an arc as they turn: a parallelogram linkage), an actuator piston on the
rod and its housing on the frame; a sail's masts stand on carriages on ground tracks with a drive motor at the end of each; a fence has its roller, motor, guide rails and a
curtain of slats. Colours: timber blades, orange cranks and pistons, blue carriages, green motors, yellow masts.

## What it does not do

* A **motion study** cannot be created in a hidden session (SOLIDWORKS returns none), so the tool moves the components itself, frame by frame, and records each frame; the same
  assembly opens in SOLIDWORKS for a real Motion Study (mates on the crank pins and the carriage tracks are the next step).
* A sail's **fabric** is a thin plate; a part cannot change its size, so there is one plate part per outline it takes (on a 15 cm grid), one showing at a time. A sail's **masts** run in and out: each is one part whose length is a dimension that the tool drives frame by frame.
* The push-rod of a louvre or screen passes through the end post (in the real part that is a slot in the post): the interference check treats it as a contact.
* Inputs come from the request (`inputs`: sections, wall thicknesses, densities); the mechanical engineer's own values go in `SportifyKineticsInputs.json` in Revit's Addins folder.
