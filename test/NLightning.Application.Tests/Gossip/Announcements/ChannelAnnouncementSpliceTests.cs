using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Gossip.Announcements;

using Application.Channels.Handlers;
using Application.Channels.Splicing.Interfaces;
using Application.Gossip.Announcements;
using Application.Protocol.Factories;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Splicing plan SP2-B-T3 (BOLT 7 SP-G-01, NL-478) on the production <see cref="ChannelAnnouncementService"/> and
/// <see cref="ChannelAnnouncementBuilder"/> with real signers: the announcement names the current funding's keys, a
/// splice is ready for <c>announcement_signatures</c> only once it is the locked current funding, and a peer's half for a
/// splice we have not sent <c>splice_locked</c> for is deferred, then taken.
/// </summary>
public class ChannelAnnouncementSpliceTests
{
    private static readonly ShortChannelId s_spliceScid = new(AnnouncementTestPair.FundingHeight + 20, 4,
                                                               AnnouncementTestPair.FundingOutputIndex);

    #region NL-478: the current funding's keys

    [Fact]
    public void Given_ASplicedChannelWithRotatedKeys_When_TheAnnouncementIsBuilt_Then_ItNamesTheCurrentFundingKeys()
    {
        // Arrange: the lock replaced the funding output with the splice's (both funding keys rotated)
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;
        var ourNewKey = NewKey(0x51);
        var theirNewKey = NewKey(0x52);
        channel.ReplaceFundingOutput(new FundingOutputInfo(LightningMoney.Satoshis(1_200_000), ourNewKey, theirNewKey,
                                                           new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray()),
                                                           AnnouncementTestPair.FundingOutputIndex));

        // Act
        var unsigned = ChannelAnnouncementBuilder.BuildUnsigned(channel, s_spliceScid, pair.Alice.NodeId,
                                                                pair.Alice.NodeOptions.BitcoinNetwork.ChainHash);
        var checks = ChannelAnnouncementBuilder.GetRemoteSignatureChecks(
            unsigned, channel, new ChannelAnnouncementSignatures(EmptySignature, EmptySignature));

