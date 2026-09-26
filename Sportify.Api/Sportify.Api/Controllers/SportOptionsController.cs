using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    /// <summary>
    /// The choices each sport offers. A padel wall system, a court surface, a
    /// basket mounting — what the configurator shows, and what a firm can add
    /// to without anyone changing code.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class SportOptionsController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public SportOptionsController(ReferenceDbContext db) => _db = db;

        [HttpGet]
        public async Task<ActionResult<IEnumerable<SportOption>>> GetAll(
            [FromQuery] string? sport = null, [FromQuery] string? group = null)
        {
            var query = _db.SportOptions.AsNoTracking().AsQueryable();

            // The configurator asks for one sport's options; the Catalogue tab asks
            // for all of them.
            if (!string.IsNullOrWhiteSpace(sport))
                query = query.Where(o => o.Sport == sport);
            if (!string.IsNullOrWhiteSpace(group))
                query = query.Where(o => o.OptionGroup == group);

            // Sorted here rather than client-side: the order a picker lists its
            // options in is part of how it reads, and every consumer wants the
            // same one.
            return Ok(await query
                .OrderBy(o => o.Sport).ThenBy(o => o.OptionGroup)
                .ThenBy(o => o.SortOrder).ThenBy(o => o.Label)
                .ToListAsync());
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<SportOption>> GetById(int id)
        {
            var option = await _db.SportOptions.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id);
            return option == null ? NotFound() : Ok(option);
        }

        [HttpPost]
        public async Task<ActionResult<SportOption>> Create([FromBody] SportOption incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming.Sport) || string.IsNullOrWhiteSpace(incoming.OptionGroup)
                || string.IsNullOrWhiteSpace(incoming.Key))
                return BadRequest("A sport option needs a sport, an option group and a key — the three together are what the configurator looks it up by.");

            if (await _db.SportOptions.AnyAsync(o =>
                    o.Sport == incoming.Sport && o.OptionGroup == incoming.OptionGroup && o.Key == incoming.Key))
                return BadRequest($"\"{incoming.Key}\" already exists in {incoming.Sport}/{incoming.OptionGroup}.");

            _db.SportOptions.Add(incoming);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = incoming.Id }, incoming);
        }

        [HttpPut("{id:int}")]
        public async Task<ActionResult<SportOption>> Update(int id, [FromBody] SportOption incoming)
        {
            var existing = await _db.SportOptions.FirstOrDefaultAsync(o => o.Id == id);
            if (existing == null) return NotFound();

            existing.Sport = incoming.Sport;
            existing.OptionGroup = incoming.OptionGroup;
            existing.Key = incoming.Key;
            existing.Label = incoming.Label;
            existing.Note = incoming.Note;
            existing.SortOrder = incoming.SortOrder;
            existing.WeightKgM2 = incoming.WeightKgM2;
            existing.WeightKgEach = incoming.WeightKgEach;
            existing.ThicknessMm = incoming.ThicknessMm;
            existing.ColourHex = incoming.ColourHex;
            existing.TextureHint = incoming.TextureHint;
            existing.PriceValue = incoming.PriceValue;
            existing.PriceUnit = incoming.PriceUnit;
            existing.PriceSource = incoming.PriceSource;
            existing.PriceIsQuoted = incoming.PriceIsQuoted;
            existing.CostGroupDin276 = incoming.CostGroupDin276;

            await _db.SaveChangesAsync();
            return Ok(existing);
        }

        [HttpDelete("{id:int}")]
        public async Task<ActionResult> Delete(int id)
        {
            var existing = await _db.SportOptions.FirstOrDefaultAsync(o => o.Id == id);
            if (existing == null) return NotFound();
            _db.SportOptions.Remove(existing);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
