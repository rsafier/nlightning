using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Integration.Tests.Persistence;

using Application.Channels.Handlers;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Onchain;
using Infrastructure.Repositories.Memory;

/// <summary>
/// The accounting feed's ChannelFunded and push events (NL-602 A1, NL-604, NL-605) on the real SQLite schema and unit of
/// work: staged by the funding confirmation's transition, saved with it, and never again for the same channel.
/// </summary>
public class ChannelFundedAccountingPersistenceTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private const uint FundingHeight = 800;

    [Fact]
    public async Task Given_OurFundedChannelWithAPushAndAFeeRow_When_FundingConfirms_Then_FundedAndPushSentAreSaved()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);
        await StoreChannelAsync(db, channel, LightningMoney.Satoshis(400_000), LightningMoney.Satoshis(1_234));

        // Act
        await ConfirmAsync(db, channel);

        // Assert
        var events = await ReadEventsAsync(db);
        Assert.Equal(2, events.Count);
        var funded = events.Single(e => e.Kind == AccountingEventKind.ChannelFunded);
        var fundingTxId = channel.FundingOutput!.TransactionId!.Value;
        Assert.Equal(AccountingEventKeys.ChannelFunded(channel.ChannelId, fundingTxId), funded.EventKey);
        Assert.Equal(1_000_000_000, funded.AmountMsat);
        Assert.Equal(1_234_000, funded.FeeMsat);
        Assert.Equal(AccountingFinality.Confirmed, funded.Finality);
        Assert.Equal(FundingHeight, funded.BlockHeight);
        Assert.Equal(fundingTxId, funded.TxId);
        Assert.Equal(1u, funded.OutputIndex);
        Assert.Equal(new ShortChannelId(FundingHeight, 3, 1), funded.ShortChannelId);
        Assert.Equal(SqliteDbTestContext.RemoteNodeId, funded.Counterparty);
        Assert.Equal(s_now, funded.OccurredAt);
        Assert.Equal("wallet", funded.Details["bucketFrom"]);
        Assert.Equal("channel", funded.Details["bucketTo"]);
        Assert.Equal("1000000", funded.Details["capacitySat"]);
        Assert.Equal("true", funded.Details["isInitiator"]);
        Assert.Equal("false", funded.Details["dualFunded"]);
        Assert.Equal("1234", funded.Details["fundingFeeSat"]);
        Assert.False(funded.Details.ContainsKey("feeUnknown"));

        var push = events.Single(e => e.Kind == AccountingEventKind.PushSent);
        Assert.Equal(AccountingEventKeys.Push(channel.ChannelId), push.EventKey);
        Assert.Equal(-400_000_000, push.AmountMsat);
        Assert.Equal(0, push.FeeMsat);
        Assert.Equal(FundingHeight, push.BlockHeight);

        // NL-602 A2 (the books, the funding entries alone: no wallet events here): the channel holds the capacity
        // less the push, the clearing account owes the wallet the capacity and the funding fee (the wallet events of
        // the funding transaction net it: our inputs spent minus our change)
        var books = BooksSimulator.Of(events);
        Assert.Equal(600_000_000, books[AccountRole.Channels]);
        Assert.Equal(400_000_000, books[AccountRole.PushSent]);
        Assert.Equal(1_234_000, books[AccountRole.FeeFunding]);
        Assert.Equal(-1_001_234_000, books[AccountRole.Clearing]);
    }

    [Fact]
    public async Task Given_AChannelOpenedWithALabelAndTags_When_ItsReloadedFundingConfirms_Then_FundedCarriesThem()
    {
        // Arrange (NL-602 A3-T1): openchannel --label/--tag stores them with the channel's first save; the funding
        // confirms after a restart (the channel read back from the database)
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);
        var labels = SourceLabels.Create("routing", ["purpose=liquidity", "peer=acme"]);
        channel.Label = labels.Label;
        channel.Tags = labels.CanonicalTags;
        await StoreChannelAsync(db, channel, LightningMoney.Zero, LightningMoney.Satoshis(1_234));
        ChannelModel reloaded;
        await using (var context = db.CreateDbContext())
            reloaded = (await new ChannelDbRepository(context, db.Sha256).GetByIdAsync(channel.ChannelId))!;

        // Act
        await ConfirmAsync(db, reloaded);

        // Assert
        var funded = Assert.Single(await ReadEventsAsync(db), e => e.Kind == AccountingEventKind.ChannelFunded);
        Assert.Equal("routing", funded.Details[AccountingDetailKeys.Label]);
        Assert.Equal("acme", funded.Details["tag.peer"]);
        Assert.Equal("liquidity", funded.Details["tag.purpose"]);
        Assert.Equal("wallet", funded.Details["bucketFrom"]);
    }

    [Fact]
    public async Task Given_APeerFundedChannelWithAPush_When_FundingConfirms_Then_NothingFundedByUsAndPushReceived()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(false, state: ChannelState.ReadyForThem);
        await StoreChannelAsync(db, channel, LightningMoney.Satoshis(600_000), fundingFee: null);

        // Act
        await ConfirmAsync(db, channel);

        // Assert
        Assert.Equal(ChannelState.Open, channel.State);
        var events = await ReadEventsAsync(db);
        Assert.Equal(2, events.Count);
        var funded = events.Single(e => e.Kind == AccountingEventKind.ChannelFunded);
        Assert.Equal(0, funded.AmountMsat);
        Assert.Equal(0, funded.FeeMsat);
        Assert.Equal("false", funded.Details["isInitiator"]);
        Assert.False(funded.Details.ContainsKey("feeUnknown"));
        var push = events.Single(e => e.Kind == AccountingEventKind.PushReceived);
        Assert.Equal(600_000_000, push.AmountMsat);

        // NL-602 A2 (the books): only the push moves, as income; the wallet is not involved
        var books = BooksSimulator.Of(events);
        Assert.Equal(600_000_000, books[AccountRole.Channels]);
        Assert.Equal(-600_000_000, books[AccountRole.PushReceived]);
        Assert.Equal(0, books[AccountRole.Clearing]);
    }

    [Fact]
    public async Task Given_NoPushAtTheOpen_When_FundingConfirms_Then_OnlyFundedIsSaved()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);
        await StoreChannelAsync(db, channel, LightningMoney.Zero, LightningMoney.Satoshis(500));

        // Act
        await ConfirmAsync(db, channel);

        // Assert
        var funded = Assert.Single(await ReadEventsAsync(db));
        Assert.Equal(AccountingEventKind.ChannelFunded, funded.Kind);
        Assert.Equal("0", funded.Details["pushMsat"]);
    }

    [Fact]
    public async Task Given_AChannelOpenedBeforeTheFeed_When_FundingConfirms_Then_FeeAndPushAreMarkedUnknown()
    {
        // Arrange: no push recorded (null) and a funding row without a fee
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);
        await StoreChannelAsync(db, channel, push: null, fundingFee: null);

        // Act
        await ConfirmAsync(db, channel);

        // Assert
        var funded = Assert.Single(await ReadEventsAsync(db));
        Assert.Equal(1_000_000_000, funded.AmountMsat);
        Assert.Equal(0, funded.FeeMsat);
        Assert.Equal("true", funded.Details["feeUnknown"]);
        Assert.Equal("true", funded.Details["pushUnknown"]);
    }

    [Fact]
    public async Task Given_AConfirmedChannel_When_TheConfirmationIsHandledAgain_Then_NoEventIsAdded()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var channel = SqliteDbTestContext.CreateChannel(true, state: ChannelState.V1FundingSigned);
        await StoreChannelAsync(db, channel, LightningMoney.Satoshis(400_000), LightningMoney.Satoshis(1_234));
        await ConfirmAsync(db, channel);

        // Act: the channel is ReadyForUs now (a replayed block, a restart's catch-up)
        await ConfirmAsync(db, channel);

        // Assert
        Assert.Equal(ChannelState.ReadyForUs, channel.State);
        Assert.Equal(2, (await ReadEventsAsync(db)).Count);
    }

    private static async Task StoreChannelAsync(SqliteDbTestContext db, ChannelModel channel, LightningMoney? push,
                                                LightningMoney? fundingFee)
    {
        await using var context = db.CreateDbContext();
        await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
        if (push is not null)
            await new ChannelFundingDbRepository(context).SetPushAmountAsync(channel.ChannelId, push);

        var fundingTx = new SignedTransaction(channel.FundingOutput!.TransactionId!.Value, [0x02, 0x00, 0x00, 0x00]);
        new BroadcastTransactionDbRepository(context).Add(
            new BroadcastTransactionModel(fundingTx, BroadcastPurpose.Funding, channel.ChannelId, 790,
                                          fee: fundingFee));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task ConfirmAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        // What ChannelManager.ConfirmFundingAsync sets before it runs the handler
        channel.FundingCreatedAtBlockHeight = FundingHeight;
        channel.ShortChannelId = new ShortChannelId(FundingHeight, 3, 1);

        var memoryRepository = new Mock<IChannelMemoryRepository>();
        memoryRepository.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
        var signer = new Mock<ILightningSigner>();
        signer.Setup(x => x.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
              .Returns(SqliteDbTestContext.RemoteNodeId);
        var clock = new Mock<TimeProvider>();
        clock.Setup(c => c.GetUtcNow()).Returns(s_now);

        using var unitOfWork = new UnitOfWork(db.CreateDbContext(), new Mock<ILogger<UnitOfWork>>().Object, db.Sha256,
                                              new UtxoMemoryRepository());
        var handler = new FundingConfirmedMessageHandler(memoryRepository.Object, signer.Object,
                                                         new Mock<ILogger<FundingConfirmedMessageHandler>>().Object,
                                                         new Mock<IMessageFactory>().Object, unitOfWork, clock.Object);
        await handler.HandleAsync(channel);
    }

    private static async Task<IReadOnlyList<AccountingEventModel>> ReadEventsAsync(SqliteDbTestContext db)
    {
        await using var context = db.CreateDbContext();
        return await new AccountingEventDbRepository(context).GetUnsealedAsync(100,
                                                                              TestContext.Current.CancellationToken);
    }
}