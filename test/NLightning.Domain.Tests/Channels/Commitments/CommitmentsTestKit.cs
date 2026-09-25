using System.Buffers.Binary;
using System.Security.Cryptography;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Shared fixtures for the commitment engine tests: parameters, fake crypto ports with deterministic per-node points
/// and secrets, and payment hashes.
/// </summary>
internal static class CommitmentsTestKit
{
    public const byte AliceTag = 0xA1;
    public const byte BobTag = 0xB0;
    public const ulong Sat = 1_000;

    public static readonly ChannelId ChannelId = new(Enumerable.Repeat((byte)0x42, 32).ToArray());
    public static readonly byte[] Onion = new byte[1366];

    public static CommitmentParty Party(ulong dustSat = 546, ulong reserveSat = 10_000, ulong htlcMinMsat = 1_000,
                                        ushort maxAccepted = 30, ulong maxInFlightMsat = ulong.MaxValue) =>
        new(dustSat, reserveSat, htlcMinMsat, maxAccepted, maxInFlightMsat);

    public static CommitmentParams Params(ulong localSat, ulong remoteSat, bool localIsFunder = true,
                                          bool anchors = false, CommitmentParty? local = null,
                                          CommitmentParty? remote = null, ulong? maxDustExposureMsat = null) =>
        new(localIsFunder, localSat + remoteSat, anchors, local ?? Party(), remote ?? Party(), maxDustExposureMsat);

    /// <summary>A fresh channel seen from one node; <paramref name="selfTag"/> picks the peer's points.</summary>
    public static ChannelCommitments Create(ulong localSat, ulong remoteSat, uint feeratePerKw = 1_000,
                                            bool localIsFunder = true, bool anchors = false,
                                            CommitmentParty? local = null, CommitmentParty? remote = null,
                                            ulong? maxDustExposureMsat = null, byte selfTag = AliceTag)
    {
        var peerTag = selfTag == AliceTag ? BobTag : AliceTag;
        return ChannelCommitments.Create(ChannelId,
                                         Params(localSat, remoteSat, localIsFunder, anchors, local, remote,
                                                maxDustExposureMsat), localSat * Sat, remoteSat * Sat, feeratePerKw,
                                         Point(peerTag, 0), Point(peerTag, 1));
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

    public static Secret Preimage(byte n) => new(Enumerable.Repeat(n, 32).ToArray());

    public static Hash HashOf(Secret preimage) => new(SHA256.HashData((byte[])preimage));

    public static Hash PaymentHash(byte n) => HashOf(Preimage(n));

    public static FakeSha256 Sha256 => new();

    /// <summary>Adds an HTLC we offer with preimage <paramref name="preimageTag"/>.</summary>
    public static CommitmentsResult Add(this ChannelCommitments c, ulong amountMsat, byte preimageTag = 1,
                                        uint cltv = 600) =>
        c.SendAdd(amountMsat, PaymentHash(preimageTag), cltv, Onion);

    public static CompactSignature Signature(byte n)
    {
        var bytes = new byte[64];
        bytes[0] = n;
        return new CompactSignature(bytes);
    }
}

/// <summary>Signs with placeholder signatures, one per untrimmed HTLC of the peer's commitment.</summary>
internal sealed class FakeCommitmentSigner(ulong remoteDustSat, bool anchors) : ICommitmentSigner
{
    public List<(ulong Number, CommitmentSpec Spec, CompactPubKey Point)> Calls { get; } = [];

    public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                                     CompactPubKey remotePerCommitmentPoint)
    {
        Calls.Add((number, spec, remotePerCommitmentPoint));
        var count = CommitmentFees.UntrimmedHtlcCount(spec, remoteDustSat, anchors);
        return new CommitmentSignatures(CommitmentsTestKit.Signature((byte)number),
                                        Enumerable.Range(0, count)
                                                  .Select(i => CommitmentsTestKit.Signature((byte)(i + 1)))
                                                  .ToList());
    }
}

/// <summary>Accepts (or rejects) every signature and records what it was asked to verify.</summary>
internal sealed class FakeCommitmentVerifier(bool valid = true) : ICommitmentVerifier
{
    public List<(ulong Number, CommitmentSpec Spec)> Calls { get; } = [];

    public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                      CommitmentSignatures signatures)
    {
        Calls.Add((number, spec));
        return valid;
    }
}

