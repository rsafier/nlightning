using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Crypto.ValueObjects;

namespace NLightning.Domain.Bitcoin.Transactions.Interfaces;

/// <summary>
/// Interface for the factory that creates commitment transactions.
/// </summary>
public interface ICommitmentTransactionModelFactory
{
    /// <summary>
    /// Creates a domain model of a commitment transaction for the specified channel.
    /// </summary>
    /// <param name="channel">The channel for which to create the commitment transaction.</param>
    /// <param name="side">Whether to create a local or remote commitment transaction.</param>
    /// <param name="commitmentNumber">
    /// The commitment number of the transaction: normally <see cref="ChannelModel.LocalCommitmentNumber"/> for
    /// <see cref="CommitmentSide.Local"/> and <see cref="ChannelModel.RemoteCommitmentNumber"/> (or the next one while
    /// signing) for <see cref="CommitmentSide.Remote"/>. It sets the obscured locktime/sequence and, for the local side,
    /// selects our per-commitment point. For the remote side the per-commitment point is taken from
    /// <c>RemoteKeySet.CurrentPerCommitmentCompactPoint</c>. The balances and HTLCs come from
    /// <see cref="CommitmentTxSpec.FromChannel"/> (gross channel balances made net).
    /// </param>
    /// <returns>A domain model of the commitment transaction.</returns>
    CommitmentTransactionModel CreateCommitmentTransactionModel(ChannelModel channel, CommitmentSide side,
                                                                ulong commitmentNumber);

    /// <summary>
    /// Creates a commitment transaction from an explicit <see cref="CommitmentTxSpec"/> (BOLT 3 "Commitment Transaction
    /// Construction"). The channel supplies only static data: keys and basepoints, funding output, obscuring factor,
    /// dust limits, to_self_delay, option_anchors and who the funder is.
    /// </summary>
    /// <param name="channel">The channel (static data only; its balances and HTLCs are ignored).</param>
    /// <param name="spec">Net balances, feerate and HTLCs from the local node's point of view.</param>
    /// <param name="side">Whose commitment to build.</param>
    /// <param name="commitmentNumber">The holder's commitment number (48 bits).</param>
    /// <param name="remotePerCommitmentPoint">
    /// Required for <see cref="CommitmentSide.Remote"/>: the remote per-commitment point for
    /// <paramref name="commitmentNumber"/>. Must be null for <see cref="CommitmentSide.Local"/>, whose point comes from
    /// the signer.
    /// </param>
    CommitmentTransactionModel CreateCommitmentTransactionModel(ChannelModel channel, CommitmentTxSpec spec,
                                                                CommitmentSide side, ulong commitmentNumber,
                                                                CompactPubKey? remotePerCommitmentPoint = null);
}