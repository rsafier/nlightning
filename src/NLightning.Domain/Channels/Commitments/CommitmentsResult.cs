namespace NLightning.Domain.Channels.Commitments;

using Events;

/// <summary>
/// The outcome of one <see cref="ChannelCommitments"/> operation.
/// </summary>
/// <remarks>
/// The caller persists <see cref="Transition"/> in one save, then swaps in <see cref="Next"/> (invariant I2), then sends
/// <see cref="Outbound"/> in order (invariant I1), then raises <see cref="Events"/> in order (invariant I8). On failure
/// the old snapshot stays valid, nothing is sent and nothing is raised. Events lost to a crash after the save are
/// re-derived on startup with <see cref="ChannelDomainEvents.DerivePending(ChannelCommitments, IEnumerable{HtlcRecord})"/>.
/// </remarks>
/// <param name="Next">The new immutable snapshot.</param>
/// <param name="Outbound">Messages to send after the save, in order.</param>
/// <param name="Transition">What changed.</param>
/// <param name="Events">Domain events to raise after the save, in order (N4-T4).</param>
public sealed record CommitmentsResult(
    ChannelCommitments Next,
    IReadOnlyList<CommitmentOutbound> Outbound,
    ChannelTransition Transition,
    IReadOnlyList<IChannelDomainEvent> Events);