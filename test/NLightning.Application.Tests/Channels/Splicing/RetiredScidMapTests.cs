using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Splicing plan D12 (SP2-B-T2): the short channel ids a splice lock retired keep resolving for
/// <see cref="RetiredShortChannelId.RetentionBlocks"/> (72) blocks, then no longer.
/// </summary>
public class RetiredScidMapTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x71, 32).ToArray());
    private static readonly ChannelId s_otherChannelId = new(Enumerable.Repeat((byte)0x72, 32).ToArray());
    private static readonly ShortChannelId s_initialScid = new(400, 1, 0);
    private static readonly ShortChannelId s_firstSpliceScid = new(600, 2, 0);
    private static readonly ShortChannelId s_secondSpliceScid = new(700, 5, 1);
    private static readonly CompactPubKey s_key = new(Enumerable.Repeat((byte)0x02, 33).ToArray());

    private readonly Mock<IBlockchainMonitor> _monitor = new();

    [Fact]
    public void Given_ARetiredScid_When_Resolved_Then_ItNamesItsChannelUntilItsExpiryHeight()
    {
        // Arrange
        using var map = CreateMap();
        map.Retire(RetiredScidMap.Create(s_initialScid, s_channelId, 600));

        // Act / Assert: 72 blocks from the lock's height (BOLT 7's forget delay, D12)
        SetTip(600);
        Assert.True(map.TryResolve(s_initialScid, out var channelId));
        Assert.Equal(s_channelId, channelId);
        SetTip(671);
        Assert.True(map.TryResolve(s_initialScid, out _));
        SetTip(672);
        Assert.False(map.TryResolve(s_initialScid, out _));
        Assert.False(map.TryResolve(s_firstSpliceScid, out _));
    }

    [Fact]
    public void Given_RetiredScids_When_PrunedAtAHeight_Then_OnlyTheExpiredOnesAreRemoved()
    {
        // Arrange
        using var map = CreateMap();
        map.Retire(RetiredScidMap.Create(s_initialScid, s_channelId, 600));
        map.Retire(RetiredScidMap.Create(s_firstSpliceScid, s_channelId, 700));

        // Act
        var removed = map.PruneExpired(672);

        // Assert
        Assert.Equal(1, removed);
        Assert.False(map.TryResolve(s_initialScid, out _));
        Assert.True(map.TryResolve(s_firstSpliceScid, out _));
        Assert.Equal(0, map.PruneExpired(700));
        Assert.Equal(1, map.PruneExpired(772));
        Assert.Empty(map.GetByChannel(s_channelId));
    }

    [Fact]
    public void Given_ANewBlock_When_TheMonitorRaisesIt_Then_TheExpiredScidsArePruned()
    {
        // Arrange
        using var map = CreateMap();
        map.Retire(RetiredScidMap.Create(s_initialScid, s_channelId, 600));

        // Act
        _monitor.Raise(m => m.OnNewBlockDetected += null,
                       new NewBlockEventArgs(672, new Hash(new byte[32])));

        // Assert: gone even for a caller that does not read the tip
        Assert.Empty(map.GetByChannel(s_channelId));
    }

    [Fact]
    public void Given_TheSameScidRetiredTwice_When_Registered_Then_TheLaterExpiryIsKept()
    {
        // Arrange
        using var map = CreateMap();

        // Act
        map.Retire(RetiredScidMap.Create(s_initialScid, s_channelId, 650));
        map.Retire(RetiredScidMap.Create(s_initialScid, s_channelId, 600));

        // Assert
        var entry = Assert.Single(map.GetByChannel(s_channelId));
        Assert.Equal(650u, entry.RetiredAtHeight);
        Assert.Equal(722u, entry.ExpiresAtHeight);
    }

    [Fact]
    public void Given_ScidsOfTwoChannels_When_ListedByChannel_Then_OnlyThatChannelsAreReturnedOldestFirst()
    {
        // Arrange
        using var map = CreateMap();
        map.Retire(RetiredScidMap.Create(s_firstSpliceScid, s_channelId, 700));
        map.Retire(RetiredScidMap.Create(s_initialScid, s_channelId, 600));
        map.Retire(RetiredScidMap.Create(s_secondSpliceScid, s_otherChannelId, 710));

        // Act
        var retired = map.GetByChannel(s_channelId);

        // Assert
        Assert.Equal([s_initialScid, s_firstSpliceScid], retired.Select(r => r.ShortChannelId));
        Assert.True(map.TryResolve(s_secondSpliceScid, out var other));
        Assert.Equal(s_otherChannelId, other);
    }

    [Fact]
    public void Given_TwoLockedSplices_When_BuiltFromTheFundingRows_Then_EachReplacedScidRetiresAtItsSuccessorsHeight()
    {
        // Arrange: initial (replaced) -> splice 1 (replaced) -> splice 2 (current), with a discarded RBF attempt
        var fundings = new List<ChannelFunding>
        {
            Funding(0x01, ChannelFundingStatus.Replaced, s_initialScid, 400),
            Funding(0x02, ChannelFundingStatus.Replaced, s_firstSpliceScid, 600),
            Funding(0x03, ChannelFundingStatus.Discarded, null, null),
            Funding(0x04, ChannelFundingStatus.Current, s_secondSpliceScid, 700)
        };

        // Act
        var retired = RetiredScidMap.FromFundings(s_channelId, fundings);

        // Assert
        Assert.Equal(
        [
            new RetiredShortChannelId(s_initialScid, s_channelId, 600, 672),
            new RetiredShortChannelId(s_firstSpliceScid, s_channelId, 700, 772)
        ], retired);
    }

    [Fact]
    public void Given_AReplacedFundingWithoutScid_When_BuiltFromTheFundingRows_Then_ItIsSkipped()
    {
        // Arrange: a row written before the short channel id was known, and a successor without a height
        var fundings = new List<ChannelFunding>
        {
            Funding(0x01, ChannelFundingStatus.Replaced, null, 400),
            Funding(0x02, ChannelFundingStatus.Replaced, s_firstSpliceScid, null),
            Funding(0x03, ChannelFundingStatus.Current, null, null)
        };

        // Act / Assert
        Assert.Empty(RetiredScidMap.FromFundings(s_channelId, fundings));
    }

    [Fact]
    public async Task Given_StoredFundings_When_Loaded_Then_TheUnexpiredRetiredScidsResolveAgain()
    {
        // Arrange: a restart at height 710 (the initial scid expired at 672; the first splice's resolves until 772)
        var fundings = new List<ChannelFunding>
        {
            Funding(0x01, ChannelFundingStatus.Replaced, s_initialScid, 400),
            Funding(0x02, ChannelFundingStatus.Replaced, s_firstSpliceScid, 600),
            Funding(0x04, ChannelFundingStatus.Current, s_secondSpliceScid, 700)
        };
        var channelDb = new Mock<IChannelDbRepository>();
        var open = CreateChannel(s_channelId, ChannelState.Open);
        channelDb.Setup(r => r.GetReadyChannelsAsync()).ReturnsAsync([open]);
        var fundingDb = new Mock<IChannelFundingDbRepository>();
        fundingDb.Setup(r => r.GetByChannelIdAsync(s_channelId)).ReturnsAsync(fundings);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundingDb.Object);
        using var map = CreateMap(unitOfWork.Object);
        map.Retire(RetiredScidMap.Create(s_secondSpliceScid, s_otherChannelId, 705));

        // Act
        await map.LoadAsync(710, TestContext.Current.CancellationToken);

        // Assert: the map is what the rows say (the entry retired in memory before is replaced)
        var entry = Assert.Single(map.GetByChannel(s_channelId));
        Assert.Equal(new RetiredShortChannelId(s_firstSpliceScid, s_channelId, 700, 772), entry);
        Assert.Empty(map.GetByChannel(s_otherChannelId));
        Assert.True(map.TryResolve(s_firstSpliceScid, out var channelId));
        Assert.Equal(s_channelId, channelId);
        Assert.False(map.TryResolve(s_initialScid, out _));
    }

    [Fact]
    public async Task Given_AnAliasOnlyChannelsFundings_When_Loaded_Then_ItsReplacedRealScidIsNotRetired()
    {
        // Arrange: option_scid_alias Compulsory, whose real short channel id must never route (BOLT 2, NL-348)
        var fundings = new List<ChannelFunding>
        {
            Funding(0x02, ChannelFundingStatus.Replaced, s_firstSpliceScid, 600),
            Funding(0x04, ChannelFundingStatus.Current, s_secondSpliceScid, 700)
        };
        var channelDb = new Mock<IChannelDbRepository>();
        var aliasOnly = SpliceLockTestChannels.Create(s_channelId, ChannelState.Open, s_secondSpliceScid,
                                                      useScidAlias: FeatureSupport.Compulsory);
        channelDb.Setup(r => r.GetReadyChannelsAsync()).ReturnsAsync([aliasOnly]);
        var fundingDb = new Mock<IChannelFundingDbRepository>();
        fundingDb.Setup(r => r.GetByChannelIdAsync(s_channelId)).ReturnsAsync(fundings);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundingDb.Object);
        using var map = CreateMap(unitOfWork.Object);

        // Act
        await map.LoadAsync(710, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(map.GetByChannel(s_channelId));
        Assert.False(map.TryResolve(s_firstSpliceScid, out _));
    }

    private RetiredScidMap CreateMap(IUnitOfWork? unitOfWork = null)
    {
        var services = new ServiceCollection();
        if (unitOfWork is not null)
            services.AddScoped(_ => unitOfWork);
        return new RetiredScidMap(services.BuildServiceProvider(), NullLogger<RetiredScidMap>.Instance,
                                  _monitor.Object);
    }

    private void SetTip(uint height) => _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(height);

    private static ChannelFunding Funding(byte tag, ChannelFundingStatus status, ShortChannelId? scid,
                                          uint? confirmedHeight) =>
        new(new TxId(Enumerable.Repeat(tag, 32).ToArray()), scid?.OutputIndex ?? 0, 1_000_000, s_key, s_key, tag, 0,
            0, tag == 0x01 ? ChannelFundingKind.Initial : ChannelFundingKind.Splice, status,
            ConfirmedHeight: confirmedHeight, ShortChannelId: scid);

    private static ChannelModel CreateChannel(ChannelId channelId, ChannelState state) =>
        SpliceLockTestChannels.Create(channelId, state, s_secondSpliceScid);
}