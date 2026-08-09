using Microsoft.AspNetCore.Identity;
using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    public static class PasswordHasher
    {
        private static readonly PasswordHasher<User> _hasher = new();

        public static string Hash(User user, string plainPassword)
        {
            return _hasher.HashPassword(user, plainPassword);
        }

        public static bool Verify(User user, string hashedPassword, string providedPassword)
        {
            var result = _hasher.VerifyHashedPassword(user, hashedPassword, providedPassword);
            return result == PasswordVerificationResult.Success;
        }
    }
}