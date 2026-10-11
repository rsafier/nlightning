using Grpc.Core;

namespace NLightning.Daemon.Tests.Cashu;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Events;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Cashu.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using NLightning.Cashu.PaymentProcessor.Grpc;

/// <summary>
/// On-chain mint and melt quotes in the CDK payment processor (NUT-30, NL-997), as CDK's <c>cdk-bdk</c> serves them:
/// a fresh wallet address per mint quote, deposits reported by outpoint once confirmed, fee options per target, and
/// melts answered pending until their transaction has its confirmations (2 here).
/// </summary>
public sealed class CdkPaymentProcessorOnchainTests : CdkProcessorTestBase
{
    private const string MintAddress = "bcrt1qcashumint";
    private const string Destination = "bcrt1qdestination";

    private static readonly TxId s_depositTx = new(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());
    private static readonly TxId s_meltTx = new(Enumerable.Range(0, 32).Select(i => (byte)(64 - i)).ToArray());

    [Fact]
    public async Task Given_AnOnchainMintQuote_When_CreatedTwice_Then_OneFreshWalletAddressByQuoteId()
    {
        // Arrange
        Wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false))
              .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 7, false, MintAddress));

        // Act
        var first = await CreateMintQuoteAsync("mint-1");
        var replay = await CreateMintQuoteAsync("mint-1");

        // Assert
        Assert.Equal(PaymentIdentifierType.QuoteId, first.RequestIdentifier.Type);
        Assert.Equal("mint-1", first.RequestIdentifier.Id);
        Assert.Equal(MintAddress, first.Request);
        Assert.Equal(MintAddress, replay.Request);
        Wallet.Verify(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false), Times.Once);
        Assert.Equal(MintAddress, (await Quotes.GetAsync("mint-1"))!.Address);
    }

    [Fact]
    public async Task Given_ADepositToAMintQuote_When_ItReachesTwoConfirmations_Then_ReportedOnceByOutpoint()
    {
        // Arrange
        Wallet.Setup(w => w.GetUnusedAddressAsync(AddressType.P2Wpkh, false))
              .ReturnsAsync(new WalletAddressModel(AddressType.P2Wpkh, 7, false, MintAddress));
        await CreateMintQuoteAsync("mint-2");
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();

        // Act 1: one confirmation (tip 101), a dust deposit too
        Tip = 101;
        RaiseDeposit(MintAddress, 50_000, 101, outputIndex: 1);
        RaiseDeposit(MintAddress, 500, 101, outputIndex: 2);
        RaiseBlock(101);
        var early = await CheckDepositsAsync("mint-2");

        // Act 2: the second confirmation
        Tip = 102;
        RaiseBlock(102);

        // Assert: reported at the second confirmation only, the dust deposit never
        Assert.Empty(early.Payments);
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        var received = stream.ResponseStream.Current.PaymentReceived;
        Assert.Equal(PaymentIdentifierType.QuoteId, received.PaymentIdentifier.Type);
        Assert.Equal("mint-2", received.PaymentIdentifier.Id);
        Assert.Equal(50_000UL, received.PaymentAmount.Value);
        Assert.Equal($"{s_depositTx}:1", received.PaymentId);
        var check = Assert.Single((await CheckDepositsAsync("mint-2")).Payments);
        Assert.Equal($"{s_depositTx}:1", check.PaymentId);
        Assert.Single(await Quotes.GetDepositsAsync("mint-2"));
        Assert.Empty(await Quotes.ListUnreportedDepositsAsync());
    }

    [Fact]
    public async Task Given_AnAddressToPay_When_GetPaymentQuote_Then_OneFeeOptionPerTargetWithItsReserve()
    {
        // Arrange: fee rate 10,000/target sat/kw; the estimate is 1/10 of the rate in sat, reserve 150 %
        WalletSpend.Setup(w => w.EstimateWithdrawFeeAsync(It.IsAny<WalletWithdrawRequest>(),
                                                          It.IsAny<CancellationToken>()))
                   .ReturnsAsync((WalletWithdrawRequest r, CancellationToken _) =>
                                     new WalletWithdrawEstimate(LightningMoney.Satoshis(r.FeeRatePerKw!.Satoshi / 10),
                                                                r.FeeRatePerKw, 1_000, 1));

        // Act
        var quote = await Client.GetPaymentQuoteAsync(MeltQuoteRequest("melt-oc", 20_000), cancellationToken: Ct);

        // Assert: targets 2, 6, 144: rates 5,000, 1,666, 69: fees 500, 166, 6: reserves 750, 249, 9
        Assert.Equal(PaymentIdentifierType.QuoteId, quote.RequestIdentifier.Type);
        Assert.Equal("melt-oc", quote.RequestIdentifier.Id);
        Assert.Equal(20_000UL, quote.Amount.Value);
        Assert.Equal([750UL, 249UL, 9UL], quote.FeeOptions.Select(o => o.FeeReserve));
        Assert.Equal([2u, 6u, 144u], quote.FeeOptions.Select(o => o.EstimatedBlocks));
        Assert.Equal([0u, 1u, 2u], quote.FeeOptions.Select(o => o.FeeIndex));
        Assert.Equal(9UL, quote.Fee.Value);
        Assert.Equal(144u, quote.EstimatedBlocks);
        Assert.Equal(CashuQuoteState.Created, (await Quotes.GetAsync("melt-oc"))!.State);
    }

    [Fact]
    public async Task Given_AnEmptyWallet_When_GetPaymentQuote_Then_FailedPrecondition()
    {
        // Arrange
        WalletSpend.Setup(w => w.EstimateWithdrawFeeAsync(It.IsAny<WalletWithdrawRequest>(),
                                                          It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new InsufficientFundsException(LightningMoney.Satoshis(20_500), LightningMoney.Zero));

        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await Client.GetPaymentQuoteAsync(MeltQuoteRequest("melt-empty", 20_000), cancellationToken: Ct));

        // Assert
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(7u)]
    public async Task Given_AnOnchainMelt_When_ItsTransactionConfirmsTwice_Then_PendingThenPaidWithItsOutpoint(uint destinationOutputIndex)
    {
        // Arrange
        WalletWithdrawRequest? asked = null;
        WalletSpend.Setup(w => w.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
                   .Callback<WalletWithdrawRequest, CancellationToken>((r, _) => asked = r)
                   .ReturnsAsync(new WalletWithdrawResult(s_meltTx, LightningMoney.Satoshis(20_000),
                                                          LightningMoney.Satoshis(210), LightningMoney.Satoshis(5_000),
                                                          LightningMoney.Satoshis(1_666), 600, 1, LightningMoney.Zero,
                                                          true)
                   { DestinationOutputIndex = destinationOutputIndex });
        var broadcast = new BroadcastTransactionModel(new SignedTransaction(s_meltTx, [1, 2, 3]),
                                                      BroadcastPurpose.WalletSend, null, 100);
        Broadcasts.Setup(b => b.GetByTransactionIdAsync(s_meltTx)).ReturnsAsync(() => broadcast);
        using var stream = Client.WaitPaymentEvent(new EmptyRequest(), cancellationToken: Ct);
        await WaitForStreamAsync();

        // Act 1: the melt at fee option 1 (6 blocks) with the mint's limit
        var response = await Client.MakePaymentAsync(MeltRequest("melt-oc-pay", 20_000, feeIndex: 1, maxFee: 300),
                                                     cancellationToken: Ct);
        var replay = await Client.MakePaymentAsync(MeltRequest("melt-oc-pay", 20_000, feeIndex: 1, maxFee: 300),
                                                   cancellationToken: Ct);

        // Act 2: confirmed at 101, tip 101 (one confirmation), then tip 102
        broadcast.MarkConfirmed(101, Hash(0x77));
        Tip = 101;
        RaiseBlock(101);
        var oneConfirmation = await CheckMeltAsync("melt-oc-pay");
        Tip = 102;
        RaiseBlock(102);

        // Assert: pending with nothing spent, sent once at the target's rate and the mint's fee limit
        Assert.Equal(QuoteState.Pending, response.Status);
        Assert.Equal(0UL, response.TotalSpent.Value);
        Assert.Equal(PaymentIdentifierType.QuoteId, response.PaymentIdentifier.Type);
        Assert.Equal(QuoteState.Pending, replay.Status);
        WalletSpend.Verify(w => w.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()),
                           Times.Once);
        Assert.Equal(Destination, asked!.Address);
        Assert.Equal(LightningMoney.Satoshis(20_000), asked.Amount);
        Assert.Equal(LightningMoney.Satoshis(10_000 / 6), asked.FeeRatePerKw);
        Assert.Equal(LightningMoney.Satoshis(300), asked.MaxFee);
        Assert.Contains("cdk_quote=melt-oc-pay", asked.Labels.TagStrings);
        Assert.Equal(QuoteState.Pending, oneConfirmation.Status);
        Assert.Equal(destinationOutputIndex, (await Quotes.GetAsync("melt-oc-pay"))!.OutputIndex);
        Assert.True(await stream.ResponseStream.MoveNext(Bounded));
        var paid = stream.ResponseStream.Current.PaymentSuccessful;
        Assert.Equal("melt-oc-pay", paid.QuoteId);
        Assert.Equal(QuoteState.Paid, paid.Details.Status);
        Assert.Equal($"{s_meltTx}:{destinationOutputIndex}", paid.Details.PaymentProof);
        Assert.Equal(20_210UL, paid.Details.TotalSpent.Value);
        var check = await CheckMeltAsync("melt-oc-pay");
        Assert.Equal(QuoteState.Paid, check.Status);
        Assert.Equal($"{s_meltTx}:{destinationOutputIndex}", check.PaymentProof);
    }

    [Fact]
    public async Task Given_AnOnchainMeltWhoseBroadcastIsAbandoned_When_BlocksCome_Then_ItStaysPending()
    {
        // Arrange (NL-1001): the transaction was published; abandoned by the rebroadcaster it can still confirm
        WalletSpend.Setup(w => w.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new WalletWithdrawResult(s_meltTx, LightningMoney.Satoshis(20_000),
                                                          LightningMoney.Satoshis(210), LightningMoney.Satoshis(5_000),
                                                          LightningMoney.Satoshis(1_666), 600, 1, LightningMoney.Zero,
                                                          true));
        var broadcast = new BroadcastTransactionModel(new SignedTransaction(s_meltTx, [1, 2, 3]),
                                                      BroadcastPurpose.WalletSend, null, 100);
        var lookups = 0;
        Broadcasts.Setup(b => b.GetByTransactionIdAsync(s_meltTx))
                  .Callback(() => Interlocked.Increment(ref lookups))
                  .ReturnsAsync(() => broadcast);
        var response = await Client.MakePaymentAsync(MeltRequest("melt-oc-abandoned", 20_000, feeIndex: 0,
                                                                 maxFee: 300), cancellationToken: Ct);

        // Act: two blocks after the abandon; the chain loop is one reader, so the second lookup comes after the first
        // block's work was saved
        broadcast.MarkAbandoned();
        var before = Volatile.Read(ref lookups);
        Tip = 110;
        RaiseBlock(110);
        await WaitForLookupsAsync(() => Volatile.Read(ref lookups) > before);
        var afterFirst = Volatile.Read(ref lookups);
        Tip = 111;
        RaiseBlock(111);
        await WaitForLookupsAsync(() => Volatile.Read(ref lookups) > afterFirst);
        var check = await CheckMeltAsync("melt-oc-abandoned");

        // Assert: never FAILED (the mint would give the ecash back for coins that may still leave the wallet)
        Assert.Equal(QuoteState.Pending, response.Status);
        Assert.Equal(QuoteState.Pending, check.Status);
        Assert.Equal(CashuQuoteState.Pending, (await Quotes.GetAsync("melt-oc-abandoned"))!.State);
    }

    [Fact]
    public async Task Given_AWithdrawalAboveTheFeeLimit_When_MakePayment_Then_FailedAndTheMintMayRetry()
    {
        // Arrange
        WalletSpend.SetupSequence(w => w.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(),
                                                       It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new WalletSpendException(WalletSpendError.FeeAboveLimit, "fee above the limit"))
                   .ReturnsAsync(new WalletWithdrawResult(s_meltTx, LightningMoney.Satoshis(20_000),
                                                          LightningMoney.Satoshis(90), LightningMoney.Zero,
                                                          LightningMoney.Satoshis(253), 600, 1, LightningMoney.Zero,
                                                          true));

        // Act
        var failed = await Client.MakePaymentAsync(MeltRequest("melt-oc-fee", 20_000, feeIndex: 0, maxFee: 100),
                                                   cancellationToken: Ct);
        var stored = await Quotes.GetAsync("melt-oc-fee");
        var retried = await Client.MakePaymentAsync(MeltRequest("melt-oc-fee", 20_000, feeIndex: 2, maxFee: 100),
                                                    cancellationToken: Ct);

        // Assert
        Assert.Equal(QuoteState.Failed, failed.Status);
        Assert.Equal(CashuQuoteState.Failed, stored!.State);
        Assert.Equal("fee above the limit", stored.FailureReason);
        Assert.Equal(QuoteState.Pending, retried.Status);
    }

    [Fact]
    public async Task Given_AnUnknownFeeIndexOrADustAmount_When_MakePayment_Then_FailedWithoutSending()
    {
        // Act
        var badIndex = await Client.MakePaymentAsync(MeltRequest("melt-oc-index", 20_000, feeIndex: 3, maxFee: 100),
                                                     cancellationToken: Ct);
        var dust = await Client.MakePaymentAsync(MeltRequest("melt-oc-dust", 999, feeIndex: 0, maxFee: 100),
                                                 cancellationToken: Ct);

        // Assert
        Assert.Equal(QuoteState.Failed, badIndex.Status);
        Assert.Equal(QuoteState.Failed, dust.Status);
        WalletSpend.Verify(w => w.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    [Fact]
    public async Task Given_AnOnchainMeltInterruptedWhileSending_When_ReplayedAfterARestart_Then_PendingNotSentAgain()
    {
        // Arrange: saved Dispatching, no transaction recorded
        var quote = new Domain.Cashu.Models.CashuQuoteModel("melt-oc-crash", CashuQuoteMethod.Onchain,
                                                            CashuQuoteDirection.Outgoing,
                                                            LightningMoney.Satoshis(20_000), DateTimeOffset.UtcNow)
        {
            Address = Destination
        };
        quote.SetState(CashuQuoteState.Dispatching, DateTimeOffset.UtcNow);
        Quotes.Add(quote);

        // Act
        var replay = await Client.MakePaymentAsync(MeltRequest("melt-oc-crash", 20_000, feeIndex: 0, maxFee: 100),
                                                   cancellationToken: Ct);

        // Assert
        Assert.Equal(QuoteState.Pending, replay.Status);
        WalletSpend.Verify(w => w.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()),
                           Times.Never);
    }

    private async Task<CreatePaymentResponse> CreateMintQuoteAsync(string quoteId) =>
        await Client.CreatePaymentAsync(new CreatePaymentRequest
        {
            Options = new IncomingPaymentOptions { Onchain = new OnchainIncomingPaymentOptions { QuoteId = quoteId } }
        }, cancellationToken: Ct);

    private async Task<CheckIncomingPaymentResponse> CheckDepositsAsync(string quoteId) =>
        await Client.CheckIncomingPaymentAsync(new CheckIncomingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.QuoteId, Id = quoteId }
        }, cancellationToken: Ct);

    private async Task<MakePaymentResponse> CheckMeltAsync(string quoteId) =>
        await Client.CheckOutgoingPaymentAsync(new CheckOutgoingPaymentRequest
        {
            RequestIdentifier = new PaymentIdentifier { Type = PaymentIdentifierType.QuoteId, Id = quoteId }
        }, cancellationToken: Ct);

    private static PaymentQuoteRequest MeltQuoteRequest(string quoteId, ulong amountSat) => new()
    {
        Request = Destination,
        Unit = "sat",
        RequestType = OutgoingPaymentRequestType.Onchain,
        QuoteId = quoteId,
        OnchainOptions = new OnchainOutgoingPaymentOptions
        {
            Address = Destination,
            Amount = new AmountMessage { Value = amountSat, Unit = "sat" },
            QuoteId = quoteId
        }
    };

    private static MakePaymentRequest MeltRequest(string quoteId, ulong amountSat, uint feeIndex, ulong maxFee) => new()
    {
        PaymentOptions = new OutgoingPaymentVariant
        {
            Onchain = new OnchainOutgoingPaymentOptions
            {
                Address = Destination,
                Amount = new AmountMessage { Value = amountSat, Unit = "sat" },
                QuoteId = quoteId,
                FeeIndex = feeIndex,
                MaxFeeAmount = new AmountMessage { Value = maxFee, Unit = "sat" }
            }
        },
        Unit = "sat"
    };

    private void RaiseDeposit(string address, ulong amountSat, uint height, uint outputIndex) =>
        BlockchainMonitor.Raise(m => m.OnWalletMovementDetected += null,
                                new WalletMovementEventArgs(address, LightningMoney.Satoshis(amountSat), s_depositTx,
                                                            height, outputIndex));

    private static async Task WaitForLookupsAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        Assert.True(done(), "the chain loop did not look the melt's transaction up");
    }

    private void RaiseBlock(uint height) =>
        BlockchainMonitor.Raise(m => m.OnNewBlockDetected += null, new NewBlockEventArgs(height, Hash(0x55)));
}