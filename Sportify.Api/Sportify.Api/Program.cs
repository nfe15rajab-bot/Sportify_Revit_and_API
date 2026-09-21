using Microsoft.EntityFrameworkCore;
using Sportify.Api;
using Sportify.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Who may change the catalogue, and from which origins (ApiSecurity.cs).
var security = new ApiSecurity(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddSingleton(security);

// A request body is read up to this and no further (the Data tab's photographs and .sql files are the biggest things sent).
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = ApiSecurity.MaxBodyBytes);

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
    // The app's own addresses: 8123 is where the add-in's StaticWebServer serves it (it runs inside Revit), 8124 the presentation copy and the plain dev
    // server used when Revit has 8123; both by name and by number. More with Api:AllowedOrigins.
    policy.WithOrigins(security.Origins.ToArray())
          .WithHeaders("Content-Type", ApiSecurity.KeyHeader)
          .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")));

// Accounts: the bearer scheme is registered only when Jwt:Key is a real key. It used to be set up with a placeholder that was in the repository, so a token signed with
// that placeholder would have been accepted.
var authentication = builder.Services.AddAuthentication();
if (security.JwtConfigured)
{
    authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(security.JwtKey!))
        };
    });
}

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

// Every write needs the key, whatever its body looks like (ApiSecurity.cs). After CORS, so that the refusal is readable by the app.
app.UseMiddleware<WriteKeyMiddleware>();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

if (security.KeyIsGenerated)
    app.Logger.LogInformation("Api:WriteKey is not set: writes need the key made for this run, {Key} (header {Header}); the app gets it from GET /api/session. Set Api:WriteKey to choose one.", security.WriteKey, ApiSecurity.KeyHeader);
else
    app.Logger.LogInformation("Writes need the configured Api:WriteKey (header {Header}).", ApiSecurity.KeyHeader);
if (!security.JwtConfigured) app.Logger.LogInformation("Jwt:Key is not a real key: the account endpoints are off.");
app.Logger.LogInformation("SQL import is {State}.", security.SqlImportEnabled ? "ON (Development or Admin:AllowSqlImport)" : "off");

// The catalog is created and seeded once by EnsureCreated, so an existing reference.db never got what a later build added. Everything the API seeds is ONE sequence
// (CatalogSeeding.Run, Moamen's seeders included): its steps add what is missing in place (tables, columns, rows by key), which keeps whatever was entered through the Data
// tab, and the guard runs it on an existing file before comparing it with what this build would create. Only what cannot be added in place is rebuilt: by default the old file
// is backed up first (ReferenceDbGuard.cs). ReferenceDb:OnDrift = Rebuild | Fail | Ignore.
var onDrift = Enum.TryParse<DriftPolicy>(app.Configuration["ReferenceDb:OnDrift"], ignoreCase: true, out var configured) ? configured : DriftPolicy.Rebuild;
ReferenceDbGuard.Prepare(refDbPath, app.Logger, onDrift);

app.Run();
