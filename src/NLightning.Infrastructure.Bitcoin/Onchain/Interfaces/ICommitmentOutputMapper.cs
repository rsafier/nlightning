namespace NLightning.Infrastructure.Bitcoin.Onchain.Interfaces;

using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// Rebuilds a commitment from its spec and maps its outputs to descriptors (BOLT 5 plan O2-T4).
/// </summary>
public interface ICommitmentOutputMapper
{
    /// <summary>
    /// Rebuilds the <paramref name="commitmentCase"/> commitment <paramref name="number"/> of the channel from
    /// <paramref name="spec"/> (the same factory and builder that signed it) and maps it.
    /// </summary>
    /// <param name="channel">The channel (static data: keys, funding output, obscuring factor, params).</param>
    /// <param name="spec">The commitment's balances, feerate and HTLCs, from our point of view.</param>
    /// <param name="commitmentCase">Whose commitment it is.</param>
    /// <param name="number">The commitment number.</param>
    /// <param name="remotePerCommitmentPoint">The peer's per-commitment point for a <see cref="CommitmentCase.Remote"/>
    /// or <see cref="CommitmentCase.Revoked"/> commitment (for a revoked one, <c>secret * G</c> from the shachain); null
    /// for <see cref="CommitmentCase.Local"/>.</param>
    /// <param name="onChain">The transaction on chain. When its txid differs from the rebuilt one, its outputs are
    /// mapped by script.</param>
    CommitmentOutputMap Map(ChannelModel channel, CommitmentTxSpec spec, CommitmentCase commitmentCase, ulong number,
                            CompactPubKey? remotePerCommitmentPoint, ChainTx? onChain = null);

    /// <summary>
    /// Maps an already built commitment model (from <c>ICommitmentTransactionModelFactory</c>).
    /// </summary>
    /// <param name="model">The commitment model.</param>
    /// <param name="committedHtlcs">Every HTLC the commitment commits to, trimmed ones included.</param>
    /// <param name="commitmentCase">Whose commitment it is (must agree with the model's holder).</param>
    /// <param name="onChain">The transaction on chain, if any.</param>
    CommitmentOutputMap Map(CommitmentTransactionModel model, IEnumerable<Htlc> committedHtlcs,
                            CommitmentCase commitmentCase, ChainTx? onChain = null);

    /// <summary>
    /// Finds our <c>to_remote</c> outputs in a peer commitment we cannot rebuild (data loss, B5-RMT-03): with
    /// static_remotekey they pay our <c>payment_basepoint</c> whatever the commitment number.
    /// </summary>
    /// <param name="onChain">The peer commitment on chain.</param>
    /// <param name="ourPaymentBasepoint">Our <c>payment_basepoint</c>.</param>
    /// <param name="hasAnchors">Whether the channel uses option_anchors (<c>to_remote</c> is then P2WSH with
    /// <c>1 OP_CSV</c>).</param>
    IReadOnlyList<CommitmentOutputDescriptor> FindPaymentToRemote(ChainTx onChain, CompactPubKey ourPaymentBasepoint,
                                                                  bool hasAnchors);
}