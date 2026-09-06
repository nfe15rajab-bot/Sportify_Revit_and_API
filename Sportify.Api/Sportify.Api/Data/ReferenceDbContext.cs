using Microsoft.EntityFrameworkCore;
using Sportify.Api.Models;

namespace Sportify.Api.Data
{
    /// <summary>
    /// Read-only reference catalog (sports/norms/materials/providers, plant
    /// palettes) — deliberately separate from AppDbContext (auth + saved
    /// configs, Postgres). SQLite, zero external server, so this actually
    /// runs without anyone having to stand up a database first.
    /// </summary>
    public class ReferenceDbContext : DbContext
    {
        public ReferenceDbContext(DbContextOptions<ReferenceDbContext> options) : base(options) { }

        public DbSet<Sport> Sports => Set<Sport>();
        public DbSet<Norm> Norms => Set<Norm>();
        public DbSet<Material> Materials => Set<Material>();
        public DbSet<Provider> Providers => Set<Provider>();
        public DbSet<Plant> Plants => Set<Plant>();
        public DbSet<PlantPalette> PlantPalettes => Set<PlantPalette>();
    }
}
