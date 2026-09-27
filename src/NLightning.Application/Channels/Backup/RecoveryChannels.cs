namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Models;

/// <summary>
/// Recovery-only channels: the records <c>restorechanbackup</c> makes from a static channel backup on a node that lost
/// its channel database (LND's "restored" channels). Such a channel is persisted <see cref="ChannelState.Failed"/> with
/// <see cref="ChannelModel.DataLossDetected"/> set, without a commitment snapshot or any signature, so nothing ever
/// signs or broadcasts a commitment for it (invariant I12: the signer refuses, the failure service skips it and the
/// executor never falls back to our commitment). On every connection to the peer it sends the BOLT 2 "we lost data"
/// <c>channel_reestablish</c> (<see cref="CreateDataLossReestablish"/>), then its stored <c>error</c>, so the peer fails
/// the channel and broadcasts its latest commitment; the on-chain watcher classifies that commitment as one we cannot
/// rebuild and the remote resolver's data-loss path sweeps our <c>to_remote</c>.
/// </summary>
/// <remarks>
/// No schema change: "recovery channel" is <see cref="IsRecoveryChannel"/>, a shape no other channel has (a Failed
/// channel that went through funding carries the peer's signature of our first commitment).
/// </remarks>
public static class RecoveryChannels
{
    /// <summary>The <c>error</c> text the peer gets for a recovery channel.</summary>
    public const string PeerErrorMessage =
        "we lost our channel state (restored from a static channel backup): please force close the channel";

    /// <summary>
    /// The per-commitment index stored as the peer's "current" point of a recovery channel: index 0 is commitment
    /// 2^48 - 1, which never exists, so nothing mistakes the placeholder point for the peer's real current point (the
    /// on-chain watcher then has no peer commitment to rebuild and treats the peer's broadcast as a data loss).
    /// </summary>
    public const ulong UnknownPerCommitmentIndex = 0;

    /// <summary>
    /// The BOLT 2 feerate floor (253 sat/kw), stored as the feerate of a recovery channel: the real one is unknown and
    /// never used (no commitment is built for such a channel).
    /// </summary>
    public static readonly LightningMoney PlaceholderFeeRatePerKw = LightningMoney.Satoshis(253);

    /// <summary>
    /// True for a channel made by <c>restorechanbackup</c>: Failed with data loss, no commitment snapshot and no
    /// signature exchanged.
    /// </summary>
    public static bool IsRecoveryChannel(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel is
        {
            State: ChannelState.Failed, DataLossDetected: true, Commitments: null, LastReceivedSignature: null,
            LastSentSignature: null
        };
    }

    /// <summary>
    /// The recovery channel of <paramref name="entry"/>: our keys from <paramref name="localBasepoints"/> (re-derived
    /// from the entry's key index, never stored in the backup), the peer's basepoints, parameters, funding outpoint and
    /// short channel id from the backup. Balances are unknown: all of it is shown on the peer's side.
    /// </summary>
    /// <param name="entry">The backed-up channel.</param>
    /// <param name="localBasepoints">Our basepoints of <see cref="ChannelBackupEntry.KeyIndex"/>; they must match the
    /// entry's funding key and payment basepoint.</param>
    /// <param name="sha256">For the commitment number obscuring factor.</param>
    /// <exception cref="ArgumentException">The basepoints are not the ones the entry recorded.</exception>
    public static ChannelModel Create(ChannelBackupEntry entry, ChannelBasepoints localBasepoints, ISha256 sha256)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(sha256);
        if (localBasepoints.FundingPubKey != entry.LocalFundingPubKey
         || localBasepoints.PaymentBasepoint != entry.LocalPaymentBasepoint)
            throw new ArgumentException($"Key index {entry.KeyIndex} does not derive the keys of channel "
                                      + $"{entry.ChannelId}", nameof(localBasepoints));

        // Our per-commitment point is never used: nothing is signed for this channel
        var localKeySet = new ChannelKeySetModel(entry.KeyIndex, localBasepoints.FundingPubKey,
                                                 localBasepoints.RevocationBasepoint,
                                                 localBasepoints.PaymentBasepoint,
                                                 localBasepoints.DelayedPaymentBasepoint,
                                                 localBasepoints.HtlcBasepoint, localBasepoints.PaymentBasepoint);
        var remoteKeySet = new ChannelKeySetModel(0, entry.RemoteFundingPubKey, entry.RemoteRevocationBasepoint,
                                                  entry.RemotePaymentBasepoint, entry.RemoteDelayedPaymentBasepoint,
                                                  entry.RemoteHtlcBasepoint, entry.RemotePaymentBasepoint,
                                                  UnknownPerCommitmentIndex);

        var channelParams = new ChannelParams(entry.Local.ToChannelParty(), entry.Remote.ToChannelParty(),
                                              PlaceholderFeeRatePerKw, entry.MinimumDepth, entry.OptionAnchorOutputs,
                                              entry.UseScidAlias)
        {
            AnnounceChannel = entry.AnnounceChannel,
            HasInferredParams = entry.HasInferredParams
        };

        var capacity = LightningMoney.Satoshis(entry.CapacitySat);
        var fundingOutput = new FundingOutputInfo(capacity, localKeySet.FundingCompactPubKey,
                                                  remoteKeySet.FundingCompactPubKey, entry.FundingTxId,
                                                  entry.FundingOutputIndex);

        // BOLT 3: the obscuring factor is SHA256(opener payment_basepoint || accepter payment_basepoint)
        var (opener, accepter) = entry.IsInitiator
                                     ? (localKeySet.PaymentCompactBasepoint, remoteKeySet.PaymentCompactBasepoint)
                                     : (remoteKeySet.PaymentCompactBasepoint, localKeySet.PaymentCompactBasepoint);
        var commitmentNumber = new CommitmentNumber(opener, accepter, sha256);

        var channel = new ChannelModel(channelParams, entry.ChannelId, commitmentNumber, fundingOutput,
                                       entry.IsInitiator, null, null, LightningMoney.Zero, localKeySet, 0, 0, capacity,
                                       remoteKeySet, 0, entry.RemoteNodeId, 0, ChannelState.Failed, entry.Version)
        {
            FundingCreatedAtBlockHeight = entry.FundingHeight
        };
        if (entry.ShortChannelId is { } shortChannelId)
            channel.ShortChannelId = shortChannelId;

        channel.MarkDataLossDetected();
        return channel;
    }

    /// <summary>
    /// The BOLT 2 "we lost data" <c>channel_reestablish</c>: <c>next_commitment_number</c> 0 (the receiver MUST fail
    /// the channel and broadcast its latest commitment, B2-RE-14), <c>next_revocation_number</c> 0 and an all-zero
    /// <c>your_last_per_commitment_secret</c> (we know no secret of the peer). <paramref name="currentPoint"/> fills
    /// <c>my_current_per_commitment_point</c>; our real current point is unknown, and a <c>static_remotekey</c> or
    /// anchors <c>to_remote</c> does not depend on it.
    /// </summary>
    public static ChannelReestablishMessage CreateDataLossReestablish(IMessageFactory messageFactory,
                                                                      ChannelId channelId,
                                                                      CompactPubKey currentPoint)
    {
        ArgumentNullException.ThrowIfNull(messageFactory);
        return messageFactory.CreateChannelReestablishMessage(channelId, 0, 0, new byte[32], currentPoint);
    }
}