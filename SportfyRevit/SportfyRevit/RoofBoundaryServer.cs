using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

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
    ///   command (AnalysisResultPublisher); the web app's Compare mode polls
    ///   it to show the real Revit-computed numbers next to its own
    ///   lightweight estimates.
    /// - GET  /recording?path=   — an MP4 an analysis recorded, so the web app
    ///   can play the Unity video beside the numbers. Only files whose path
    ///   appears as a "video_path" in the published results are served (a
    ///   loopback server must not read arbitrary files for any page that asks),
    ///   with byte ranges so the browser can seek.
    /// Binding to "localhost" specifically (not a wildcard host) means Windows
    /// doesn't require a URL ACL reservation or admin rights to start it.
    /// Started/stopped by SportfyRevitApp alongside Revit's own lifecycle, so
    /// it's already running by the time anyone opens the Combine tab.
    /// </summary>
    public static class RoofBoundaryServer
    {
        private const int Port = 5679;
        private const string Prefix = "http://localhost:5679/";
        private static HttpListener? _listener;

        /// <summary>Who may talk to this server and how much they may send (see LocalRequestGuard). Replaced in tests.</summary>
        internal static LocalRequestGuard Guard { get; set; } = LocalRequestGuard.FromEnvironment(Port);
        private static int _refusals;
        private static readonly object PayloadLock = new();
        private static string? _payloadJson;

        private static readonly object CombinedLayoutLock = new();
        private static string? _combinedLayoutJson;
        private static int _combinedLayoutVersion;
        private static string? _combinedLayoutId;

        /// <summary>The identity (LayoutIdentity) of the newest layout the web app has sent, draft or export; null when none has arrived.</summary>
        public static string? CurrentLayoutId { get { lock (CombinedLayoutLock) return _combinedLayoutId; } }

        /// <summary>
        /// The identity of the layout THIS thread last read with TryGetLatestCombinedLayout, else the current one. An analysis reads the layout when it
        /// starts and may publish minutes later (a Unity run): the result belongs to the layout it read, not to whatever has arrived since.
        /// </summary>
        [ThreadStatic] private static string? _idReadOnThisThread;
        public static string? LayoutIdForPublishing => _idReadOnThisThread ?? CurrentLayoutId;

        private static readonly object AnalysisResultsLock = new();
        private static string? _analysisResultsJson;

        // The recordings the published results name: the only files /recording will serve.
        private static readonly HashSet<string> AllowedRecordings = new(StringComparer.OrdinalIgnoreCase);

        private static readonly object FamiliesLock = new();
        private static string? _familiesJson;

        private static readonly object IterationsLock = new();
        private static string? _iterationsJson;

        public static void Start()
        {
            if (_listener != null) return;
            _listener = new HttpListener();
            _listener.Prefixes.Add(Prefix);
            try
            {
                // A client that sends its headers or its body at a trickle must not hold a connection (and this loop) for ever.
                var t = _listener.TimeoutManager;
                t.HeaderWait = TimeSpan.FromSeconds(30);
                t.EntityBody = TimeSpan.FromSeconds(60);
                t.IdleConnection = TimeSpan.FromMinutes(2);
            }
            catch (Exception ex) { SportifyLog.Warn("server", "the listener's time limits could not be set: " + ex.Message); }
            _listener.Start();
            SportifyLog.Info("server", "listening on " + Prefix + " for " + string.Join(", ", Guard.Origins));
            _ = Task.Run(ListenLoop);
        }

        public static void Stop()
        {
            _listener?.Stop();
            _listener?.Close();
            _listener = null;
        }

        /// <summary>The roof pushed last, as the app receives it (null before the first push): what a partial push is laid onto (RoofPushMerge).</summary>
        internal static string? CurrentPayload
        {
            get { lock (PayloadLock) { return _payloadJson; } }
        }

        /// <summary>Called by the push commands (PushRoofCommandBase) every time the user pushes a roof.</summary>
        public static void SetPayload(string json)
        {
            lock (PayloadLock) { _payloadJson = json; }
        }

        /// <summary>
        /// Called when the frontend POSTs its Combine export to /combined-layout —
        /// and also by ImportSportifyLayoutCommand after a manual file-picker
        /// import, so a manually-imported layout is just as visible to
        /// TryGetLatestCombinedLayout's callers (e.g. SimulateBallTrajectoriesCommand)
        /// as a live Combine push, with no separate cache needed.
        /// </summary>
        public static void SetCombinedLayoutPayload(string json)
        {
            lock (CombinedLayoutLock)
            {
                _combinedLayoutJson = json;
                _combinedLayoutId = LayoutIdentity.Of(json);
                _combinedLayoutVersion++;
            }
        }

        /// <summary>
        /// The layout as the web app has it right now, sent automatically as it changes (POST /combined-layout?draft=1). The analyses, the charts and the PDFs read
        /// the newest layout, so this updates it; but it is not an export, so the version that Auto Import watches stays where it was and nothing is imported into
        /// the Revit model until the designer exports.
        /// </summary>
        public static void SetDraftLayoutPayload(string json)
        {
            lock (CombinedLayoutLock) { _combinedLayoutJson = json; _combinedLayoutId = LayoutIdentity.Of(json); }
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
                _idReadOnThisThread = _combinedLayoutId;
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
            var videos = VideoPathsIn(json);
            lock (AnalysisResultsLock)
            {
                _analysisResultsJson = json;
                AllowedRecordings.Clear();
                foreach (var v in videos) AllowedRecordings.Add(v);
            }
        }

        /// <summary>
        /// Called when the frontend POSTs its saved iterations (compareController.js's savedCompareConfigs, up to 3) to /iterations, for
        /// ImportIterationsAsOptionsCommand to read in-process — the same "the web app's own state, mirrored here" pattern as SetCombinedLayoutPayload.
        /// </summary>
        public static void SetIterationsPayload(string json)
        {
            lock (IterationsLock) { _iterationsJson = json; }
        }

        /// <summary>The saved iterations the web app last sent, or null if it never has this session.</summary>
        public static bool TryGetLatestIterations(out string? json)
        {
            lock (IterationsLock) { json = _iterationsJson; }
            return json != null;
        }

        /// <summary>Every "video_path" (a .mp4 that exists) named anywhere in a published results document, as a full path.</summary>
        internal static List<string> VideoPathsIn(string json)
        {
            var found = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                void Walk(JsonElement e)
                {
                    if (e.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in e.EnumerateObject())
                        {
                            if (p.Name == "video_path" && p.Value.ValueKind == JsonValueKind.String)
                            {
                                var path = p.Value.GetString();
                                if (!string.IsNullOrEmpty(path) && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                                {
                                    try { found.Add(Path.GetFullPath(path)); } catch (Exception) { /* not a path */ }
                                }
                            }
                            else Walk(p.Value);
                        }
                    }
                    else if (e.ValueKind == JsonValueKind.Array)
                        foreach (var item in e.EnumerateArray()) Walk(item);
                }
                Walk(doc.RootElement);
            }
            catch (JsonException) { /* not JSON: nothing to allow */ }
            return found;
        }

        /// <summary>True when a published result names this file as a recording, so /recording may serve it.</summary>
        internal static bool IsAllowedRecording(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                lock (AnalysisResultsLock) { return AllowedRecordings.Contains(full) && File.Exists(full); }
            }
            catch (Exception) { return false; }
        }

        /// <summary>The byte range a Range header asks of a file of this length: null for none or a malformed one, (-1, -1) when unsatisfiable.</summary>
        internal static (long Start, long End)? ParseRange(string? header, long length)
        {
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return null;
            var spec = header.Substring(6).Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash < 0) return null;
            var a = spec.Substring(0, dash).Trim();
            var b = spec.Substring(dash + 1).Trim();
            long start, end;
            if (a == "")
            {
                if (!long.TryParse(b, out var suffix) || suffix <= 0) return null;   // the last N bytes
                start = Math.Max(0, length - suffix);
                end = length - 1;
            }
            else
            {
                if (!long.TryParse(a, out start) || start < 0) return null;
                if (b == "") end = length - 1;
                else if (!long.TryParse(b, out end) || end < start) return null;
            }
            if (start >= length) return (-1, -1);
            return (start, Math.Min(end, length - 1));
        }

        private static async Task ServeRecording(HttpListenerContext ctx)
        {
            var requested = ctx.Request.QueryString["path"];
            if (string.IsNullOrEmpty(requested) || !IsAllowedRecording(requested))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            await ServeFile(ctx, Path.GetFullPath(requested), "video/mp4");
        }

        /// <summary>Streams a file, honouring a Range header (a video seeks; a PDF is read whole).</summary>
        private static async Task ServeFile(HttpListenerContext ctx, string file, string contentType)
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var length = stream.Length;
            var range = ParseRange(ctx.Request.Headers["Range"], length);

            ctx.Response.ContentType = contentType;
            ctx.Response.Headers.Add("Accept-Ranges", "bytes");
            if (Path.GetExtension(file).ToLowerInvariant() is ".csv" or ".dxf" or ".json" or ".txt")     // a browser would show these as text: offer them as files
                ctx.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"{Path.GetFileName(file)}\"");
            long from = 0, to = length - 1;
            if (range is { } r)
            {
                if (r.Start < 0)
                {
                    ctx.Response.StatusCode = 416;
                    ctx.Response.Headers.Add("Content-Range", $"bytes */{length}");
                    ctx.Response.Close();
                    return;
                }
                from = r.Start; to = r.End;
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.Add("Content-Range", $"bytes {from}-{to}/{length}");
            }

            ctx.Response.ContentLength64 = to - from + 1;
            if (ctx.Request.HttpMethod == "HEAD") { ctx.Response.Close(); return; }

            stream.Seek(from, SeekOrigin.Begin);
            var buffer = new byte[81920];
            var left = to - from + 1;
            try
            {
                while (left > 0)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)));
                    if (read <= 0) break;
                    await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                    left -= read;
                }
            }
            catch (Exception) { /* the player closed the connection (a seek): nothing to tell it */ }
            finally { try { ctx.Response.Close(); } catch (Exception) { /* already gone */ } }
        }

        /// <summary>Logs a refused request: the first few, then one in a hundred, so that a page hammering the port cannot fill the disk.</summary>
        private static void NoteRefusal(HttpListenerContext ctx, LocalRequestGuard.Verdict verdict)
        {
            var n = Interlocked.Increment(ref _refusals);
            if (n > 20 && n % 100 != 0) return;
            SportifyLog.Warn("server", $"refused {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath} ({verdict.Status}): {verdict.Reason}" + (n > 20 ? $" [{n} refusals so far]" : ""));
        }

        private static async Task RefuseTooLarge(HttpListenerContext ctx, long limit)
        {
            SportifyLog.Warn("server", $"refused {ctx.Request.HttpMethod} {ctx.Request.Url?.AbsolutePath}: the body is larger than {limit} bytes");
            var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { error = new LocalRequestGuard.BodyTooLargeException(limit).Message }));
            ctx.Response.StatusCode = 413;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.KeepAlive = false;
            try { await ctx.Response.OutputStream.WriteAsync(bytes); } catch (Exception) { /* the sender is still sending: it gets the closed connection */ }
            ctx.Response.Close();
        }

        private static async Task Write(HttpListenerContext ctx, EndpointResponse answer)
        {
            if (answer.FilePath != null)
            {
                await ServeFile(ctx, answer.FilePath, answer.ContentType);
                return;
            }
            ctx.Response.StatusCode = answer.Status;
            ctx.Response.ContentType = answer.ContentType;
            var bytes = answer.Body ?? Array.Empty<byte>();
            ctx.Response.ContentLength64 = bytes.Length;
            if (ctx.Request.HttpMethod != "HEAD") await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public static bool TryGetLatestAnalysisResults(out string? json)
        {
            lock (AnalysisResultsLock) { json = _analysisResultsJson; }
            return json != null;
        }

        /// <summary>
        /// Called in-process by LoadFamiliesCommand after the user picks .rfa
        /// files: publishes the firm's OWN loaded families — names, types and
        /// writable dimension parameters — for the web app to offer as
        /// placeable pieces alongside its built-in catalog. Same direct
        /// in-process call as PushRoofBoundaryCommand's SetPayload, no HTTP
        /// hop needed to reach a server living in this same process.
        /// </summary>
        public static void PublishFamilies(string json)
        {
            lock (FamiliesLock) { _familiesJson = json; }
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
                    var path = ctx.Request.Url?.AbsolutePath;
                    ctx.Response.Headers.Add("Cache-Control", "no-store");
                    ctx.Response.Headers.Add("X-Content-Type-Options", "nosniff");

                    // Who is asking. The web app's fetch runs from another origin (localhost:8123), so the browser needs CORS headers to let its script read the
                    // answer; they name the asking origin when it is one of the app's, and are never "*". Everything but the handshake needs the session token.
                    var verdict = Guard.Judge(ctx.Request.HttpMethod, ctx.Request.Headers["Host"], ctx.Request.Headers["Origin"], path,
                        ctx.Request.Headers[LocalRequestGuard.TokenHeader], ctx.Request.QueryString[LocalRequestGuard.TokenQuery]);
                    if (verdict.CorsOrigin != null)
                    {
                        ctx.Response.Headers.Add("Access-Control-Allow-Origin", verdict.CorsOrigin);
                        ctx.Response.Headers.Add("Vary", "Origin");
                    }

                    if (verdict.Outcome == LocalRequestGuard.Outcome.Preflight)
                    {
                        // A JSON POST (combined-layout push) is a non-simple request, so the browser preflights it and needs these headers before it'll send the POST.
                        if (verdict.CorsOrigin != null)
                        {
                            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type, " + LocalRequestGuard.TokenHeader);
                            ctx.Response.Headers.Add("Access-Control-Max-Age", "600");
                        }
                        ctx.Response.StatusCode = 204;
                        ctx.Response.Close();
                        continue;
                    }

                    if (verdict.Outcome == LocalRequestGuard.Outcome.Refuse)
                    {
                        NoteRefusal(ctx, verdict);
                        var refusedBytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { error = verdict.Reason }));
                        ctx.Response.StatusCode = verdict.Status;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = refusedBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(refusedBytes);
                        ctx.Response.Close();
                        continue;
                    }

                    if (verdict.Outcome == LocalRequestGuard.Outcome.Session)
                    {
                        var sessionBytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new { token = Guard.Token, header = LocalRequestGuard.TokenHeader, query = LocalRequestGuard.TokenQuery }));
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = sessionBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(sessionBytes);
                        ctx.Response.Close();
                        continue;
                    }

                    // Whatever body there is, it is read up to what this endpoint takes and no further (a Content-Length beyond it is refused before a byte is read).
                    var bodyLimit = LocalRequestGuard.BodyLimit(ctx.Request.HttpMethod, path);
                    if (ctx.Request.ContentLength64 > bodyLimit)
                    {
                        await RefuseTooLarge(ctx, bodyLimit);
                        continue;
                    }

                    if (path == "/combined-layout" && ctx.Request.HttpMethod == "POST")
                    {
                        string body;
                        try { body = Encoding.UTF8.GetString(LocalRequestGuard.ReadBounded(ctx.Request.InputStream, bodyLimit, ctx.Request.ContentLength64)); }
                        catch (LocalRequestGuard.BodyTooLargeException) { await RefuseTooLarge(ctx, bodyLimit); continue; }
                        var draft = ctx.Request.QueryString["draft"] == "1";
                        if (draft) SetDraftLayoutPayload(body); else SetCombinedLayoutPayload(body);
                        // The identity of what was just received, so that the web app knows which layout the results that follow are about.
                        var answerBytes = Encoding.UTF8.GetBytes("{\"layout_id\":\"" + LayoutIdentity.Of(body) + "\",\"draft\":" + (draft ? "true" : "false") + "}");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = answerBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(answerBytes);
                        ctx.Response.Close();
                        continue;
                    }

                    if (path == "/iterations" && ctx.Request.HttpMethod == "POST")
                    {
                        string body;
                        try { body = Encoding.UTF8.GetString(LocalRequestGuard.ReadBounded(ctx.Request.InputStream, bodyLimit, ctx.Request.ContentLength64)); }
                        catch (LocalRequestGuard.BodyTooLargeException) { await RefuseTooLarge(ctx, bodyLimit); continue; }
                        SetIterationsPayload(body);
                        var countBytes = Encoding.UTF8.GetBytes("{\"received\":true}");
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = countBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(countBytes);
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

                    if (path == "/recording" && (ctx.Request.HttpMethod == "GET" || ctx.Request.HttpMethod == "HEAD"))
                    {
                        await ServeRecording(ctx);
                        continue;
                    }

                    if (path == "/families" && ctx.Request.HttpMethod == "GET")
                    {
                        string? familiesJson;
                        lock (FamiliesLock) { familiesJson = _familiesJson; }

                        if (familiesJson == null)
                        {
                            // Nothing loaded yet — same "connected, nothing pushed"
                            // shape the roof-boundary poll below already uses.
                            ctx.Response.StatusCode = 404;
                            ctx.Response.Close();
                            continue;
                        }

                        var familiesBytes = Encoding.UTF8.GetBytes(familiesJson);
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = familiesBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(familiesBytes);
                        ctx.Response.Close();
                        continue;
                    }

                    // the workspace: what is in it, saving into it, the analyses and their charts, the PDFs (WorkspaceEndpoints: plain functions of the request)
                    {
                        var request = ctx.Request;
                        EndpointResponse? answer;
                        try { answer = await Task.Run(() => WorkspaceEndpoints.Handle(request.HttpMethod, path, request.QueryString, () => LocalRequestGuard.ReadBounded(request.InputStream, bodyLimit, request.ContentLength64))); }
                        catch (Exception ex) when (ex is LocalRequestGuard.BodyTooLargeException || ex is AggregateException { InnerException: LocalRequestGuard.BodyTooLargeException }) { await RefuseTooLarge(ctx, bodyLimit); continue; }
                        if (answer != null)
                        {
                            await Write(ctx, answer);
                            continue;
                        }
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
