using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Interfaces;
using Application.Channels.Splicing;
using Application.Channels.Splicing.Interfaces;
using Application.Gossip.Announcements.Interfaces;
using Application.Protocol.Factories;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Harness;

/// <summary>
/// BOLT 2 "Splice Completion" (splicing plan §1.7, SP2-B-T1/T2) on <see cref="SpliceService"/> with a scripted
/// <see cref="ISpliceStatePort"/> over the real <see cref="FundingSet"/> transitions: SP-LK-02 (unknown txid), SP-LK-03
/// (the lock when both sides named the same txid, RBF siblings discarded; different candidates ignored, D11), duplicates,
/// SP-RE-04 (<c>my_current_funding_locked</c> as <c>splice_locked</c>), the short channel id switch (D12) and the
/// announcement restart of a public channel (SP-G-01).
/// </summary>
public class SpliceLockRulesTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x5A, 32).ToArray());
    private static readonly ShortChannelId s_oldScid = new(400, 1, 0);
    private static readonly ShortChannelId s_spliceScid = new(600, 2, 0);
    private static readonly TxId s_currentTx = new(Enumerable.Repeat((byte)0x01, 32).ToArray());
    private static readonly TxId s_spliceTx = new(Enumerable.Repeat((byte)0x02, 32).ToArray());
    private static readonly TxId s_rbfTx = new(Enumerable.Repeat((byte)0x03, 32).ToArray());

    #region SP-LK-03: the lock

    [Fact]
    public async Task Given_OurSpliceLockedSent_When_ThePeersNamesTheSameTxid_Then_LockedSiblingsDiscardedAndScidSwitched()
    {
        // Arrange: ours went out for the splice at 600; an RBF sibling is pending too
        using var fixture = new LockFixture(spliceSent: true, withRbfSibling: true);

        // Act
        var replies = await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                                    fixture.UnitOfWork.Object,
                                                                    TestContext.Current.CancellationToken);

        // Assert: locked (the current funding replaced with its scid kept for the map, the sibling discarded)
        Assert.Empty(replies);
        var (next, retired) = Assert.Single(fixture.Staged);
        Assert.Equal(s_spliceTx, next.Current.FundingTxId);
        Assert.Empty(next.Pending);
        Assert.Equal(2, retired.Count);
        Assert.Equal((s_currentTx, ChannelFundingStatus.Replaced), (retired[0].FundingTxId, retired[0].Status));
        Assert.Equal(s_oldScid, retired[0].ShortChannelId);
        Assert.Equal((s_rbfTx, ChannelFundingStatus.Discarded), (retired[1].FundingTxId, retired[1].Status));

        // D12: the channel runs on the splice's short channel id, the old one resolves for 72 blocks from 600
        Assert.Equal(s_spliceScid, fixture.Channel.ShortChannelId);
        var entry = Assert.Single(fixture.RetiredMap.GetByChannel(s_channelId));
        Assert.Equal((s_oldScid, 600u, 672u), (entry.ShortChannelId, entry.RetiredAtHeight, entry.ExpiresAtHeight));
        Assert.True(fixture.RetiredMap.TryResolve(s_oldScid, out var resolved));
        Assert.Equal(s_channelId, resolved);

        // The new funding output is watched from the lock's save on
        Assert.Contains(fixture.AddedOutpoints, w => w.TransactionId == s_spliceTx);
    }

    [Fact]
    public async Task Given_ThePeersSpliceLockedFirst_When_OurDepthIsReached_Then_OursGoesOutAndTheSpliceLocks()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: false);
        await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                      fixture.UnitOfWork.Object,
                                                      TestContext.Current.CancellationToken);
        Assert.Equal(s_currentTx, fixture.Fundings.Current.FundingTxId);

        // Act: the splice at 600, index 2 in its block
        await fixture.Service.OnSpliceDepthReachedAsync(s_channelId, s_spliceTx, 600, 2,
                                                        TestContext.Current.CancellationToken);

        // Assert: splice_locked published after the lock's save; the channel takes the scid of the confirmation
        var published = Assert.Single(fixture.Published);
        var locked = Assert.IsType<SpliceLockedMessage>(Assert.Single(published));
        Assert.Equal(s_spliceTx, locked.Payload.SpliceTxId);
        Assert.Equal(s_spliceTx, fixture.Fundings.Current.FundingTxId);
        Assert.Equal(new ShortChannelId(600, 2, 0), fixture.Channel.ShortChannelId);
        Assert.True(fixture.RetiredMap.TryResolve(s_oldScid, out _));
    }

    [Fact]
    public async Task Given_OurSpliceLockedAlreadySent_When_TheDepthIsReportedAgain_Then_NothingMoreIsSent()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true);

        // Act
        await fixture.Service.OnSpliceDepthReachedAsync(s_channelId, s_spliceTx, 600, 2,
                                                        TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(fixture.Published);
        Assert.Empty(fixture.Staged);
    }

    #endregion

    #region SP-LK-02, duplicates, D11

    [Fact]
    public async Task Given_AnUnknownTxid_When_SpliceLockedIsReceived_Then_WarningAndClose()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true);
        var unknown = new TxId(Enumerable.Repeat((byte)0x99, 32).ToArray());

        // Act
        var exception = await Assert.ThrowsAsync<ChannelWarningException>(() =>
            fixture.Service.HandleSpliceLockedAsync(fixture.Locked(unknown), fixture.PeerId, fixture.UnitOfWork.Object,
                                                    TestContext.Current.CancellationToken));

        // Assert
        Assert.True(exception.CloseConnection);
        Assert.Contains("SP-LK-02", exception.Message);
        Assert.Empty(fixture.Staged);
    }

    [Fact]
    public async Task Given_TheCurrentFundingsTxid_When_SpliceLockedIsReceived_Then_ItIsIgnored()
    {
        // Arrange: a retransmission after the lock names the funding that is current now
        using var fixture = new LockFixture(spliceSent: true);

        // Act
        var replies = await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_currentTx), fixture.PeerId,
                                                                    fixture.UnitOfWork.Object,
                                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(replies);
        Assert.Empty(fixture.Staged);
    }

    [Fact]
    public async Task Given_ThePeersSpliceLockedAlreadyReceived_When_ItArrivesAgain_Then_TheDuplicateIsIgnored()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: false);
        await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                      fixture.UnitOfWork.Object,
                                                      TestContext.Current.CancellationToken);
        var saves = fixture.Saves;

        // Act
        var replies = await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                                    fixture.UnitOfWork.Object,
                                                                    TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(replies);
        Assert.Single(fixture.Staged);
        Assert.Equal(saves, fixture.Saves);
        Assert.True(Assert.Single(fixture.Fundings.Pending).SpliceLockedReceived);
    }

    [Fact]
    public async Task Given_OursNamedAnotherRbfCandidate_When_ThePeerNamesItsSibling_Then_NothingLocksAndNothingFails()
    {
        // Arrange: the nodes are on different forks: ours named the splice, the peer names its RBF sibling (D11)
        using var fixture = new LockFixture(spliceSent: true, withRbfSibling: true);

        // Act
        var replies = await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_rbfTx), fixture.PeerId,
                                                                    fixture.UnitOfWork.Object,
                                                                    TestContext.Current.CancellationToken);

        // Assert: remembered, not locked, the short channel id unchanged
        Assert.Empty(replies);
        var (next, retired) = Assert.Single(fixture.Staged);
        Assert.Empty(retired);
        Assert.Equal(s_currentTx, next.Current.FundingTxId);
        Assert.True(next.Pending.Single(f => f.FundingTxId == s_rbfTx).SpliceLockedReceived);
        Assert.False(next.Pending.Single(f => f.FundingTxId == s_spliceTx).SpliceLockedReceived);
        Assert.Equal(s_oldScid, fixture.Channel.ShortChannelId);
        Assert.Empty(fixture.RetiredMap.GetByChannel(s_channelId));
    }

    #endregion

    #region SP-RE-04: my_current_funding_locked

    [Fact]
    public async Task Given_APendingSplice_When_MyCurrentFundingLockedNamesIt_Then_ItIsProcessedAsSpliceLocked()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true);

        // Act
        var replies = await fixture.Service.HandlePeerFundingLockedAsync(fixture.Channel, s_spliceTx,
                                                                         fixture.UnitOfWork.Object,
                                                                         TestContext.Current.CancellationToken);

        // Assert: locked as by splice_locked
        Assert.Empty(replies);
        Assert.Equal(s_spliceTx, fixture.Fundings.Current.FundingTxId);
        Assert.Equal(s_spliceScid, fixture.Channel.ShortChannelId);
    }

    [Theory]
    [InlineData(0x01)] // the current funding
    [InlineData(0x99)] // unknown
    public async Task Given_NoPendingSpliceWithThatTxid_When_MyCurrentFundingLockedNamesIt_Then_NothingHappens(
        byte txTag)
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true);

        // Act
        var replies = await fixture.Service.HandlePeerFundingLockedAsync(
                          fixture.Channel, new TxId(Enumerable.Repeat(txTag, 32).ToArray()), fixture.UnitOfWork.Object,
                          TestContext.Current.CancellationToken);

        // Assert: no warning, nothing staged
        Assert.Empty(replies);
        Assert.Empty(fixture.Staged);
    }

    [Fact]
    public async Task Given_ThePeersSpliceLockedAlreadyReceived_When_MyCurrentFundingLockedRepeatsIt_Then_NothingHappens()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: false, spliceReceived: true);

        // Act
        var replies = await fixture.Service.HandlePeerFundingLockedAsync(fixture.Channel, s_spliceTx,
                                                                         fixture.UnitOfWork.Object,
                                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(replies);
        Assert.Empty(fixture.Staged);
    }

    #endregion

    #region SP-G-01: the announcement of a public channel restarts on the splice

    [Fact]
    public async Task Given_APublicAnnouncedChannel_When_TheSpliceLocks_Then_TheOldHalvesAreForgottenAndOursIsReturned()
    {
        // Arrange: announced on the original funding; ours for the splice is due at once (already 6 deep)
        using var fixture = new LockFixture(spliceSent: true, announce: true);
        var signature = new CompactSignature(Enumerable.Repeat((byte)0x01, 64).ToArray());
        fixture.Channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(signature, signature));
        fixture.Channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UnixEpoch);
        var ours = new AnnouncementSignaturesMessage(
            new AnnouncementSignaturesPayload(s_channelId, s_spliceScid, signature, signature));
        ChannelModel? seen = null;
        fixture.Announcements
               .Setup(a => a.PrepareOwnAnnouncementSignaturesAsync(It.IsAny<ChannelModel>(), fixture.PeerId,
                                                                   It.IsAny<IUnitOfWork>()))
               .Callback<ChannelModel, CompactPubKey, IUnitOfWork>((c, _, _) => seen = c)
               .ReturnsAsync(ours);

        // Act
        var replies = await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                                    fixture.UnitOfWork.Object,
                                                                    TestContext.Current.CancellationToken);

        // Assert: both halves of the replaced funding forgotten and saved, the per-connection record cleared, then
        // ours for the new short channel id returned after the lock
        Assert.Same(ours, Assert.Single(replies));
        Assert.NotNull(seen);
        Assert.Equal(s_spliceScid, seen.ShortChannelId);
        Assert.Null(fixture.Channel.RemoteAnnouncementSignatures);
        Assert.Null(fixture.Channel.LocalAnnouncementSignaturesSentAt);
        fixture.Announcements.Verify(a => a.OnShortChannelIdChanged(s_channelId), Times.Once);
        fixture.ChannelDb.Verify(r => r.UpdateAsync(fixture.Channel), Times.Once);
        fixture.Announcements.Verify(a => a.CompleteAnnouncementAsync(fixture.Channel, It.IsAny<IUnitOfWork>()),
                                     Times.Once);
    }

    [Fact]
    public async Task Given_APublicAnnouncedChannel_When_TheSpliceLocks_Then_TheHalvesAreForgottenInTheLocksOwnSave()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true, announce: true);
        var signature = new CompactSignature(Enumerable.Repeat((byte)0x01, 64).ToArray());
        fixture.Channel.SetRemoteAnnouncementSignatures(new ChannelAnnouncementSignatures(signature, signature));
        fixture.Channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UnixEpoch);

        // Act
        await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                      fixture.UnitOfWork.Object, TestContext.Current.CancellationToken);

        // Assert: one save only (the lock's), with the channel row staged before it without either half, so no crash
        // can leave the new short channel id beside the replaced funding's halves
        Assert.Equal(1, fixture.Saves);
        var (savesBefore, remote, sentAt) = Assert.Single(fixture.StagedChannels);
        Assert.Equal(0, savesBefore);
        Assert.Null(remote);
        Assert.Null(sentAt);
        Assert.Single(fixture.Staged);
    }

    [Fact]
    public async Task Given_APublicAnnouncedChannel_When_TheLockSaveFails_Then_TheHalvesStayAndNothingIsRetired()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true, announce: true);
        var signature = new CompactSignature(Enumerable.Repeat((byte)0x01, 64).ToArray());
        var halves = new ChannelAnnouncementSignatures(signature, signature);
        fixture.Channel.SetRemoteAnnouncementSignatures(halves);
        fixture.Channel.MarkAnnouncementSignaturesSent(DateTimeOffset.UnixEpoch);
        fixture.FailSave = true;

        // Act
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.HandleSpliceLockedAsync(
                                                                fixture.Locked(s_spliceTx), fixture.PeerId,
                                                                fixture.UnitOfWork.Object,
                                                                TestContext.Current.CancellationToken));

        // Assert: the shared model still matches the database (nothing saved, nothing applied)
        Assert.Same(halves, fixture.Channel.RemoteAnnouncementSignatures);
        Assert.Equal(DateTimeOffset.UnixEpoch, fixture.Channel.LocalAnnouncementSignaturesSentAt);
        Assert.Equal(s_oldScid, fixture.Channel.ShortChannelId);
        Assert.Empty(fixture.RetiredMap.GetByChannel(s_channelId));
        fixture.Announcements.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Given_AnAliasOnlyChannel_When_TheSpliceLocks_Then_TheOldRealScidIsNotRetired()
    {
        // Arrange: option_scid_alias Compulsory (BOLT 2: MUST NOT allow incoming HTLCs by the real scid, NL-348)
        using var fixture = new LockFixture(spliceSent: true, useScidAlias: FeatureSupport.Compulsory);

        // Act
        await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                      fixture.UnitOfWork.Object, TestContext.Current.CancellationToken);

        // Assert: locked, but the replaced real short channel id never resolves
        Assert.Equal(s_spliceScid, fixture.Channel.ShortChannelId);
        Assert.False(fixture.RetiredMap.TryResolve(s_oldScid, out _));
        Assert.Empty(fixture.RetiredMap.GetByChannel(s_channelId));
    }

    [Fact]
    public async Task Given_APrivateChannel_When_TheSpliceLocks_Then_NoAnnouncementWorkIsDone()
    {
        // Arrange
        using var fixture = new LockFixture(spliceSent: true, announce: false);

        // Act
        await fixture.Service.HandleSpliceLockedAsync(fixture.Locked(s_spliceTx), fixture.PeerId,
                                                      fixture.UnitOfWork.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Announcements.VerifyNoOtherCalls();
        fixture.ChannelDb.Verify(r => r.UpdateAsync(It.IsAny<ChannelModel>()), Times.Never);
        Assert.True(fixture.RetiredMap.TryResolve(s_oldScid, out _));
    }

    #endregion

    /// <summary>A <see cref="SpliceService"/> over one open channel with a scripted funding set.</summary>
    [ExcludeFromCodeCoverage]
    private sealed class LockFixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly InMemoryChannelRepository _memory = new();

        public LockFixture(bool spliceSent, bool withRbfSibling = false, bool spliceReceived = false,
                           bool announce = false, FeatureSupport useScidAlias = FeatureSupport.No)
        {
            Channel = SpliceLockTestChannels.Create(s_channelId, ChannelState.Open, s_oldScid, announce, useScidAlias);
            PeerId = Channel.RemoteNodeId;
            _memory.AddChannel(Channel);

            var key = Channel.LocalFundingPubKey;
            var current = new ChannelFunding(s_currentTx, 0, 1_000_000, key, key, 0, 0, 0,
                                             ChannelFundingKind.Initial, ChannelFundingStatus.Current);
            var splice = new ChannelFunding(s_spliceTx, 0, 1_100_000, key, key, 1, 100_000_000, 0,
                                            ChannelFundingKind.Splice, ChannelFundingStatus.Pending,
                                            ConfirmedHeight: spliceSent ? 600 : null,
                                            ShortChannelId: spliceSent ? s_spliceScid : (ShortChannelId?)null,
                                            SpliceLockedSent: spliceSent, SpliceLockedReceived: spliceReceived);
            Fundings = new FundingSet(current, withRbfSibling
                                                   ?
                                                   [
                                                       splice,
                                                       splice with
                                                       {
                                                           FundingTxId = s_rbfTx,
                                                           Kind = ChannelFundingKind.SpliceRbf,
                                                           RbfOf = s_spliceTx,
                                                           ConfirmedHeight = null,
                                                           ShortChannelId = null,
                                                           SpliceLockedSent = false
                                                       }
                                                   ]
                                                   : [splice]);

            var port = new Mock<ISpliceStatePort>();
            port.Setup(p => p.GetFundings(It.IsAny<ChannelModel>())).Returns(() => Fundings);
            port.Setup(p => p.Lock(It.IsAny<FundingSet>(), It.IsAny<TxId>()))
                .Returns((FundingSet set, TxId txId) => set.Lock(txId));
            port.Setup(p => p.StageFundingsAsync(It.IsAny<ChannelModel>(), It.IsAny<FundingSet>(),
                                                 It.IsAny<IReadOnlyList<ChannelFunding>>(), It.IsAny<IUnitOfWork>(),
                                                 It.IsAny<CancellationToken>()))
                .Callback<ChannelModel, FundingSet, IReadOnlyList<ChannelFunding>, IUnitOfWork, CancellationToken>(
                     (_, next, retired, _, _) => Staged.Add((next, retired)))
                .Returns(Task.CompletedTask);
            port.Setup(p => p.ApplyFundings(It.IsAny<ChannelModel>(), It.IsAny<FundingSet>(),
                                            It.IsAny<IReadOnlyList<ChannelFunding>>()))
                .Callback<ChannelModel, FundingSet, IReadOnlyList<ChannelFunding>>((channel, next, retired) =>
                 {
                     // As the engine port: the lock moves the channel's short channel id
                     Fundings = next;
                     if (retired.Count > 0 && next.Current.ShortChannelId is { } scid)
                         channel.ShortChannelId = scid;
                 });

            var outpoints = new Mock<IWatchedOutpointDbRepository>();
            outpoints.Setup(o => o.GetAsync(It.IsAny<TxId>(), It.IsAny<uint>()))
                     .ReturnsAsync((WatchedOutpointModel?)null);
            outpoints.Setup(o => o.Add(It.IsAny<WatchedOutpointModel>()))
                     .Callback<WatchedOutpointModel>(AddedOutpoints.Add);
            UnitOfWork.SetupGet(u => u.WatchedOutpointDbRepository).Returns(outpoints.Object);
            UnitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(ChannelDb.Object);
            UnitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
            {
                if (FailSave)
                    throw new InvalidOperationException("Simulated failed save");
                Saves++;
                return Task.CompletedTask;
            });
            ChannelDb.Setup(r => r.UpdateAsync(It.IsAny<ChannelModel>()))
                     .Callback<ChannelModel>(c => StagedChannels.Add((Saves, c.RemoteAnnouncementSignatures,
                                                                      c.LocalAnnouncementSignaturesSentAt)))
                     .Returns(Task.CompletedTask);

            var publisher = new Mock<IChannelMessagePublisher>();
            publisher.Setup(p => p.Publish(It.IsAny<CompactPubKey>(), It.IsAny<IReadOnlyList<IChannelMessage>>()))
                     .Callback<CompactPubKey, IReadOnlyList<IChannelMessage>>((_, m) => Published.Add(m));

            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddScoped(_ => UnitOfWork.Object);
            services.AddSingleton(publisher.Object);
            services.AddSingleton(Announcements.Object);
            services.AddSingleton<IRetiredScidMap, RetiredScidMap>();
            _provider = services.BuildServiceProvider();
            RetiredMap = _provider.GetRequiredService<IRetiredScidMap>();

            var nodeOptions = Options.Create(new NodeOptions());
            Service = new SpliceService(new Application.Channels.Services.ChannelLockProvider(), _memory,
                                        new MessageFactory(nodeOptions), new Mock<ILightningSigner>().Object,
                                        port.Object, _provider, NullLogger<SpliceService>.Instance,
                                        nodeOptions: nodeOptions);
        }

        public ChannelModel Channel { get; }
        public CompactPubKey PeerId { get; }
        public FundingSet Fundings { get; private set; }
        public SpliceService Service { get; }
        public IRetiredScidMap RetiredMap { get; }
        public Mock<IUnitOfWork> UnitOfWork { get; } = new();
        public Mock<IChannelDbRepository> ChannelDb { get; } = new();
        public Mock<IChannelAnnouncementService> Announcements { get; } = new();
        public List<(FundingSet Next, IReadOnlyList<ChannelFunding> Retired)> Staged { get; } = [];
        public List<WatchedOutpointModel> AddedOutpoints { get; } = [];
        public List<IReadOnlyList<IChannelMessage>> Published { get; } = [];
        public int Saves { get; private set; }

        /// <summary>Every save throws, with nothing saved (a crash or a failed database write).</summary>
        public bool FailSave { get; set; }

        /// <summary>The channel rows staged: (saves before it, the remote half, our sent mark) as staged.</summary>
        public List<(int SavesBefore, ChannelAnnouncementSignatures? Remote, DateTimeOffset? SentAt)> StagedChannels
        {
            get;
        } = [];

        public SpliceLockedMessage Locked(TxId txId) => new(new SpliceLockedPayload(s_channelId, txId));

        public void Dispose()
        {
            Service.Dispose();
            _provider.Dispose();
        }
    }
}

