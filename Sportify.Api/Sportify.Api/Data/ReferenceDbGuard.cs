using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Sportify.Api.Data
{
    /// <summary>What to do when reference.db no longer matches this build of the API.</summary>
    public enum DriftPolicy
    {
        /// <summary>Back the old file up (with a list of the rows that are not in the new catalog), then create it afresh from the seed. The default.</summary>
        Rebuild,
        /// <summary>Refuse to start, saying what differs. For anyone who would rather decide by hand.</summary>
        Fail,
        /// <summary>Carry on with the old file as it is (the behaviour before this guard: a request can then fail with "no such column").</summary>
        Ignore,
    }

    public sealed class ReferenceDbReport
    {
        /// <summary>created | ok | adopted | rebuilt | ignored</summary>
        public string Action = "ok";
        public string Reason = "";
        public string? BackupPath;
        /// <summary>A text file listing rows of the old database that are not in the new catalog (added through the Data tab, or changed in the seed), or null when there are none.</summary>
        public string? RowsReportPath;
        public int RowsNotInNewCatalog;
    }

    /// <summary>
    /// The catalog in reference.db is created with EnsureCreated and seeded once, so an existing file never got the tables, columns or seed rows a later build of the API
    /// added: the API then answered "no such column: p.CrownM" or "no such table: RoofAssemblies" until somebody renamed the file by hand. This guard runs at startup and
    /// closes that trap.
    ///
    /// How it knows: it builds the catalog the current code would create in a throw-away in-memory database, and compares (1) the tables and columns, and (2) a digest of the
    /// seed data with the one written into the file when it was seeded (table __SportifyMeta). A file made before this guard has no digest: it is accepted when it has every
    /// table, column and seed row of the new catalog, and otherwise treated as out of date.
    ///
    /// What it does about it (see <see cref="DriftPolicy"/>): the default rebuilds, but never silently and never without a way back: the old file is copied to
    /// reference.&lt;time&gt;.backup.db beside it, and rows that were in it but are not in the new catalog (what the Data tab added) are listed in a text file, so nothing typed
    /// in is lost without a trace. Backups match the *.db rule of .gitignore.
    /// </summary>
    public static class ReferenceDbGuard
    {
        const string MetaTable = "__SportifyMeta";
        const string DigestKey = "seed_digest";

        public static ReferenceDbReport Prepare(string dbPath, ILogger? log = null, DriftPolicy policy = DriftPolicy.Rebuild)
        {
            var report = new ReferenceDbReport();

            // what the current code would create, in memory
            using var fresh = new SqliteConnection("Data Source=:memory:");
            fresh.Open();
            Build(fresh);
            var freshDigest = Digest(fresh);

            if (!File.Exists(dbPath))
            {
                CreateOnDisk(dbPath, freshDigest);
                report.Action = "created";
                report.Reason = "There was no reference.db.";
                log?.LogInformation("reference.db created and seeded.");
                return report;
            }

            var problem = Compare(dbPath, fresh, freshDigest, out var adopt);
            if (problem == null)
            {
                if (adopt)
                {
                    WriteDigest(dbPath, freshDigest);
                    report.Action = "adopted";
                    report.Reason = "reference.db predates the schema guard but has everything the current catalog has; its seed digest is now recorded.";
                    log?.LogInformation("reference.db is up to date (recorded its seed digest).");
                }
                return report;
            }

            report.Reason = problem;
            switch (policy)
            {
                case DriftPolicy.Ignore:
                    report.Action = "ignored";
                    log?.LogWarning("reference.db is out of date ({Problem}); carrying on with it as configured (ReferenceDb:OnDrift = Ignore). Requests that need what is missing will fail.", problem);
                    return report;
                case DriftPolicy.Fail:
                    throw new InvalidOperationException($"reference.db is out of date: {problem}. Rename or delete it to have it created afresh, or set ReferenceDb:OnDrift to Rebuild to have the API back it up and do that itself. File: {dbPath}");
            }

            Rebuild(dbPath, fresh, freshDigest, report);
            log?.LogWarning("reference.db was out of date ({Problem}) and has been rebuilt from the seed. The old file is at {Backup}." +
                            (report.RowsNotInNewCatalog > 0 ? " {Rows} row(s) it held are not in the new catalog: see {Report}." : ""),
                            problem, report.BackupPath, report.RowsNotInNewCatalog, report.RowsReportPath);
            return report;
        }

        // ------------------------------------------------------------------------------------------------------------ building

        /// <summary>The catalog the current code creates: the schema from the model, then the seed.</summary>
        static void Build(SqliteConnection connection)
        {
            var options = new DbContextOptionsBuilder<ReferenceDbContext>().UseSqlite(connection).Options;
            using var db = new ReferenceDbContext(options);
            db.Database.EnsureCreated();
            ReferenceDataSeeder.Seed(db);
        }

        static void CreateOnDisk(string dbPath, string digest)
        {
            var options = new DbContextOptionsBuilder<ReferenceDbContext>().UseSqlite($"Data Source={dbPath};Pooling=False").Options;
            using (var db = new ReferenceDbContext(options))
            {
                db.Database.EnsureCreated();
                ReferenceDataSeeder.Seed(db);
            }
            WriteDigest(dbPath, digest);
        }

        static void Rebuild(string dbPath, SqliteConnection fresh, string freshDigest, ReferenceDbReport report)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
            for (var n = 2; File.Exists(Path.Combine(dir, $"reference.{stamp}.backup.db")); n++)     // two rebuilds in one second must not overwrite each other
                stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + n;
            var backup = Path.Combine(dir, $"reference.{stamp}.backup.db");

            // The database runs in WAL mode: what was written last may still be in reference.db-wal, so fold it into the file before copying it.
            try
            {
                using var old = new SqliteConnection($"Data Source={dbPath};Pooling=False");
                old.Open();
                Exec(old, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch (SqliteException) { /* a file that is not a database at all: nothing to fold in, the copy below still keeps it */ }
            File.Copy(dbPath, backup, overwrite: false);
            report.BackupPath = backup;

            // the rows the new catalog does not have, before the old file goes
            try
            {
                using var old = new SqliteConnection($"Data Source={backup};Mode=ReadOnly;Pooling=False");
                old.Open();
                var lines = RowsNotIn(old, fresh);
                report.RowsNotInNewCatalog = lines.Count;
                if (lines.Count > 0)
                {
                    report.RowsReportPath = Path.Combine(dir, $"reference.{stamp}.backup.rows-not-in-new-catalog.txt");
                    File.WriteAllLines(report.RowsReportPath, new[]
                    {
                        "Rows that were in reference.db before it was rebuilt and are not in the new catalog (rows added through the Data tab, or seed rows the code has changed).",
                        $"The whole old file is {Path.GetFileName(backup)}. Surrogate keys (Id, ...Id columns) are left out of the comparison and of these lines.",
                        "",
                    }.Concat(lines));
                }
            }
            catch (SqliteException) { /* not readable as a database: the backup is all there is to say */ }

            SqliteConnection.ClearAllPools();
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm" })
                    if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
            }
            catch (IOException ex)
            {
                // Nothing was replaced (the file is held open), so the copy just made would only pile up on every retry: take it back.
                if (File.Exists(dbPath))
                {
                    try { File.Delete(backup); if (report.RowsReportPath != null) File.Delete(report.RowsReportPath); } catch (IOException) { /* leave them */ }
                }
                throw new InvalidOperationException($"reference.db is out of date ({report.Reason}) and could not be replaced because it is in use: is another Sportify API running? {ex.Message}");
            }

            CreateOnDisk(dbPath, freshDigest);
            report.Action = "rebuilt";
        }

        // ------------------------------------------------------------------------------------------------------------ comparing

        /// <summary>null when the file is up to date; otherwise what is wrong. <paramref name="adopt"/> is set for an older file that is fine but has no digest yet.</summary>
        static string? Compare(string dbPath, SqliteConnection fresh, string freshDigest, out bool adopt)
        {
            adopt = false;
            using var disk = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            try { disk.Open(); Query(disk, "SELECT count(*) FROM sqlite_master"); }
            catch (SqliteException ex) { return "the file cannot be read as a database (" + ex.Message + ")"; }

            var freshSchema = Schema(fresh);
            var diskSchema = Schema(disk);
            var missing = new List<string>();
            foreach (var (table, columns) in freshSchema)
            {
                if (!diskSchema.TryGetValue(table, out var have)) { missing.Add("table " + table); continue; }
                foreach (var c in columns.Except(have, StringComparer.OrdinalIgnoreCase)) missing.Add($"column {table}.{c}");
            }
            if (missing.Count > 0) return "it lacks " + string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? $" and {missing.Count - 6} more" : "");

            var stored = ReadDigest(disk);
            if (stored == freshDigest) return null;
            if (stored != null) return "the seed data of this build differs from the one this file was seeded with";

            // made before the guard: fine when every row of the new catalog is in it
            var absent = RowsNotIn(fresh, disk, limit: 1);
            if (absent.Count == 0) { adopt = true; return null; }
            return "it lacks seed rows the current catalog has (" + absent[0] + ")";
        }

        static Dictionary<string, List<string>> Schema(SqliteConnection c)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in Tables(c))
                result[table] = Query(c, $"PRAGMA table_info(\"{table}\")").Select(r => (string)r["name"]!).ToList();
            return result;
        }

        static List<string> Tables(SqliteConnection c)
        {
            return Query(c, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name")
                .Select(r => (string)r["name"]!).Where(n => n != MetaTable).ToList();
        }

        /// <summary>A digest of every row of every table, so two catalogs made from the same seed have the same one.</summary>
        static string Digest(SqliteConnection c)
        {
            using var sha = SHA256.Create();
            var text = new StringBuilder();
            foreach (var table in Tables(c))
            {
                text.Append('#').Append(table).Append('\n');
                var rows = Query(c, $"SELECT * FROM \"{table}\"").Select(r => string.Join('', r.Values.Select(Cell))).ToList();
                rows.Sort(StringComparer.Ordinal);
                foreach (var row in rows) text.Append(row).Append('\n');
            }
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }

        /// <summary>
        /// Rows of <paramref name="from"/> whose visible content (every column but the surrogate keys) is in no row of <paramref name="other"/>, as one line each.
        /// Surrogate keys are left out because a rebuilt catalog may number its rows differently.
        /// </summary>
        static List<string> RowsNotIn(SqliteConnection from, SqliteConnection other, int limit = 500)
        {
            var lines = new List<string>();
            var otherSchema = Schema(other);
            foreach (var table in Tables(from))
            {
                if (!otherSchema.TryGetValue(table, out var otherColumns)) continue;
                var fromRows = Query(from, $"SELECT * FROM \"{table}\"");
                if (fromRows.Count == 0) continue;
                var columns = fromRows[0].Keys.Where(k => !IsKey(k) && otherColumns.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                var known = new HashSet<string>(Query(other, $"SELECT * FROM \"{table}\"").Select(r => Fingerprint(r, columns)));
                foreach (var row in fromRows)
                {
                    if (known.Contains(Fingerprint(row, columns))) continue;
                    lines.Add($"{table}: " + string.Join("; ", columns.Select(k => k + "=" + Trim(Cell(row[k]), 70))));
                    if (lines.Count >= limit) return lines;
                }
            }
            return lines;
        }

        static bool IsKey(string column) => column == "Id" || (column.Length > 2 && column.EndsWith("Id", StringComparison.Ordinal));
        static string Fingerprint(Dictionary<string, object?> row, List<string> columns) => string.Join('', columns.Select(k => Cell(row[k])));
        static string Cell(object? v) => v == null || v is DBNull ? "\0" : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        static string Trim(string s, int max) => s.Length <= max ? s.Replace('\0', '-') : s.Substring(0, max - 1).Replace('\0', '-') + "…";

        // ------------------------------------------------------------------------------------------------------------ the digest in the file

        static string? ReadDigest(SqliteConnection c)
        {
            var hasMeta = Query(c, $"SELECT name FROM sqlite_master WHERE type = 'table' AND name = '{MetaTable}'").Count > 0;
            if (!hasMeta) return null;
            var rows = Query(c, $"SELECT value FROM {MetaTable} WHERE key = '{DigestKey}'");
            return rows.Count == 0 ? null : (string?)rows[0]["value"];
        }

        static void WriteDigest(string dbPath, string digest)
        {
            using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            c.Open();
            Exec(c, $"CREATE TABLE IF NOT EXISTS {MetaTable} (key TEXT NOT NULL PRIMARY KEY, value TEXT NOT NULL)");
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"INSERT OR REPLACE INTO {MetaTable} (key, value) VALUES ('{DigestKey}', $v)";
            cmd.Parameters.AddWithValue("$v", digest);
            cmd.ExecuteNonQuery();
        }

        // ------------------------------------------------------------------------------------------------------------ SQLite plumbing

        static void Exec(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        static List<Dictionary<string, object?>> Query(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
    }
}
