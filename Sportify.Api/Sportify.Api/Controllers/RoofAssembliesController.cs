using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    /// <summary>
    /// Provider roof build-ups — what a drawn green roof zone is made of, and
    /// what becomes a Revit floor type on import.
    ///
    /// Layers always travel with their system. A build-up without its layers is
    /// a name, and every consumer of this endpoint needs the thicknesses: the
    /// web app to draw the section, Revit to build the floor type, and the
    /// planting rules to know whether a tree's roots fit.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class RoofAssembliesController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public RoofAssembliesController(ReferenceDbContext db) => _db = db;

        [HttpGet]
        public async Task<ActionResult<IEnumerable<RoofAssembly>>> GetAll([FromQuery] string? category = null)
        {
            var query = _db.RoofAssemblies
                .Include(a => a.Layers)
                .AsNoTracking()
                .AsQueryable();

            // Lets the web app ask for only what suits a zone kind, instead of
            // fetching everything and filtering client-side.
            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(a => a.Category == category);

            var list = await query.OrderBy(a => a.Provider).ThenBy(a => a.SystemName).ToListAsync();

            // Ordered here rather than relying on insertion order: the sequence
            // IS the specification, and a build-up drawn bottom-up is wrong.
            foreach (var a in list)
                a.Layers = a.Layers.OrderBy(l => l.LayerOrder).ToList();

            return Ok(list);
        }

        [HttpGet("{key}")]
        public async Task<ActionResult<RoofAssembly>> GetByKey(string key)
        {
            var assembly = await _db.RoofAssemblies
                .Include(a => a.Layers)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Key == key);

            if (assembly == null) return NotFound();
            assembly.Layers = assembly.Layers.OrderBy(l => l.LayerOrder).ToList();
            return Ok(assembly);
        }

        /// <summary>
        /// Creates a build-up with its layers in one call.
        ///
        /// Layers are never edited separately from their system: a build-up
        /// without them is a name, and saving the two halves independently
        /// invites a half-updated system that looks complete.
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<RoofAssembly>> Create([FromBody] RoofAssembly incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming.Key))
                return BadRequest("A build-up needs a key — the stable name exports and imports reference it by.");
            if (await _db.RoofAssemblies.AnyAsync(a => a.Key == incoming.Key))
                return BadRequest($"A build-up with the key \"{incoming.Key}\" already exists.");

            Renumber(incoming);
            _db.RoofAssemblies.Add(incoming);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetByKey), new { key = incoming.Key }, incoming);
        }

        /// <summary>
        /// Replaces a build-up, layers included. The old layers are deleted and
        /// the new set written: reconciling row by row would mean matching
        /// layers that have no stable identity of their own, and a build-up is
        /// small enough that replacing it wholesale is both simpler and safer.
        /// </summary>
        [HttpPut("{id:int}")]
        public async Task<ActionResult<RoofAssembly>> Update(int id, [FromBody] RoofAssembly incoming)
        {
            var existing = await _db.RoofAssemblies
                .Include(a => a.Layers)
                .FirstOrDefaultAsync(a => a.Id == id);
            if (existing == null) return NotFound();

            existing.Key = incoming.Key;
            existing.Provider = incoming.Provider;
            existing.ProviderCountry = incoming.ProviderCountry;
            existing.SystemName = incoming.SystemName;
            existing.Category = incoming.Category;
            existing.Description = incoming.Description;
            existing.BuildUpMm = incoming.BuildUpMm;
            existing.SaturatedKgM2 = incoming.SaturatedKgM2;
            existing.WaterStorageLM2 = incoming.WaterStorageLM2;
            existing.SourceUrl = incoming.SourceUrl;

            _db.RoofAssemblyLayers.RemoveRange(existing.Layers);
            Renumber(incoming);
            existing.Layers = incoming.Layers.Select(l => new RoofAssemblyLayer
            {
                LayerOrder = l.LayerOrder, Name = l.Name, Function = l.Function,
                ThicknessMm = l.ThicknessMm, ThicknessSource = l.ThicknessSource,
            }).ToList();

            await _db.SaveChangesAsync();
            return Ok(existing);
        }

        [HttpDelete("{id:int}")]
        public async Task<ActionResult> Delete(int id)
        {
            var existing = await _db.RoofAssemblies.Include(a => a.Layers).FirstOrDefaultAsync(a => a.Id == id);
            if (existing == null) return NotFound();
            _db.RoofAssemblyLayers.RemoveRange(existing.Layers);
            _db.RoofAssemblies.Remove(existing);
            await _db.SaveChangesAsync();
            return NoContent();
        }

        /// <summary>
        /// Renumbers layers from the order they arrive in, so the client never
        /// has to maintain indices while dragging rows around — the sequence on
        /// screen IS the sequence, which is the only thing that matters since
        /// order is part of the specification.
        /// </summary>
        private static void Renumber(RoofAssembly assembly)
        {
            for (int i = 0; i < assembly.Layers.Count; i++)
                assembly.Layers[i].LayerOrder = i;
        }

    }
}
