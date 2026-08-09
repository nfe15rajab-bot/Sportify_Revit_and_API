using Microsoft.EntityFrameworkCore;
using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<SavedConfiguration> SavedConfigurations { get; set; }
    }
}
