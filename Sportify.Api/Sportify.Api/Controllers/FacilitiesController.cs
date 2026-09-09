using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FacilitiesController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public FacilitiesController(ReferenceDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<FacilityGuideline>>> GetAll()
        {
            var guidelines = await _db.FacilityGuidelines.AsNoTracking().ToListAsync();
            return Ok(guidelines);
        }
    }
}
