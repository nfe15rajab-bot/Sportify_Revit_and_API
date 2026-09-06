using System.Net;
using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// Tiny loopback HTTP server serving the last roof boundary pushed via
    /// PushRoofBoundaryCommand, in exactly the shape the frontend's
    /// pollRevitBoundary() (main.js) already polls for every 2s:
    /// { roof: { length_m, width_m, boundary_m, origin_x_m, origin_y_m,
    /// source_element_name } }. Binding to "localhost" specifically (not a
    /// wildcard host) means Windows doesn't require a URL ACL reservation
    /// or admin rights to start it. Started/stopped by SportfyRevitApp
    /// alongside Revit's own lifecycle, so it's already running by the time
    /// anyone opens the Combine tab and starts polling.
    /// </summary>
    public static class RoofBoundaryServer
    {
        private const string Prefix = "http://localhost:5679/";
        private static HttpListener? _listener;
        private static readonly object PayloadLock = new();
        private static string? _payloadJson;

        public static void Start()
        {
            if (_listener != null) return;
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(ListenLoop);
        }

        public static void Stop()
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }

        /// <summary>Called by PushRoofBoundaryCommand every time the user pushes a roof.</summary>
        public static void SetPayload(string json)
        {
            lock (PayloadLock) { _payloadJson = json; }
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
                    // The frontend's fetch runs from http://localhost:8123 — a
                    // different origin — so every response needs an explicit
                    // CORS header or the browser blocks the JS from reading it,
                    // even though the request itself succeeds.
                    ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    ctx.Response.Headers.Add("Cache-Control", "no-store");

                    if (ctx.Request.HttpMethod == "OPTIONS")
                    {
                        ctx.Response.StatusCode = 204;
                        ctx.Response.Close();
                        continue;
                    }

                    string? json;
                    lock (PayloadLock) { json = _payloadJson; }

                    if (ctx.Request.Url?.AbsolutePath != "/roof-boundary")
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }
                    if (json == null)
                    {
                        // No push yet — pollRevitBoundary() already treats any
                        // non-OK response as "connected, waiting for a push".
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }

                    var bytes = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
                catch (Exception)
                {
                    // A single malformed/aborted request shouldn't take the
                    // whole listener down — log-and-continue in spirit, but
                    // this add-in has no logging pipeline yet, so just drop it.
                    try { ctx.Response.Close(); } catch { /* already closed */ }
                }
            }
        }
    }
}
