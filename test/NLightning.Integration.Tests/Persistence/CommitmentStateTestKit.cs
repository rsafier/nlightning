using System.Buffers.Binary;
using System.Security.Cryptography;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// Drives two commitment engines against each other with fake crypto ports (like the Domain simulator) and hands out
/// every transition of "our" side, for the persistence tests (BOLT2 plan N5-T2).
/// </summary>
/// <remarks>
/// Messages are delivered at once, except the peer's <c>revoke_and_ack</c> for our <c>commitment_signed</c>, which
/// waits for its own step so that states with an unacked remote commitment (<c>RemoteNextCommit</c>) are persisted
/// and reloaded too. The peer never signs while that RAA is pending, which keeps the wire order.
/// </remarks>
internal sealed class CommitmentDanceDriver
{
    public const byte UsTag = 0xA1;
    public const byte PeerTag = 0xB0;

    private readonly Random _rng;
    private readonly FakeRevocationVerifier _revocationVerifier = new();
    private readonly FakeCommitmentVerifier _verifier = new();
    private readonly FakeSha256 _sha256 = new();
    private readonly Dictionary<(HtlcDirection, ulong), byte> _preimageTags = [];
    private OutboundRevokeAndAck? _pendingRevokeForUs;
    private byte _nextPreimageTag = 1;

    public ChannelCommitments Us { get; private set; }
    public ChannelCommitments Peer { get; private set; }

    public CommitmentDanceDriver(ChannelId channelId, CommitmentParams usParams, ulong usBalanceMsat,
                                 ulong peerBalanceMsat, uint feeratePerKw = 2_500, ulong usCommitmentNumber = 0,
                                 ulong peerCommitmentNumber = 0, int seed = 1)
    {
        _rng = new Random(seed);
        var peerParams = new CommitmentParams(!usParams.LocalIsFunder, usParams.FundingSatoshis,
                                              usParams.OptionAnchors, usParams.Remote, usParams.Local);
        Us = ChannelCommitments.Create(channelId, usParams, usBalanceMsat, peerBalanceMsat, feeratePerKw,
                                       Point(PeerTag, peerCommitmentNumber), Point(PeerTag, peerCommitmentNumber + 1),
                                       Signatures(0, 0), usCommitmentNumber, peerCommitmentNumber);
        Peer = ChannelCommitments.Create(channelId, peerParams, peerBalanceMsat, usBalanceMsat, feeratePerKw,
                                         Point(UsTag, usCommitmentNumber), Point(UsTag, usCommitmentNumber + 1),
                                         Signatures(0, 0), peerCommitmentNumber, usCommitmentNumber);
    }

    /// <summary>Wire-like bytes standing in for the diff we sent with our last <c>commitment_signed</c>.</summary>
    public static byte[] DiffFor(ulong remoteCommitmentNumber) =>
        [0x00, 0x84, .. BitConverter.GetBytes(remoteCommitmentNumber), 0xC5];

