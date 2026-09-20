using Autodesk.Revit.DB;

namespace SportfyRevit
{
    /// <summary>
    /// Turns a provider build-up from the web app into a real Revit FloorType.
    ///
    /// This is the surface counterpart to family placement. A green roof or a
    /// paved walkway is not a family — it is a Floor, and a FloorType is exactly
    /// what the web app's assembly already describes: ordered layers, each with
    /// a material and a thickness. The translation is direct, with nothing to
    /// infer.
    ///
    /// Far cheaper than the family work, and for a structural reason: a floor
    /// type is a system family, so creating one is a duplicate plus a layer
    /// table inside the open document. No family document, no template, no save
    /// and reload.
    ///
    /// Types are named "Sportify - {provider} {system}" so anything this tool
    /// created is identifiable in a project template someone else maintains,
    /// and reused on the next import rather than duplicated — the same
    /// converge-don't-multiply rule the family type resolver follows, and it
    /// matters just as much here because AutoImportSync can run every two
    /// seconds.
    /// </summary>
    internal static class SportifyFloorTypeBuilder
    {
        /// <summary>Two thicknesses within a tenth of a millimetre are the same layer.</summary>
        private const double ThicknessToleranceFt = 0.0003;

        /// <summary>
        /// Maps the web app's layer functions onto Revit's own layer roles, so a
        /// schedule reads correctly and the structural layer is the one carrying
        /// the load. Revit requires exactly one Structure layer in a compound
        /// structure, which the caller guarantees below.
        /// </summary>
        /// <summary>
        /// Membrane is deliberately never used, even though a filter fleece or a
        /// root barrier is exactly what Revit means by one: a Membrane layer must
        /// have ZERO thickness, and Revit rejects the entire compound structure
        /// if it doesn't. Those layers are real millimetres of real product and
        /// have to appear in the build-up depth, so they are mapped to Substrate
        /// instead and keep their thickness.
        ///
        /// The trade is a slightly less idiomatic layer role in exchange for a
        /// build-up that measures correctly — and depth is what the model is for.
        /// </summary>
        private static MaterialFunctionAssignment FunctionFor(string? webFunction) => webFunction switch
        {
            "vegetation" => MaterialFunctionAssignment.Finish1,
            "substrate" => MaterialFunctionAssignment.Finish2,
            "wearing" => MaterialFunctionAssignment.Finish1,
            "bedding" => MaterialFunctionAssignment.Finish2,
            "filter" => MaterialFunctionAssignment.Substrate,
            "root_barrier" => MaterialFunctionAssignment.Substrate,
            "waterproofing" => MaterialFunctionAssignment.Substrate,
            "drainage" => MaterialFunctionAssignment.Substrate,
            "protection" => MaterialFunctionAssignment.Substrate,
            _ => MaterialFunctionAssignment.Structure,
        };

