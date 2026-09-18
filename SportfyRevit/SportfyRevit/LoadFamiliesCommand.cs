using System.IO;
using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace SportfyRevit
{
    /// <summary>
    /// How a firm brings its OWN content into Sportify: pick .rfa files,
    /// load them into this project, and publish what they expose — family
    /// name, each type, and every writable parameter — to the web app, so
    /// the Combine tab can offer them as placeable pieces beside its own
    /// built-in catalog.
    ///
    /// Loads as well as reads, deliberately. Revit has to open each .rfa to
    /// see inside it anyway, and a family that was read but never loaded
    /// can't be placed later — the user would design with pieces that turn
    /// out not to exist in the document.
    ///
    /// Replaces the earlier "dump every loaded family" pass, which returned
    /// 1327 types (880 of them furniture) on a real project: letting the
    /// person who knows the library pick the files removes the whole
    /// filtering-heuristics problem rather than solving it.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public class LoadFamiliesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                message = "Open a project first — families are loaded into a document.";
                return Result.Failed;
            }

            // A multi-select dialog, because a firm brings a set of families
            // rather than one — Revit's own FileOpenDialog can't do that.
            //
            // ShowDialog MUST be given Revit's main window as owner. Without an
            // owner, Windows disables the active window for the duration of the
            // modal and has nothing to re-enable afterwards: Revit's main window
            // stays disabled once the dialog closes. The process keeps pumping
            // messages, so it reports itself responsive and sits near 0% CPU
            // while accepting no input at all — indistinguishable from a hang,
            // and it took two of them to spot.
            using var dialog = new System.Windows.Forms.OpenFileDialog
            {
                Title = "Select the Revit families to bring into Sportify",
                Filter = "Revit family files (*.rfa)|*.rfa",
                Multiselect = true,
                InitialDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            };

            var owner = new RevitOwnerWindow(commandData.Application.MainWindowHandle);
            if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK)
                return Result.Cancelled;

            var loaded = new List<object>();
            var failures = new List<string>();

            using (var tx = new Transaction(doc, "Load Sportify families"))
            {
                tx.Start();
                foreach (var file in dialog.FileNames)
                {
                    try
                    {
                        // LoadFamily returns FALSE for an already-loaded family,
                        // which is not a failure — it's the normal case when
                        // someone re-picks a file, or iterates on their own .rfa.
                        // The load options say "yes, overwrite" so a re-pick
                        // actually refreshes the family rather than silently
                        // keeping the old version; if Revit still declines, fall
                        // back to the copy already in the document.
                        doc.LoadFamily(file, new OverwriteFamilyLoadOptions(), out Family family);
                        family ??= FindLoadedFamilyByName(doc, Path.GetFileNameWithoutExtension(file));

                        if (family == null)
                        {
                            failures.Add($"{Path.GetFileName(file)} — Revit declined to load it, and no family of that name is in the project");
                            continue;
                        }
                        loaded.Add(DescribeFamily(doc, family, file));
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{Path.GetFileName(file)} — {ex.Message}");
                    }
                }
                tx.Commit();
            }

            var payload = new
            {
                version = "1.1",
                generator = "SportfyRevit-LoadFamilies",
                document = doc.Title,
                family_count = loaded.Count,
                families = loaded,
                // Every material in the project, so the web app can offer a real
                // dropdown for each Material parameter instead of showing the
                // current value read-only. Ids travel with the names because
                // assigning a material back means setting an ElementId — a name
                // alone would need a second lookup, and two materials can share
                // a name across different classes.
                materials = ReadProjectMaterials(doc)
            };

            string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });

            // Both: the bridge is what the web app actually reads, the file is
            // what a human can open, diff and attach to a mail when it doesn't
            // behave — cheap to write and the first thing worth looking at.
            RoofBoundaryServer.PublishFamilies(json);

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sportify");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "loaded-families.json");
            try { File.WriteAllText(path, json); } catch (Exception) { /* bridge already has it */ }

            var summary = $"Loaded {loaded.Count} famil{(loaded.Count == 1 ? "y" : "ies")} and published them to the web app.";
            if (failures.Count > 0)
                summary += $"\n\nSkipped {failures.Count}:\n  " + string.Join("\n  ", failures);
            summary += $"\n\nDetails: {path}";

            TaskDialog.Show("Sportify", summary);
            return Result.Succeeded;
        }

        /// <summary>
        /// Measures a family by placing one instance, reading its bounding box,
        /// and deleting it again — the only way to get a true footprint for a
        /// family that exposes no dimension parameters and has never been placed.
        ///
        /// Runs inside the caller's open transaction, so the temporary instance
        /// is created and removed in the same undo step and never reaches the
        /// user's model. Placed far from the origin to avoid any chance of it
        /// interacting with (or auto-joining to) real geometry during its brief
        /// life.
        ///
        /// Returns null for anything that can't be placed this way — a hosted
        /// family needs a face or level to sit on, and refusing honestly is
        /// better than reporting a size measured from a failed placement.
        /// </summary>
        private static BoundingBoxXYZ? MeasureByTemporaryInstance(Document doc, FamilySymbol symbol)
        {
            ElementId? tempId = null;
            try
            {
                if (!symbol.IsActive) symbol.Activate();

                // 1 km out on both axes: comfortably clear of any real model
                // content, and irrelevant to the result since only the box's
                // SIZE is used, never its position.
                var somewhereEmpty = new XYZ(
                    UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Meters),
                    UnitUtils.ConvertToInternalUnits(1000, UnitTypeId.Meters),
                    0);

                var instance = doc.Create.NewFamilyInstance(
                    somewhereEmpty, symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                if (instance == null) return null;
                tempId = instance.Id;

                // Regenerate so the instance actually has geometry to measure —
                // without this the bounding box of a just-created element can
                // come back null.
                doc.Regenerate();
                return instance.get_BoundingBox(null);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (tempId != null)
                {
                    try { doc.Delete(tempId); } catch (Exception) { /* nothing left to do */ }
                }
            }
        }

        /// <summary>
        /// Wraps Revit's main window handle so WinForms can own a dialog with
        /// it. Revit hands out an HWND, not an IWin32Window, so this three-line
        /// adapter is the whole bridge between them.
        /// </summary>
        private sealed class RevitOwnerWindow : System.Windows.Forms.IWin32Window
        {
            public RevitOwnerWindow(IntPtr handle) { Handle = handle; }
            public IntPtr Handle { get; }
        }

        /// <summary>
        /// The symbol's own bounding box, in meters — the fallback footprint
        /// for a family that exposes no dimension parameters.
        ///
        /// Returns null rather than a guess when Revit won't give one: a
        /// FamilySymbol that has never been placed often has no cached
        /// geometry, so this genuinely is unknown sometimes, and a made-up
        /// size would be worse than an honest gap the UI can report.
        /// </summary>
        private static object? ReadFootprintM(Document doc, FamilySymbol symbol)
        {
            try
            {
                var bb = symbol.get_BoundingBox(null);

                // A symbol that has never been placed usually has no cached
                // geometry and reports nothing — the common case for a family
                // just loaded from a .rfa. An instance of it, if the project
                // already has one, does have a bounding box.
                if (bb == null)
                {
                    var instance = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance))
                        .Cast<FamilyInstance>()
                        .FirstOrDefault(fi => fi.Symbol?.Id == symbol.Id);
                    bb = instance?.get_BoundingBox(null);
                }

                // Still nothing: measure it directly. A family with no Length/
                // Width parameters still has a real, knowable size — asking the
                // user to type it in was guesswork that would quietly corrupt
                // every clearance check on the canvas.
                bb ??= MeasureByTemporaryInstance(doc, symbol);

                if (bb == null) return null;

                double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
                double x = M(bb.Max.X - bb.Min.X);
                double y = M(bb.Max.Y - bb.Min.Y);
                double z = M(bb.Max.Z - bb.Min.Z);
                if (x <= 0 || y <= 0) return null;

                return new { length_m = Math.Max(x, y), width_m = Math.Min(x, y), height_m = z };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The project's material palette — what a Material parameter is
        /// allowed to be set to. Includes Revit's own identity data (class,
        /// manufacturer, cost) because that's the model's view of cost;
        /// the web app's catalog stays the richer source for carbon/provider
        /// data, and mapping the two is a decision for the app, not something
        /// to bake in here.
        /// </summary>
        private static List<object> ReadProjectMaterials(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .OrderBy(m => m.Name)
                // Only the fields that come straight off the Material object.
                // The identity extras (Cost/Manufacturer/Description) each cost a
                // LookupParameter, which scans the element's whole parameter set —
                // times 131 materials that was a measurable share of a 37-second
                // command, to populate fields most projects leave blank. Read them
                // on demand later if a project turns out to fill them in.
                .Select(m => (object)new
                {
                    id = m.Id.Value.ToString(),
                    name = m.Name,
                    material_class = m.MaterialClass,
                    category = m.MaterialCategory
                })
                .ToList();
        }

        /// <summary>
        /// A .rfa's file name is normally the family name, but not always —
        /// so this matches on name and simply returns null if nothing lines
        /// up, leaving the caller to report an honest failure rather than
        /// guessing at the wrong family.
        /// </summary>
        private static Family? FindLoadedFamilyByName(Document doc, string name)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Re-picking a file the user has edited should bring the edit in,
        /// so this answers Revit's "this family is already here" prompt with
        /// "overwrite, values included" instead of leaving a stale copy —
        /// and answers it in code, so a multi-file pick never stops to ask.
        /// </summary>
        private sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = true;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse,
                out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        private static object DescribeFamily(Document doc, Family family, string sourceFile)
        {
            var types = new List<object>();

            foreach (var id in family.GetFamilySymbolIds())
            {
                if (doc.GetElement(id) is not FamilySymbol symbol) continue;

                var all = ReadParameters(symbol);
                // Only measure geometry when there's nothing better. get_BoundingBox
                // can make Revit regenerate a symbol's geometry, which on a real
                // project dominated the command's runtime (37s for two families) —
                // and a family that already reports its own Length/Width has no use
                // for a measured fallback anyway.
                bool resizable = all.Any(p => p.spec_is_length && !p.is_read_only);
                var bbox = resizable ? null : ReadFootprintM(doc, symbol);

                types.Add(new
                {
                    type_name = symbol.Name,
                    // A family with no writable Length parameters isn't unusable —
                    // it just can't be resized. Its geometric footprint still lets
                    // the Combine tab lay it out at its natural size, which is the
                    // difference between "fixed size" and "unsupported". Real
                    // third-party content is often like this (a flat 2D court
                    // symbol exposes no parameters at all).
                    footprint_m = bbox,
                    is_resizable = resizable,
                    // The shortlist the web app should actually offer as "which
                    // parameter is the length/width" — a writable Length is the
                    // language-independent signal (a German family's "Feldlänge"
                    // is as findable as an English "Length"), and on a real
                    // third-party court that cut 31 parameters down to 2.
                    dimension_candidates = all
                        .Where(p => p.spec_is_length && !p.is_read_only)
                        .Select(p => new { p.name, p.value_m })
                        .ToList(),
                    // Material slots map onto the app's own low/medium/high
                    // quality tiers, which already mean exactly this.
                    material_candidates = all
                        .Where(p => p.storage_type == "ElementId" && !p.is_read_only
                                    && p.spec_is_material)
                        // current_id is what preselects the dropdown and what a
                        // write-back compares against; the name alone can't do
                        // either, since assignment is by ElementId.
                        .Select(p => new { p.name, value = p.display_value, current_id = p.value })
                        .ToList(),
                    parameters = all
                });
            }

            return new
            {
                family_name = family.Name,
                category = family.FamilyCategory?.Name,
                category_id = family.FamilyCategory?.Id.Value.ToString(),
                source_file = sourceFile,
                types
            };
        }

        private sealed record ParamInfo(
            string name, string storage_type, string? spec, bool is_read_only, bool is_shared,
            string? value, double? value_m, string? display_value,
            bool spec_is_length, bool spec_is_material);

        private static List<ParamInfo> ReadParameters(FamilySymbol symbol)
        {
            var list = new List<ParamInfo>();
            foreach (Parameter p in symbol.Parameters)
            {
                if (p?.Definition == null) continue;

                ForgeTypeId? spec = null;
                try { spec = p.Definition.GetDataType(); }
                catch (Exception) { /* a few built-ins have no spec at all */ }

                bool isLength = spec != null && spec == SpecTypeId.Length;
                bool isMaterial = spec != null && spec == SpecTypeId.Reference.Material;

                string? value = p.StorageType switch
                {
                    StorageType.Double => p.AsDouble().ToString("G"),
                    StorageType.Integer => p.AsInteger().ToString(),
                    StorageType.String => p.AsString(),
                    StorageType.ElementId => p.AsElementId().Value.ToString(),
                    _ => null
                };

                double? valueM = isLength
                    ? UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.Meters)
                    : null;

                list.Add(new ParamInfo(
                    p.Definition.Name, p.StorageType.ToString(), spec?.TypeId,
                    p.IsReadOnly, p.IsShared, value, valueM, p.AsValueString(),
                    isLength, isMaterial));
            }
            return list;
        }
    }
}
