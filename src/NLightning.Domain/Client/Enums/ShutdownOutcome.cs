namespace NLightning.Domain.Client.Enums;

/// <summary>
/// How a <c>shutdown</c> ended (<c>ClientCommand.Shutdown</c>, NL-592); the response of an accepted shutdown carries
/// it. A refused shutdown (HTLCs in flight without <c>--force</c>/<c>--wait</c>, or one already running) stays an
/// error envelope, as in the first pass (NL-591).
/// </summary>
public enum ShutdownOutcome : byte
{
    /// <summary>The node stopped: nothing was in flight, or <c>--wait</c> drained to idle.</summary>
    Stopped = 0,

    /// <summary>
    /// <c>--wait</c> ran out of time: the drain is over (the gate is open again) and the node keeps running; the
    /// response reports what is still busy.
    /// </summary>
    TimedOut = 1,

    /// <summary>
    /// <c>--force</c> stopped the node although HTLCs or negotiations were (or had been) in flight; the response
    /// reports them and the nearest HTLC deadline.
    /// </summary>
    Forced = 2
}