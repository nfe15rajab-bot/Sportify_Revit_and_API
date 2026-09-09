using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PlantsController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public PlantsController(ReferenceDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Plant>>> GetAll()
        {
            var plants = await _db.Plants.AsNoTracking().ToListAsync();
            return Ok(plants);
        }

        [HttpGet("palettes")]
        public async Task<ActionResult<IEnumerable<PlantPalette>>> GetPalettes()
        {
            var palettes = await _db.PlantPalettes
                .Include(p => p.Plants)
                .Include(p => p.Norms)
                .Include(p => p.Materials)
                .Include(p => p.Providers)
                .AsNoTracking()
                .ToListAsync();
            return Ok(palettes);
        }

        [HttpGet("palettes/{id:int}")]
        public async Task<ActionResult<PlantPalette>> GetPaletteById(int id)
        {
            var palette = await _db.PlantPalettes
                .Include(p => p.Plants)
                .Include(p => p.Norms)
                .Include(p => p.Materials)
                .Include(p => p.Providers)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);
            return palette is null ? NotFound() : Ok(palette);
        }
    }
}
