using System.IO;
using System.Net;
using System.Text;

namespace SportfyRevit
{
    /// <summary>
    /// Tiny loopback HTTP server handling both directions of the
    /// frontend/Revit round trip on one port:
    /// - GET  /roof-boundary     — last roof pushed via PushRoofBoundaryCommand,
    ///   in the shape the frontend's pollRevitBoundary() (main.js) polls every 2s.
    /// - POST /combined-layout   — the frontend's "Export Combined JSON" payload,
    ///   pushed automatically on every export so AutoImportSync can pick it up
    ///   without anyone opening a file picker.
    /// - GET  /analysis-results  — latest results published by an Analyze*
    ///   command (AnalysisResultPublisher), for a future frontend poll loop
    ///   to show real Revit-computed numbers instead of only its own
    ///   lightweight web estimate.
    /// Binding to "localhost" specifically (not a wildcard host) means Windows
    /// doesn't require a URL ACL reservation or admin rights to start it.
    /// Started/stopped by SportfyRevitApp alongside Revit's own lifecycle, so
    /// it's already running by the time anyone opens the Combine tab.
    /// </summary>
    public static class RoofBoundaryServer
    {
        private const string Prefix = "http://localhost:5679/";
        private static HttpListener? _listener;
        private static readonly object PayloadLock = new();
        private static string? _payloadJson;

        private static readonly object CombinedLayoutLock = new();
        private static string? _combinedLayoutJson;
        private static int _combinedLayoutVersion;

        private static readonly object AnalysisResultsLock = new();
        private static string? _analysisResultsJson;

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

        /// <summary>Called when the frontend POSTs its Combine export to /combined-layout.</summary>
        private static void SetCombinedLayoutPayload(string json)
        {
            lock (CombinedLayoutLock)
            {
                _combinedLayoutJson = json;
                _combinedLayoutVersion++;
            }
        }

        /// <summary>
        /// Polled in-process by AutoImportSync's Idling handler (same
        /// process as this server, so no HTTP round trip needed) — returns
        /// the latest pushed payload and a version counter that only ever
        /// increases, so the caller can tell "new since last time I looked"
        /// from a single int comparison.
        /// </summary>
        public static bool TryGetLatestCombinedLayout(out string? json, out int version)
        {
            lock (CombinedLayoutLock)
            {
                json = _combinedLayoutJson;
                version = _combinedLayoutVersion;
            }
            return json != null;
        }

        /// <summary>
        /// Called in-process by each Analyze* command right after it
        /// computes a real result — no HTTP hop needed since the caller
        /// lives in the same process as this server, same as how
        /// PushRoofBoundaryCommand calls SetPayload directly above.
        /// </summary>
        public static void PublishAnalysisResults(string json)
        {
            lock (AnalysisResultsLock) { _analysisResultsJson = json; }
        }

        public static bool TryGetLatestAnalysisResults(out string? json)
        {
            lock (AnalysisResultsLock) { json = _analysisResultsJson; }
            return json != null;
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
                        // A JSON POST (combined-layout push) is a non-simple
                        // request, so the browser preflights it and needs
                        // these two extra headers before it'll send the POST.
                        ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                        ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                        ctx.Response.StatusCode = 204;
                        ctx.Response.Close();
                        continue;
                    }

                    var path = ctx.Request.Url?.AbsolutePath;

                    if (path == "/combined-layout" && ctx.Request.HttpMethod == "POST")
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                        var body = await reader.ReadToEndAsync();
                        SetCombinedLayoutPayload(body);
                        ctx.Response.StatusCode = 204;
                        ctx.Response.Close();
                        continue;
                    }

                    if (path == "/analysis-results" && ctx.Request.HttpMethod == "GET")
                    {
                        string? resultsJson;
                        lock (AnalysisResultsLock) { resultsJson = _analysisResultsJson; }

                        if (resultsJson == null)
                        {
                            ctx.Response.StatusCode = 404;
                            ctx.Response.Close();
                            continue;
                        }

                        var resultsBytes = Encoding.UTF8.GetBytes(resultsJson);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = resultsBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(resultsBytes);
                        ctx.Response.Close();
                        continue;
                    }

                    if (path != "/roof-boundary")
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                        continue;
                    }

                    string? json;
                    lock (PayloadLock) { json = _payloadJson; }

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
