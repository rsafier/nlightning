namespace NLightning.Domain.Channels.Acceptance;

/// <summary>
/// One external decider of inbound channel opens (an LND <c>ChannelAcceptor</c> stream, a JIT-LSP policy, NL-1180).
/// It owns its own timeout: an answer that does not come in time is a rejection.
/// </summary>
public interface IChannelOpenDecider
{
    /// <summary>Decides on <paramref name="request"/>; never throws for a slow or gone client (rejects instead).</summary>
    Task<ChannelOpenDecision> DecideAsync(ChannelOpenRequest request, CancellationToken cancellationToken);
}