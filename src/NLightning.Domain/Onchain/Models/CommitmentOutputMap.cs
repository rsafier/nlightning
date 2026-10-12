namespace NLightning.Domain.Onchain.Models;

using Bitcoin.ValueObjects;
using Channels.Commitments;
using Crypto.ValueObjects;
using Enums;

/// <summary>
/// A commitment rebuilt from its spec and mapped output by output (BOLT 5 plan O2-T4): what each vout is, and which
/// committed HTLCs have no output (trimmed, B5-LCL-LO-04, B5-RMT-LO-03, B5-REV-RES-03).
/// </summary>
/// <param name="Case">Whose commitment it is.</param>
/// <param name="Number">Its commitment number.</param>
/// <param name="PerCommitmentPoint">The holder's per-commitment point: every key of the commitment derives from it.</param>
/// <param name="ExpectedTxId">The txid of the rebuilt (unsigned) commitment.</param>
/// <param name="OnChainTxId">The txid of the transaction that was mapped, when one was given.</param>
/// <param name="Outputs">One descriptor per mapped vout, in vout order.</param>
/// <param name="HtlcsWithoutOutput">Committed HTLCs that have no output in the mapped transaction.</param>
/// <param name="UnmappedVouts">Vouts of the given transaction that match no expected output (always empty when the txid
/// matched).</param>
public sealed record CommitmentOutputMap(
    CommitmentCase Case,
    ulong Number,
    CompactPubKey PerCommitmentPoint,
    TxId ExpectedTxId,
    TxId? OnChainTxId,
    IReadOnlyList<CommitmentOutputDescriptor> Outputs,
    IReadOnlyList<SpecHtlc> HtlcsWithoutOutput,
    IReadOnlyList<uint> UnmappedVouts)
{
    /// <summary>True when no transaction was given, or the given one has exactly the rebuilt txid.</summary>
    public bool TxIdMatched => OnChainTxId is null || OnChainTxId.Value == ExpectedTxId;

    /// <summary>The descriptor of <paramref name="vout"/>, or null.</summary>
    public CommitmentOutputDescriptor? GetOutput(uint vout) => Outputs.FirstOrDefault(o => o.Vout == vout);
}