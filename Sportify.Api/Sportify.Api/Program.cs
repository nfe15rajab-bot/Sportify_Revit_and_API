using Microsoft.EntityFrameworkCore;
using Sportify.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Reference catalog (sports/norms/materials/providers, plant palettes) —
// deliberately separate from AppDbContext: SQLite, zero external server,
// so it actually runs without anyone standing up Postgres first.
var refDbPath = Path.Combine(builder.Environment.ContentRootPath, "reference.db");
builder.Services.AddDbContext<ReferenceDbContext>(options =>
    options.UseSqlite($"Data Source={refDbPath}"));

const string FrontendCorsPolicy = "FrontendDev";
builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy =>
    // 8123 is where the add-in's own StaticWebServer serves the app, and where
    // it runs inside Revit. 8124 is the plain dev server used when Revit has
    // 8123 — without it the Data tab reports "backend not reachable" while the
    // API is running perfectly well, which reads as a dead backend rather than
    // a blocked origin.
    policy.WithOrigins("http://localhost:8123", "http://localhost:8124")
          .AllowAnyHeader().AllowAnyMethod()));

var jwtKey = builder.Configuration["Jwt:Key"]!;
var jwtIssuer = builder.Configuration["Jwt:Issuer"];

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors(FrontendCorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

using (var scope = app.Services.CreateScope())
{
    var refDb = scope.ServiceProvider.GetRequiredService<ReferenceDbContext>();
    refDb.Database.EnsureCreated();
    ReferenceDataSeeder.Seed(refDb);
    // Before the price backfill, so the finishes' own layers get priced in the
    // same pass rather than waiting for the next start.
    RoofFinishSeeder.Seed(refDb);
    // The choices padel and basketball offer, moved out of the web app's
    // JavaScript so a fourth surface is a row rather than a code change.
    SportOptionSeeder.Seed(refDb);
    // Site furniture from German manufacturers, entered by hand with sources.
    FurnitureSeeder.Seed(refDb);
    // Runs every start, not just on an empty catalog: the price columns were
    // added after there was already data, and it only fills what is missing.
    PriceSeeder.Backfill(refDb);
}

app.Run();