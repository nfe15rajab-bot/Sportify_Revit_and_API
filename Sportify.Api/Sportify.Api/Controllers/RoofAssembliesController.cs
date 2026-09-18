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
    }
}
