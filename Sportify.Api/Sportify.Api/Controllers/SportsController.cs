using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SportsController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public SportsController(ReferenceDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Sport>>> GetAll()
        {
            var sports = await _db.Sports
                .Include(s => s.Norms)
                .Include(s => s.Materials)
                .Include(s => s.Providers)
                .Include(s => s.Variants)
                .AsNoTracking()
                .ToListAsync();
            return Ok(sports);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<Sport>> GetById(int id)
        {
            var sport = await _db.Sports
                .Include(s => s.Norms)
                .Include(s => s.Materials)
                .Include(s => s.Providers)
                .Include(s => s.Variants)
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == id);
            return sport is null ? NotFound() : Ok(sport);
        }

        [HttpGet("materials")]
        public async Task<ActionResult<IEnumerable<Material>>> GetMaterials()
        {
            var materials = await _db.Materials.AsNoTracking().ToListAsync();
            return Ok(materials);
        }

        [HttpGet("providers")]
        public async Task<ActionResult<IEnumerable<Provider>>> GetProviders()
        {
            var providers = await _db.Providers.AsNoTracking().ToListAsync();
            return Ok(providers);
        }
    }
}
