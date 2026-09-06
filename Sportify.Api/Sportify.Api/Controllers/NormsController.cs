using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class NormsController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public NormsController(ReferenceDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<Norm>>> GetAll()
        {
            var norms = await _db.Norms.AsNoTracking().ToListAsync();
            return Ok(norms);
        }
    }
}
