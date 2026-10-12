namespace NLightning.Domain.Channels.Splicing.Interfaces;

/// <summary>
/// Follows the depth of the channels' splice transactions (splicing plan SP2-0; lane SP2-B, SP2-B-T1; reorgs SP2-C-T4):
/// <c>splice_locked</c> at the channel's <c>minimum_depth</c> (D8), the announcement depth of a locked splice of a
/// public channel (SP-G-01) and a splice transaction leaving the chain. Implemented by the Application
/// <c>SpliceDepthWatcher</c> (a singleton subscribed to the chain monitor).
/// </summary>
public interface ISpliceDepthWatcher
{
    /// <summary>
    /// Hands over every pending splice whose depth was reached while nothing listened (host startup, after the chain
    /// monitor and the peer manager started). Idempotent; returns how many were handed over.
    /// </summary>
    Task<int> CatchUpAsync(CancellationToken cancellationToken = default);

    /// <summary>Completes when no confirmation handed over by the watcher is still being handled (tests).</summary>
    Task WhenIdleAsync();
}