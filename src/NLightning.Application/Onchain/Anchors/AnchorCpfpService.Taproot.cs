using NBitcoin;

namespace NLightning.Application.Onchain.Anchors;

using Domain.Bitcoin.Transactions.Extensions;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Simple taproot anchors (NL-966): P2TR outputs of their owner's key with one leaf <c>OP_16 OP_CSV</c>, keyed per
/// commitment (bolt-simple-taproot.md §Anchor Outputs): our <c>to_local_anchor</c> on our commitment to our
/// <c>local_delayedpubkey</c> at our point of that commitment, our <c>to_remote_anchor</c> on the peer's commitment to
/// our payment basepoint (<c>remotepubkey</c>), and the peer's two the other way round. The child spends ours by key
/// path (<see cref="Domain.Bitcoin.Interfaces.ILightningSigner.SignTaprootAnchorInput"/>), the sweep any of them by the
/// leaf. A P2WSH anchor channel keeps its funding-key anchors.
/// </summary>
public sealed partial class AnchorCpfpService
{
    private static bool IsTaprootChannel(ChannelModel channel) => channel.ChannelParams.CommitmentFormat.IsTaproot();

    /// <summary>
    /// Our anchor in a commitment: on our commitment (<paramref name="isPeers"/> false) of number
    /// <paramref name="ourCommitmentNumber"/> (null: the snapshot's latest local commitment), else on the peer's; null when
    /// it has none or its key cannot be derived.
    /// </summary>
    private AnchorOutpoint? FindOurAnchor(ChannelModel channel, TxId commitmentTxId, byte[] commitmentTransaction,
                                          bool isPeers, ulong? ourCommitmentNumber)
    {
        if (!IsTaprootChannel(channel))
            return _builder.FindAnchorOutput(commitmentTransaction, channel.LocalFundingPubKey) is { } vout
                       ? new AnchorOutpoint(commitmentTxId, vout, channel.LocalFundingPubKey)
                       : null;

        if (isPeers)
        {
            var paymentBasepoint = channel.LocalKeySet.PaymentCompactBasepoint;
            return _builder.FindTaprootAnchorOutput(commitmentTransaction, paymentBasepoint) is { } peerVout
                       ? new AnchorOutpoint(commitmentTxId, peerVout, paymentBasepoint, true)
                       : null;
        }

        if ((ourCommitmentNumber ?? channel.Commitments?.LocalCommit.Number) is not { } number
         || DeriveOurDelayedKey(channel, number) is not var (delayedPubKey, point))
            return null;

        return _builder.FindTaprootAnchorOutput(commitmentTransaction, delayedPubKey) is { } ourVout
                   ? new AnchorOutpoint(commitmentTxId, ourVout, delayedPubKey, true, point)
                   : null;
    }

    /// <summary>
    /// The peer's anchor in a commitment, for the sweep anyone may make after 16 blocks: on our commitment keyed to the
    /// peer's payment basepoint; on the peer's (simple taproot) keyed to its <c>local_delayedpubkey</c>, found for the
    /// per-commitment points we hold (its current and next commitment, and <paramref name="extraPoint"/>: the point of
    /// a revoked commitment, from the secret the peer revealed, NL-1050). A future commitment's point is never known
    /// (BOLT 2: <c>my_current_per_commitment_point</c> is ignored), so its anchor stays the peer's.
    /// </summary>
    private AnchorOutpoint? FindPeerAnchor(ChannelModel channel, TxId commitmentTxId, byte[] commitmentTransaction,
                                           bool isPeers, CompactPubKey? extraPoint = null)
    {
        if (!IsTaprootChannel(channel))
            return channel.RemoteFundingPubKey is { } theirs
                && _builder.FindAnchorOutput(commitmentTransaction, theirs) is { } vout
                       ? new AnchorOutpoint(commitmentTxId, vout, theirs)
                       : null;

        if (channel.RemoteKeySet is null)
            return null;

        if (!isPeers)
        {
            var remotePaymentBasepoint = channel.RemoteKeySet.PaymentCompactBasepoint;
            return _builder.FindTaprootAnchorOutput(commitmentTransaction, remotePaymentBasepoint) is { } ourVout
                       ? new AnchorOutpoint(commitmentTxId, ourVout, remotePaymentBasepoint, true)
                       : null;
        }

        if (_keyDerivation is null || channel.Commitments is not { } commitments)
            return null;

        var points = new List<CompactPubKey> { commitments.RemoteCommit.PerCommitmentPoint };
        if (commitments.RemoteNextCommit is { } next)
            points.Add(next.Commit.PerCommitmentPoint);
        if (extraPoint is { } extra)
            points.Add(extra);
        foreach (var point in points)
        {
            try
            {
                var keys = _keyDerivation.DeriveRemoteCommitmentKeys(
                    _lightningSigner.GetChannelBasepoints(channel.LocalKeySet.KeyIndex), RemoteBasepoints(channel),
                    point);
                if (_builder.FindTaprootAnchorOutput(commitmentTransaction, keys.LocalDelayedPubKey) is { } vout)
                    return new AnchorOutpoint(commitmentTxId, vout, keys.LocalDelayedPubKey, true);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // A point that derives no key is not this commitment's
            }
        }

        return null;
    }

