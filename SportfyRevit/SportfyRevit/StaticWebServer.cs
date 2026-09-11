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
            [".ico"] = "image/x-icon",
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

            var requestPath = ctx.Request.Url?.AbsolutePath ?? "/";
            if (requestPath == "/") requestPath = "/index.html";

            var webRootFull = Path.GetFullPath(_webRoot);
            var fullPath = Path.GetFullPath(Path.Combine(webRootFull, requestPath.TrimStart('/')));

            // Confirms the resolved path is still inside webRoot — blocks a
            // crafted "/../../something" request path from escaping the
            // served folder.
            if (!fullPath.StartsWith(webRootFull, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            var ext = Path.GetExtension(fullPath);
            ctx.Response.ContentType = ContentTypes.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";

            var bytes = await File.ReadAllBytesAsync(fullPath);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
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
