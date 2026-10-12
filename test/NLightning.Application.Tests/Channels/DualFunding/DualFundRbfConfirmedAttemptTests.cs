using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Application.InteractiveTx.Interfaces;
using Application.InteractiveTx.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.LiquidityAds.Enums;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using InteractiveTx.TestDoubles;
using static LiquidityAdsKit;

/// <summary>
/// NL-867 (found live on Mutinynet, 2026-10-03): the first attempt of a dual-funded open confirmed in the same second
/// the opener sent <c>tx_init_rbf</c>; the accepter ran the RBF negotiation, then failed to sign its inputs (the wallet
/// had seen them spent by the confirmed attempt), answered with an internal-error warning and a disconnection, and
/// every reconnection's <c>next_funding</c> retransmission did it again until the funding depth. BOLT 2: "If the previous
/// transaction confirms in the middle of an RBF attempt, the attempt MUST be abandoned", with <c>tx_abort</c> before our
/// <c>tx_signatures</c> (IT-ABT-01). On <see cref="DualFundHarness"/>: an earlier attempt confirming during the
/// negotiation, after the <c>commitment_signed</c> and before the <c>tx_signatures</c>, and across a reconnection, in
/// both roles and with a liquidity purchase, ends the RBF attempt with <c>tx_abort</c>, no failed connection, the
/// reservation and the sale slot given back, the purchase replaced and the bump answered at once.
/// </summary>
public class DualFundRbfConfirmedAttemptTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;
    private const uint BumpFeeratePerKw = 5_000;

    [Fact]
    public async Task Given_ARunningRbfNegotiation_When_TheFirstAttemptConfirms_Then_ItIsAbandonedWithTxAbort()
    {
        // Arrange: Bob funded nothing at the open and contributes fresh inputs to Alice's bump
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var bump = harness.Alice.DualFund.BumpAsync(channelId, BumpFeeratePerKw, s_aliceShare,
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, m) => m is TxCompleteMessage);
        var bobReservation = Assert.Single(harness.Bob.Wallet.ActiveReservations);

        // Act: the first attempt is mined while the attempt is being negotiated
        foreach (var node in harness.Nodes)
            await node.SeeInBlockAsync(channelId, firstTxId);
        await harness.PumpAsync();

        // Assert: our tx_abort, the bump answered, Bob's fresh reservation released, both still on the first attempt
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {firstTxId} confirmed", result.FailureReason);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxAbortMessage });
        Assert.Contains(bobReservation, harness.Bob.Wallet.Released);
        await AssertBackOnFirstAttemptAsync(harness, channelId, firstTxId);
    }

    [Fact]
    public async Task Given_TheCommitmentSignedExchanged_When_TheFirstAttemptConfirms_Then_AbandonedBeforeTxSignatures()
    {
        // Arrange: both contributed to the first attempt, so the RBF re-adds inputs of both that the block spends
        await using var harness = await CreateBothFundedAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, BumpFeeratePerKw, s_aliceShare,
                                                    TestContext.Current.CancellationToken);
        await PumpUntilBothConstructedAsync(harness, channelId, firstTxId);

        // Act
        foreach (var node in harness.Nodes)
            await node.SeeInBlockAsync(channelId, firstTxId);
        await harness.PumpAsync();

        // Assert: nobody signed the attempt, both rows aborted, the bump answered
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {firstTxId} confirmed", result.FailureReason);
        Assert.DoesNotContain(harness.Transcript,
                              t => t.Message is TxSignaturesMessage signatures
                                && new TxId(signatures.Payload.TxId) != firstTxId);
        await AssertAttemptRowsAbortedAsync(harness, channelId, firstTxId);
        await AssertBackOnFirstAttemptAsync(harness, channelId, firstTxId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_TheBlockNotRaisedYet_When_TheSignerIsAboutToSign_Then_TxAbortInsteadOfAFailedConnection(
        bool walletOnly)
    {
        // Arrange: Bob (the smaller input) signs first; only his node knows of the confirmation, either through his
        // wallet that saw his input spent (the live case) or through the attempt's watch
        await using var harness = await CreateBothFundedAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, BumpFeeratePerKw, s_aliceShare,
                                                    TestContext.Current.CancellationToken);
        await PumpUntilBothConstructedAsync(harness, channelId, firstTxId);
        if (walletOnly)
            await MarkWalletSpentAsync(harness.Bob, channelId, firstTxId);
        else
            await harness.Bob.SeeInBlockAsync(channelId, firstTxId, raiseBlock: false, spendWallet: false);

        // Act: the commitment_signed exchange goes on, and Bob is asked for his tx_signatures
        await harness.PumpAsync();

        // Assert: Bob's tx_abort instead of an exception (an internal-error warning and a disconnection live)
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.NotNull(result.FailureReason);
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxAbortMessage });
        await AssertAttemptRowsAbortedAsync(harness, channelId, firstTxId);
        await AssertBackOnFirstAttemptAsync(harness, channelId, firstTxId);
    }

    [Fact]
    public async Task Given_ADisconnectionAfterTheCommitmentSigned_When_ReconnectingAfterTheConfirmation_Then_TxAbort()
    {
        // Arrange: the link drops with Alice's commitment_signed for the attempt lost (Bob will ask for it again)
        await using var harness = await CreateBothFundedAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, BumpFeeratePerKw, s_aliceShare,
                                                    TestContext.Current.CancellationToken);
        await PumpUntilBothConstructedAsync(harness, channelId, firstTxId);
        await harness.DisconnectAsync();

        // ...the first attempt confirmed meanwhile: the watches and wallets know, no block event ran the abandonment
        foreach (var node in harness.Nodes)
            await node.SeeInBlockAsync(channelId, firstTxId, raiseBlock: false);
        var sentBefore = harness.Transcript.Count;

        // Act
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: next_funding answered with tx_abort, no commitment_signed for the attempt again, no failure
        var afterReconnect = harness.Transcript.Skip(sentBefore).ToList();
        Assert.Contains(afterReconnect, t => t.Message is TxAbortMessage);
        Assert.DoesNotContain(afterReconnect, t => t.Message is CommitmentSignedMessage);
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {firstTxId} confirmed", result.FailureReason);
        await AssertAttemptRowsAbortedAsync(harness, channelId, firstTxId);
        await AssertBackOnFirstAttemptAsync(harness, channelId, firstTxId);
    }

    [Fact]
    public async Task Given_TheAccepterBumps_When_TheFirstAttemptConfirmsDuringItsNegotiation_Then_ItsBumpEndsAtOnce()
    {
        // Arrange: Bob (the accepter) is the interactive-tx initiator of his bump
        await using var harness = await CreateBothFundedAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Bob.DualFund.BumpAsync(channelId, BumpFeeratePerKw, LightningMoney.Satoshis(BobShareSat),
                                                  TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, m) => m is TxCompleteMessage);

        // Act
        foreach (var node in harness.Nodes)
            await node.SeeInBlockAsync(channelId, firstTxId);
        await harness.PumpAsync();

        // Assert
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {firstTxId} confirmed", result.FailureReason);
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxAbortMessage });
        await AssertBackOnFirstAttemptAsync(harness, channelId, firstTxId);
    }

    [Fact]
    public async Task Given_OurTxInitRbfUnanswered_When_TheFirstAttemptConfirms_Then_ItIsWithdrawnAndTheBumpEnds()
    {
        // Arrange: Alice's tx_init_rbf is still on its way
        await using var harness = await CreateBothFundedAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, BumpFeeratePerKw, s_aliceShare,
                                                    TestContext.Current.CancellationToken);
        var initRbf = harness.TakeNext(harness.Alice);
        Assert.IsType<TxInitRbfMessage>(initRbf);

        // Act
        await harness.Alice.SeeInBlockAsync(channelId, firstTxId);

        // Assert: withdrawn with tx_abort, the bump answered without waiting for its timeout
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {firstTxId} confirmed", result.FailureReason);
        Assert.IsType<TxAbortMessage>(harness.TakeNext(harness.Alice));
        Assert.Empty(harness.Alice.Errors);
    }

    [Fact]
    public async Task Given_AnRbfAttemptBuyingLiquidity_When_TheFirstAttemptConfirms_Then_ThePurchaseIsReplaced()
    {
        // Arrange: Alice bought liquidity with the open and buys it again in her bump (Bob sells in both)
        await using var harness = await LiquidityAdsKit.CreateAsync();
        var first = await LiquidityAdsKit.OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, BumpFeeratePerKw, null, null,
                                                    TestContext.Current.CancellationToken);
        await PumpUntilBothConstructedAsync(harness, channelId, firstTxId);
        var attemptTxId = harness.Bob.Channel(channelId).FundingOutput!.TransactionId!.Value;
        foreach (var node in harness.Nodes)
            Assert.Contains(await PurchasesAsync(node, channelId),
                            p => p.FundingTxId == attemptTxId && p.Status == LiquidityPurchaseStatus.Pending);
        Assert.Equal(1, LiquidityAds(harness.Bob).SalesInProgress);

        // Act
        foreach (var node in harness.Nodes)
            await node.SeeInBlockAsync(channelId, firstTxId);
        await harness.PumpAsync();

        // Assert: the attempt's purchase replaced on both sides (the open's still pending), the sale slot back
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Contains($"an earlier attempt {firstTxId} confirmed", result.FailureReason);
        foreach (var node in harness.Nodes)
        {
            var purchases = await PurchasesAsync(node, channelId);
            Assert.Equal(LiquidityPurchaseStatus.Replaced, purchases.Single(p => p.FundingTxId == attemptTxId).Status);
            Assert.Equal(LiquidityPurchaseStatus.Pending, purchases.Single(p => p.FundingTxId == firstTxId).Status);
        }

        Assert.Equal(0, LiquidityAds(harness.Bob).SalesInProgress);
        Assert.Empty(harness.Alice.Errors);
        Assert.Empty(harness.Bob.Errors);
        await LiquidityAdsKit.AssertBalancesAsync(harness, channelId, 600_000, RequestedSat, Fees(2_500).TotalMsat);
    }

    [Fact]
    public async Task Given_AnOpenDualFundedChannel_When_ABlockArrivesDuringASpliceNegotiation_Then_TheSpliceIsKept()
    {
        // Arrange: NL-1293 (cluster taproot suite on Core 31.1): the open's negotiation stays in memory after the
        // channel is open, and a block raised while the driver ran a splice on the channel found the open's confirmed
        // funding and aborted the splice with "an earlier attempt ... confirmed"
        await using var harness = await CreateBothFundedAsync();
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        await harness.ConfirmFundingAsync(channelId, firstTxId);
        Assert.All(harness.Nodes, n => Assert.NotEqual(Domain.Channels.Enums.ChannelState.V1FundingSigned,
                                                       n.Channel(channelId).State));
        var driver = harness.Alice.Services.GetRequiredService<IInteractiveTxDriver>();
        var splice = new TestSharedFundingHost
        {
            LocalOutputShare = s_aliceShare,
            RemoteOutputShare = LightningMoney.Satoshis(BobShareSat)
        };
        using (await harness.Alice.Services.GetRequiredService<IChannelLockProvider>().AcquireAsync(channelId,
                   TestContext.Current.CancellationToken))
        {
            await driver.StartAsync(new InteractiveTxTerms(channelId, harness.Alice.NodeId, harness.Bob.NodeId, true,
                                                           2_500, 0),
                                    splice, TestContext.Current.CancellationToken);
        }

        var session = driver.GetInfo(channelId)?.SessionId;
        Assert.NotNull(session);

        // Act: a block (the open's funding has its first-seen height)
        await harness.Alice.SeeInBlockAsync(channelId, firstTxId, spendWallet: false);

        // Assert: the splice negotiation still runs, nothing aborted it
        Assert.Equal(session, driver.GetInfo(channelId)?.SessionId);
        Assert.Empty(splice.Aborts);
        Assert.Empty(harness.Alice.Errors);
    }

    private static async Task<DualFundHarness> CreateBothFundedAsync()
    {
        var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        return harness;
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness)
    {
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        return result;
    }

    /// <summary>
    /// Pumps the RBF attempt until both nodes constructed it (each sent its <c>commitment_signed</c>, which moved its
    /// channel to the attempt) and before either <c>commitment_signed</c> is delivered.
    /// </summary>
    private static async Task PumpUntilBothConstructedAsync(DualFundHarness harness, ChannelId channelId,
                                                            TxId firstTxId)
    {
        await harness.PumpAsync((_, m) => m is CommitmentSignedMessage
                                       && harness.Nodes.All(n => n.Channel(channelId).FundingOutput!.TransactionId
                                                              != firstTxId));
        Assert.All(harness.Nodes, n => Assert.NotEqual(firstTxId, n.Channel(channelId).FundingOutput!.TransactionId));
        Assert.DoesNotContain(harness.Transcript,
                              t => t.Message is CommitmentSignedMessage { FundingTxIdTlv: { } fundingTxId }
                                && fundingTxId.FundingTxId != firstTxId);
    }

    /// <summary>The wallet of <paramref name="node"/> saw the inputs of <paramref name="txId"/> spent (no block yet).</summary>
    private static Task MarkWalletSpentAsync(DualFundNode node, ChannelId channelId, TxId txId) =>
        node.InScopeAsync(async unitOfWork =>
        {
            var session = (await unitOfWork.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId))
               .First(s => s.ConstructedTx?.TxId == txId);
            foreach (var input in session.ConstructedTx!.Inputs)
                node.Wallet.SpentOnChain.Add((input.PrevTxId, input.PrevTxVout));
            return 0;
        });

    /// <summary>Every stored row of an attempt other than <paramref name="firstTxId"/> is aborted, on both nodes.</summary>
    private static async Task AssertAttemptRowsAbortedAsync(DualFundHarness harness, ChannelId channelId,
                                                            TxId firstTxId)
    {
        foreach (var node in harness.Nodes)
        {
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId));
            Assert.All(sessions.Where(s => s.ConstructedTx?.TxId != firstTxId),
                       s => Assert.Equal(InteractiveTxSessionState.Aborted, s.State));
        }
    }

    /// <summary>
    /// Both nodes, in memory and stored, are on the first attempt with its capacity, no exception was raised and the
    /// interactive-tx driver holds nothing for the channel.
    /// </summary>
    private static async Task AssertBackOnFirstAttemptAsync(DualFundHarness harness, ChannelId channelId,
                                                            TxId firstTxId)
    {
        foreach (var node in harness.Nodes)
        {
            Assert.True(node.Errors.Count == 0, harness.Describe());
            Assert.Equal(firstTxId, node.Channel(channelId).FundingOutput!.TransactionId);
            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(firstTxId, stored!.FundingOutput!.TransactionId);
            Assert.Equal([firstTxId], node.DualFund.GetSignedFundingTxIds(channelId));
        }
    }
}