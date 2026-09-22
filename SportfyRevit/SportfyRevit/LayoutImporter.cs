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
        /// Auto Import asks once when it is turned on and remembers the answer for as long as it stays on (ToggleAutoImportCommand, AutoImportSync.ClearIterationsToo).</summary>
        public static ImportOutcome Run(Document doc, SportifyLayout layout, ImportSource source, bool clearIterations)
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

                var prepared = FamilyPreparation.Prepare(doc, layout, allowTemplateDialog: interactive);

                using var transaction = new Transaction(doc, "Import Sportify layout");
                transaction.Start();
                try
                {
                    outcome.Replaced = ImportLedger.RemovePrevious(doc);
                    if (clearIterations) outcome.ReplacedIterations = IterationLedger.RemovePrevious(doc);
                    outcome.Summary = SportifyLayoutBuilder.BuildGeometry(doc, layout, prepared, useWorksets: choice != WorksharingChoice.NoWorksets);
                    ImportLedger.Write(doc, outcome.Summary.CreatedIds, sourceName);
                }
                catch (Exception ex)
                {
                    try { if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack(); } catch (Exception) { /* already rolled back */ }
                    outcome.Error = "the geometry could not be built: " + ex.Message;
                    SportifyLog.Error("import", "building the geometry failed; the project is as it was before this import", ex);
                    return Finish(outcome, sourceName, clock);
                }

                var status = transaction.Commit();
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
                (outcome.ReplacedIterations > 0 ? $" and {outcome.ReplacedIterations} element(s) of the previously imported iterations" : "") + "; report:",
                outcome.Report);
            return outcome;
        }
    }
}
