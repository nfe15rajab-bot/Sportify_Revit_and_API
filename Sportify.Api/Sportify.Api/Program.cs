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

// The catalog is created and seeded once by EnsureCreated, so an existing reference.db never got what a later build added. Everything the API seeds is ONE sequence
// (CatalogSeeding.Run, Moamen's seeders included): its steps add what is missing in place (tables, columns, rows by key), which keeps whatever was entered through the Data
// tab, and the guard runs it on an existing file before comparing it with what this build would create. Only what cannot be added in place is rebuilt: by default the old file
// is backed up first (ReferenceDbGuard.cs). ReferenceDb:OnDrift = Rebuild | Fail | Ignore.
var onDrift = Enum.TryParse<DriftPolicy>(app.Configuration["ReferenceDb:OnDrift"], ignoreCase: true, out var configured) ? configured : DriftPolicy.Rebuild;
ReferenceDbGuard.Prepare(refDbPath, app.Logger, onDrift);

app.Run();