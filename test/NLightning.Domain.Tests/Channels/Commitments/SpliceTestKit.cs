namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Bitcoin.Transactions.Extensions;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Fixtures for the splicing engine tests (splicing plan SP1-B): fundings seen from both nodes, a signer and a verifier
/// that bind every signature to its (number, funding), and a two-engine pair that exchanges batches.
/// </summary>
internal static class SpliceTestKit
{
    public static readonly TxId InitialTxId = TxIdOf(0x11);

    public static TxId TxIdOf(byte tag) => Enumerable.Repeat(tag, 32).ToArray();

    public static CompactPubKey Key(byte tag)
    {
        var bytes = new byte[33];
        bytes[0] = 0x02;
        bytes[1] = tag;
        return new CompactPubKey(bytes);
    }

    /// <summary>The initial funding as <paramref name="selfTag"/>'s node sees it.</summary>
    public static ChannelFunding Initial(ulong capacitySat, byte selfTag = CommitmentsTestKit.AliceTag) =>
        new(InitialTxId, 0, capacitySat, Key(selfTag), Key(Peer(selfTag)), 0, 0, 0, ChannelFundingKind.Initial,
            ChannelFundingStatus.Current);

    /// <summary>
    /// A pending splice as <paramref name="selfTag"/>'s node sees it: <paramref name="localDeltaSat"/> is that node's
    /// balance change, <paramref name="remoteDeltaSat"/> its peer's.
    /// </summary>
    public static ChannelFunding Splice(byte txTag, ulong capacitySat, long localDeltaSat, long remoteDeltaSat,
                                        byte selfTag = CommitmentsTestKit.AliceTag,
                                        ChannelFundingKind kind = ChannelFundingKind.Splice, TxId? rbfOf = null) =>
        new(TxIdOf(txTag), 1, capacitySat, Key((byte)(selfTag + 1)), Key((byte)(Peer(selfTag) + 1)), 1,
            localDeltaSat * 1_000, remoteDeltaSat * 1_000, kind, ChannelFundingStatus.Pending, 1_000, 0, rbfOf);

    /// <summary>The same splice seen from the other node.</summary>
    public static ChannelFunding Mirror(ChannelFunding funding) =>
        funding with
        {
            LocalFundingPubKey = funding.RemoteFundingPubKey,
            RemoteFundingPubKey = funding.LocalFundingPubKey,
            LocalBalanceDeltaMsat = funding.RemoteBalanceDeltaMsat,
            RemoteBalanceDeltaMsat = funding.LocalBalanceDeltaMsat
        };

    public static byte Peer(byte tag) =>
        tag == CommitmentsTestKit.AliceTag ? CommitmentsTestKit.BobTag : CommitmentsTestKit.AliceTag;

    /// <summary>A fresh channel with funding data, seen from <paramref name="selfTag"/>.</summary>
    public static ChannelCommitments Create(ulong localSat, ulong remoteSat, bool localIsFunder = true,
                                            byte selfTag = CommitmentsTestKit.AliceTag, uint feeratePerKw = 1_000,
                                            CommitmentParty? local = null, CommitmentParty? remote = null,
                                            bool anchors = false)
    {
        var peerTag = Peer(selfTag);
        var parameters = CommitmentsTestKit.Params(localSat, remoteSat, localIsFunder, anchors, local, remote) with
        {
            Funding = Initial(localSat + remoteSat, selfTag)
        };
        return ChannelCommitments.Create(CommitmentsTestKit.ChannelId, parameters, localSat * CommitmentsTestKit.Sat,
                                         remoteSat * CommitmentsTestKit.Sat, feeratePerKw,
                                         CommitmentsTestKit.Point(peerTag, 0), CommitmentsTestKit.Point(peerTag, 1));
    }

    /// <summary>The batch members of an outbound list (every <see cref="OutboundCommitmentSigned"/>, in order).</summary>
    public static IReadOnlyList<ReceivedCommitmentSigned> ToBatch(IEnumerable<CommitmentOutbound> outbound) =>
        outbound.OfType<OutboundCommitmentSigned>()
                .Select(cs => new ReceivedCommitmentSigned(cs.FundingTxId, cs.Signatures))
                .ToList();
}

/// <summary>
/// Signs with signatures that encode the commitment number and the funding (first txid byte, 0 when unknown), so the
/// <see cref="BindingCommitmentVerifier"/> rejects a signature presented for another funding or number.
/// </summary>
internal sealed class BindingCommitmentSigner(ulong remoteDustSat, bool anchors) : ICommitmentSigner
{
    public List<(TxId? FundingTxId, ulong Number, CommitmentSpec Spec)> Calls { get; } = [];