    /// <summary>
    /// Runs random steps until one changes our side; returns that transition. Local refusals and steps that are not
    /// possible right now are skipped.
    /// </summary>
    public CommitmentsResult NextTransition()
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var result = _rng.Next(100) switch
            {
                < 18 => TryUsAdd(),
                < 30 => TryPeerAdd(),
                < 38 => TryUsRemove(),
                < 46 => TryPeerRemove(),
                < 50 => TryUsFee(),
                < 68 => TryUsCommit(),
                < 82 => TryPeerCommit(),
                _ => TryDeliverRevokeToUs()
            };
            if (result is not null)
                return result;
        }

        throw new InvalidOperationException("The dance is stuck");
    }

    /// <summary>Our <c>update_add_htlc</c> (the peer receives it at once).</summary>
    public CommitmentsResult? TryUsAdd(ulong? amountMsat = null)
    {
        var tag = _nextPreimageTag;
        CommitmentsResult result;
        try
        {
            result = Us.SendAdd(amountMsat ?? (ulong)_rng.NextInt64(1_000, 30_000_000), PaymentHash(tag),
                                (uint)_rng.Next(500, 700), Onion(tag), _rng.Next(4) == 0 ? Point(0x77, tag) : null);
        }
        catch (CommitmentRefusedException)
        {
            return null;
        }

        var add = result.Outbound.OfType<OutboundAddHtlc>().Single().Htlc;
        Peer = Peer.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket,
                               add.PathKey).Next;
        _preimageTags[(HtlcDirection.Outgoing, add.Id)] = tag;
        _nextPreimageTag = (byte)(_nextPreimageTag % 250 + 1);
        Us = result.Next;
        return result;
    }

    /// <summary>The peer's <c>update_add_htlc</c> (we receive it).</summary>
    public CommitmentsResult? TryPeerAdd()
    {
        // A pending fee update of ours could cross the peer's add (the Domain simulator's documented failure)
        if (Us.FeeUpdates.Count > 1)
            return null;

        var tag = _nextPreimageTag;
        CommitmentsResult sent;
        try
        {
            sent = Peer.SendAdd((ulong)_rng.NextInt64(1_000, 30_000_000), PaymentHash(tag), (uint)_rng.Next(500, 700),
                                Onion(tag));
        }
        catch (CommitmentRefusedException)
        {
            return null;
        }

        var add = sent.Outbound.OfType<OutboundAddHtlc>().Single().Htlc;
        var result = Us.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket);
        _preimageTags[(HtlcDirection.Incoming, add.Id)] = tag;
        _nextPreimageTag = (byte)(_nextPreimageTag % 250 + 1);
        Peer = sent.Next;
        Us = result.Next;
        return result;
    }

    /// <summary>We fulfill or fail an HTLC the peer offered once it is locked in.</summary>
    public CommitmentsResult? TryUsRemove()
    {
        var htlc = Us.Htlcs.Values.FirstOrDefault(h => h.State == HtlcState.RcvdAddAckRevocation);
        if (htlc is null)
            return null;

        CommitmentsResult result;
        switch (_rng.Next(3))
        {
            case 0:
                var preimage = Preimage(_preimageTags[(HtlcDirection.Incoming, htlc.Id)]);
                result = Us.SendFulfill(htlc.Id, preimage, _sha256);
                Peer = Peer.ReceiveFulfill(htlc.Id, preimage, _sha256).Next;
                break;
            case 1:
                result = Us.SendFail(htlc.Id, new byte[] { 0xFA, (byte)htlc.Id, 0x11 });
                Peer = Peer.ReceiveFail(htlc.Id, new byte[] { 0xFA, (byte)htlc.Id, 0x11 }).Next;
                break;
            default:
                var sha = SHA256.HashData(new[] { (byte)htlc.Id });
                const ushort code = ChannelCommitments.BadOnionFlag | 5;
                result = Us.SendFailMalformed(htlc.Id, code, sha);
                Peer = Peer.ReceiveFailMalformed(htlc.Id, code, sha).Next;
                break;
        }

        Us = result.Next;
        return result;
    }

    /// <summary>The peer fulfills or fails an HTLC we offered once it is locked in.</summary>
    public CommitmentsResult? TryPeerRemove()
    {
        var htlc = Peer.Htlcs.Values.FirstOrDefault(h => h.State == HtlcState.RcvdAddAckRevocation);
        if (htlc is null)
            return null;

        CommitmentsResult result;
        if (_rng.Next(2) == 0)
        {
            var preimage = Preimage(_preimageTags[(HtlcDirection.Outgoing, htlc.Id)]);
            Peer = Peer.SendFulfill(htlc.Id, preimage, _sha256).Next;
            result = Us.ReceiveFulfill(htlc.Id, preimage, _sha256);
        }
        else
        {
            Peer = Peer.SendFail(htlc.Id, new byte[] { 0xEE, (byte)htlc.Id }).Next;
            result = Us.ReceiveFail(htlc.Id, new byte[] { 0xEE, (byte)htlc.Id });
        }

        Us = result.Next;
        return result;
    }

    /// <summary>Our <c>update_fee</c> (we are the funder), only when nothing else is pending.</summary>
    public CommitmentsResult? TryUsFee()
    {
        if (!Us.Params.LocalIsFunder || Us.HasPendingChangesForRemote || Us.HasPendingChangesForLocal
         || Peer.HasPendingChangesForRemote || Us.RemoteNextCommit is not null || _pendingRevokeForUs is not null
         || Us.FeeUpdates.Count > 1)
            return null;

        var feerate = (uint)_rng.Next(253, 5_000);
        CommitmentsResult result;
        try
        {
            result = Us.SendFee(feerate);
        }
        catch (CommitmentRefusedException)
        {
            return null;
        }

        Peer = Peer.ReceiveFee(feerate, 253, 100_000).Next;
        Us = result.Next;
        return result;
    }

    /// <summary>We sign; the peer applies it and its RAA waits for <see cref="TryDeliverRevokeToUs"/>.</summary>
    public CommitmentsResult? TryUsCommit()
    {
        if (!Us.CanSendCommit || _pendingRevokeForUs is not null)
            return null;

        var signer = new FakeCommitmentSigner(Us.Params.Remote.DustLimitSatoshis, Us.Params.OptionAnchors);
        var result = Us.SendCommit(signer);
        var cs = result.Outbound.OfType<OutboundCommitmentSigned>().Single();
        var received = Peer.ReceiveCommit(cs.Signatures, _verifier);
        _pendingRevokeForUs = received.Outbound.OfType<OutboundRevokeAndAck>().Single();
        Peer = received.Next;
        Us = result.Next;
        return result;
    }

    /// <summary>The peer signs; we apply it and our RAA is delivered at once.</summary>
    public CommitmentsResult? TryPeerCommit()
    {
        if (!Peer.CanSendCommit || _pendingRevokeForUs is not null)
            return null;

        var signer = new FakeCommitmentSigner(Peer.Params.Remote.DustLimitSatoshis, Peer.Params.OptionAnchors);
        var sent = Peer.SendCommit(signer);
        var cs = sent.Outbound.OfType<OutboundCommitmentSigned>().Single();
        var result = Us.ReceiveCommit(cs.Signatures, _verifier);
        var raa = result.Outbound.OfType<OutboundRevokeAndAck>().Single();
        Peer = sent.Next.ReceiveRevoke(SecretFor(UsTag, raa.RevokedCommitmentNumber),
                                       Point(UsTag, raa.NextCommitmentNumber), _revocationVerifier).Next;
        Us = result.Next;
        return result;
    }

    /// <summary>The peer's RAA for our last <c>commitment_signed</c>.</summary>
    public CommitmentsResult? TryDeliverRevokeToUs()
    {
        if (_pendingRevokeForUs is not { } raa)
            return null;

        var result = Us.ReceiveRevoke(SecretFor(PeerTag, raa.RevokedCommitmentNumber),
                                      Point(PeerTag, raa.NextCommitmentNumber), _revocationVerifier);
        _pendingRevokeForUs = null;
        Us = result.Next;
        return result;
    }

    /// <summary>A disconnection seen from our side only (<see cref="ChannelCommitments.RevertUncommitted"/>).</summary>
    public CommitmentsResult RevertUs()
    {
        var result = Us.RevertUncommitted();
        Us = result.Next;
        return result;
    }

    /// <summary>Deterministic stand-in for the per-commitment point of <paramref name="node"/>'s commitment n.</summary>
    public static CompactPubKey Point(byte node, ulong number)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[1] = node;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(2), number);
        return new CompactPubKey(bytes);
    }

    /// <summary>The stand-in secret matching <see cref="Point"/>.</summary>
    public static Secret SecretFor(byte node, ulong number)
    {
        var bytes = new byte[32];
        bytes[0] = node;
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(1), number);
        return new Secret(bytes);
    }

    public static Secret Preimage(byte tag) => new(Enumerable.Repeat(tag, 32).ToArray());

    public static Hash PaymentHash(byte tag) => new(SHA256.HashData((byte[])Preimage(tag)));

    private static byte[] Onion(byte tag)
    {
        var onion = new byte[1366];
        onion[0] = 0x00;
        onion[1] = tag;
        onion[^1] = (byte)~tag;
        return onion;
    }

    internal static CommitmentSignatures Signatures(byte tag, int htlcCount) =>
        new(Signature(tag), Enumerable.Range(0, htlcCount).Select(i => Signature((byte)(tag + i + 1))).ToList());

    private static CompactSignature Signature(byte tag)
    {
        var bytes = new byte[64];
        bytes[0] = tag;
        bytes[63] = 0x5A;
        return new CompactSignature(bytes);
    }

    private sealed class FakeCommitmentSigner(ulong remoteDustSat, bool anchors) : ICommitmentSigner
    {
        public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                                         CompactPubKey remotePerCommitmentPoint) =>
            Signatures((byte)number, CommitmentFeeCalculator.UntrimmedHtlcCount(spec, remoteDustSat, anchors));
    }

    private sealed class FakeCommitmentVerifier : ICommitmentVerifier
    {
        public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                          CommitmentSignatures signatures) => true;
    }

    /// <summary>A secret is valid for a point when both encode the same (node, number).</summary>
    private sealed class FakeRevocationVerifier : IRevocationVerifier
    {
        public bool IsValidSecret(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint) =>
            ((byte[])perCommitmentSecret).AsSpan(0, 9).SequenceEqual(((byte[])expectedPerCommitmentPoint).AsSpan(1, 9));
    }
}

