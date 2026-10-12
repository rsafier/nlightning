using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Handlers;

using Daemon.Handlers;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;

/// <summary>
/// <c>listaccountingevents</c> (ClientCommand 41) and <c>accountingsnapshot</c> (42), NL-602: the events committed
/// so far are sealed before the page is read, the cursor and page are checked, a short channel id names a loaded
/// channel, and a node without a snapshot source answers "not available".
/// </summary>
public class AccountingClientHandlersTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x44, 32).ToArray());

    private readonly Mock<IAccountingEventDbRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IAccountingEventSealer> _sealer = new(MockBehavior.Strict);
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly List<string> _calls = [];

    public AccountingClientHandlersTests()
    {
        _unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(_repository.Object);
        _sealer.Setup(s => s.SealNowAsync(It.IsAny<CancellationToken>()))
               .Callback(() => _calls.Add("seal"))
               .ReturnsAsync(new AccountingSealRoundResult(0, 0, AccountingChainTip.Genesis));
        _repository.Setup(r => r.GetChainTipAsync(It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new AccountingChainTip(7, new byte[32]));
    }

    [Fact]
    public async Task Given_APage_When_Listed_Then_TheFeedIsSealedFirstAndTheCursorIsTheLastSequence()
    {
        // Arrange
        AccountingEventQuery? query = null;
        _repository.Setup(r => r.ListAsync(It.IsAny<AccountingEventQuery>(), It.IsAny<CancellationToken>()))
                   .Callback((AccountingEventQuery q, CancellationToken _) =>
                    {
                        _calls.Add("list");
                        query = q;
                    })
                   .ReturnsAsync([Sealed(4), Sealed(5)]);
        var handler = CreateHandler();
        var request = new ListAccountingEventsClientRequest
        {
            AfterLedgerSeq = 3,
            Take = 2,
            Kinds = [AccountingEventKind.ForwardSettled],
            ChannelId = s_channel,
            Since = s_at,
            Until = s_at.AddDays(1)
        };

        // Act
        var response = await handler.HandleAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["seal", "list"], _calls);
        Assert.Equal(new AccountingEventQuery(3, 2, request.Kinds, s_channel, s_at, s_at.AddDays(1)), query);
        Assert.Same(request.Kinds, query!.Kinds);
        Assert.Equal(5, response.NextAfter);
        Assert.True(response.HasMore);
        Assert.Equal(7, response.ChainTipLedgerSeq);
        Assert.Equal([4L, 5L], response.Events.Select(e => e.LedgerSeq!.Value));
    }

    [Fact]
    public async Task Given_AnEmptyPage_When_Listed_Then_TheCursorStaysAndNothingMoreIsReported()
    {
        // Arrange
        _repository.Setup(r => r.ListAsync(It.IsAny<AccountingEventQuery>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([]);

        // Act
        var response = await CreateHandler().HandleAsync(new ListAccountingEventsClientRequest { AfterLedgerSeq = 9 },
                                                          TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(response.Events);
        Assert.Equal(9, response.NextAfter);
        Assert.False(response.HasMore);
    }

    [Fact]
    public async Task Given_TheSealFails_When_Listed_Then_TheSealedRowsAreListedAnyway()
    {
        // Arrange
        _sealer.Setup(s => s.SealNowAsync(It.IsAny<CancellationToken>()))
               .ThrowsAsync(new InvalidOperationException("database busy"));
        _repository.Setup(r => r.ListAsync(It.IsAny<AccountingEventQuery>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync([Sealed(1)]);

        // Act
        var response = await CreateHandler().HandleAsync(new ListAccountingEventsClientRequest(),
                                                          TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(response.Events);
    }

    [Fact]
    public async Task Given_AShortChannelIdOfALoadedChannel_When_Listed_Then_ItsChannelIdFilters()
    {
        // Arrange
        var scid = new ShortChannelId(800_000, 1, 0);
        var channel = new Mock<IChannelMemoryRepository>();
        channel.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns([CreateChannel(scid)]);
        AccountingEventQuery? query = null;
        _repository.Setup(r => r.ListAsync(It.IsAny<AccountingEventQuery>(), It.IsAny<CancellationToken>()))
                   .Callback((AccountingEventQuery q, CancellationToken _) => query = q)
                   .ReturnsAsync([]);

        // Act
        await CreateHandler(channel.Object).HandleAsync(new ListAccountingEventsClientRequest { ChannelScid = scid },
                                                        TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_channel, query!.ChannelId);
    }

    [Fact]
    public async Task Given_AShortChannelIdOfNoLoadedChannel_When_Listed_Then_ItIsRefused()
    {
        // Arrange
        var channel = new Mock<IChannelMemoryRepository>();
        channel.Setup(c => c.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => CreateHandler(channel.Object).HandleAsync(
                                                                      new ListAccountingEventsClientRequest
                                                                      {
                                                                          ChannelScid = new ShortChannelId(1, 2, 3)
                                                                      }, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("64-hex channel id", exception.Message);
        Assert.Empty(_calls);
    }

    [Theory]
    [InlineData(-1, 100)]
    [InlineData(0, 0)]
    [InlineData(0, 1_001)]
    public async Task Given_ABadCursorOrPage_When_Listed_Then_ItIsRefusedBeforeSealing(long after, int take)
    {
        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(() => CreateHandler().HandleAsync(
                                                                      new ListAccountingEventsClientRequest
                                                                      {
                                                                          AfterLedgerSeq = after,
                                                                          Take = take
                                                                      }, TestContext.Current.CancellationToken));

        // Assert
        Assert.NotEmpty(exception.Message);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Given_UntilNotAfterSince_When_Listed_Then_ItIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ClientException>(() => CreateHandler().HandleAsync(
                                                      new ListAccountingEventsClientRequest
                                                      {
                                                          Since = s_at,
                                                          Until = s_at
                                                      }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_ASnapshotSource_When_Asked_Then_ItsSnapshotIsReturned()
    {
        // Arrange
        var snapshot = new AccountingSnapshot(s_at, 800_000, [], new WalletBalanceBucket(1, 2, 3));
        var source = new Mock<INodeSnapshotSource>();
        source.Setup(s => s.TakeSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        // Act
        var response = await new AccountingSnapshotClientHandler(source.Object)
                          .HandleAsync(new AccountingSnapshotClientRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(snapshot, response.Snapshot);
    }

    [Fact]
    public async Task Given_NoSnapshotSource_When_Asked_Then_NotAvailable()
    {
        // Act & Assert
        var exception = await Assert.ThrowsAsync<ClientException>(() => new AccountingSnapshotClientHandler(null)
                                                                      .HandleAsync(new AccountingSnapshotClientRequest(),
                                                                          TestContext.Current.CancellationToken));
        Assert.Contains("not available", exception.Message);
    }

    private ListAccountingEventsClientHandler CreateHandler(IChannelMemoryRepository? channels = null) =>
        new(_unitOfWork.Object, NullLogger<ListAccountingEventsClientHandler>.Instance, _sealer.Object, channels);

    private static AccountingEventModel Sealed(long ledgerSeq) => new()
    {
        EventKey = $"k{ledgerSeq}",
        Kind = AccountingEventKind.ForwardSettled,
        OccurredAt = s_at,
        LedgerSeq = ledgerSeq,
        Hash = new byte[32]
    };

    private static ChannelModel CreateChannel(ShortChannelId scid)
    {
        var key = new CompactPubKey(
            Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619"));
        var party = new ChannelParty(LightningMoney.Satoshis(354),
                                     LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(1),
                                     30, LightningMoney.Satoshis(100_000), 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, false,
                                              FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, key, key, key, key, key, key);
        return new ChannelModel(channelParams, s_channel, null, null, true, null, null,
                                LightningMoney.Satoshis(100_000), keySet, 0, 0,
                                LightningMoney.Zero, null, 0, key, 0, ChannelState.Open,
                                ChannelVersion.V1)
        {
            ShortChannelId = scid
        };
    }
}