    /// <summary>
    /// The peer's per-commitment point of the revoked commitment of <paramref name="close"/> on a simple taproot channel
    /// (<c>secret * G</c>, the secret from our copy of the peer's shachain, as <c>RevokedCommitDataSource</c> reads it),
    /// so the sweep finds the peer's anchor too (NL-1050); null for any other close or when the secret is not held.
    /// </summary>
    private async Task<CompactPubKey?> GetRevokedPeerPointAsync(ChannelModel channel, IUnitOfWork unitOfWork,
                                                                ChannelCloseModel close)
    {
        if (close.Kind != ChannelCloseKind.RevokedCommitment || !IsTaprootChannel(channel)
         || close.CommitmentNumber is not { } number || _secretStorageServiceFactory is null)
            return null;

        try
        {
            var entries = await unitOfWork.RemoteShachainDbRepository.GetByChannelIdAsync(close.ChannelId);
            using var shachain = _secretStorageServiceFactory.CreatePerCommitmentStorage();
            shachain.Load(entries);
            var secret = shachain.DeriveOldSecret(PerCommitmentIndex.From(number));
            using var key = new Key((byte[])secret);
            return key.PubKey.ToBytes();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
        {
            LogOnce($"{channel.ChannelId}:{number}:revoked-point", e,
                    "The peer's secret of revoked commitment {Number} of channel {ChannelId} is not held; its anchor is "
                  + "not swept", number, channel.ChannelId);
            return null;
        }
    }

    /// <summary>
    /// Our <c>local_delayedpubkey</c> of our commitment <paramref name="number"/> and our point of it, or null when the
    /// keys cannot be derived (no key derivation service registered).
    /// </summary>
    private (CompactPubKey DelayedPubKey, CompactPubKey Point)? DeriveOurDelayedKey(ChannelModel channel, ulong number)
    {
        if (channel.RemoteKeySet is null)
            return null;

        if (_keyDerivation is null)
        {
            LogOnce($"{channel.ChannelId}:taproot-keys",
                    "No commitment key derivation is registered: the taproot anchors of channel {ChannelId} cannot be "
                  + "found", channel.ChannelId);
            return null;
        }

        var keys = _keyDerivation.DeriveLocalCommitmentKeys(
            channel.LocalKeySet.KeyIndex, _lightningSigner.GetChannelBasepoints(channel.LocalKeySet.KeyIndex),
            RemoteBasepoints(channel), number);
        return (keys.LocalDelayedPubKey, keys.PerCommitmentPoint);
    }

    private static ChannelBasepoints RemoteBasepoints(ChannelModel channel)
    {
        var remote = channel.RemoteKeySet ?? throw new InvalidOperationException("The peer's keys are not known");
        return new ChannelBasepoints(remote.FundingCompactPubKey, remote.RevocationCompactBasepoint,
                                     remote.PaymentCompactBasepoint, remote.DelayedPaymentCompactBasepoint,
                                     remote.HtlcCompactBasepoint);
    }
}