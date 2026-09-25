using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Channels.Models;

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
    /// selects our per-commitment point. For the remote side the per-commitment point is still taken from
    /// <c>RemoteKeySet.CurrentPerCommitmentCompactPoint</c>.
    /// </param>
    /// <returns>A domain model of the commitment transaction.</returns>
    CommitmentTransactionModel CreateCommitmentTransactionModel(ChannelModel channel, CommitmentSide side,
                                                                ulong commitmentNumber);
}