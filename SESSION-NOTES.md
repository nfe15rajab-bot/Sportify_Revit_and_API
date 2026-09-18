# Session notes — 18 September 2026

Revit add-in and reference API. The web app half is in `sportfify_goldbeck` on
the same branch name.

**Builds on the family-loading work already merged** (`368845f`, *Load a firm's
own Revit families and place them from the web app*). Nothing here changes it —
this PR is what happens *after* a layout comes back from the web app: the ground
it sits on, and the planting on top.

---

## 1. Floor types and floors from the web app's build-ups

The web app now specifies real provider build-ups — ZinCo Roof Garden, Bauder
EXTENSIVE Lightweight Sedum — each with ordered layers. Those map one-to-one
onto a Revit **floor type**, which is the same idea Revit already has: a layered
assembly with a material and thickness per layer.

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
3. Type names reject `{ } [ ] | ; < > ? \` ~`.

> **A silent failure fixed along the way:** `CreateSimpleCompoundStructure`
> validates layer ordering and, when it disagrees, **returns without applying
> anything** — the type silently kept the template's single 300 mm layer while
> the import reported success. Now the type's own structure is edited and
> `IsValid` is called explicitly, so a rejected build-up is reported with the
> offending layer named.

---

## 2. A Revit family per plant species

A tree is not an assembly and not a floor — it is a family, and the web app now
names real species rather than size categories.

The add-in **generates a family per species** it receives: trunk and crown as
extruded circles, sized from that species' own published dimensions.

Each family carries its botanical data as parameters:

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

## 3. Reference API: the catalogs live in the database

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

## 4. Import diagnostics

Every new piece reports its own outcome — floor type created, floor drawn, plant
placed, or the specific reason it failed. A summary that says "imported
successfully" while three zones silently came in without their build-up is worse
than no summary.

---

## 5. One build break

`Color` became ambiguous once WinForms was in the project —
`System.Drawing.Color` and `Autodesk.Revit.DB.Color` both resolve. Fully
qualified in the layer-colouring code.

---

## Try it

1. Start the API (above).
2. Serve the web app, open the Sportify pane.
3. **Push Roof to Sportify** → draw zones, place plants → export.
4. **Import Configuration** → floors with layered build-ups, plants as generated
   species families.

---

## Two things still open

- **No catalogued system can currently carry a tree.** Trees need 800 mm (Van
  den Berk's roof guidance); ZinCo's deepest intensive system gives 250 mm. The
  web app flags every tree placement as a result — a real constraint, not a bug.
- **ZinCo publishes 318 mm for Roof Garden, but the seeded layers sum to 418 mm.**
  They state only the substrate depth; the typical values fill past their stated
  total. **Revit builds the 418 mm version.** Those values want checking against
  a datasheet.