        // Assert: bitcoin_key_N of node_id_N is the splice's key, never the key set's original funding key
        var aliceIsNode1 = ChannelAnnouncementBuilder.IsNode1(pair.Alice.NodeId, pair.Bob.NodeId);
        Assert.Equal(aliceIsNode1 ? ourNewKey : theirNewKey, unsigned.BitcoinKey1);
        Assert.Equal(aliceIsNode1 ? theirNewKey : ourNewKey, unsigned.BitcoinKey2);
        Assert.NotEqual(pair.Alice.Basepoints.FundingPubKey, ourNewKey);
        Assert.Equal(s_spliceScid, unsigned.ShortChannelId);
        Assert.Equal(theirNewKey, checks[1].PublicKey);
    }

    #endregion

    #region SP-G-01: readiness per funding

    [Fact]
    public void Given_ACurrentFundingSixDeep_When_AskedPerFunding_Then_OnlyTheCurrentOneIsReady()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var channel = pair.Alice.Channel;
        var current = channel.FundingOutput!.TransactionId!.Value;

        // Act / Assert: a pending or replaced funding is never announced; the current one at the depth is
        Assert.True(pair.Alice.Service.IsReadyForAnnouncementSignatures(channel, current));
        Assert.False(pair.Alice.Service.IsReadyForAnnouncementSignatures(
                         channel, new TxId(Enumerable.Repeat((byte)0x99, 32).ToArray())));
        pair.SetTipDepth(5);
        Assert.False(pair.Alice.Service.IsReadyForAnnouncementSignatures(channel, current));
    }

    #endregion

    #region SP-G-01: a splice's half before our splice_locked is deferred

    [Theory]
    [InlineData(false, AnnouncementTestPair.FundingOutputIndex, true)] // pending, ours not sent: defer
    [InlineData(true, AnnouncementTestPair.FundingOutputIndex, false)] // ours sent: handled as usual
    [InlineData(false, (ushort)3, false)] // no pending splice at that output
    public void Given_APendingSplice_When_ThePeersHalfNamesIt_Then_ItIsDeferredOnlyBeforeOurSpliceLocked(
        bool ourSpliceLockedSent, ushort outputIndex, bool expected)
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var service = CreateService(pair.Alice, Pending(ourSpliceLockedSent, null));
        var scid = new ShortChannelId(s_spliceScid.BlockHeight, s_spliceScid.TransactionIndex, outputIndex);

        // Act / Assert
        Assert.Equal(expected, service.ShouldDeferRemoteAnnouncementSignatures(pair.Alice.Channel, scid));
        Assert.False(service.ShouldDeferRemoteAnnouncementSignatures(pair.Alice.Channel,
                                                                      AnnouncementTestPair.ShortChannelId));
    }

    [Fact]
    public void Given_ASpliceWhoseScidIsKnown_When_AnotherScidIsNamed_Then_ItIsNotDeferred()
    {
        // Arrange: our depth watcher already gave the pending splice its short channel id
        using var pair = new AnnouncementTestPair();
        var service = CreateService(pair.Alice, Pending(false, s_spliceScid));

        // Act / Assert
        Assert.True(service.ShouldDeferRemoteAnnouncementSignatures(pair.Alice.Channel, s_spliceScid));
        Assert.False(service.ShouldDeferRemoteAnnouncementSignatures(
                         pair.Alice.Channel,
                         new ShortChannelId(s_spliceScid.BlockHeight + 1, 1, AnnouncementTestPair.FundingOutputIndex)));
    }

    [Fact]
    public async Task Given_ThePeersHalfForAPendingSplice_When_Received_Then_ItIsDeferredWithoutAWarningOrASave()
    {
        // Arrange
        using var pair = new AnnouncementTestPair();
        var service = CreateService(pair.Alice, Pending(false, null));
        var handler = new AnnouncementSignaturesMessageHandler(service, pair.Alice.Channels,
                                                               NullLogger<AnnouncementSignaturesMessageHandler>
                                                                  .Instance, pair.Alice.UnitOfWork.Object);
        var message = new AnnouncementSignaturesMessage(
            new AnnouncementSignaturesPayload(AnnouncementTestPair.ChannelId, s_spliceScid, EmptySignature,
                                              EmptySignature));

        // Act
        var replies = await handler.HandleAsync(message, Domain.Channels.Enums.ChannelState.Open, new FeatureOptions(),
                                                pair.Bob.NodeId);

        // Assert: nothing sent, nothing stored or saved (BOLT 7 SHOULD defer), handled after our lock
        Assert.Empty(replies);
        Assert.Null(pair.Alice.Channel.RemoteAnnouncementSignatures);
        Assert.Equal(0, pair.Alice.Saves);
    }

    [Fact]
    public async Task Given_ADeferredHalfForTheCurrentScid_When_Processed_Then_StoredAnsweredAndAnnounced()
    {
        // Arrange: Bob's half for the channel's current short channel id (the splice's, after the lock), deferred
        using var pair = new AnnouncementTestPair();
        var bobs = pair.Bob.Service.CreateAnnouncementSignatures(pair.Bob.Channel).Payload;
        pair.Alice.Service.DeferRemoteAnnouncementSignatures(
            AnnouncementTestPair.ChannelId, bobs.ShortChannelId,
            new ChannelAnnouncementSignatures(bobs.NodeSignature, bobs.BitcoinSignature));

        // Act
        var reply = await pair.Alice.Service.ProcessDeferredRemoteAnnouncementSignaturesAsync(
                        pair.Alice.Channel, pair.Bob.NodeId, pair.Alice.UnitOfWork.Object);

        // Assert: stored and saved, ours answers it (marked sent on this connection), the announcement handed on once
        Assert.NotNull(reply);
        Assert.Equal(AnnouncementTestPair.ShortChannelId, reply.Payload.ShortChannelId);
        Assert.NotNull(pair.Alice.Channel.RemoteAnnouncementSignatures);
        Assert.NotNull(pair.Alice.Channel.LocalAnnouncementSignaturesSentAt);
        Assert.Equal(1, pair.Alice.Saves);
        Assert.True(pair.Alice.Service.WasSentOnConnection(AnnouncementTestPair.ChannelId));
        var (announcement, _) = Assert.Single(pair.Alice.Sink.ChannelAnnouncements);
        Assert.Equal(AnnouncementTestPair.ShortChannelId, announcement.ShortChannelId);
        Assert.True(pair.Alice.Verifier.VerifyAll(ChannelAnnouncementBuilder.GetAllSignatureChecks(announcement)));

        // And it is taken once
        Assert.Null(await pair.Alice.Service.ProcessDeferredRemoteAnnouncementSignaturesAsync(
                        pair.Alice.Channel, pair.Bob.NodeId, pair.Alice.UnitOfWork.Object));
    }

    [Fact]
    public async Task Given_ADeferredHalfForAnotherScid_When_Processed_Then_ItIsDropped()
    {
        // Arrange: the half named a splice that is not the channel's current funding (another RBF candidate)
        using var pair = new AnnouncementTestPair();
        pair.Alice.Service.DeferRemoteAnnouncementSignatures(
            AnnouncementTestPair.ChannelId, s_spliceScid,
            new ChannelAnnouncementSignatures(EmptySignature, EmptySignature));

        // Act
        var reply = await pair.Alice.Service.ProcessDeferredRemoteAnnouncementSignaturesAsync(
                        pair.Alice.Channel, pair.Bob.NodeId, pair.Alice.UnitOfWork.Object);

        // Assert
        Assert.Null(reply);
        Assert.Null(pair.Alice.Channel.RemoteAnnouncementSignatures);
        Assert.Equal(0, pair.Alice.Saves);
        Assert.Empty(pair.Alice.Sink.ChannelAnnouncements);
    }

    #endregion

    private static CompactSignature EmptySignature => new(new byte[64]);

    private static CompactPubKey NewKey(byte tag)
    {
        using var key = new Key(Enumerable.Repeat(tag, 32).ToArray());
        return key.PubKey.ToBytes();
    }

    private static ChannelFunding Pending(bool spliceLockedSent, ShortChannelId? shortChannelId)
    {
        var key = NewKey(0x61);
        return new ChannelFunding(new TxId(Enumerable.Repeat((byte)0x66, 32).ToArray()),
                                  AnnouncementTestPair.FundingOutputIndex, 1_100_000, key, key, 1, 100_000_000, 0,
                                  ChannelFundingKind.Splice, ChannelFundingStatus.Pending,
                                  ShortChannelId: shortChannelId, SpliceLockedSent: spliceLockedSent);
    }

    /// <summary>The production service of <paramref name="node"/> with a splice state port holding one pending
    /// splice.</summary>
    private static ChannelAnnouncementService CreateService(AnnouncementTestNode node, ChannelFunding pending)
    {
        var channel = node.Channel;
        var current = ChannelFunding.FromFundingOutput(channel.FundingOutput!)!;
        var port = new Mock<ISpliceStatePort>();
        port.Setup(p => p.GetFundings(It.IsAny<ChannelModel>())).Returns(new FundingSet(current, [pending]));
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(() => node.Tip);
        return new ChannelAnnouncementService(monitor.Object, node.Verifier, node.Signer,
                                              NullLogger<ChannelAnnouncementService>.Instance,
                                              new MessageFactory(Options.Create(node.NodeOptions)),
                                              new OwnGossipPublisher(node.Sink, node.Relay),
                                              Options.Create(node.NodeOptions), Options.Create(new GossipOptions()),
                                              spliceStatePort: port.Object);
    }
}