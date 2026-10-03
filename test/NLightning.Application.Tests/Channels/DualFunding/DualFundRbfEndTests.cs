using System.Diagnostics;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Money;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-527: our <c>tx_init_rbf</c> of a dual-funded open that ends before any attempt exists (the peer's
/// <c>tx_abort</c>, a disconnection) ends <see cref="Application.Channels.DualFunding.DualFundedOpenService.BumpAsync(
/// Domain.Channels.ValueObjects.ChannelId, uint, CancellationToken)"/> at once with the reason, instead of after its
/// open timeout; the signed open stands and a later bump works.
/// </summary>
public class DualFundRbfEndTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_OurTxInitRbf_When_ThePeerAnswersTxAbort_Then_BumpAsyncReturnsThePeersReasonAtOnce()
    {
        // Arrange: a 60 s open timeout, which BumpAsync used to wait out
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var clock = Stopwatch.StartNew();
        var bump = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Alice" && message is TxInitRbfMessage);
        Assert.IsType<TxInitRbfMessage>(harness.TakeNext(harness.Alice));

        // Act: Bob refuses it
        await harness.DeliverAsync(harness.Bob,
                                   new TxAbortMessage(new TxAbortPayload(channelId, "not today"u8.ToArray())));
        await harness.PumpAsync();
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert: the peer's reason, long before the open timeout, and Alice echoed the tx_abort
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"{clock.Elapsed}");
        Assert.Null(result.FundingTxId);
        Assert.Contains("not today", result.FailureReason);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxAbortMessage });
        AssertOnFirstFunding(harness, first);

        // ...and a later bump goes through
        var second = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                             TestContext.Current.CancellationToken));
        Assert.True(second.FailureReason is null, $"{second.FailureReason}\n{harness.Describe()}");
        Assert.Equal(second.FundingTxId, harness.Bob.Channel(channelId).FundingOutput!.TransactionId);
    }

    [Fact]
    public async Task Given_OurTxInitRbf_When_TheLinkDropsBeforeTheAnswer_Then_BumpAsyncReturnsAtOnce()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        var first = await OpenAsync(harness);
        var bump = harness.Alice.DualFund.BumpAsync(first.ChannelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Alice" && message is TxInitRbfMessage);

        // Act
        await harness.DisconnectAsync();
        var result = await bump.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(result.FundingTxId);
        Assert.Contains("disconnected", result.FailureReason);
        AssertOnFirstFunding(harness, first);
    }

    private static void AssertOnFirstFunding(DualFundHarness harness, DualFundedOpenResult first)
    {
        foreach (var node in harness.Nodes)
        {
            Assert.Equal([first.FundingTxId!.Value], node.DualFund.GetSignedFundingTxIds(first.ChannelId));
            Assert.Equal(first.FundingTxId, node.Channel(first.ChannelId).FundingOutput!.TransactionId);
            Assert.Equal(ChannelState.V1FundingSigned, node.Channel(first.ChannelId).State);
        }
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness)
    {
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        return result;
    }
}