namespace NLightning.Domain.Protocol.Onion.Models;

/// <summary>
/// A failure an intermediate trampoline node received for the route it built to the next trampoline node, after it
/// removed every layer of that route.
/// </summary>
/// <remarks>
/// <para>
/// When an outer hop's <c>um</c> key matched (<see cref="Failure"/> set), that hop sent the failure to this node: a
/// node before the next trampoline node, or the next trampoline node itself answering for its outer layer only (the
/// last index). This node may then replace it with its own failure for the origin (BOLT 4: "they may replace it with
/// their own error").
/// </para>
/// <para>
/// Otherwise the failure is encrypted for an earlier node by the next trampoline node's trampoline layer:
/// <see cref="UnwrappedPacket"/> is that packet, which this node must return with both of its layers
/// (<see cref="Interfaces.ITrampolineFailureOnionService.WrapTrampolineErrorPacket"/>).
/// </para>
/// </remarks>
public sealed class TrampolineDownstreamFailure
{
    /// <summary>
    /// The failure when an outer hop of this node's route authenticated it (hop index in that route), else
    /// <c>null</c>.
    /// </summary>
    public DecryptedFailure? Failure { get; }

    /// <summary>
    /// The packet with every layer of this node's route removed, to re-wrap with both of this node's layers; empty when
    /// <see cref="Failure"/> is set.
    /// </summary>
    public ReadOnlyMemory<byte> UnwrappedPacket { get; }

    /// <summary>
    /// Whether the failure comes from the next trampoline node's trampoline layer (or a node beyond it), so it must be
    /// re-wrapped rather than replaced.
    /// </summary>
    public bool MustRewrap => Failure is null;

    private TrampolineDownstreamFailure(DecryptedFailure? failure, ReadOnlyMemory<byte> unwrappedPacket)
    {
        Failure = failure;
        UnwrappedPacket = unwrappedPacket;
    }

    /// <summary>
    /// A failure an outer hop of this node's route sent.
    /// </summary>
    public static TrampolineDownstreamFailure FromOuterHop(DecryptedFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new TrampolineDownstreamFailure(failure, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>
    /// A failure to re-wrap: the packet with this node's route layers removed.
    /// </summary>
    public static TrampolineDownstreamFailure ToRewrap(ReadOnlyMemory<byte> unwrappedPacket) =>
        new(null, unwrappedPacket);
}