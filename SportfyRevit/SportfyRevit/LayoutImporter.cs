using Autodesk.Revit.DB;

namespace SportfyRevit
{
    internal enum ImportSource { Manual, Auto }

    internal sealed class ImportOutcome
    {
        public bool Succeeded { get; set; }
        /// <summary>The person chose not to import (the worksharing question).</summary>
        public bool Cancelled { get; set; }
        public string? Error { get; set; }
        public ImportSummary? Summary { get; set; }
        /// <summary>Elements of the previous import of this project that this one replaced.</summary>
        public int Replaced { get; set; }
        /// <summary>Stale, untracked leftovers (from before RoofId tracking, or a ledger entry that never landed) this import also found and removed as duplicates.</summary>
        public int RemovedDuplicates { get; set; }
        /// <summary>Elements of another roof's own previous import, removed because the person asked to when switching roofs (ImportSportifyLayoutCommand's prompt).</summary>
        public int RemovedOtherRoofs { get; set; }
        /// <summary>Elements of an earlier "Import Iterations as Design Options" run that this one also cleared (0 unless asked to).</summary>
        public int ReplacedIterations { get; set; }
        /// <summary>ImportDiagnostics.Report(): which family every piece got, or why it became a box.</summary>
        public string Report { get; set; } = "";
    }

