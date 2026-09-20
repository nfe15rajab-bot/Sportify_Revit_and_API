namespace Sportify.Api.Data
{
    /// <summary>
    /// Everything the API puts into reference.db, in the order it has to run. One list, because two callers need exactly the same sequence: the API at startup, and
    /// <see cref="ReferenceDbGuard"/>, which builds "what this build would create" to compare an existing file with. A new seeder goes here, not in Program.cs.
    ///
    /// Every step after the first works IN PLACE and is safe to run again: a table or column the file lacks is added to the file that is there (CREATE TABLE IF NOT EXISTS,
    /// ALTER TABLE ADD COLUMN), rows are added by key, prices are only filled where they are missing. So running it on a file that has data in it keeps that data, which is
    /// why the guard runs it on an existing file BEFORE it compares: a file that only lacks what these steps add is brought up to date instead of being rebuilt.
    /// (ReferenceDataSeeder itself only seeds an empty database; what a newer build changed in it is what the guard's comparison is for.)
    /// </summary>
    public static class CatalogSeeding
    {
        public static void Run(ReferenceDbContext db)
        {
            ReferenceDataSeeder.Seed(db);

            // The price columns are added by the backfill's first step, and the seeders below insert rows into tables that have them (the finishes' layers), so on a file made
            // before those columns existed they have to be there first: an INSERT that names a column the file lacks fails, and with it the whole start. On a new file the columns
            // are already there and this only prices the rows seeded above; the second call, below, prices what the seeders in between add.
            PriceSeeder.Backfill(db);

            // Before the price backfill, so the finishes' own layers get priced in the
            // same pass rather than waiting for the next start.
            RoofFinishSeeder.Seed(db);
            // The choices padel and basketball offer, moved out of the web app's
            // JavaScript so a fourth surface is a row rather than a code change.
            SportOptionSeeder.Seed(db);
            // Site furniture from German manufacturers, entered by hand with sources.
            FurnitureSeeder.Seed(db);
            // Runs every start, not just on an empty catalog: the price columns were
            // added after there was already data, and it only fills what is missing.
            PriceSeeder.Backfill(db);
        }
    }
}