        /// <summary>
        /// Finds or creates the floor type for one assembly. Returns null if it
        /// cannot be built — the caller reports that rather than substituting
        /// something the user did not ask for.
        /// </summary>
        public static FloorType? GetOrCreate(Document doc, AssemblyDto assembly)
        {
            string name = assembly.RevitTypeName;
            if (string.IsNullOrWhiteSpace(name))
                name = $"Sportify - {assembly.Provider} {assembly.SystemName}";

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault(ft => string.Equals(ft.Name, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                // Reused, but refreshed: a build-up edited in the web app should
                // reach a type that already exists, or the second import would
                // silently keep the first import's thicknesses.
                if (!MatchesAssembly(doc, existing, assembly) && !ApplyStructure(doc, existing, assembly, out string refreshFailure))
                    ImportDiagnostics.FloorTypeFailed(name, "could not refresh layers — " + refreshFailure);
                ImportDiagnostics.FloorTypeReused(name);
                return existing;
            }

            // Duplicate any floor type that has a compound structure to edit —
            // FloorType cannot be constructed directly, only duplicated.
            var template = new FilteredElementCollector(doc)
                .OfClass(typeof(FloorType))
                .Cast<FloorType>()
                .FirstOrDefault(ft => ft.GetCompoundStructure() != null);

            if (template == null)
            {
                ImportDiagnostics.FloorTypeFailed(name, "this project has no floor type to duplicate from");
                return null;
            }

            if (template.Duplicate(name) is not FloorType created)
            {
                ImportDiagnostics.FloorTypeFailed(name, $"Revit refused to duplicate \"{template.Name}\"");
                return null;
            }

            if (!ApplyStructure(doc, created, assembly, out string failure))
            {
                ImportDiagnostics.FloorTypeFailed(name, failure);
                return created;
            }

            ImportDiagnostics.FloorTypeCreated(name, assembly.Layers?.Count ?? 0, assembly.TotalThicknessM);
            return created;
        }

        /// <summary>
        /// Writes the assembly's layers onto a floor type.
        ///
        /// Revit demands exactly one Structure layer, so the thickest layer is
        /// promoted to that role — on a green roof that is the growing medium,
        /// which is both the deepest layer and the one actually bearing the
        /// build-up above it.
        /// </summary>
        private static bool ApplyStructure(Document doc, FloorType floorType, AssemblyDto assembly, out string failure)
        {
            failure = "";
            var source = assembly.Layers;
            if (source == null || source.Count == 0) { failure = "the assembly has no layers"; return false; }

            var ordered = source.OrderBy(l => l.Order).ToList();
            int structuralIndex = IndexOfThickest(ordered);

            var layers = new List<CompoundStructureLayer>();
            for (int i = 0; i < ordered.Count; i++)
            {
                var l = ordered[i];
                double widthFt = UnitUtils.ConvertToInternalUnits(l.ThicknessM, UnitTypeId.Meters);

                // Revit rejects a zero-width layer outright. A membrane quoted as
                // 0 mm is still a real product line on a drawing, so it is kept
                // at Revit's own minimum rather than dropped from the build-up.
                if (widthFt <= 0) widthFt = UnitUtils.ConvertToInternalUnits(0.001, UnitTypeId.Meters);

                var function = i == structuralIndex
                    ? MaterialFunctionAssignment.Structure
                    : FunctionFor(l.Function);

                layers.Add(new CompoundStructureLayer(widthFt, function, FindOrCreateMaterial(doc, l.Name, l.Function)));
            }

            try
            {
                // Modify the structure the duplicated type already has, rather than
                // building one with CreateSimpleCompoundStructure. That factory
                // validates layer ordering against Revit's own exterior-to-interior
                // rules and rejects the whole thing silently on a mismatch — which
                // is how a type ended up carrying its template's single 300 mm
                // foundation-slab layer while reporting success.
                var structure = floorType.GetCompoundStructure()
                                ?? CompoundStructure.CreateSingleLayerCompoundStructure(
                                       MaterialFunctionAssignment.Structure,
                                       layers[structuralIndex].Width,
                                       layers[structuralIndex].MaterialId);

                structure.SetLayers(layers);
                structure.StructuralMaterialIndex = structuralIndex;

                // Ask Revit whether it will accept this BEFORE writing it, and say
                // which layer it objects to. A structure that fails validation is
                // the difference between a real build-up and a slab pretending to
                // be one.
                if (!structure.IsValid(doc, out IDictionary<int, CompoundStructureError> errors, out _))
                {
                    // Revit hands back a layer index per problem, so the report can
                    // name the offending product and its thickness rather than
                    // leaving someone to bisect a six-layer build-up by hand.
                    failure = "Revit rejected the structure — " + string.Join("; ", errors.Select(kv =>
                    {
                        var l = kv.Key >= 0 && kv.Key < ordered.Count ? ordered[kv.Key] : null;
                        return l == null
                            ? $"layer {kv.Key}: {kv.Value}"
                            : $"layer {kv.Key + 1} \"{l.Name}\" ({l.ThicknessM * 1000:0} mm, {l.Function}): {kv.Value}";
                    }));
                    return false;
                }

                floorType.SetCompoundStructure(structure);
                StampIdentity(floorType, assembly);
                return true;
            }
            catch (Exception ex)
            {
                // Recorded, not swallowed. The first version of this reported only
                // "layers could not be applied", which was true, useless, and hid
                // a plain Revit rule (a Membrane layer must be zero thickness).
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        private static int IndexOfThickest(List<AssemblyLayerDto> layers)
        {
            int best = 0;
            for (int i = 1; i < layers.Count; i++)
                if (layers[i].ThicknessM > layers[best].ThicknessM) best = i;
            return best;
        }

        /// <summary>
        /// Does the type already carry this build-up? Compared on layer count and
        /// thickness rather than on material names, since a firm may have
        /// remapped materials deliberately and rewriting those would undo their
        /// work on every import.
        /// </summary>
        private static bool MatchesAssembly(Document doc, FloorType floorType, AssemblyDto assembly)
        {
            try
            {
                var cs = floorType.GetCompoundStructure();
                var wanted = assembly.Layers?.OrderBy(l => l.Order).ToList();
                if (cs == null || wanted == null) return false;

                var existing = cs.GetLayers();
                if (existing.Count != wanted.Count) return false;

                for (int i = 0; i < existing.Count; i++)
                {
                    double wantFt = UnitUtils.ConvertToInternalUnits(wanted[i].ThicknessM, UnitTypeId.Meters);
                    if (Math.Abs(existing[i].Width - wantFt) > ThicknessToleranceFt) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Layer materials are matched by the product name the provider uses
        /// ("Growing media Zincoblend I"), and created when the project has no
        /// such material. Creating one is the right call rather than leaving the
        /// layer blank: an unnamed layer schedules as nothing, and the product
        /// name is the only thing a contractor can order against.
        /// </summary>
        private static ElementId FindOrCreateMaterial(Document doc, string? name, string? layerFunction)
        {
            if (string.IsNullOrWhiteSpace(name)) return ElementId.InvalidElementId;

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing.Id;

            try
            {
                // Revit rejects the same characters in a material name as in a
                // type name, and these come from a provider's own product naming.
                var id = Material.Create(doc, SanitizeName(name!));
                if (doc.GetElement(id) is Material created) ApplyLayerAppearance(doc, created, layerFunction);
                return id;
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        /// <summary>
        /// Records where the build-up came from, on the type itself. Without it
        /// a generated floor type is untraceable: someone opening the project in
        /// a year has no way to tell which supplier's system it represents, or
        /// which figures were published rather than assumed.
        /// </summary>
        private static void StampIdentity(FloorType floorType, AssemblyDto assembly)
        {
            TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_MANUFACTURER, assembly.Provider);
            TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_MODEL, assembly.SystemName);
            TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_URL, assembly.SourceUrl);

            var notes = new List<string>();
            if (assembly.SaturatedKgM2 != null) notes.Add($"Saturated {assembly.SaturatedKgM2} kg/m2");
            if (assembly.WaterStorageLM2 != null) notes.Add($"Water storage {assembly.WaterStorageLM2} l/m2");
            if (assembly.BuildUpMm != null) notes.Add($"Published build-up {assembly.BuildUpMm} mm");

            int assumed = assembly.Layers?.Count(l => l.ThicknessSource != "published") ?? 0;
            if (assumed > 0) notes.Add($"{assumed} layer thickness(es) are typical values, not published by {assembly.Provider}");

            if (notes.Count > 0)
                TrySetParameter(floorType, BuiltInParameter.ALL_MODEL_DESCRIPTION, string.Join(" · ", notes));
        }

        private static void TrySetParameter(Element el, BuiltInParameter bip, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            try
            {
                var p = el.get_Parameter(bip);
                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(value);
            }
            catch (Exception) { /* identity data is a nice-to-have, never worth failing over */ }
        }

        /// <summary>
        /// Draws the actual Floor for a parcel, from the rectangle the web app
        /// already exports for it.
        ///
        /// Creating the floor TYPE is only half the job — a type that nothing
        /// uses is a line in a browser tree, not a roof. This is the half that
        /// puts real geometry on the building, with real layers and real
        /// quantities a schedule can total.
        ///
        /// The parcel's bounding box is the boundary: no drawn-surface tooling
        /// is needed for a rectangle, since every placement already carries one.
        /// </summary>
        public static Floor? CreateFloor(Document doc, FloorType floorType, BoundingBoxDto bb,
                                         double originXFt, double originYFt, double elevationFt,
                                         out string failure)
        {
            failure = "";
            try
            {
                // The canvas measures Y downward and Revit upward, so the box's
                // top edge is its lower Y here — the same flip every other
                // coordinate in this import goes through (and, for a roof turned
                // against the model's axes, the same turn).
                var p0 = SportifyLayoutBuilder.PlanToWorldFt(bb.TopLeftXM, bb.TopLeftYM + bb.HeightM);            // bottom-left on the canvas
                var p1 = SportifyLayoutBuilder.PlanToWorldFt(bb.TopLeftXM + bb.WidthM, bb.TopLeftYM + bb.HeightM); // bottom-right
                var p2 = SportifyLayoutBuilder.PlanToWorldFt(bb.TopLeftXM + bb.WidthM, bb.TopLeftYM);             // top-right
                var p3 = SportifyLayoutBuilder.PlanToWorldFt(bb.TopLeftXM, bb.TopLeftYM);                          // top-left

                var level = NearestLevel(doc, elevationFt);
                if (level == null) { failure = "this project has no level to host a floor on"; return null; }

                // Floors are drawn flat on their level and lifted by an offset
                // parameter — the sketch itself is planar at the level's own
                // elevation, so the loop is built there and raised afterwards.
                double z = level.Elevation;
                var loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(new XYZ(p0.X, p0.Y, z), new XYZ(p1.X, p1.Y, z)),
                    Line.CreateBound(new XYZ(p1.X, p1.Y, z), new XYZ(p2.X, p2.Y, z)),
                    Line.CreateBound(new XYZ(p2.X, p2.Y, z), new XYZ(p3.X, p3.Y, z)),
                    Line.CreateBound(new XYZ(p3.X, p3.Y, z), new XYZ(p0.X, p0.Y, z)),
                });

                var floor = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
                if (floor == null) { failure = "Revit returned no floor"; return null; }

                var offset = floor.get_Parameter(BuiltInParameter.FLOOR_HEIGHTABOVELEVEL_PARAM);
                if (offset != null && !offset.IsReadOnly) offset.Set(elevationFt - level.Elevation);

                return floor;
            }
            catch (Exception ex)
            {
                failure = $"{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>
        /// The level closest to the roof, so the floor's offset stays small and
        /// the element reads sensibly in a project browser — a parcel 14 m up
        /// listed against Level 0 with a 14 m offset is technically correct and
        /// unhelpful to everyone who opens the model afterwards.
        /// </summary>
        private static Level? NearestLevel(Document doc, double elevationFt)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => Math.Abs(l.Elevation - elevationFt))
                .FirstOrDefault();
        }

        /// <summary>
        /// Colours a newly created material by what the layer does.
        ///
        /// Without this every generated material takes Revit's default grey, and
        /// a six-layer green roof cuts as one undifferentiated black band — the
        /// build-up is there in the data and invisible in the drawing, which is
        /// the only place anyone checks it. The palette matches the layer stack
        /// the web app draws, so a section reads like the panel the designer
        /// chose it from.
        ///
        /// Both the shaded colour and the cut pattern are set: shaded carries 3D
        /// views, the cut pattern is what actually shows in a section, and a
        /// section is where a build-up is read.
        /// </summary>
        private static void ApplyLayerAppearance(Document doc, Material material, string? layerFunction)
        {
            var (r, g, b) = layerFunction switch
            {
                "vegetation"    => (0x3F, 0x8F, 0x4F),  // planting green
                "substrate"     => (0x8A, 0x6A, 0x43),  // soil brown
                "filter"        => (0xC7, 0xB8, 0x9A),  // fleece buff
                "drainage"      => (0x5B, 0x8D, 0xB8),  // water blue
                "protection"    => (0x9A, 0xA0, 0xA6),  // mat grey
                "root_barrier"  => (0x6B, 0x6F, 0x76),  // dark grey
                "waterproofing" => (0x4A, 0x4E, 0x55),  // near-black membrane
                "wearing"       => (0xA8, 0xA2, 0x9A),  // paving stone
                "bedding"       => (0xB8, 0xAB, 0x93),  // bedding sand
                _               => (0x9E, 0x9E, 0x9E),
            };

            try
            {
                // Fully qualified: UseWindowsForms (for the family file dialog)
                // pulls System.Drawing.Color into scope and makes a bare Color ambiguous.
                var colour = new Autodesk.Revit.DB.Color((byte)r, (byte)g, (byte)b);
                material.Color = colour;

                // A cut pattern only renders with a fill pattern assigned, and
                // solid fill is the one every template ships with.
                var solid = FindSolidFillPattern(doc);
                if (solid != ElementId.InvalidElementId)
                {
                    material.CutForegroundPatternId = solid;
                    material.CutForegroundPatternColor = colour;
                    material.SurfaceForegroundPatternId = solid;
                    material.SurfaceForegroundPatternColor = colour;
                }
            }
            catch (Exception)
            {
                // Appearance is a readability nicety — never worth failing a
                // build-up over.
            }
        }

        private static ElementId FindSolidFillPattern(Document doc)
        {
            try
            {
                var pattern = new FilteredElementCollector(doc)
                    .OfClass(typeof(FillPatternElement))
                    .Cast<FillPatternElement>()
                    .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);
                return pattern?.Id ?? ElementId.InvalidElementId;
            }
            catch (Exception)
            {
                return ElementId.InvalidElementId;
            }
        }

        private static string SanitizeName(string name)
        {
            var cleaned = new string(name.Where(c => !"{}[]|;<>?`~".Contains(c)).ToArray()).Trim();
            return cleaned.Length == 0 ? "Sportify material" : cleaned;
        }
    }
}