    public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                                     CommitmentSpec spec, CompactPubKey remotePerCommitmentPoint,
                                                     MusigPublicNonce? remoteVerificationNonce = null)
    {
        Calls.Add((funding?.FundingTxId, number, spec));
        var count = CommitmentFeeCalculator.UntrimmedHtlcCount(spec, remoteDustSat, CommitmentFormatExtensions.FromOptionAnchors(anchors));
        return new CommitmentSignatures(Sign(funding, number, spec), Enumerable.Range(0, count)
                                                                              .Select(i => CommitmentsTestKit.Signature((byte)(i + 1)))
                                                                              .ToList());
    }

    public static CompactSignature Sign(ChannelFunding? funding, ulong number, CommitmentSpec spec)
    {
        var bytes = new byte[64];
        bytes[0] = (byte)number;
        bytes[1] = funding is null ? (byte)0 : ((byte[])funding.FundingTxId)[0];
        // Bind the balances (from the holder's view) so a wrong spec is rejected too
        BitConverter.TryWriteBytes(bytes.AsSpan(2, 8), spec.ToHolderMsat);
        BitConverter.TryWriteBytes(bytes.AsSpan(10, 8), spec.ToCounterpartyMsat);
        return new CompactSignature(bytes);
    }
}

/// <summary>Accepts exactly the signatures <see cref="BindingCommitmentSigner"/> produces for the same commitment.</summary>
internal sealed class BindingCommitmentVerifier : ICommitmentVerifier
{
    public List<(TxId? FundingTxId, ulong Number, CommitmentSpec Spec)> Calls { get; } = [];

    public bool VerifyLocalCommitment(ChannelId channelId, ChannelFunding? funding, ulong number, CommitmentSpec spec,
                                      CommitmentSignatures signatures)
    {
        Calls.Add((funding?.FundingTxId, number, spec));
        return signatures.Signature.Equals(BindingCommitmentSigner.Sign(funding, number, spec));
    }
}

/// <summary>
/// Two engines with funding data joined by hand (Alice = funder): splice steps, batched commitment exchanges, and the
/// per-funding checks (I6 per funding, both sides agree on every commitment of every funding).
/// </summary>
internal sealed class SplicePair
{
    private readonly FakeRevocationVerifier _revocationVerifier = new();

    public ChannelCommitments Alice { get; set; }
    public ChannelCommitments Bob { get; set; }

    public SplicePair(ulong aliceSat, ulong bobSat, CommitmentParty? aliceParty = null, CommitmentParty? bobParty = null,
                      bool anchors = false)
    {
        var alice = aliceParty ?? CommitmentsTestKit.Party();
        var bob = bobParty ?? CommitmentsTestKit.Party();
        Alice = SpliceTestKit.Create(aliceSat, bobSat, true, CommitmentsTestKit.AliceTag, local: alice, remote: bob,
                                     anchors: anchors);
        Bob = SpliceTestKit.Create(bobSat, aliceSat, false, CommitmentsTestKit.BobTag, local: bob, remote: alice,
                                   anchors: anchors);
    }

    /// <summary>
    /// Negotiated splice (Alice's view): both sides sign and verify the splice commitments (SP-CS-01/02), so the
    /// funding becomes pending on both.
    /// </summary>
    public void Splice(ChannelFunding aliceView)
    {
        var bobView = SpliceTestKit.Mirror(aliceView);
        var aliceSigner = new BindingCommitmentSigner(Alice.Params.Remote.DustLimitSatoshis, Alice.Params.OptionAnchors);
        var bobSigner = new BindingCommitmentSigner(Bob.Params.Remote.DustLimitSatoshis, Bob.Params.OptionAnchors);

        var aliceCs = Assert.IsType<OutboundCommitmentSigned>(
            Assert.Single(Alice.SignSpliceCommitment(aliceView, aliceSigner).Outbound));
        var bobCs = Assert.IsType<OutboundCommitmentSigned>(
            Assert.Single(Bob.SignSpliceCommitment(bobView, bobSigner).Outbound));

        Bob = Bob.ReceiveSpliceCommitment(bobView, aliceCs.Signatures, new BindingCommitmentVerifier()).Next;
        Alice = Alice.ReceiveSpliceCommitment(aliceView, bobCs.Signatures, new BindingCommitmentVerifier()).Next;
        AssertConserved();
    }

