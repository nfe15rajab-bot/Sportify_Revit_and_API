# Session notes — 18 September 2026

Revit add-in and reference API. The web app half is in `sportfify_goldbeck` on
the same branch name.

---

## 1. Firms load their own families

The add-in previously had no way to use a firm's own content. Shipping families
with the tool is the wrong shape: a firm that has invested years in its Revit
library does not want ours, and the geometry is the least interesting part —
their families carry their material standards, their parameters and their
detail level.

**Revit → Sportify → Load Families** opens a file picker. Pick .rfa files; the
add-in loads them and publishes what it found to the web app.

**Why a picker and not "read what's loaded":** the first version read every
family in the document and returned **1327**, of which ~880 were furniture. A
project model is full of families that have nothing to do with a sports roof.
The firm knows which ones matter; nothing in the model says so.

### Parameters are discovered, not configured

The add-in does not look for parameters called `Length` or `Width`. It reads
each type parameter's **storage type and spec** and keeps the ones that are
lengths. A firm calling its parameter `Spielfeld_Laenge` works with no mapping
file, because the question asked is "is this a length?" — not "is this called
what we expect?"

Size is measured by **placing a temporary instance 1 km from the model,
regenerating, reading its bounding box, then deleting it**. Type parameters
often do not describe the real extent, and geometry cannot lie.

---

## 2. Floor types and floors from the web app's build-ups

The web app now specifies real provider build-ups — ZinCo Roof Garden, Bauder
EXTENSIVE Lightweight Sedum — each with ordered layers. Those map one-to-one
onto a Revit **floor type**, which is the same idea Revit already has.

On import the add-in creates a floor type per build-up, then **draws the actual
Floor** for every zone the designer drew. A type with nobody using it is an
entry in a dialog, not a design.

Layer materials are generated with a **colour per function** (vegetation green,
substrate brown, drainage blue…) so a build-up reads in section instead of
appearing as grey bands.

### Three Revit constraints worth knowing

1. **Membrane layers must be exactly zero thickness.** A root barrier given
   1 mm is rejected. Real thin layers are mapped to `Substrate` instead, which
   is the function that permits a thickness.
2. **Exactly one layer may be structural**, and it cannot be the top or bottom.
3. Type names reject `{ } [ ] | ; < > ? \` ~`. Generated types are named in the
   family's own convention (`26000 x 14000 mm`).

> **A silent failure fixed:** `CreateSimpleCompoundStructure` validates layer
> ordering and, when it disagrees, **returns without applying anything** — the
> type silently kept the template's single 300 mm layer and the import reported
> success. Now the type's own structure is edited and `IsValid` is called
> explicitly, so a rejected build-up is reported with the offending layer named.

---

## 3. A Revit family per plant species

A tree is not an assembly and not a floor — it is a family, and the web app now
names real species rather than size categories.

The add-in **generates a family per species** it receives: trunk and crown as
extruded circles, sized from that species' own published dimensions.

Each family carries its botanical data as shared-style parameters:

| Parameter | Why |
|---|---|
| `Sportify_BotanicalName` | The species, for a schedule or a plant list |
| `Sportify_MatureHeightM` | **Shading, wind and clearance studies** |
| `Sportify_MinSubstrateM` | The substrate depth those roots need |
| `Sportify_CrownM`, `Sportify_Source` | Size, and where the figure came from |

Mature height is carried whether or not this add-in uses it. A partner doing a
shading or wind study needs it and cannot recover it from a plan footprint — so
it travels through the pipeline rather than being re-entered later.

Crowns are drawn as **two half-arcs**, because Revit rejects a full circle as a
single curve in a profile loop.

---

## 4. Reference API: the catalogs live in the database

Build-ups and species were constants in the web app's JavaScript, so adding a
ZinCo product meant a code change and a deploy.

- **`RoofAssembly` + `RoofAssemblyLayer`** — new tables. Two tables rather than
  one because six layers cannot live in a row, and their **order is part of the
  specification**.
- **`RoofAssembliesController`** — GET / GET by key / POST / PUT / DELETE.
  Layers always travel with their system; saving them separately invites a
  half-updated build-up that looks complete. Layers are renumbered from arrival
  order, so the client never maintains indices while dragging rows.
- **`Plant`** gained the roof-relevant fields: form, mature height, height
  range, crown min/max, minimum substrate, source and source URL.
- **Seeded** with five real provider systems and seven species — the four trees
  Van den Berk specifically recommends for roof gardens, plus lavender, blue
  fescue and a sedum mat.

**Every figure is marked published or typical**, and system-level values are
left **null** where a manufacturer does not publish them rather than carrying a
guess. Those are the numbers an engineer checks a deck against.

- **CORS** now allows both `http://localhost:8123` and `:8124`.

> **The web app has no fallback catalog, deliberately.** A built-in copy that
> stands in when the API is down means two catalogs that drift apart. **The API
> must be running** for the Zones and Plants panels. It does *not* need
> PostgreSQL — the reference data is SQLite and creates itself:
>
> ```
> cd Sportify.Api/Sportify.Api
> dotnet run --launch-profile http
> ```

---

## 5. The add-in had no manifest

`SportfyRevit.addin` did not exist. Revit finds add-ins through that manifest
file, not by scanning for DLLs, so the compiled add-in was being **silently
ignored** — no error, no ribbon tab, nothing to debug. Added, and the build now
copies it to the output folder automatically.

---

## 6. Bugs fixed

**Revit froze completely, twice.** A WPF dialog shown with no owner window left
the main Revit window disabled behind it; the process reported *Responding =
True* at 2% CPU, so it looked healthy while being unusable. Dialogs are now
WinForms, owned by `MainWindowHandle`.

**The layout landed at ground level** instead of on the roof — three separate
causes: the roof elevation was never sent, the import hardcoded Z to 0, and
`NewFamilyInstance` **ignores the Z of its placement point**. Fixed by sending
`origin_z_m` from the pushed roof face and moving each instance to elevation
after `doc.Regenerate()`.

**The layout came in mirrored.** Canvas Y runs downward, Revit Y runs upward.
Items, circulation and entries are flipped; the **boundary polygon deliberately
is not**, because the web app already flips it when drawing. Both halves now
agree.

**`LoadFamily` returns false when the family is already loaded**, which was
being read as failure. Now uses an overwrite handler and falls back to a name
lookup.

**`Color` became ambiguous** once WinForms was enabled — `System.Drawing.Color`
and `Autodesk.Revit.DB.Color`. Fully qualified.

---

## 7. Import diagnostics

Every piece reports its own outcome — family resolved, type duplicated, floor
type created, floor drawn, plant placed, or the specific reason it failed.
A summary that says "imported successfully" while three courts silently came in
at the wrong size is worse than no summary.

---

## Try it

1. Start the API (above).
2. Serve the web app, open the Sportify pane.
3. **Load Families** → pick a firm .rfa.
4. **Push Roof to Sportify** → configure → export.
5. **Import Configuration** → read the diagnostics.
