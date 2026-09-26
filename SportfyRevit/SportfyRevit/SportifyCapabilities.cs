using System.Text.Json.Nodes;

namespace SportfyRevit
{
    /// <summary>One tool this computer may have: whether it was found, where, and one sentence saying so in words a person can act on.</summary>
    internal sealed record ToolStatus(bool Found, string? Path, string Note);

    /// <summary>
    /// What this computer can do besides Revit itself: render the 3D videos (Unity), build the mechanical assembly of a dynamic unit (SOLIDWORKS), open the web app in Chrome.
    /// <see cref="UnityProjectFree"/> is whether Unity's Editor is NOT holding the Sportify.Simulation project open (a project can only be open once).
    /// </summary>
    internal sealed record Capabilities(ToolStatus Unity, bool UnityProjectFree, ToolStatus SolidWorks, ToolStatus Chrome)
    {
        /// <summary>Nothing found: what a probe that has not been installed yet says, and the safe answer when a probe fails.</summary>
        public static Capabilities None { get; } = new(new ToolStatus(false, null, "Not checked."), false, new ToolStatus(false, null, "Not checked."), new ToolStatus(false, null, "Not checked."));

        public JsonObject ToJson() => new()
        {
            ["unity"] = Status(Unity),
            ["unity_project_free"] = UnityProjectFree,
            ["solidworks"] = Status(SolidWorks),
            ["chrome"] = Status(Chrome),
        };

        static JsonObject Status(ToolStatus s) => new() { ["found"] = s.Found, ["path"] = s.Path, ["note"] = s.Note };
    }

    /// <summary>
    /// The one answer to "does this computer have Unity / SOLIDWORKS / Chrome?", so nothing has to ask the person. The dialogs of the analyses offer the 3D video only when Unity is
    /// there, the ribbon hides the buttons that cannot work (RibbonVisibility), and the web app's Profile tab says what this computer can do (GET /capabilities).
    ///
    /// Revit-free and dependency-free on purpose: the real probes (they know where Unity Hub puts the Editor, how SOLIDWORKS registers itself) are installed by the add-in at
    /// start-up (CapabilityProbes), and Tools/ContractCheck and Tools/AddinCheck install their own. The answer is kept for a short while: looking is cheap but not free, and the
    /// ribbon and the web app ask often.
    /// </summary>
    internal static class SportifyCapabilities
    {
        /// <summary>How long an answer is reused before the computer is looked at again.</summary>
        public static TimeSpan MaxAge = TimeSpan.FromSeconds(30);

        static readonly object Gate = new();
        static Func<Capabilities> _probe = () => Capabilities.None;
        static Capabilities? _cached;
        static DateTime _cachedAt;

        /// <summary>Installs the function that looks at the computer (and forgets the last answer).</summary>
        public static void UseProbe(Func<Capabilities> probe)
        {
            lock (Gate) { _probe = probe; _cached = null; }
        }

        /// <summary>The computer as it is now; a probe that throws counts as "nothing found". <paramref name="refresh"/> looks again even if the last answer is recent.</summary>
        public static Capabilities Current(bool refresh = false)
        {
            lock (Gate)
            {
                if (!refresh && _cached != null && DateTime.UtcNow - _cachedAt < MaxAge) return _cached;
                try { _cached = _probe(); }
                catch (Exception ex) { _cached = Capabilities.None with { Unity = new ToolStatus(false, null, "The check failed: " + ex.Message) }; }
                _cachedAt = DateTime.UtcNow;
                return _cached;
            }
        }
    }
}
