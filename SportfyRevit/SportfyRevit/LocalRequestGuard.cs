using System.IO;
using System.Security.Cryptography;

namespace SportfyRevit
{
    /// <summary>
    /// Who may talk to the add-in's local servers, and how much they may send. A plain class with no Revit and no listener in it, so Tools/AddinCheck tests every rule.
    ///
    /// The server used to answer everybody: "Access-Control-Allow-Origin: *" meant that ANY web page open in the user's browser could read the layout, the results and the
    /// Sportify folder from localhost:5679 and post its own layout over the designer's, and a request body was read to the end whatever its size. Now:
    ///   Host      must be this machine's own name for the port (a page reaching the server through a name it controls, "DNS rebinding", is refused);
    ///   Origin    when the request has one (every browser fetch from a page does) it must be one of the app's: the add-in's own web server (localhost:8123), the presentation
    ///             copy (8124), both also as 127.0.0.1; more can be added with SPORTIFY_ALLOWED_ORIGINS (semicolon-separated) for a dev server elsewhere;
    ///   token     everything except the handshake needs the session token, in the X-Sportify-Token header (or ?token= where a header cannot be sent: a video's src, a download
    ///             link). The token is made when the add-in starts and is given only by GET /session, and only to a request whose Origin is one of the app's: a page from
    ///             another origin cannot obtain it, and cannot read the answer of a request it has no token for;
    ///   size      a request body is capped by what its endpoint takes (a layout, a saved file, anything else), counted while it is read, not after.
    /// The token is not a password against software already running as the user (that could read the add-in's memory anyway); it stops the browser's other pages.
    /// </summary>
    internal sealed class LocalRequestGuard
    {
        public const string TokenHeader = "X-Sportify-Token";
        public const string TokenQuery = "token";

        /// <summary>A layout: the file command refuses more than this too.</summary>
        public const long MaxLayoutBytes = 64L * 1024 * 1024;
        /// <summary>A file the web app saves into the Sportify folder (PNG, JSON, DXF, PDF).</summary>
        public const long MaxDeliverableBytes = 64L * 1024 * 1024;
        /// <summary>Everything else: the requests that carry no body at all, and one that has a little.</summary>
        public const long MaxOtherBytes = 1L * 1024 * 1024;

        /// <summary>The web app's own addresses: the add-in's web server and the presentation copy, by name and by number.</summary>
        public static readonly string[] DefaultOrigins =
        {
            "http://localhost:8123", "http://127.0.0.1:8123",
            "http://localhost:8124", "http://127.0.0.1:8124",
        };

        public enum Outcome { Proceed, Preflight, Session, Refuse }

        public readonly struct Verdict
        {
            public Verdict(Outcome outcome, int status, string reason, string? corsOrigin) { Outcome = outcome; Status = status; Reason = reason; CorsOrigin = corsOrigin; }
            public Outcome Outcome { get; }
            /// <summary>The status to answer with when the request is refused (or 204 for a preflight).</summary>
            public int Status { get; }
            public string Reason { get; }
            /// <summary>The origin to echo in Access-Control-Allow-Origin, or null for none: only ever one of the allowed ones, never "*".</summary>
            public string? CorsOrigin { get; }
        }

        readonly HashSet<string> _origins = new(StringComparer.OrdinalIgnoreCase);
        readonly string[] _hosts;

        /// <summary>The session token: 256 random bits, different every time the add-in starts.</summary>
        public string Token { get; }

        public LocalRequestGuard(int port, IEnumerable<string>? extraOrigins = null, string? token = null)
        {
            foreach (var o in DefaultOrigins) _origins.Add(o);
            foreach (var o in extraOrigins ?? Array.Empty<string>())
            {
                var n = NormaliseOrigin(o);
                if (n != null) _origins.Add(n);
            }
            _hosts = new[] { "localhost:" + port, "127.0.0.1:" + port, "[::1]:" + port };
            Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }

        /// <summary>A guard for the port with the origins the environment adds (SPORTIFY_ALLOWED_ORIGINS).</summary>
        public static LocalRequestGuard FromEnvironment(int port)
        {
            var extra = (Environment.GetEnvironmentVariable("SPORTIFY_ALLOWED_ORIGINS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
            return new LocalRequestGuard(port, extra);
        }

        /// <summary>"HTTP://Localhost:9000/" → "http://localhost:9000"; null for what is not a plain http(s) origin ("*", "null", a path, no scheme).</summary>
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

        public IReadOnlyCollection<string> Origins => _origins;

        static bool SameToken(string? given, string expected)
        {
            if (string.IsNullOrEmpty(given) || given.Length != expected.Length) return false;
            return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(given), System.Text.Encoding.ASCII.GetBytes(expected));
        }

        /// <summary>What to do with a request, from its method, its Host and Origin headers, its path and the token it carries (header, query).</summary>
        public Verdict Judge(string method, string? host, string? origin, string? path, string? tokenHeader, string? tokenQuery)
        {
            if (host == null || !_hosts.Contains(host.Trim(), StringComparer.OrdinalIgnoreCase))
                return new Verdict(Outcome.Refuse, 400, "the Host header \"" + host + "\" is not this machine's name for the server", null);

            var hasOrigin = !string.IsNullOrWhiteSpace(origin);
            string? cors = null;
            if (hasOrigin)
            {
                if (!IsAllowedOrigin(origin)) return new Verdict(Outcome.Refuse, 403, "the origin \"" + origin + "\" is not one of the app's", null);
                cors = NormaliseOrigin(origin);
            }

            if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                return new Verdict(Outcome.Preflight, 204, "", cors);

            if (path == "/session")
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)) return new Verdict(Outcome.Refuse, 405, "the session is read with GET", cors);
                if (cors == null) return new Verdict(Outcome.Refuse, 403, "the session token is given only to a page of the app (a request with no Origin is not one)", null);
                return new Verdict(Outcome.Session, 200, "", cors);
            }

            if (!SameToken(tokenHeader, Token) && !SameToken(tokenQuery, Token))
                return new Verdict(Outcome.Refuse, 401, "no valid session token (GET /session from the app gives it)", cors);

            return new Verdict(Outcome.Proceed, 200, "", cors);
        }

        /// <summary>How much body an endpoint takes.</summary>
        public static long BodyLimit(string method, string? path)
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase)) return MaxOtherBytes;
            return path switch
            {
                "/combined-layout" => MaxLayoutBytes,
                "/deliverable" => MaxDeliverableBytes,
                "/iterations" => MaxLayoutBytes,
                _ => MaxOtherBytes,
            };
        }

        /// <summary>The body was longer than the endpoint takes.</summary>
        public sealed class BodyTooLargeException : Exception
        {
            public long Limit { get; }
            public BodyTooLargeException(long limit) : base("The request body is larger than " + (limit >= 1024 * 1024 ? limit / (1024 * 1024) + " MB" : limit / 1024 + " KB") + ".") { Limit = limit; }
        }

        /// <summary>
        /// Reads a request body up to <paramref name="limit"/> bytes. A Content-Length beyond it is refused before anything is read; without one (chunked) the count is kept while
        /// reading and it stops the moment it is passed, so a body of any size never sits in memory.
        /// </summary>
        public static byte[] ReadBounded(Stream input, long limit, long declaredLength = -1)
        {
            if (declaredLength > limit) throw new BodyTooLargeException(limit);
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > limit) throw new BodyTooLargeException(limit);
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// The file a request path names inside <paramref name="root"/>, or null when it leaves the folder or is not a file. The check compares with the folder's name plus a
        /// separator: "C:\web" must not accept "C:\web-secrets\x" (the old check was a plain StartsWith on the name).
        /// </summary>
        public static string? ResolveInside(string root, string requestPath)
        {
            try
            {
                var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var relative = Uri.UnescapeDataString(requestPath).TrimStart('/', '\\');
                if (relative.Contains('\0')) return null;
                var full = Path.GetFullPath(Path.Combine(rootFull, relative));
                if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(full) ? full : null;
            }
            catch (Exception) { return null; }
        }
    }
}