/// <summary>
/// Structural equality of engine snapshots: the records hold collections and <see cref="ReadOnlyMemory{T}"/>, whose
/// default equality is by reference.
/// </summary>
internal static class CommitmentsAssert
{
    public static void Equal(ChannelCommitments expected, ChannelCommitments actual)
    {
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.Params, actual.Params);
        Assert.Equal(expected.LocalBalanceMsat, actual.LocalBalanceMsat);
        Assert.Equal(expected.RemoteBalanceMsat, actual.RemoteBalanceMsat);
        Assert.Equal(expected.LocalNextHtlcId, actual.LocalNextHtlcId);
        Assert.Equal(expected.RemoteNextHtlcId, actual.RemoteNextHtlcId);
        Assert.Equal(expected.RemoteNextPerCommitmentPoint, actual.RemoteNextPerCommitmentPoint);
        Assert.Equal(expected.FeeUpdates, actual.FeeUpdates);

        Assert.Equal(expected.Htlcs.Keys, actual.Htlcs.Keys);
        foreach (var (key, htlc) in expected.Htlcs)
            HtlcEqual(htlc, actual.Htlcs[key]);

        Assert.Equal(expected.LocalCommit.Number, actual.LocalCommit.Number);
        Assert.Equal(expected.LocalCommit.Spec, actual.LocalCommit.Spec);
        SignaturesEqual(expected.LocalCommit.RemoteSignatures, actual.LocalCommit.RemoteSignatures);

