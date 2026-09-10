using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    /// <summary>
    /// Write access to the reference catalog — lets the Data tab grow the
    /// database itself (manual entry or a .sql import) instead of only
    /// ever reading what ReferenceDataSeeder shipped with. Deliberately
    /// unauthenticated, matching every other reference-catalog endpoint in
    /// this project (AuthController's JWT login was never wired up to this
    /// DbContext at all — see the project notes); this is a local school-
    /// project dev database, not a multi-tenant service.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

        // Only entity types with no nested navigation collections in their
        // own JSON shape are accepted here (Material/Provider/Norm/Plant/
        // PlantPalette/FacilityGuideline/Sport are all "leaf" scalars from
        // the form's point of view; FieldVariant's SportId is a plain
        // foreign-key column, not a collection). A generic `_db.Add(entity)`
        // marks every reachable navigation on the deserialized graph as
        // "Added" — safe here only because the form never sends nested
        // Norms/Materials/Providers arrays, so those stay at their default
        // empty list rather than becoming accidental duplicate inserts.
        private static readonly HashSet<string> AllowedEntityTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "Sport", "Material", "Provider", "Norm", "Plant", "PlantPalette", "FacilityGuideline", "FieldVariant", "AnalysisParameter"
        };

        public AdminController(ReferenceDbContext db)
        {
            _db = db;
        }

        public record CreateRecordRequest(string EntityType, JsonElement Data);

        [HttpPost("records")]
        public async Task<ActionResult> CreateRecord([FromBody] CreateRecordRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.EntityType) || !AllowedEntityTypes.Contains(req.EntityType))
                return BadRequest($"Unknown or unsupported entity type '{req.EntityType}'. Allowed: {string.Join(", ", AllowedEntityTypes)}.");

            try
            {
                var raw = req.Data.GetRawText();
                object entity = req.EntityType switch
                {
                    "Sport" => JsonSerializer.Deserialize<Sport>(raw, JsonOpts)!,
                    "Material" => JsonSerializer.Deserialize<Material>(raw, JsonOpts)!,
                    "Provider" => JsonSerializer.Deserialize<Provider>(raw, JsonOpts)!,
                    "Norm" => JsonSerializer.Deserialize<Norm>(raw, JsonOpts)!,
                    "Plant" => JsonSerializer.Deserialize<Plant>(raw, JsonOpts)!,
                    "PlantPalette" => JsonSerializer.Deserialize<PlantPalette>(raw, JsonOpts)!,
                    "FacilityGuideline" => JsonSerializer.Deserialize<FacilityGuideline>(raw, JsonOpts)!,
                    "FieldVariant" => JsonSerializer.Deserialize<FieldVariant>(raw, JsonOpts)!,
                    // Was already in AllowedEntityTypes above but had no case
                    // here — CreateRecord fell through to "unreachable" for
                    // every AnalysisParameter create attempt.
                    "AnalysisParameter" => JsonSerializer.Deserialize<AnalysisParameter>(raw, JsonOpts)!,
                    _ => throw new InvalidOperationException("unreachable"),
                };

                _db.Add(entity);
                await _db.SaveChangesAsync();
                return Ok(entity);
            }
            catch (Exception ex)
            {
                return BadRequest($"Could not create {req.EntityType}: {ex.Message}");
            }
        }

        /// <summary>
        /// Partial update of one existing row — the "fill in missing data"
        /// counterpart to CreateRecord: only the fields actually present in
        /// the request body are touched (via reflection over the JSON
        /// property names present, not a full-object deserialize+overwrite,
        /// which would otherwise blank out every field the caller didn't
        /// send). List/navigation-typed properties are skipped defensively,
        /// same safety principle as CreateRecord never sending them.
        /// </summary>
        [HttpPatch("records/{entityType}/{id:int}")]
        public async Task<ActionResult> UpdateRecord(string entityType, int id, [FromBody] JsonElement data)
        {
            if (string.IsNullOrWhiteSpace(entityType) || !AllowedEntityTypes.Contains(entityType))
                return BadRequest($"Unknown or unsupported entity type '{entityType}'. Allowed: {string.Join(", ", AllowedEntityTypes)}.");

            object? existing = entityType switch
            {
                "Sport" => await _db.Sports.FindAsync(id),
                "Material" => await _db.Materials.FindAsync(id),
                "Provider" => await _db.Providers.FindAsync(id),
                "Norm" => await _db.Norms.FindAsync(id),
                "Plant" => await _db.Plants.FindAsync(id),
                "PlantPalette" => await _db.PlantPalettes.FindAsync(id),
                "FacilityGuideline" => await _db.FacilityGuidelines.FindAsync(id),
                "FieldVariant" => await _db.FieldVariants.FindAsync(id),
                "AnalysisParameter" => await _db.AnalysisParameters.FindAsync(id),
                _ => null,
            };
            if (existing is null) return NotFound($"No {entityType} with id {id}.");

            try
            {
                var clrType = existing.GetType();
                foreach (var jsonProp in data.EnumerateObject())
                {
                    var clrProp = clrType.GetProperties().FirstOrDefault(p =>
                        string.Equals(p.Name, jsonProp.Name, StringComparison.OrdinalIgnoreCase) && p.CanWrite);
                    if (clrProp is null) continue;
                    var isCollection = typeof(System.Collections.IEnumerable).IsAssignableFrom(clrProp.PropertyType) && clrProp.PropertyType != typeof(string);
                    if (isCollection) continue; // never let a PATCH touch a Norms/Materials/Providers/Plants list

                    var value = jsonProp.Value.ValueKind == JsonValueKind.Null
                        ? null
                        : JsonSerializer.Deserialize(jsonProp.Value.GetRawText(), clrProp.PropertyType, JsonOpts);
                    clrProp.SetValue(existing, value);
                }
                await _db.SaveChangesAsync();
                return Ok(existing);
            }
            catch (Exception ex)
            {
                return BadRequest($"Could not update {entityType} #{id}: {ex.Message}");
            }
        }

        public record ImportSqlRequest(string Sql);

        /// <summary>
        /// Executes a raw .sql script (as uploaded/pasted text) against the
        /// reference SQLite database inside one transaction — an all-or-
        /// nothing bulk import, rolled back on the first failing statement.
        /// This runs whatever SQL it's given (Microsoft.Data.Sqlite executes
        /// semicolon-separated statements in one call) — appropriate for a
        /// single-user local dev database the caller already controls, not
        /// something to expose beyond that.
        /// </summary>
        [HttpPost("import-sql")]
        public async Task<ActionResult> ImportSql([FromBody] ImportSqlRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Sql))
                return BadRequest("Empty SQL script.");

            await using var transaction = await _db.Database.BeginTransactionAsync();
            try
            {
                var rowsAffected = await _db.Database.ExecuteSqlRawAsync(req.Sql);
                await transaction.CommitAsync();
                return Ok(new { rowsAffected });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest($"Import failed, nothing was committed: {ex.Message}");
            }
        }
    }
}