/// <summary>A secret is valid for a point when both encode the same (node, number) (see the kit).</summary>
internal sealed class FakeRevocationVerifier : IRevocationVerifier
{
    public bool IsValidSecret(Secret perCommitmentSecret, CompactPubKey expectedPerCommitmentPoint)
    {
        byte[] secret = perCommitmentSecret;
        byte[] point = expectedPerCommitmentPoint;
        return secret.AsSpan(0, 9).SequenceEqual(point.AsSpan(1, 9));
    }
}

/// <summary>
/// Two engines (Alice = funder, Bob) joined by hand: each method delivers one message and checks that both sides
/// agree on every commitment that gets signed (invariant I7 at the spec level) and that every spec conserves the
/// funding amount (invariant I6).
/// </summary>
internal sealed class CommitmentPair
{
    private readonly FakeCommitmentVerifier _verifier = new();
    private readonly FakeRevocationVerifier _revocationVerifier = new();

    public ChannelCommitments Alice { get; private set; }
    public ChannelCommitments Bob { get; private set; }

    public CommitmentPair(ulong aliceSat, ulong bobSat, uint feeratePerKw = 1_000, bool anchors = false,
                          CommitmentParty? aliceParty = null, CommitmentParty? bobParty = null)
    {
        var alice = aliceParty ?? CommitmentsTestKit.Party();
        var bob = bobParty ?? CommitmentsTestKit.Party();
        Alice = CommitmentsTestKit.Create(aliceSat, bobSat, feeratePerKw, true, anchors, alice, bob,
                                          selfTag: CommitmentsTestKit.AliceTag);
        Bob = CommitmentsTestKit.Create(bobSat, aliceSat, feeratePerKw, false, anchors, bob, alice,
                                        selfTag: CommitmentsTestKit.BobTag);
    }

