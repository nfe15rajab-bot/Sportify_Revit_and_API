using System.Diagnostics;

namespace Sportify.Mechanical;

/// <summary>
/// Stops a run whose SOLIDWORKS has gone quiet. SOLIDWORKS is driven through COM from one thread; when it opens a window of its own (a dialog nobody can see, because the session
/// is hidden) every call blocks for ever, and nothing in the process can tell. A second thread watches the heartbeat the run gives at every step (Progress and CheckCancel in
/// UnitAssembly): when it stops for longer than the limit, or the whole run takes longer than it should, the SOLIDWORKS this run started is killed and the tool ends with exit code 4
/// and a "STALLED ..." line, so Revit's progress window (and anything else waiting on the tool) gets an answer instead of waiting for ever. A SOLIDWORKS the person already had
/// open is never killed: only its name is in the message.
/// </summary>
internal static class Watchdog
{
    public const int ExitStalled = 4;

    static long _last = Stopwatch.GetTimestamp();
    static string _what = "starting";
    static int _beats;
    static volatile bool _stop;

    /// <param name="firstBeat">How long SOLIDWORKS may take to start (its first start can take minutes) before the first step is reported.</param>
    /// <param name="stall">How long any later step may take without a sign of life.</param>
    /// <param name="overall">How long the whole run may take.</param>
    public static void Start(TimeSpan firstBeat, TimeSpan stall, TimeSpan overall)
    {
        _last = Stopwatch.GetTimestamp();
        _what = "starting SOLIDWORKS";
        _beats = 0;
        _stop = false;
        var began = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            while (!_stop)
            {
                Thread.Sleep(250);
                if (_stop) return;
                var idle = Stopwatch.GetElapsedTime(_last);
                var limit = Volatile.Read(ref _beats) == 0 ? firstBeat : stall;
                string? why = null;
                if (began.Elapsed > overall) why = "the run took longer than " + Minutes(overall) + " overall (last step: " + _what + ")";
                else if (idle > limit)
                    why = "SOLIDWORKS did not answer for " + Minutes(idle) + " (last step: " + _what + "). It is usually waiting on a window of its own that a hidden session cannot show";
                if (why != null) Stall(why);
            }
        }) { IsBackground = true, Name = "sportify-watchdog" };
        thread.Start();
    }

    /// <summary>A sign of life: the run is still getting answers.</summary>
    public static void Beat(string? what = null)
    {
        Volatile.Write(ref _last, Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _beats);
        if (what != null) _what = what;
    }

    public static void Stop() => _stop = true;

    static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? (int)t.TotalMinutes + " min " + t.Seconds + " s" : t.TotalSeconds.ToString("0.#") + " s";

    static void Stall(string why)
    {
        _stop = true;
        try { Console.WriteLine("STALLED " + why); Console.Out.Flush(); Console.Error.WriteLine("STALLED " + why); Console.Error.Flush(); } catch (Exception) { /* the console may be gone */ }
        SolidWorksSession.KillStartedByThisRun();
        Thread.Sleep(1500);      // let the killed SOLIDWORKS release the blocked call, so the main thread can unwind if it is going to
        Environment.Exit(ExitStalled);
    }
}
