using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Sportify.Api.Models;

namespace Sportify.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AnalysisParametersController : ControllerBase
    {
        private readonly ReferenceDbContext _db;

        public AnalysisParametersController(ReferenceDbContext db)
        {
            _db = db;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<AnalysisParameter>>> GetAll()
        {
            var parameters = await _db.AnalysisParameters.AsNoTracking().ToListAsync();
            return Ok(parameters);
        }
    }
}