    public ulong AliceAdd(ulong amountMsat, byte preimageTag = 1)
    {
        var result = Alice.Add(amountMsat, preimageTag);
        var add = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
        Alice = result.Next;
        Bob = Bob.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket).Next;
        return add.Id;
    }

    public ulong BobAdd(ulong amountMsat, byte preimageTag = 2)
    {
        var result = Bob.Add(amountMsat, preimageTag);
        var add = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
        Bob = result.Next;
        Alice = Alice.ReceiveAdd(add.Id, add.AmountMsat, add.PaymentHash, add.CltvExpiry, add.OnionRoutingPacket).Next;
        return add.Id;
    }

    public void BobFulfill(ulong aliceHtlcId, byte preimageTag = 1)
    {
        Bob = Bob.SendFulfill(aliceHtlcId, CommitmentsTestKit.Preimage(preimageTag), CommitmentsTestKit.Sha256).Next;
        Alice = Alice.ReceiveFulfill(aliceHtlcId, CommitmentsTestKit.Preimage(preimageTag), CommitmentsTestKit.Sha256)
                     .Next;
    }

    /// <summary>One signer's full round trip: CS batch, RAA; returns the sender's outbound.</summary>
    public IReadOnlyList<CommitmentOutbound> Commit(bool fromAlice)
    {
        var sender = fromAlice ? Alice : Bob;
        var receiver = fromAlice ? Bob : Alice;
        var signer = new BindingCommitmentSigner(sender.Params.Remote.DustLimitSatoshis, sender.Params.OptionAnchors);
        var sent = sender.SendCommit(signer);
        var batch = SpliceTestKit.ToBatch(sent.Outbound);
        var received = sent.Outbound[0] is OutboundStartBatch
                           ? receiver.ReceiveCommitBatch(batch, new BindingCommitmentVerifier())
                           : receiver.ReceiveCommit(Assert.Single(batch).Signatures, new BindingCommitmentVerifier());
        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(received.Outbound));

        var revoked = sent.Next.ReceiveRevoke(
            CommitmentsTestKit.SecretFor(fromAlice ? CommitmentsTestKit.BobTag : CommitmentsTestKit.AliceTag,
                                         raa.RevokedCommitmentNumber),
            CommitmentsTestKit.Point(fromAlice ? CommitmentsTestKit.BobTag : CommitmentsTestKit.AliceTag,
                                     raa.NextCommitmentNumber), _revocationVerifier);
        if (fromAlice)
        {
            Alice = revoked.Next;
            Bob = received.Next;
        }
        else
        {
            Bob = revoked.Next;
            Alice = received.Next;
        }

        AssertConserved();
        return sent.Outbound;
    }

    /// <summary>Signs in turn until nothing is pending.</summary>
    public void Converge()
    {
        for (var i = 0; i < 16 && (Alice.CanSendCommit || Bob.CanSendCommit); i++)
        {
            if (Alice.CanSendCommit)
                Commit(fromAlice: true);
            if (Bob.CanSendCommit)
                Commit(fromAlice: false);
        }
    }

    /// <summary>I6 per funding: every commitment of every active funding adds up to that funding's capacity, and
    /// both nodes hold the same pending fundings (mirrored).</summary>
    public void AssertConserved()
    {
        foreach (var c in new[] { Alice, Bob })
        {
            Assert.Equal(c.Params.FundingMsat, c.LocalCommit.Spec.TotalMsat);
            Assert.Equal(c.Params.FundingMsat, c.RemoteCommit.Spec.TotalMsat);
            Assert.Equal(c.PendingFundings.Select(f => f.FundingTxId),
                         c.LocalCommit.PendingFundingSignatures.Select(s => s.FundingTxId));
            foreach (var funding in c.PendingFundings)
            {
                Assert.Equal(funding.CapacityMsat, ChannelCommitments.SpecFor(c.LocalCommit.Spec, funding).TotalMsat);
                Assert.Equal(funding.CapacityMsat, ChannelCommitments.SpecFor(c.RemoteCommit.Spec, funding).TotalMsat);
                Assert.Equal(funding.CapacityMsat, c.BuildSpec(CommitmentSide.Local, funding).TotalMsat);
                Assert.Equal(funding.CapacityMsat, c.BuildSpec(CommitmentSide.Remote, funding).TotalMsat);
            }
        }

        Assert.Equal(Alice.PendingFundings.Select(f => f.FundingTxId), Bob.PendingFundings.Select(f => f.FundingTxId));
    }
}