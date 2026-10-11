namespace NLightning.Domain.Protocol.InteractiveTx;

using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// Who sends <c>tx_signatures</c> first (IT-SIG-01, IT1-T3).
/// </summary>
/// <remarks>
/// BOLT 2 (tx_signatures sender): "if it has the lowest total satoshis contributed, as defined by total
/// <c>tx_add_input</c> values, or both peers have contributed equal amounts but it has the lowest <c>node_id</c>
/// (sorted lexicographically): MUST transmit their <c>tx_signatures</c> first". BOLT 2 splicing (commitment_signed
/// receiver): "since the initiator sends <c>tx_add_input</c> for the shared input (corresponding to the previous channel
/// output), 100% of the previous channel capacity is attributed to the initiator when computing who must send
/// <c>tx_signatures</c> first (instead of using each node's previous balance)".
/// </remarks>
public static class TxSignaturesOrder
{
    /// <summary>
    /// The total <c>tx_add_input</c> value each side contributed, in millisatoshis: every input counts for the side
    /// that added it, the shared input included (only the initiator adds it, so 100 % of it counts for the initiator).
    /// </summary>
    public static (ulong LocalMsat, ulong RemoteMsat) ContributedTotals(IReadOnlyList<InteractiveTxInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        ulong local = 0, remote = 0;
        foreach (var input in inputs)
        {
            if (input.AddedBy == InteractiveTxParty.Local)
                local = checked(local + input.Amount.MilliSatoshi);
            else
                remote = checked(remote + input.Amount.MilliSatoshi);
        }

        return (local, remote);
    }

    /// <summary>
    /// Whether we transmit our <c>tx_signatures</c> first: we contributed less, or the same and our node id sorts
    /// lower.
    /// </summary>
    /// <exception cref="ArgumentException">Both node ids are the same.</exception>
    public static bool LocalSendsFirst(ulong localContributedMsat, ulong remoteContributedMsat,
                                       CompactPubKey localNodeId, CompactPubKey remoteNodeId)
    {
        if (localContributedMsat != remoteContributedMsat)
            return localContributedMsat < remoteContributedMsat;

        var comparison = ((ReadOnlySpan<byte>)localNodeId).SequenceCompareTo(remoteNodeId);
        if (comparison == 0)
            throw new ArgumentException("Both sides have the same node id.", nameof(remoteNodeId));

        return comparison < 0;
    }

    /// <summary>
    /// <see cref="LocalSendsFirst(ulong, ulong, CompactPubKey, CompactPubKey)"/> over the negotiated inputs.
    /// </summary>
    public static bool LocalSendsFirst(IReadOnlyList<InteractiveTxInput> inputs, CompactPubKey localNodeId,
                                       CompactPubKey remoteNodeId)
    {
        var (local, remote) = ContributedTotals(inputs);
        return LocalSendsFirst(local, remote, localNodeId, remoteNodeId);
    }
}