using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Accounting;

namespace NLightning.Application.Tests.Channels.Accounting;

using Application.Accounting.Books;
using Application.Channels.Accounting;
using Domain.Accounting.Books;
using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Handlers;

/// <summary>
/// The accounting feed of a liquidity ads purchase (NL-850 LA5): the fee event of either side, its replacement by an
/// RBF, and the funding and splice events that book our contribution without the fee, so the channels account matches
/// the balance the node reports after the fee moved.
/// </summary>
public class LiquidityAccountingEventsTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x4c, 32).ToArray());
    private static readonly TxId s_fundingTxId = new(Enumerable.Repeat((byte)0x5a, 32).ToArray());
    private static readonly TxId s_rbfTxId = new(Enumerable.Repeat((byte)0x6b, 32).ToArray());
    private static readonly CompactPubKey s_peer = NormalOperationTestContext.PeerNodeId;

    // 400,000 sat requested: 1,500 sat mining fee, 3,000 sat service fee
    private const ulong MiningFeeSat = 1_500;
    private const ulong ServiceFeeSat = 3_000;
    private const long FeeMsat = 4_500_000;

    #region The fee event

    [Fact]
    public void Given_ABuyersPurchase_When_Built_Then_ItIsAPaidFeeOutOfTheChannelWithEveryDetail()
    {
        // Act
        var built = ChannelAccountingEvents.BuildLiquidityPurchase(
            s_channelId, s_fundingTxId, true, s_peer, 400_000, 410_000, MiningFeeSat, ServiceFeeSat,
            AccountingLiquidityKind.Open, s_at, 800, new ShortChannelId(800, 1, 0));

        // Assert
        Assert.NotNull(built);
        Assert.Equal(AccountingEventKind.LiquidityFeePaid, built.Kind);
        Assert.Equal(AccountingEventKeys.LiquidityFee(s_channelId, s_fundingTxId), built.EventKey);
        Assert.Equal(-FeeMsat, built.AmountMsat);
        Assert.Equal(FeeMsat, built.FeeMsat);
        Assert.Equal(AccountingFinality.Confirmed, built.Finality);
        Assert.Equal((uint?)800, built.BlockHeight);
        Assert.Equal(s_peer, built.Counterparty);
        Assert.Equal(s_fundingTxId, built.TxId);
        Assert.Equal("buyer", built.Details[AccountingDetailKeys.LiquidityRole]);
        Assert.Equal("open", built.Details[AccountingDetailKeys.Kind]);
        Assert.Equal(s_channelId.ToString(), built.Details[AccountingDetailKeys.PurchaseChannelId]);
        Assert.Equal(s_fundingTxId.ToString(), built.Details[AccountingDetailKeys.PurchaseFundingTxId]);
        Assert.Equal(s_peer.ToString(), built.Details[AccountingDetailKeys.PurchasePeer]);
        Assert.Equal("400000", built.Details[AccountingDetailKeys.RequestedSat]);
        Assert.Equal("410000", built.Details[AccountingDetailKeys.ContributedSat]);
        Assert.Equal("1500000", built.Details[AccountingDetailKeys.MiningFeeMsat]);
        Assert.Equal("3000000", built.Details[AccountingDetailKeys.ServiceFeeMsat]);
        Assert.Equal(AccountingDetailKeys.ChannelBucket, built.Details[AccountingDetailKeys.BucketFrom]);
    }

    [Fact]
    public void Given_ASellersPurchase_When_Built_Then_ItIsAnEarnedFeeIntoTheChannel()
    {
        // Act
        var built = ChannelAccountingEvents.BuildLiquidityPurchase(
            s_channelId, s_fundingTxId, false, s_peer, 400_000, 400_000, MiningFeeSat, ServiceFeeSat,
            AccountingLiquidityKind.Splice, s_at);

        // Assert
        Assert.NotNull(built);
        Assert.Equal(AccountingEventKind.LiquidityFeeEarned, built.Kind);
        Assert.Equal(FeeMsat, built.AmountMsat);
        Assert.Equal(0, built.FeeMsat);
        Assert.Equal(AccountingFinality.Final, built.Finality);
        Assert.Null(built.BlockHeight);
        Assert.Equal("seller", built.Details[AccountingDetailKeys.LiquidityRole]);
        Assert.Equal("splice", built.Details[AccountingDetailKeys.Kind]);
        Assert.Equal(AccountingDetailKeys.ChannelBucket, built.Details[AccountingDetailKeys.BucketTo]);
    }

    [Fact]
    public async Task Given_AZeroFee_When_Recorded_Then_NothingIsStaged()
    {
        // Arrange
        var feed = new RecordingFeed();

        // Act
        await RecordAsync(feed, true, 0, 0);

        // Assert
        Assert.Null(ChannelAccountingEvents.BuildLiquidityPurchase(s_channelId, s_fundingTxId, true, s_peer, 1, 1, 0,
                                                                   0, AccountingLiquidityKind.Open, s_at));
        Assert.Empty(feed.Events);
    }

    [Fact]
    public async Task Given_APurchaseRecordedTwice_When_Recorded_Then_ItIsStagedOnce()
    {
        // Arrange
        var feed = new RecordingFeed();

        // Act
        await RecordAsync(feed, true);
        await RecordAsync(feed, true);

        // Assert
        var recorded = Assert.Single(feed.Events);
        Assert.Equal(AccountingEventKeys.LiquidityFee(s_channelId, s_fundingTxId), recorded.EventKey);
    }

    [Fact]
    public async Task Given_AnRbfThatReplacesTheAttempt_When_TheOldIsReplaced_Then_OnlyTheNewFeeStaysInTheBooks()
    {
        // Arrange: the first attempt bought at one fee, the RBF at a higher one (a higher mining fee)
        var feed = new RecordingFeed();
        await RecordAsync(feed, true);

        // Act
        await ChannelAccountingEvents.RecordLiquidityPurchaseReplacedAsync(Uow(feed), s_channelId, s_fundingTxId,
                                                                           s_at, NullLogger.Instance, s_rbfTxId);
        await ChannelAccountingEvents.RecordLiquidityPurchaseReplacedAsync(Uow(feed), s_channelId, s_fundingTxId,
                                                                           s_at, NullLogger.Instance, s_rbfTxId);
        await RecordAsync(feed, true, 2_000, ServiceFeeSat, s_rbfTxId, AccountingLiquidityKind.Rbf);

        // Assert: one reversal, keyed by the reversed event, and the books hold the new fee only
        var replaced = Assert.Single(feed.Events, e => e.Kind == AccountingEventKind.Reversal);
        var original = AccountingEventKeys.LiquidityFee(s_channelId, s_fundingTxId);
        Assert.Equal(AccountingEventKeys.Replaced(original), replaced.EventKey);
        Assert.Equal(original, replaced.Details[AccountingConfirmations.ReversesDetail]);
        Assert.Equal(nameof(AccountingEventKind.LiquidityFeePaid),
                     replaced.Details[AccountingConfirmations.OriginalKindDetail]);
        Assert.Equal(s_rbfTxId.ToString(), replaced.Details[AccountingDetailKeys.ReplacedBy]);
        Assert.Equal((FeeMsat, -FeeMsat), (replaced.AmountMsat, replaced.FeeMsat));
        var books = BooksSimulator.Of(feed.Events);
        Assert.Equal(-5_000_000, books[AccountRole.Channels]);
        Assert.Equal(5_000_000, books[AccountRole.LiquidityFees]);
    }

    [Fact]
    public async Task Given_AReplacedAttemptThatConfirmsAfterAll_When_RecordedAgain_Then_ItTakesTheNextKey()
    {
        // Arrange
        var feed = new RecordingFeed();
        await RecordAsync(feed, false);
        await ChannelAccountingEvents.RecordLiquidityPurchaseReplacedAsync(Uow(feed), s_channelId, s_fundingTxId,
                                                                           s_at, NullLogger.Instance);

        // Act
        await RecordAsync(feed, false);
        await RecordAsync(feed, false);

        // Assert
        var baseKey = AccountingEventKeys.LiquidityFee(s_channelId, s_fundingTxId);
        Assert.Equal([baseKey, AccountingEventKeys.Replaced(baseKey), AccountingEventKeys.Reconfirmed(baseKey, 2)],
                     feed.Events.Select(e => e.EventKey));
        var books = BooksSimulator.Of(feed.Events);
        Assert.Equal(FeeMsat, books[AccountRole.Channels]);
        Assert.Equal(-FeeMsat, books[AccountRole.LiquidityIncome]);
    }

    [Fact]
    public async Task Given_NoPurchaseRecorded_When_Replaced_Then_NothingIsStaged()
    {
        // Arrange
        var feed = new RecordingFeed();

        // Act
        await ChannelAccountingEvents.RecordLiquidityPurchaseReplacedAsync(Uow(feed), s_channelId, s_fundingTxId,
                                                                           s_at, NullLogger.Instance);

        // Assert
        Assert.Empty(feed.Events);
    }

    #endregion

    #region Funding and splice without the fee

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ADualFundedOpenWithALiquidityFee_When_ItsFundingConfirms_Then_TheBooksReconcileClean(
        bool weBought)
    {
        // Arrange: the buyer adds 500,000 sat (wallet 600,000 in, 99,300 change: 700 sat of fee), the seller
        // 400,000 sat (450,000 in, 49,700 change: 300 sat); the fee moved 4,500 sat from the buyer to the seller
        var (contributionSat, inputSat, changeSat) = weBought ? (500_000L, 600_000L, 99_300L)
                                                              : (400_000L, 450_000L, 49_700L);
        var signedFeeMsat = weBought ? FeeMsat : -FeeMsat;
        var localBalanceMsat = contributionSat * 1_000 - signedFeeMsat;
        var channel = CreateChannel(ChannelVersion.V2, 900_000, localBalanceMsat, s_fundingTxId);
        var feed = new RecordingFeed();
        var unitOfWork = Uow(feed, Attempt(s_fundingTxId, contributionSat, inputSat, changeSat));

        // Act
        await ChannelAccountingEvents.StageChannelFundedAsync(unitOfWork, channel, s_at, NullLogger.Instance,
                                                              signedFeeMsat);
        await RecordAsync(feed, weBought, blockHeight: 800);

        // Assert: ChannelFunded is the contribution with the on-chain fee share only
        var funded = Assert.Single(feed.Events, e => e.Kind == AccountingEventKind.ChannelFunded);
        Assert.Equal(contributionSat * 1_000, funded.AmountMsat);
        Assert.Equal((inputSat - changeSat - contributionSat) * 1_000, funded.FeeMsat);
        Assert.Equal(signedFeeMsat.ToString(), funded.Details[AccountingDetailKeys.LiquidityFeeMsat]);

        // The channels account is the balance the node reports, the fee an expense or income
        var books = BooksSimulator.Of(feed.Events);
        Assert.Equal(localBalanceMsat, books[AccountRole.Channels]);
        Assert.Equal(weBought ? FeeMsat : 0, books[AccountRole.LiquidityFees]);
        Assert.Equal(weBought ? 0 : -FeeMsat, books[AccountRole.LiquidityIncome]);
        Assert.Equal(-(inputSat - changeSat) * 1_000, books[AccountRole.Clearing]);
        var channels = Reconcile(books, localBalanceMsat);
        Assert.Equal(0, channels.DriftMsat);
    }

    [Fact]
    public async Task Given_ADualFundedOpenWithoutItsStoredContribution_When_Built_Then_TheBalancePlusTheFeeStandsIn()
    {
        // Arrange: no stored attempt: the channel balance (after a 4,500 sat fee we paid) is all there is
        var channel = CreateChannel(ChannelVersion.V2, 900_000, 495_500_000, s_fundingTxId);
        var feed = new RecordingFeed();

        // Act
        var built = await ChannelAccountingEvents.BuildChannelFundedAsync(Uow(feed), channel, s_at,
                                                                          liquidityFeeMsat: FeeMsat);

        // Assert
        var funded = Assert.Single(built);
        Assert.Equal(500_000_000, funded.AmountMsat);
        Assert.Equal("true", funded.Details["feeUnknown"]);
    }

    [Fact]
    public async Task Given_NoLiquidityFee_When_AFundingIsBuilt_Then_TheEventIsAsBefore()
    {
        // Arrange
        var channel = CreateChannel(ChannelVersion.V2, 900_000, 500_000_000, s_fundingTxId);
        var feed = new RecordingFeed();
        var unitOfWork = Uow(feed, Attempt(s_fundingTxId, 500_000, 600_000, 99_300));

        // Act
        var built = await ChannelAccountingEvents.BuildChannelFundedAsync(unitOfWork, channel, s_at);

        // Assert
        var funded = Assert.Single(built);
        Assert.Equal((500_000_000L, 700_000L), (funded.AmountMsat, funded.FeeMsat));
        Assert.False(funded.Details.ContainsKey(AccountingDetailKeys.LiquidityFeeMsat));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ASpliceWithALiquidityFee_When_ItLocks_Then_SpliceLockedBooksTheContributionWithoutTheFee(
        bool weBought)
    {
        // Arrange: the buyer splices in 200,000 sat (300,000 in, 99,000 change: 1,000 sat of fee), the seller
        // 300,000 sat (350,000 in, 49,500 change: 500 sat); the balance delta includes the 4,500 sat fee
        var (contributionSat, inputSat, changeSat) = weBought ? (200_000L, 300_000L, 99_000L)
                                                              : (300_000L, 350_000L, 49_500L);
        var signedFeeMsat = weBought ? FeeMsat : -FeeMsat;
        var deltaMsat = contributionSat * 1_000 - signedFeeMsat;
        var channel = CreateChannel(ChannelVersion.V1, 900_000, 450_000_000, s_fundingTxId);
        var key = NormalOperationTestContext.Point(0x01);
        var previous = new ChannelFunding(s_fundingTxId, 0, 900_000, key, key, 0, 0, 0, ChannelFundingKind.Initial,
                                          ChannelFundingStatus.Current);
        var locked = new ChannelFunding(s_rbfTxId, 0, 1_400_000, key, key, 1, deltaMsat,
                                        500_000_000 - deltaMsat, ChannelFundingKind.Splice,
                                        ChannelFundingStatus.Pending, ConfirmedHeight: 900,
                                        ShortChannelId: new ShortChannelId(900, 1, 0));
        var feed = new RecordingFeed();
        var unitOfWork = Uow(feed, Attempt(s_rbfTxId, contributionSat, inputSat, changeSat, isSplice: true));

        // Act
        await ChannelAccountingEvents.StageSpliceLockedAsync(unitOfWork, channel, locked, previous, s_at,
                                                             NullLogger.Instance, signedFeeMsat);
        await RecordAsync(feed, weBought, fundingTxId: s_rbfTxId, kind: AccountingLiquidityKind.Splice,
                          blockHeight: 900);

        // Assert
        var splice = Assert.Single(feed.Events, e => e.Kind == AccountingEventKind.SpliceLocked);
        Assert.Equal(contributionSat * 1_000, splice.AmountMsat);
        Assert.Equal((inputSat - changeSat - contributionSat) * 1_000, splice.FeeMsat);
        Assert.Equal(deltaMsat.ToString(), splice.Details["grossDeltaMsat"]);
        var books = BooksSimulator.Of(feed.Events);
        Assert.Equal(deltaMsat, books[AccountRole.Channels]);
        Assert.Equal(-(inputSat - changeSat) * 1_000, books[AccountRole.Clearing]);
    }

    #endregion

    private static Task RecordAsync(RecordingFeed feed, bool weBought, ulong miningFeeSat = MiningFeeSat,
                                    ulong serviceFeeSat = ServiceFeeSat, TxId? fundingTxId = null,
                                    AccountingLiquidityKind kind = AccountingLiquidityKind.Open,
                                    uint? blockHeight = null) =>
        ChannelAccountingEvents.RecordLiquidityPurchaseAsync(Uow(feed), s_channelId, fundingTxId ?? s_fundingTxId,
                                                             weBought, s_peer, 400_000, 400_000, miningFeeSat,
                                                             serviceFeeSat, kind, s_at, NullLogger.Instance,
                                                             blockHeight);

    private static AccountingReconcileLine Reconcile(BooksSimulator books, long localBalanceMsat)
    {
        var bucket = new ChannelBalanceBucket(s_channelId, new ShortChannelId(800, 1, 0), ChannelState.Open, s_peer,
                                              900_000_000, localBalanceMsat, 900_000_000 - localBalanceMsat, 0, 0, 0,
                                              0, 0, true);
        var snapshot = new AccountingSnapshot(s_at, 801, [bucket], new WalletBalanceBucket(0, 0, 0));
        return AccountingBooksService.BuildReconcileLines(snapshot, books.Balances)
                                     .Single(l => l.Account == AccountRole.Channels);
    }

    private static IUnitOfWork Uow(RecordingFeed feed, InteractiveTxSessionModel? attempt = null)
    {
        var sessions = new Mock<IInteractiveTxSessionDbRepository>();
        sessions.Setup(s => s.GetByChannelIdAsync(It.IsAny<ChannelId>()))
                .ReturnsAsync(attempt is null ? [] : [attempt]);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.AccountingEventDbRepository).Returns(feed);
        unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(sessions.Object);
        return unitOfWork.Object;
    }

    /// <summary>A constructed negotiation where our wallet input and change around the shared output are ours.</summary>
    private static InteractiveTxSessionModel Attempt(TxId txId, long contributionSat, long inputSat, long changeSat,
                                                     bool isSplice = false)
    {
        var script = new BitcoinScript(Enumerable.Repeat((byte)0x51, 22).ToArray());
        List<InteractiveTxInput> inputs =
        [
            new(0, InteractiveTxParty.Local, new TxId(Enumerable.Repeat((byte)0x71, 32).ToArray()), 0, 0xFFFFFFFD,
                LightningMoney.Satoshis(inputSat), script, [0x02], false),
            new(1, InteractiveTxParty.Remote, new TxId(Enumerable.Repeat((byte)0x72, 32).ToArray()), 0, 0xFFFFFFFD,
                LightningMoney.Satoshis(1_000_000), script, [0x02], false)
        ];
        if (isSplice)
            inputs.Add(new InteractiveTxInput(2, InteractiveTxParty.Local, s_fundingTxId, 0, 0xFFFFFFFD,
                                              LightningMoney.Satoshis(900_000), script, null, true));
        List<InteractiveTxOutput> outputs =
        [
            new(0, InteractiveTxParty.Local, LightningMoney.Satoshis(changeSat), script, false),
            new(2, InteractiveTxParty.Local, LightningMoney.Satoshis(1_400_000), script, true)
        ];
        var constructed = new ConstructedInteractiveTx(txId, [0x02], 0, inputs, outputs, 1_000, 1);
        return new InteractiveTxSessionModel
        {
            ChannelId = s_channelId,
            SessionId = Guid.NewGuid(),
            Purpose = isSplice ? InteractiveTxPurpose.Splice : InteractiveTxPurpose.DualFund,
            IsInitiator = true,
            FeeratePerKw = 2_500,
            Locktime = 0,
            Inputs = inputs,
            Outputs = outputs,
            LocalContribution = InteractiveTxContribution.Empty,
            ConstructedTx = constructed,
            LocalFundingSatoshis = contributionSat,
            State = InteractiveTxSessionState.Signed,
            CreatedAt = s_at
        };
    }

    private static ChannelModel CreateChannel(ChannelVersion version, long capacitySat, long localBalanceMsat,
                                              TxId fundingTxId)
    {
        var capacity = LightningMoney.Satoshis(capacitySat);
        var local = LightningMoney.MilliSatoshis(localBalanceMsat);
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, capacity, 144);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var fundingOutput = new FundingOutputInfo(capacity, NormalOperationTestContext.Point(0x01),
                                                  NormalOperationTestContext.Point(0x02))
        {
            TransactionId = fundingTxId,
            Index = 0
        };
        var keySet = new ChannelKeySetModel(0, NormalOperationTestContext.Point(0x01),
                                            NormalOperationTestContext.Point(0x03),
                                            NormalOperationTestContext.Point(0x04),
                                            NormalOperationTestContext.Point(0x05),
                                            NormalOperationTestContext.Point(0x06),
                                            NormalOperationTestContext.Point(0x07));
        return new ChannelModel(channelParams, s_channelId, null, fundingOutput, true, null, null, local, keySet, 0, 0,
                                capacity - local, keySet, 0, s_peer, 0, ChannelState.Open, version);
    }

    /// <summary>The feed's table in memory: staged events, with the key lookups the writers use.</summary>
    private sealed class RecordingFeed : IAccountingEventDbRepository
    {
        public List<AccountingEventModel> Events { get; } = [];

        public void Add(AccountingEventModel accountingEvent) => Events.Add(accountingEvent);

        public Task<bool> ExistsAsync(string eventKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Events.Any(e => e.EventKey == eventKey));

        public Task<IReadOnlyList<AccountingEventModel>> GetUnsealedAsync(int max,
                                                                          CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>(Events);

        public Task<AccountingChainTip> GetChainTipAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountingChainTip(0, new byte[32]));

        public Task<IReadOnlySet<string>> GetSealedKeysAsync(IReadOnlyCollection<string> eventKeys,
                                                             CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task ApplySealsAsync(IReadOnlyList<AccountingSeal> seals, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AccountingEventModel>> ListAsync(AccountingEventQuery query,
                                                                   CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>(Events);

        public Task<IReadOnlyList<AccountingEventModel>> GetSealedRangeAsync(
            long fromLedgerSeq, int take, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

        public Task<IReadOnlyList<AccountingEventModel>> GetAtOrAboveHeightAsync(
            uint height, IReadOnlyCollection<AccountingEventKind> kinds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>([]);

        public Task<IReadOnlyList<AccountingEventModel>> GetByKeyPrefixAsync(
            string keyPrefix, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AccountingEventModel>>(
                Events.Where(e => e.EventKey.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList());
    }
}