        RemoteCommitEqual(expected.RemoteCommit, actual.RemoteCommit);
        Assert.Equal(expected.RemoteNextCommit is null, actual.RemoteNextCommit is null);
        if (expected.RemoteNextCommit is { } pending)
        {
            RemoteCommitEqual(pending.Commit, actual.RemoteNextCommit!.Commit);
            SignaturesEqual(pending.SentSignatures, actual.RemoteNextCommit.SentSignatures);
        }
    }

    public static void HtlcEqual(HtlcRecord expected, HtlcRecord actual)
    {
        Assert.Equal(expected.Key, actual.Key);
        Assert.Equal(expected.AmountMsat, actual.AmountMsat);
        Assert.Equal(expected.PaymentHash, actual.PaymentHash);
        Assert.Equal(expected.CltvExpiry, actual.CltvExpiry);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.OnionRoutingPacket.ToArray(), actual.OnionRoutingPacket.ToArray());
        Assert.Equal(expected.PathKey, actual.PathKey);
        Assert.Equal(expected.KnownPreimage, actual.KnownPreimage);
        Assert.Equal(expected.Removal is null, actual.Removal is null);
        if (expected.Removal is not { } removal)
            return;

        Assert.Equal(removal.Kind, actual.Removal!.Kind);
        Assert.Equal(removal.PaymentPreimage, actual.Removal.PaymentPreimage);
        Assert.Equal(removal.Reason.ToArray(), actual.Removal.Reason.ToArray());
        Assert.Equal(removal.FailureCode, actual.Removal.FailureCode);
        Assert.Equal(removal.Sha256OfOnion.ToArray(), actual.Removal.Sha256OfOnion.ToArray());
    }

    private static void RemoteCommitEqual(RemoteCommit expected, RemoteCommit actual)
    {
        Assert.Equal(expected.Number, actual.Number);
        Assert.Equal(expected.Spec, actual.Spec);
        Assert.Equal(expected.PerCommitmentPoint, actual.PerCommitmentPoint);
    }

    private static void SignaturesEqual(CommitmentSignatures? expected, CommitmentSignatures? actual)
    {
        Assert.Equal(expected is null, actual is null);
        if (expected is null)
            return;

        Assert.Equal(expected.Signature, actual!.Signature);
        Assert.Equal(expected.HtlcSignatures, actual.HtlcSignatures);
    }
}