    /// <summary>
    /// One import, the same for the manual command and for Auto Import.
    ///
    /// The order matters and is the point: everything that has to happen OUTSIDE a transaction happens first (the worksharing question, and the
    /// families: FamilyPreparation builds and loads each distinct family in a small transaction of its own, so one that cannot be built is a line in
    /// the report and cannot abort the import), then ONE transaction that replaces what the previous import of this project created (ImportLedger),
    /// builds the layout from the prepared families, and records what it created. A failure anywhere rolls that transaction back, so the previous
    /// import is still there. The report goes to the log whatever happens; the manual command also shows it.
    /// </summary>
    internal static class LayoutImporter
    {
        /// <summary>`clearIterations`: also remove what an earlier "Import Iterations as Design Options" run built (IterationLedger) — that command's own worksets/elements are
        /// otherwise untouched by a normal import, so left in place they sit alongside it, sports and gardens overlapping. Manual asks each time there is something to clear;
        /// Auto Import asks once when it is turned on and remembers the answer for as long as it stays on (ToggleAutoImportCommand, AutoImportSync.ClearIterationsToo).
        /// `roofId`: which roof this layout is for (RoofBoundaryServer's key, or ImportLedger.UnknownRoofId) — only THIS roof's previous import is replaced; a project with
        /// several roofs pushed and imported independently never has importing one delete what an earlier import of another built.
        /// `clearOtherRoofs`: other roofs' own previous imports to also remove, when the person switching to this one asked to (ImportSportifyLayoutCommand); null or empty
        /// for the ordinary case — those roofs' content is left exactly as it was, which is what makes several roofs "switchable" rather than one replacing another.</summary>
        /// <summary>The layout as the web app sent it, with the roof height the import built at (RoofIdentity.FromLayout may have corrected a height that named no roof).</summary>
        static string KeptJson(SportifyLayout layout, string? rawJson)
        {
            if (string.IsNullOrEmpty(rawJson)) return System.Text.Json.JsonSerializer.Serialize(layout);
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(rawJson) is System.Text.Json.Nodes.JsonObject root && root["roof_context"] is System.Text.Json.Nodes.JsonObject rc && layout.RoofContext != null)
                {
                    rc["world_origin_z_m"] = layout.RoofContext.WorldOriginZM;
                    return root.ToJsonString();
                }
            }
            catch (Exception) { /* kept as sent */ }
            return rawJson;
        }

        public static ImportOutcome Run(Document doc, SportifyLayout layout, ImportSource source, bool clearIterations, string roofId, IReadOnlyList<string>? clearOtherRoofs = null, string? rawJson = null)
        {
            var outcome = new ImportOutcome();
            var sourceName = source.ToString().ToLowerInvariant();
            bool interactive = source == ImportSource.Manual;
            var clock = System.Diagnostics.Stopwatch.StartNew();

            SportifyLog.Info("import", $"{sourceName} import into \"{doc.Title}\": {layout.Placements?.Count ?? 0} placement(s), {layout.Zones?.Count ?? 0} zone(s), " +
                                       $"{layout.Assemblies?.Count ?? 0} build-up(s), roof finish {(layout.RoofFinish?.AssemblyKey ?? "none")}");
            ImportDiagnostics.Begin();

            try
            {
                // A design option being edited would catch the whole layout (what the API creates lands in the option being edited), and this roof's
                // main-model content could not be cleared from inside an option: a sync or Auto Import waits for the Main Model. An iteration goes
                // into an option through Import Iterations as Design Options.
                if (DesignOption.GetActiveDesignOptionId(doc) != ElementId.InvalidElementId)
                {
                    outcome.Error = "a design option is being edited: switch the Design Options toolbar back to Main Model and sync again " +
                                    "(to put an iteration into this option, use Import Iterations as Design Options)";
                    SportifyLog.Warn("import", "not imported: " + outcome.Error);
                    return Finish(outcome, sourceName, clock);
                }

                var choice = WorksharingConsent.Decide(doc, interactive: true);
                if (choice == WorksharingChoice.Cancel)
                {
                    outcome.Cancelled = true;
                    SportifyLog.Info("import", "cancelled at the worksharing question");
                    return outcome;
                }
                if (choice == WorksharingChoice.Enable)
                {
                    // Revit's own names for the two worksets it makes. (The old call named the workset for grids and levels "Sports".)
                    doc.EnableWorksharing("Shared Levels and Grids", "Workset1");
                    SportifyLog.Info("import", "worksharing enabled with the person's consent");
                }

                // The roof, as one of the building's own: the pushed one when it is still a live building roof, else the one the layout's own
                // origin stands on (an import with no roof pushed this session used to be recorded as roof "0", a push that took one of Sportify's
                // own floors for the roof as that floor's id — and neither was ever replaced by the next import of the same roof).
                var byOutline = false;
                var roof = RoofIdentity.RoofOfKey(doc, roofId) ?? RoofIdentity.FromLayout(doc, layout, out byOutline);
                if (roof != null && roof.Id.Value.ToString() != roofId)
                {
                    SportifyLog.Info("import", $"roof \"{roofId}\" is not a building roof: the layout stands on {roof.Name} (id {roof.Id.Value}), recorded under that");
                    roofId = roof.Id.Value.ToString();
                }
                // Its height named no roof, its outline did (RoofIdentity.FromLayout): built on that roof, at its height, and whatever an earlier sync of
                // it built at the stated height (recorded under a key that names no roof) goes with this roof's earlier imports.
                double? strayZFt = null;
                if (byOutline && roof != null && layout.RoofContext != null)
                {
                    PushRoofCommandBase.FindTopFace(roof, out var topFt);
                    var topM = Math.Round(UnitUtils.ConvertFromInternalUnits(topFt ?? roof.get_BoundingBox(null).Max.Z, UnitTypeId.Meters), 3);
                    strayZFt = SportifyLayoutBuilder.FeetFromMeters(layout.RoofContext.WorldOriginZM);
                    SportifyLog.Warn("import", $"the layout puts its roof at {layout.RoofContext.WorldOriginZM:0.###} m, where no roof is; its outline is {roof.Name} (id {roof.Id.Value}): built on it, at {topM:0.###} m");
                    layout.RoofContext.WorldOriginZM = topM;
                }

                var prepared = FamilyPreparation.Prepare(doc, layout, allowTemplateDialog: interactive);

                using var transaction = new Transaction(doc, "Import Sportify layout");
                transaction.Start();
                var warnings = ImportWarnings.On(transaction);     // an automatic import has nobody to click OK on a warning (live test 2026-09-30)
                try
                {
                    // Everything earlier imports left on this roof goes, whatever id they were recorded under; other roofs, design options and
                    // the iterations import keep theirs (ImportLedger.RemoveOnRoof).
                    outcome.Replaced = ImportLedger.RemoveOnRoof(doc, roofId, roof, leftovers: true, strayAtZFt: strayZFt);
                    if (clearOtherRoofs != null) foreach (var other in clearOtherRoofs) outcome.RemovedOtherRoofs += ImportLedger.RemovePrevious(doc, other);
                    if (clearIterations) outcome.ReplacedIterations = IterationLedger.RemovePrevious(doc);
                    outcome.Summary = SportifyLayoutBuilder.BuildGeometry(doc, layout, prepared, useWorksets: choice != WorksharingChoice.NoWorksets);
                    foreach (var id in outcome.Summary.CreatedIds.Distinct())
                        if (doc.GetElement(id) is Element made) SportifySharedParameters.SetRoofId(made, roofId);
                    ImportLedger.Write(doc, outcome.Summary.CreatedIds, sourceName, roofId);
                    // the layout itself, in the project, for the analyses when the web app has sent nothing this Revit session (ImportedLayoutStore)
                    try { ImportedLayoutStore.Write(doc, roofId, KeptJson(layout, rawJson)); }
                    catch (Exception ex) { SportifyLog.Warn("import", "the layout could not be kept in the project for the analyses: " + ex.Message); }
                    outcome.RemovedDuplicates = DuplicateCleanup.RemoveForRoof(doc, roofId, roof, justBuilt: outcome.Summary.CreatedIds);
                }
                catch (Exception ex)
                {
                    try { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); } catch (Exception) { /* already rolled back */ }
                    outcome.Error = "the geometry could not be built: " + ex.Message;
                    SportifyLog.Error("import", "building the geometry failed; the project is as it was before this import", ex);
                    return Finish(outcome, sourceName, clock);
                }

                var status = transaction.Commit();
                warnings.Log("import", "layout import");
                if (status != TransactionStatus.Committed)
                {
                    outcome.Summary = null;
                    outcome.Error = "Revit rolled the import back when it was committed (" + status + "): see Revit's own warning dialog, if it showed one";
                    SportifyLog.Error("import", outcome.Error);
                    return Finish(outcome, sourceName, clock);
                }

                outcome.Succeeded = true;
                return Finish(outcome, sourceName, clock);
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                SportifyLog.Error("import", "the import stopped before it could build anything", ex);
                return Finish(outcome, sourceName, clock);
            }
        }

        private static ImportOutcome Finish(ImportOutcome outcome, string sourceName, System.Diagnostics.Stopwatch clock)
        {
            outcome.Report = ImportDiagnostics.Report();
            SportifyLog.Block("import",
                $"{sourceName} import {(outcome.Succeeded ? "finished" : outcome.Cancelled ? "cancelled" : "FAILED: " + outcome.Error)} in {clock.ElapsedMilliseconds} ms; " +
                $"replaced {outcome.Replaced} element(s) of the previous import (Revit counts the sketches and lines that depend on what was tagged)" +
                (outcome.ReplacedIterations > 0 ? $" and {outcome.ReplacedIterations} element(s) of the previously imported iterations" : "") +
                (outcome.RemovedDuplicates > 0 ? $"; removed {outcome.RemovedDuplicates} stale duplicate(s)" : "") +
                (outcome.RemovedOtherRoofs > 0 ? $"; removed {outcome.RemovedOtherRoofs} element(s) of another roof's import, by request" : "") + "; report:",
                outcome.Report);
            return outcome;
        }
    }
}
