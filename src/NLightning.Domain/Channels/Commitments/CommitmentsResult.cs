namespace NLightning.Domain.Channels.Commitments;

/// <summary>
/// The outcome of one <see cref="ChannelCommitments"/> operation.
/// </summary>
/// <remarks>
/// The caller persists <see cref="Transition"/> in one save, then swaps in <see cref="Next"/> (invariant I2), then sends
/// <see cref="Outbound"/> in order (invariant I1). On failure the old snapshot stays valid and nothing is sent.
/// </remarks>
/// <param name="Next">The new immutable snapshot.</param>
/// <param name="Outbound">Messages to send after the save, in order.</param>
/// <param name="Transition">What changed.</param>
public sealed record CommitmentsResult(
    ChannelCommitments Next,
    IReadOnlyList<CommitmentOutbound> Outbound,
    ChannelTransition Transition);