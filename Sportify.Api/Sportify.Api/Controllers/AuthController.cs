using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Sportify.Api.Data;
using Sportify.Api.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Sportify.Api.Controllers
{
    /// <summary>
    /// Accounts. Off unless <c>Jwt:Key</c> is a real key (ApiSecurity.JwtConfigured) AND the accounts database has a connection string: nothing in the app uses accounts
    /// yet, and this used to answer with a signing key that was in the repository.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IServiceProvider _services;
        private readonly ApiSecurity _security;
        private readonly IConfiguration _config;

        public AuthController(IServiceProvider services, ApiSecurity security, IConfiguration config)
        {
            _services = services;
            _security = security;
            _config = config;
        }

        private IActionResult? NotAvailable()
        {
            if (!_security.JwtConfigured)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Accounts are not set up on this API (no Jwt:Key)." });
            if (string.IsNullOrWhiteSpace(_config.GetConnectionString("DefaultConnection")))
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "Accounts are not set up on this API (no DefaultConnection)." });
            return null;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register(RegisterRequest request)
        {
            if (NotAvailable() is { } off) return off;
            var db = _services.GetRequiredService<AppDbContext>();

            if (await db.Users.AnyAsync(u => u.Email == request.Email))
                return BadRequest("A user with this email already exists.");

            var user = new User { Email = request.Email };
            user.PasswordHash = PasswordHasher.Hash(user, request.Password);

            db.Users.Add(user);
            await db.SaveChangesAsync();

            return Ok(new { message = "User registered successfully.", userId = user.Id });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login(LoginRequest request)
        {
            if (NotAvailable() is { } off) return off;
            var db = _services.GetRequiredService<AppDbContext>();

            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
            if (user == null)
                return Unauthorized("Invalid email or password.");

            bool validPassword = PasswordHasher.Verify(user, user.PasswordHash, request.Password);
            if (!validPassword)
                return Unauthorized("Invalid email or password.");

            string token = GenerateJwtToken(user);
            return Ok(new { token });
        }

        private string GenerateJwtToken(User user)
        {
            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_security.JwtKey!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