    public ulong AliceAdd(ulong amountMsat, byte preimageTag = 1, uint cltv = 600)
    {
        var result = Alice.Add(amountMsat, preimageTag, cltv);
        var add = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
        Alice = result.Next;
        Bob = Bob.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket).Next;
        return add.Id;
    }

    public ulong BobAdd(ulong amountMsat, byte preimageTag = 2, uint cltv = 600)
    {
        var result = Bob.Add(amountMsat, preimageTag, cltv);
        var add = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
        Bob = result.Next;
        Alice = Alice.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket)
                     .Next;
        return add.Id;
    }

    public void AliceFulfill(ulong bobHtlcId, byte preimageTag = 2)
    {
        Alice = Alice.SendFulfill(bobHtlcId, CommitmentsTestKit.Preimage(preimageTag), CommitmentsTestKit.Sha256)
                     .Next;
        Bob = Bob.ReceiveFulfill(bobHtlcId, CommitmentsTestKit.Preimage(preimageTag), CommitmentsTestKit.Sha256)
                 .Next;
    }

    public void BobFulfill(ulong aliceHtlcId, byte preimageTag = 1)
    {
        Bob = Bob.SendFulfill(aliceHtlcId, CommitmentsTestKit.Preimage(preimageTag), CommitmentsTestKit.Sha256).Next;
        Alice = Alice.ReceiveFulfill(aliceHtlcId, CommitmentsTestKit.Preimage(preimageTag), CommitmentsTestKit.Sha256)
                     .Next;
    }

    public void AliceFail(ulong bobHtlcId)
    {
        Alice = Alice.SendFail(bobHtlcId, new byte[] { 4, 5, 6 }).Next;
        Bob = Bob.ReceiveFail(bobHtlcId, new byte[] { 4, 5, 6 }).Next;
    }

    public void BobFail(ulong aliceHtlcId)
    {
        Bob = Bob.SendFail(aliceHtlcId, new byte[] { 1, 2, 3 }).Next;
        Alice = Alice.ReceiveFail(aliceHtlcId, new byte[] { 1, 2, 3 }).Next;
    }

    public void AliceFee(uint feeratePerKw)
    {
        Alice = Alice.SendFee(feeratePerKw).Next;
        Bob = Bob.ReceiveFee(feeratePerKw, 253, 100_000).Next;
    }

    /// <summary>Alice signs; Bob applies it and returns the RAA placeholder (not yet delivered).</summary>
    public OutboundRevokeAndAck AliceCommits() => Commit(fromAlice: true);

    /// <summary>Bob signs; Alice applies it and returns the RAA placeholder (not yet delivered).</summary>
    public OutboundRevokeAndAck BobCommits() => Commit(fromAlice: false);

    public void DeliverBobRevoke(OutboundRevokeAndAck raa) =>
        Alice = Alice.ReceiveRevoke(CommitmentsTestKit.SecretFor(CommitmentsTestKit.BobTag, raa.RevokedCommitmentNumber),
                                    CommitmentsTestKit.Point(CommitmentsTestKit.BobTag, raa.NextCommitmentNumber),
                                    _revocationVerifier).Next;

    public void DeliverAliceRevoke(OutboundRevokeAndAck raa) =>
        Bob = Bob.ReceiveRevoke(CommitmentsTestKit.SecretFor(CommitmentsTestKit.AliceTag, raa.RevokedCommitmentNumber),
                                CommitmentsTestKit.Point(CommitmentsTestKit.AliceTag, raa.NextCommitmentNumber),
                                _revocationVerifier).Next;

    /// <summary>A full round started by Alice: CS, RAA, CS, RAA.</summary>
    public void AliceFullRound()
    {
        DeliverBobRevoke(AliceCommits());
        DeliverAliceRevoke(BobCommits());
    }

    /// <summary>A full round started by Bob: CS, RAA, CS, RAA.</summary>
    public void BobFullRound()
    {
        DeliverAliceRevoke(BobCommits());
        DeliverBobRevoke(AliceCommits());
    }

    /// <summary>Signs and revokes in turn until neither side has anything left to sign.</summary>
    public void Converge()
    {
        for (var i = 0; i < 16 && (Alice.CanSendCommit || Bob.CanSendCommit); i++)
        {
            if (Alice.CanSendCommit)
                DeliverBobRevoke(AliceCommits());
            if (Bob.CanSendCommit)
                DeliverAliceRevoke(BobCommits());
        }

        Assert.False(Alice.HasPendingChangesForRemote || Bob.HasPendingChangesForRemote);
    }

    private OutboundRevokeAndAck Commit(bool fromAlice)
    {
        var sender = fromAlice ? Alice : Bob;
        var receiver = fromAlice ? Bob : Alice;
        var signer = new FakeCommitmentSigner(sender.Params.Remote.DustLimitSatoshis, sender.Params.OptionAnchors);

        var sent = sender.SendCommit(signer);
        var cs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(sent.Outbound));
        var received = receiver.ReceiveCommit(cs.Signatures, _verifier);
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));

        // I7 (spec level): the commitment the sender signed is the one the receiver now holds.
        var signed = sent.Next.RemoteNextCommit!.Commit;
        Assert.Equal(signed.Number, received.Next.LocalCommit.Number);
        AssertMirrored(signed.Spec, received.Next.LocalCommit.Spec);

        if (fromAlice)
        {
            Alice = sent.Next;
            Bob = received.Next;
        }
        else
        {
            Bob = sent.Next;
            Alice = received.Next;
        }

        AssertConserved();
        return raa;
    }

    public void AssertConserved()
    {
        foreach (var c in new[] { Alice, Bob })
        {
            var funding = c.Params.FundingMsat;
            Assert.Equal(funding, c.BuildSpec(CommitmentSide.Local).TotalMsat);
            Assert.Equal(funding, c.BuildSpec(CommitmentSide.Remote).TotalMsat);
            Assert.Equal(funding, c.LocalCommit.Spec.TotalMsat);
            Assert.Equal(funding, c.RemoteCommit.Spec.TotalMsat);
        }
    }

    /// <summary>The same commitment seen from both nodes: holder flips, balances and HTLC directions swap.</summary>
    public static void AssertMirrored(CommitmentSpec fromSender, CommitmentSpec fromReceiver)
    {
        Assert.NotEqual(fromSender.Holder, fromReceiver.Holder);
        Assert.Equal(fromSender.FeeratePerKw, fromReceiver.FeeratePerKw);
        Assert.Equal(fromSender.ToHolderMsat, fromReceiver.ToHolderMsat);
        Assert.Equal(fromSender.ToCounterpartyMsat, fromReceiver.ToCounterpartyMsat);
        var flipped = fromSender.Htlcs
                                .Select(h => h with
                                {
                                    Direction = h.Direction == HtlcDirection.Outgoing
                                                     ? HtlcDirection.Incoming
                                                     : HtlcDirection.Outgoing
                                })
                                .OrderBy(h => h.Direction).ThenBy(h => h.Id);
        Assert.Equal(flipped, fromReceiver.Htlcs);
    }
}