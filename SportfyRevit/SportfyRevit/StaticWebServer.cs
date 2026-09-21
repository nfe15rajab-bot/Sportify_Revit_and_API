using System.IO;
using System.Net;
using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// Serves the Sportify web app's static files (plain HTML/CSS/JS, no
    /// build step) directly from the Revit add-in — no Node.js/npx "serve"
    /// dependency needed once this is bundled into an installer. Kept
    /// deliberately separate from RoofBoundaryServer (different port,
    /// different job — that one is the sync API, this is a dumb file
    /// server) so extending one never risks the other's already-working
    /// endpoints.
    /// Serves from "&lt;assembly folder&gt;\web\" — wherever a real
    /// installer copies the packaged web files alongside the add-in DLL.
    /// In this dev checkout, SportfyRevit.csproj copies them there at
    /// build time from the sibling sportfify_goldbeck repo, so the
    /// bundled experience is testable without actually packaging an
    /// installer yet.
    /// </summary>
    internal static class StaticWebServer
    {
        private const string Prefix = "http://localhost:8123/";
        private static HttpListener? _listener;
        private static string? _webRoot;

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "application/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".ico"] = "image/x-icon",
            [".woff2"] = "font/woff2",
            [".woff"] = "font/woff",
            [".ttf"] = "font/ttf",
            [".txt"] = "text/plain; charset=utf-8",
        };

        public static void Start()
        {
            if (_listener != null) return;

            var assemblyDir = Path.GetDirectoryName(typeof(StaticWebServer).Assembly.Location);
            _webRoot = assemblyDir != null ? Path.Combine(assemblyDir, "web") : null;

            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            try
            {
                _listener.Start();
            }
            catch (Exception)
            {
                // Most likely port 8123 is already taken (e.g. a dev "npx
                // serve" already running there) — leave whatever's already
                // serving it alone rather than crash Revit startup over it.
                _listener = null;
                return;
            }
            _ = Task.Run(ListenLoop);
        }

        public static void Stop()
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }

        private static async Task ListenLoop()
        {
            var listener = _listener;
            while (listener != null && listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { break; } // listener was stopped

                try
                {
                    await ServeFile(ctx);
                }
                catch (Exception)
                {
                    try { ctx.Response.Close(); } catch { /* already closed */ }
                }
            }
        }

        private static async Task ServeFile(HttpListenerContext ctx)
        {
            if (_webRoot == null || !Directory.Exists(_webRoot))
            {
                await WriteText(ctx, 500,
                    "Sportify web files not found next to the add-in. This dev checkout expects a sibling " +
                    "sportfify_goldbeck folder at build time; a packaged installer would bundle them here directly.");
                return;
            }

            // A file server: reading only. Anything else is refused before it looks at a path.
            var method = ctx.Request.HttpMethod;
            if (method != "GET" && method != "HEAD")
            {
                ctx.Response.StatusCode = 405;
                ctx.Response.Headers.Add("Allow", "GET, HEAD");
                ctx.Response.Close();
                return;
            }

            var requestPath = ctx.Request.Url?.AbsolutePath ?? "/";
            if (requestPath == "/") requestPath = "/index.html";

            // The file must be inside the web folder: a crafted "/../../something" request, or a sibling folder whose name begins with the web folder's, is a 404.
            var fullPath = LocalRequestGuard.ResolveInside(_webRoot, requestPath);
            if (fullPath == null)
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var ext = Path.GetExtension(fullPath);
            ctx.Response.ContentType = ContentTypes.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";

            // WebView2 caches aggressively by default, and the build copies fresh
            // web files into this folder on every rebuild — so without this the
            // pane keeps running the previous build's JavaScript indefinitely. It
            // cost an evening once: a fix was deployed, verified present on disk,
            // and the pane went on producing exports from the old code.
            // These files are served from the local disk, so there is nothing to
            // gain from caching them anyway.
            ctx.Response.Headers.Add("Cache-Control", "no-store, must-revalidate");
            ctx.Response.Headers.Add("X-Content-Type-Options", "nosniff");
            // Only the origin (http://localhost:8123/), never the page's address, goes to another site: enough for what the app has to be recognised by. "no-referrer" broke the map:
            // OpenStreetMap's tile servers (and Nominatim, the address search) refuse a request that says nothing about where it comes from ("Access blocked ... tile usage policy").
            ctx.Response.Headers.Add("Referrer-Policy", "strict-origin-when-cross-origin");
            // The page's own policy (index.html's <meta>) limits what it loads; the two things a <meta> cannot say are said here: no other site may frame the app.
            ctx.Response.Headers.Add("Content-Security-Policy", "frame-ancestors 'none'");
            ctx.Response.Headers.Add("X-Frame-Options", "DENY");

            var bytes = await File.ReadAllBytesAsync(fullPath);
            ctx.Response.ContentLength64 = bytes.Length;
            if (method != "HEAD") await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        private static async Task WriteText(HttpListenerContext ctx, int statusCode, string text)
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }
}
