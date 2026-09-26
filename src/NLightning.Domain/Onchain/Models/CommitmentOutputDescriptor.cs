namespace NLightning.Domain.Onchain.Models;

using Bitcoin.Transactions.Models;
using Channels.Commitments;
using Enums;

/// <summary>
/// One output of a commitment on chain, with what we know about it (BOLT 5 plan §3.3). Keys are never stored: they are
/// re-derived from the commitment's per-commitment point (<see cref="CommitmentOutputMap.PerCommitmentPoint"/>) and the
/// channel basepoints when a spend is signed.
/// </summary>
/// <param name="Vout">The output index in the commitment transaction on chain.</param>
/// <param name="AmountSat">The output amount in satoshis.</param>
/// <param name="Kind">What the output is to us.</param>
/// <param name="ScriptPubKey">The output script.</param>
/// <param name="WitnessScript">The P2WSH witness script; null for a P2WPKH output (non-anchor <c>to_remote</c>).</param>
/// <param name="Htlc">The HTLC of an HTLC output (direction from our point of view); null otherwise.</param>
/// <param name="CsvDelay">The relative delay of a delayed output (<c>to_self_delay</c> on <c>to_local</c>, 1 on
/// anchor HTLC and <c>to_remote</c> outputs, 16 on anchors); 0 when there is none.</param>
/// <param name="HasAnchors">Whether the commitment uses option_anchors (HTLC spends need <c>nSequence = 1</c>).</param>
/// <param name="SecondLevel">For an HTLC output of our own commitment: the HTLC-timeout (we offered it) or
/// HTLC-success (we received it) transaction that spends it, whose peer signature is in our stored commitment
/// signatures in output order.</param>
public sealed record CommitmentOutputDescriptor(
    uint Vout,
    ulong AmountSat,
    OutputDescriptorKind Kind,
    byte[] ScriptPubKey,
    byte[]? WitnessScript,
    SpecHtlc? Htlc,
    ushort CsvDelay,
    bool HasAnchors,
    HtlcTransactionModel? SecondLevel = null)
{
    /// <summary>
    /// True when we have something to do with the output (sweep, claim, penalize or watch it); false for the peer's
    /// balance, its anchor and unknown outputs.
    /// </summary>
    public bool IsOurs => Kind is not (OutputDescriptorKind.Unknown or OutputDescriptorKind.PeerOutput
                                    or OutputDescriptorKind.PeerAnchor);
}