/// <summary>An open channel model for the lock and retired short channel id tests.</summary>
[ExcludeFromCodeCoverage]
internal static class SpliceLockTestChannels
{
    public static ChannelModel Create(ChannelId channelId, ChannelState state, ShortChannelId shortChannelId,
                                      bool announce = false, FeatureSupport useScidAlias = FeatureSupport.No)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false, useScidAlias)
        {
            AnnounceChannel = announce
        };
        CompactPubKey ours = new(Enumerable.Repeat((byte)0x02, 33).ToArray());
        CompactPubKey theirs = new(Enumerable.Repeat((byte)0x03, 33).ToArray());
        var keySet = new ChannelKeySetModel(0, ours, ours, ours, ours, ours, ours);
        var remoteKeySet = new ChannelKeySetModel(0, theirs, theirs, theirs, theirs, theirs, theirs);
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), ours, theirs,
                                                  new TxId(Enumerable.Repeat((byte)0x01, 32).ToArray()), 0);
        return new ChannelModel(channelParams, channelId, null, fundingOutput, true, null, null,
                                LightningMoney.Satoshis(1_000_000), keySet, 0, 0, LightningMoney.Zero, remoteKeySet, 0,
                                theirs, 0, state, ChannelVersion.V1)
        {
            ShortChannelId = shortChannelId
        };
    }
}