using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    /// <summary>
    /// Site furniture — benches, tables, bins, bollards and bollard lights.
    /// A catalog of real products, maintainable without a developer.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class FurnitureController : ControllerBase
    {
        private readonly ReferenceDbContext _db;
        public FurnitureController(ReferenceDbContext db) => _db = db;

        [HttpGet]
        public async Task<ActionResult<IEnumerable<FurnitureItem>>> GetAll([FromQuery] string? category = null)
        {
            var q = _db.FurnitureItems.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(category)) q = q.Where(f => f.Category == category);
            // Grouped by what a thing IS, then by who makes it — the order a
            // picker reads best in, and the same one for every consumer.
            return Ok(await q.OrderBy(f => f.Category).ThenBy(f => f.Manufacturer)
                             .ThenBy(f => f.ProductName).ToListAsync());
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<FurnitureItem>> GetById(int id)
        {
            var item = await _db.FurnitureItems.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
            return item == null ? NotFound() : Ok(item);
        }

        [HttpPost]
        public async Task<ActionResult<FurnitureItem>> Create([FromBody] FurnitureItem incoming)
        {
            if (string.IsNullOrWhiteSpace(incoming.Key))
                return BadRequest("Furniture needs a key — the stable name exports and Revit families reference it by.");
            if (await _db.FurnitureItems.AnyAsync(f => f.Key == incoming.Key))
                return BadRequest($"Furniture with the key \"{incoming.Key}\" already exists.");

            _db.FurnitureItems.Add(incoming);
            await _db.SaveChangesAsync();
            return CreatedAtAction(nameof(GetById), new { id = incoming.Id }, incoming);
        }

        [HttpPut("{id:int}")]
        public async Task<ActionResult<FurnitureItem>> Update(int id, [FromBody] FurnitureItem incoming)
        {
            var e = await _db.FurnitureItems.FirstOrDefaultAsync(f => f.Id == id);
            if (e == null) return NotFound();

            e.Key = incoming.Key; e.Manufacturer = incoming.Manufacturer;
            e.ManufacturerCountry = incoming.ManufacturerCountry;
            e.ProductName = incoming.ProductName; e.Category = incoming.Category;
            e.Description = incoming.Description; e.Material = incoming.Material;
            e.LengthM = incoming.LengthM; e.WidthM = incoming.WidthM; e.HeightM = incoming.HeightM;
            e.DimensionsPublished = incoming.DimensionsPublished;
            e.Seats = incoming.Seats;
            e.WeightKg = incoming.WeightKg; e.WeightPublished = incoming.WeightPublished;
            e.CapacityLitres = incoming.CapacityLitres;
            e.PriceValue = incoming.PriceValue; e.PriceUnit = incoming.PriceUnit;
            e.PriceSource = incoming.PriceSource; e.PriceIsQuoted = incoming.PriceIsQuoted;
            e.CostGroupDin276 = incoming.CostGroupDin276; e.SourceUrl = incoming.SourceUrl;

            await _db.SaveChangesAsync();
            return Ok(e);
        }

        [HttpDelete("{id:int}")]
        public async Task<ActionResult> Delete(int id)
        {
            var e = await _db.FurnitureItems.FirstOrDefaultAsync(f => f.Id == id);
            if (e == null) return NotFound();
            _db.FurnitureItems.Remove(e);
            await _db.SaveChangesAsync();
            return NoContent();
        }
    }
}
