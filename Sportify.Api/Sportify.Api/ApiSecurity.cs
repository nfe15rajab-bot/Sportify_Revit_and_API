using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace Sportify.Api
{
    /// <summary>
    /// Who may change the catalogue. Reading it stays open (the app and the Revit add-in read it, and it holds nothing private); every write needs a key.
    ///
    /// Until now nothing on this API was protected: any web page open in the browser could POST to <c>/api/Admin/import-sql</c> (which runs the SQL it is given against the
    /// catalogue), and the JWT bearer was set up with a placeholder signing key that was in the repository, so a token signed with it would have been accepted.
    ///
    ///   write key   POST/PUT/PATCH/DELETE need the header <c>X-Sportify-Key</c>. Set <c>Api:WriteKey</c> (environment: <c>Api__WriteKey</c>) to choose it; the people who may write
    ///               are then the ones who know it, and the Catalogue tab asks for it once per browser tab. When none is set the API makes a random one at start and gives it,
    ///               through <c>GET /api/session</c>, only to a page of the app (a request whose Origin is one of the app's): the local single-user case works without setup and
    ///               another web page in the same browser cannot get it. It is logged once at start for scripts.
    ///   origins     the app's own addresses (localhost and 127.0.0.1, ports 8123 and 8124) plus <c>Api:AllowedOrigins</c> (semicolon-separated); CORS allows only those.
    ///   SQL import  <c>/api/Admin/import-sql</c> runs whatever SQL it is given, so it exists only in Development or with <c>Admin:AllowSqlImport=true</c>, still needs the key,
    ///               and refuses ATTACH, DETACH, PRAGMA, VACUUM and load_extension.
    ///   JWT         the bearer scheme and the account endpoints exist only when <c>Jwt:Key</c> is a real key (32 characters or more, not the old placeholder).
    /// </summary>
    public sealed class ApiSecurity
    {
        public const string KeyHeader = "X-Sportify-Key";
        public const long MaxBodyBytes = 16L * 1024 * 1024;
        public const int MaxSqlChars = 5 * 1024 * 1024;

        public static readonly string[] DefaultOrigins =
        {
            "http://localhost:8123", "http://127.0.0.1:8123", "http://localhost:8124", "http://127.0.0.1:8124",
        };

        /// <summary>The key that used to be in appsettings.json: never valid.</summary>
        public const string OldPlaceholderJwtKey = "REPLACE_WITH_ENV_VAR_OR_USER_SECRET";

        readonly HashSet<string> _origins = new(StringComparer.OrdinalIgnoreCase);

        public string WriteKey { get; }
        /// <summary>True when the key was made at start (nothing configured): only then does GET /api/session hand it out.</summary>
        public bool KeyIsGenerated { get; }
        public bool SqlImportEnabled { get; }
        public bool JwtConfigured { get; }
        public string? JwtKey { get; }
        public IReadOnlyCollection<string> Origins => _origins;

        public ApiSecurity(IConfiguration config, bool isDevelopment)
        {
            var configured = config["Api:WriteKey"];
            if (string.IsNullOrWhiteSpace(configured))
            {
                WriteKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                KeyIsGenerated = true;
            }
            else
            {
                WriteKey = configured.Trim();
                KeyIsGenerated = false;
            }

            foreach (var o in DefaultOrigins) _origins.Add(o);
            foreach (var o in (config["Api:AllowedOrigins"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var n = NormaliseOrigin(o);
                if (n != null) _origins.Add(n);
            }

            SqlImportEnabled = isDevelopment || string.Equals(config["Admin:AllowSqlImport"], "true", StringComparison.OrdinalIgnoreCase);

            var jwt = config["Jwt:Key"];
            JwtConfigured = IsRealJwtKey(jwt);
            JwtKey = JwtConfigured ? jwt : null;
        }

        public static bool IsRealJwtKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length >= 32 && key != OldPlaceholderJwtKey && !key.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase);

        public static string? NormaliseOrigin(string? origin)
        {
            var o = (origin ?? "").Trim().TrimEnd('/');
            if (o.Length == 0 || o == "*" || o == "null") return null;
            if (!Uri.TryCreate(o, UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
            if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return null;
            return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        }

        public bool IsAllowedOrigin(string? origin)
        {
            var n = NormaliseOrigin(origin);
            return n != null && _origins.Contains(n);
        }

        public bool KeyMatches(string? given)
        {
            if (string.IsNullOrEmpty(given)) return false;
            var a = Encoding.UTF8.GetBytes(given);
            var b = Encoding.UTF8.GetBytes(WriteKey);
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }

        public static bool IsWrite(string method) =>
            HttpMethods.IsPost(method) || HttpMethods.IsPut(method) || HttpMethods.IsPatch(method) || HttpMethods.IsDelete(method);

        static readonly string[] ForbiddenSql = { "attach", "detach", "pragma", "vacuum", "load_extension", "writable_schema" };

        /// <summary>
        /// Why a script may not be run, or null. The words are looked for outside string literals and comments (a description that says "vacuum drainage" is data, not a
        /// statement), as whole words, in any case.
        /// </summary>
        public static string? SqlRefusal(string sql)
        {
            if (sql.Length > MaxSqlChars) return $"The script is longer than {MaxSqlChars / (1024 * 1024)} MB.";
            var code = System.Text.RegularExpressions.Regex.Replace(sql, @"'(?:[^']|'')*'|--[^\r\n]*|/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (var word in ForbiddenSql)
                if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\b" + word + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return $"The script uses \"{word.ToUpperInvariant()}\", which an import may not: it reaches beyond the catalogue tables.";
            return null;
        }
    }

    /// <summary>
    /// A write without the key is refused before anything else looks at it: middleware, not a filter, because model binding (a 400 for a body that does not fit) runs before
    /// action filters, and a request with no key should be told to bring one, not what is wrong with its body. The account endpoints have their own authentication.
    /// </summary>
    public sealed class WriteKeyMiddleware
    {
        readonly RequestDelegate _next;
        readonly ApiSecurity _security;
        readonly ILogger<WriteKeyMiddleware> _log;
        public WriteKeyMiddleware(RequestDelegate next, ApiSecurity security, ILogger<WriteKeyMiddleware> log) { _next = next; _security = security; _log = log; }

        public async Task InvokeAsync(HttpContext context)
        {
            var request = context.Request;
            if (ApiSecurity.IsWrite(request.Method) && !request.Path.StartsWithSegments("/api/auth", StringComparison.OrdinalIgnoreCase) && !_security.KeyMatches(request.Headers[ApiSecurity.KeyHeader]))
            {
                _log.LogWarning("Refused {Method} {Path}: no valid {Header} header.", request.Method, request.Path, ApiSecurity.KeyHeader);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new { error = $"Changing the catalogue needs the write key ({ApiSecurity.KeyHeader} header)." });
                return;
            }
            await _next(context);
        }
    }

    /// <summary>The handshake: the app asks for the write key. Given only to a request from one of the app's origins, and only when the API made the key itself.</summary>
    [ApiController]
    [Route("api/session")]
    public class SessionController : ControllerBase
    {
        readonly ApiSecurity _security;
        public SessionController(ApiSecurity security) => _security = security;

        [HttpGet]
        public IActionResult Get()
        {
            var origin = Request.Headers.Origin.ToString();
            // A page this API serves itself (the installed web app on localhost:5107) asks with no Origin header, because a browser leaves it off a same-origin GET; what it sends
            // instead is Sec-Fetch-Site: same-origin, which a page cannot forge or change. Only for a request that came to the local address.
            var sameOrigin = string.Equals(Request.Headers["Sec-Fetch-Site"].ToString(), "same-origin", StringComparison.OrdinalIgnoreCase) && (Request.Host.Host is "localhost" or "127.0.0.1");
            if (!sameOrigin && !_security.IsAllowedOrigin(origin))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "The write key is given only to a page of the app (a request with no Origin, or another one, is not)." });
            if (!_security.KeyIsGenerated)
                return Ok(new { mode = "configured", header = ApiSecurity.KeyHeader });      // the key was chosen by whoever runs this API: it is not handed out
            return Ok(new { mode = "handshake", header = ApiSecurity.KeyHeader, key = _security.WriteKey });
        }
    }
}
