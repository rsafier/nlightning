namespace NLightning.Domain.Channels.Acceptance;

/// <summary>
/// The gate every inbound <c>open_channel</c>/<c>open_channel2</c> passes before we commit anything to it (NL-1180):
/// with no decider registered the open goes on as usual; with deciders every one must accept (LND's
/// <c>ChainedAcceptor</c>), and their non-zero values must agree.
/// </summary>
/// <remarks>
/// The open handlers await it under the temporary channel's lock, on the peer's inbound loop: while a decider thinks,
/// only that peer's channel messages wait (pings and other peers do not), as in LND, where the funding manager waits
/// for its acceptors.
/// </remarks>
public interface IChannelOpenDecisionGate
{
    /// <summary>Whether any decider is registered (none: <see cref="DecideAsync"/> accepts at once).</summary>
    bool HasDeciders { get; }

    /// <summary>Registers a decider until the returned handle is disposed.</summary>
    IDisposable Register(IChannelOpenDecider decider);

    /// <summary>The combined decision on <paramref name="request"/>.</summary>
    Task<ChannelOpenDecision> DecideAsync(ChannelOpenRequest request, CancellationToken cancellationToken = default);
}