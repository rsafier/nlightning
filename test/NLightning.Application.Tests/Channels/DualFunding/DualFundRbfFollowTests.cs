namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-528: every fully signed attempt of a dual-funded open's RBF can be the one that confirms (BOLT 2 "Fee bumping":
/// the attempts double-spend each other and either may be mined), not only the latest. On
/// <see cref="DualFundHarness"/>: the channel follows the attempt that confirmed (its outpoint, capacity, balances,
/// reserve, the peer's signature of our first commitment stored with the attempt's negotiation, the signer's funding)
/// in both roles; the peer's <c>channel_ready</c> that arrives before our own confirmation waits for it; a restart
/// during an RBF attempt that then ends unsigned puts the channel back on the signed attempt from the stored rows; and
/// an RBF of an open through restarts of both nodes confirms and carries payments.
/// </summary>
public class DualFundRbfFollowTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_ABumpedOpen_When_TheFirstAttemptConfirms_Then_BothNodesFollowItAndCarryPayments()
    {
        // Arrange: Bob funded nothing at the open and 400,000 sat in the RBF, so the attempts differ in capacity and
        // balances, not only in outpoint
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var bump = await BumpAsync(harness, first.ChannelId);
        await AssertOnFundingAsync(harness, first.ChannelId, bump.FundingTxId!.Value, s_aliceShare,
                                   LightningMoney.Satoshis(BobShareSat));

        // Act: the first attempt is the one mined
        await harness.ConfirmFundingAsync(first.ChannelId, first.FundingTxId!.Value);

        // Assert: both on the first attempt, with its capacity and balances, open, and payments both ways
        await AssertOnFundingAsync(harness, first.ChannelId, first.FundingTxId.Value, s_aliceShare,
                                   LightningMoney.Zero);

        // The accounting feed (NL-602) follows the attempt that confirmed: Alice funded it alone and paid its whole fee,
        // Bob's 400,000 sat of the replacement never left his wallet
        var aliceFunded = await DualFundHarnessTests.AssertChannelFundedAsync(
                              harness.Alice, first.ChannelId, first.FundingTxId.Value, s_aliceShare);
        var firstFee = harness.Alice.Published.First(p => p.TransactionId == first.FundingTxId.Value).Fee!;
        Assert.Equal(checked((long)firstFee.MilliSatoshi), aliceFunded.FeeMsat);
        var bobFunded = await DualFundHarnessTests.AssertChannelFundedAsync(
                            harness.Bob, first.ChannelId, first.FundingTxId.Value, LightningMoney.Zero);
        Assert.Equal(0, bobFunded.FeeMsat);
        await AssertOpenAndPayBothWaysAsync(harness, first.ChannelId);
    }

    [Fact]
    public async Task Given_ThePeerSawTheFirstAttemptConfirmFirst_When_ItsChannelReadyArrivesEarly_Then_ItWaitsForOurs()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        var bump = await BumpAsync(harness, first.ChannelId, LightningMoney.Satoshis(650_000));
        Assert.NotEqual(first.FundingTxId, bump.FundingTxId);

        // Act: Bob's monitor reaches the first attempt's depth first; his channel_ready reaches Alice before hers does
        await harness.Bob.ConfirmAsync(first.ChannelId, first.FundingTxId!.Value);
        await harness.PumpAsync();

        // Assert: Alice kept it (no first commitment state on the RBF attempt), then follows the first attempt
        var alice = harness.Alice.Channel(first.ChannelId);
        Assert.Equal(ChannelState.V1FundingSigned, alice.State);
        Assert.Null(alice.Commitments);
        Assert.Equal(bump.FundingTxId, alice.FundingOutput!.TransactionId);

        await harness.Alice.ConfirmAsync(first.ChannelId, first.FundingTxId.Value);
        await harness.PumpAsync();
        await AssertOnFundingAsync(harness, first.ChannelId, first.FundingTxId.Value, s_aliceShare,
                                   LightningMoney.Satoshis(BobShareSat));
        await AssertOpenAndPayBothWaysAsync(harness, first.ChannelId);
    }

    [Fact]
    public async Task Given_ARestartDuringAnRbfAttempt_When_ThePeerForgotTheAttempt_Then_BackOnTheSignedFundingThatOpens()
    {
        // Arrange: stop as soon as one node constructed the RBF attempt (its commitment_signed stored it and moved its
        // channel to it) and before the other one could
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var bump = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, _) => harness.Nodes.Any(n => n.Channel(channelId).FundingOutput!.TransactionId
                                                                   != firstTxId));
        var constructed = harness.Nodes.Single(n => n.Channel(channelId).FundingOutput!.TransactionId != firstTxId);
        var other = harness.Other(constructed);
        Assert.Equal(firstTxId, other.Channel(channelId).FundingOutput!.TransactionId);

        // Act: the link drops (the other node forgets its unconstructed attempt, BOLT 2), the node that stored the
        // attempt restarts, and the reconnection's next_funding names an attempt the other node does not know
        await harness.RestartAsync(constructed);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: tx_abort, and both nodes on the first funding with its capacity and balances, in memory and stored
        // (when Alice restarted, her BumpAsync call went with the stopped process)
        if (constructed == harness.Bob)
            Assert.NotNull((await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
                              .FailureReason);
        Assert.Contains(harness.Transcript, t => t.Message is TxAbortMessage);
        await AssertOnFundingAsync(harness, channelId, firstTxId, s_aliceShare, LightningMoney.Zero);

        // ...and the first funding opens the channel, signatures included (the restored one came from the stored row)
        await harness.ConfirmFundingAsync(channelId, firstTxId);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_AnOpenBumpedTwiceWithBothNodesRestarted_When_TheLatestConfirms_Then_OpenAndPaymentsBothWays()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;

        // Act: a bump, the opener restarts, a second bump, the accepter restarts, then the latest confirms
        var second = await BumpAsync(harness, channelId);
        await harness.RestartAsync(harness.Alice);
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        var third = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 6_000,
                                                                            TestContext.Current.CancellationToken));
        Assert.True(third.FailureReason is null, $"{third.FailureReason}\n{harness.Describe()}");
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        await harness.ConfirmFundingAsync(channelId, third.FundingTxId!.Value);

        // Assert: three signed attempts on both sides, the latest one open
        foreach (var node in harness.Nodes)
        {
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            Assert.Equal([first.FundingTxId!.Value, second.FundingTxId!.Value, third.FundingTxId.Value],
                         sessions.Where(s => s.State == InteractiveTxSessionState.Signed)
                                 .Select(s => s.ConstructedTx!.TxId));
            Assert.All(sessions.Where(s => s.State == InteractiveTxSessionState.Signed),
                       s => Assert.NotNull(s.TheirCommitmentSignature));
        }

        await AssertOnFundingAsync(harness, channelId, third.FundingTxId.Value, s_aliceShare,
                                   LightningMoney.Satoshis(BobShareSat));
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness)
    {
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        return result;
    }

    private static async Task<DualFundedOpenResult> BumpAsync(DualFundHarness harness, ChannelId channelId,
                                                              LightningMoney? aliceShare = null)
    {
        var result = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                             aliceShare ?? s_aliceShare,
                                                                             TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        return result;
    }

    /// <summary>
    /// Both nodes, in memory and in the database: the channel is on <paramref name="txId"/> with the capacity of both
    /// shares, the balances and a reserve of 1% of it on both sides.
    /// </summary>
    private static async Task AssertOnFundingAsync(DualFundHarness harness, ChannelId channelId, TxId txId,
                                                   LightningMoney aliceShare, LightningMoney bobShare)
    {
        var total = LightningMoney.MilliSatoshis(aliceShare.MilliSatoshi + bobShare.MilliSatoshi);
        var reserve = LightningMoney.Satoshis(total.Satoshi / 100);
        foreach (var node in harness.Nodes)
        {
            var (local, remote) = node == harness.Alice ? (aliceShare, bobShare) : (bobShare, aliceShare);
            var channel = node.Channel(channelId);
            Assert.True(txId == channel.FundingOutput!.TransactionId,
                        $"{node.Name} is on {channel.FundingOutput.TransactionId}, not {txId}\n{harness.Describe()}");
            Assert.Equal(total, channel.FundingOutput.Amount);
            Assert.Equal(local, channel.LocalBalance);
            Assert.Equal(remote, channel.RemoteBalance);
            Assert.Equal(reserve, channel.ChannelParams.Local.ChannelReserveAmount);
            Assert.Equal(reserve, channel.ChannelParams.Remote.ChannelReserveAmount);

            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(txId, stored!.FundingOutput!.TransactionId);
            Assert.Equal(total, stored.FundingOutput.Amount);
            Assert.Equal(local, stored.LocalBalance);
        }
    }

    private static async Task AssertOpenAndPayBothWaysAsync(DualFundHarness harness, ChannelId channelId)
    {
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        var bobBefore = harness.Bob.Channel(channelId).LocalBalance;
        await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(30_000));
        await harness.PumpAsync();
        await harness.Bob.PayAsync(harness.Alice, channelId, LightningMoney.Satoshis(20_000));
        await harness.PumpAsync();
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Single(harness.Bob.PaymentHandler.Fulfilled);
        Assert.Equal(bobBefore + LightningMoney.Satoshis(10_000), harness.Bob.Channel(channelId).LocalBalance);
        Assert.Empty(harness.Alice.Errors);
        Assert.Empty(harness.Bob.Errors);
    }
}