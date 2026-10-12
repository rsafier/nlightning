namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-530: the accepter of a dual-funded open starts its RBF (BOLT 2 "Fee bumping": the sender of <c>tx_init_rbf</c>
/// "MAY be either the <i>initiator</i> or the <i>accepter</i>"; "If the sender is the accepter, it becomes the initiator
/// of the <c>interactive-tx</c> session and thus: MUST send <c>tx_add_output</c> for the channel output, MUST pay the
/// fees for the shared transaction fields"; "MUST NOT have sent or received a <c>channel_ready</c> message"). On
/// <see cref="DualFundHarness"/>, Bob (the accepter) bumps Alice's open: he is the interactive-tx initiator of the new
/// attempt (even serial ids, the funding output his), Alice (the opener, non-initiator) re-adds her inputs (IT-RBF-01),
/// both follow the replacement, or the earlier attempt when that one confirms (NL-528), a restart during the attempt
/// puts both back on the signed funding, and the refusals leave the open as it was.
/// </summary>
public class DualFundAccepterRbfTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;
    private const uint BumpFeeratePerKw = 5_000;

    [Fact]
    public async Task Given_AnOpenBothFunded_When_TheAccepterBumps_Then_ItIsTheInitiatorAndTheReplacementOpens()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;

        // Act
        var bump = await BobBumpsAsync(harness, channelId);

        // Assert: Bob sent tx_init_rbf with his share, Alice answered with hers
        var init = (TxInitRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxInitRbfMessage).Message;
        Assert.Equal("Bob", Assert.Single(harness.Transcript, t => t.Message is TxInitRbfMessage).From);
        Assert.Equal(BumpFeeratePerKw, init.Payload.Feerate);
        Assert.Equal(BobShareSat, init.FundingOutputContributionTlv?.Satoshis);
        var ack = (TxAckRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxAckRbfMessage).Message;
        Assert.Equal(s_aliceShare.Satoshi, ack.FundingOutputContributionTlv?.Satoshis);

        // ...Bob is the interactive-tx initiator of the attempt: he added the funding output with an even serial id,
        // and both re-added the inputs of the first attempt (each contributed to it, IT-RBF-01)
        var bobAttempt = await GetSessionAsync(harness.Bob, channelId, bump.FundingTxId!.Value);
        var aliceAttempt = await GetSessionAsync(harness.Alice, channelId, bump.FundingTxId.Value);
        Assert.True(bobAttempt.IsInitiator);
        Assert.False(aliceAttempt.IsInitiator);
        var funding = Assert.Single(bobAttempt.Outputs, o => o.IsShared);
        Assert.Equal(InteractiveTxParty.Local, funding.AddedBy);
        Assert.Equal(0UL, funding.SerialId % 2);
        Assert.Equal(InteractiveTxParty.Remote, Assert.Single(aliceAttempt.Outputs, o => o.IsShared).AddedBy);
        var firstBob = await GetSessionAsync(harness.Bob, channelId, first.FundingTxId!.Value);
        Assert.Equal(firstBob.Inputs.Select(i => (i.PrevTxId, i.PrevTxVout)).ToHashSet(),
                     bobAttempt.Inputs.Select(i => (i.PrevTxId, i.PrevTxVout)).ToHashSet());
        Assert.Contains(bobAttempt.Inputs, i => i.AddedBy == InteractiveTxParty.Remote);

        // ...both on the replacement with unchanged shares, and it opens and carries payments both ways
        await AssertOnFundingAsync(harness, channelId, bump.FundingTxId.Value, s_aliceShare,
                                   LightningMoney.Satoshis(BobShareSat));
        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId.Value);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_AnAccepterThatFundedNothing_When_ItBumpsWithAContribution_Then_FreshInputsFundItsShare()
    {
        // Arrange: Bob's wallet is empty at the open (he contributes nothing), then funded
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        Assert.Equal(s_aliceShare, harness.Alice.Channel(channelId).FundingOutput!.Amount);
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var bobShare = LightningMoney.Satoshis(300_000);

        // Act
        var bump = await BobBumpsAsync(harness, channelId, bobShare);

        // Assert: Bob's new input pays his share and the initiator's fees; the channel is 900,000 sat on both sides
        var init = (TxInitRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxInitRbfMessage).Message;
        Assert.Equal(300_000, init.FundingOutputContributionTlv?.Satoshis);
        var bobAttempt = await GetSessionAsync(harness.Bob, channelId, bump.FundingTxId!.Value);
        Assert.True(bobAttempt.IsInitiator);
        Assert.Contains(bobAttempt.Inputs, i => i.AddedBy == InteractiveTxParty.Local);
        await AssertOnFundingAsync(harness, channelId, bump.FundingTxId.Value, s_aliceShare, bobShare);

        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId.Value);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_AnAccepterThatFundedNothing_When_ItBumpsWithoutAContribution_Then_ItPaysOnlyTheFees()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(100_000));

        // Act: no new contribution, so Bob's share stays 0; as initiator he still pays the shared fields
        var bump = await BobBumpsAsync(harness, channelId, LightningMoney.Zero);

        // Assert: no funding_output_contribution (he contributes nothing), Bob's input and change are in the
        // attempt, and the capacity is still Alice's share alone
        var init = (TxInitRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxInitRbfMessage).Message;
        Assert.Null(init.FundingOutputContributionTlv);
        var bobAttempt = await GetSessionAsync(harness.Bob, channelId, bump.FundingTxId!.Value);
        Assert.True(bobAttempt.IsInitiator);
        var bobInput = Assert.Single(bobAttempt.Inputs, i => i.AddedBy == InteractiveTxParty.Local);
        var bobChange = Assert.Single(bobAttempt.Outputs, o => o is { AddedBy: InteractiveTxParty.Local, IsShared: false });
        Assert.True(bobChange.Amount < bobInput.Amount);
        await AssertOnFundingAsync(harness, channelId, bump.FundingTxId.Value, s_aliceShare, LightningMoney.Zero);

        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId.Value);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_AnAccepterBump_When_TheFirstAttemptConfirms_Then_BothFollowItAndCarryPayments()
    {
        // Arrange: Bob funded nothing at the open and 400,000 sat in his own RBF, so the attempts differ in capacity
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var bump = await BobBumpsAsync(harness, channelId, LightningMoney.Satoshis(BobShareSat));
        await AssertOnFundingAsync(harness, channelId, bump.FundingTxId!.Value, s_aliceShare,
                                   LightningMoney.Satoshis(BobShareSat));

        // Act: the first attempt is the one mined
        await harness.ConfirmFundingAsync(channelId, first.FundingTxId!.Value);

        // Assert: both on the first attempt, with its capacity and balances, open, payments both ways
        await AssertOnFundingAsync(harness, channelId, first.FundingTxId.Value, s_aliceShare, LightningMoney.Zero);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_TheAccepterRestarted_When_ItBumps_Then_TheNegotiationComesFromItsRowsAndTheBumpOpens()
    {
        // Arrange: Bob's negotiation is only in his database after the restart
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        await harness.RestartAsync(harness.Bob);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Act
        var bump = await BobBumpsAsync(harness, channelId);

        // Assert
        Assert.NotEqual(first.FundingTxId, bump.FundingTxId);
        await AssertOnFundingAsync(harness, channelId, bump.FundingTxId!.Value, s_aliceShare,
                                   LightningMoney.Satoshis(BobShareSat));
        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId.Value);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_ARestartDuringTheAccepterBump_When_ThePeerForgotTheAttempt_Then_BackOnTheSignedFunding()
    {
        // Arrange: stop as soon as one node constructed Bob's RBF attempt (its commitment_signed stored it and moved
        // its channel to it) and before the other one could
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        var firstTxId = first.FundingTxId!.Value;
        var bump = harness.Bob.DualFund.BumpAsync(channelId, BumpFeeratePerKw, TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, _) => harness.Nodes.Any(n => n.Channel(channelId).FundingOutput!.TransactionId
                                                                   != firstTxId));
        var constructed = harness.Nodes.Single(n => n.Channel(channelId).FundingOutput!.TransactionId != firstTxId);
        Assert.Equal(firstTxId, harness.Other(constructed).Channel(channelId).FundingOutput!.TransactionId);

        // Act: the node that stored the attempt restarts; the reconnection's next_funding names an attempt the other
        // node forgot with the link (BOLT 2)
        await harness.RestartAsync(constructed);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: tx_abort, both back on the first funding, which opens
        if (constructed == harness.Alice)
            Assert.NotNull((await bump.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
                              .FailureReason);
        Assert.Contains(harness.Transcript, t => t.Message is TxAbortMessage);
        await AssertOnFundingAsync(harness, channelId, firstTxId, s_aliceShare, LightningMoney.Satoshis(BobShareSat));
        await harness.ConfirmFundingAsync(channelId, firstTxId);
        await AssertOpenAndPayBothWaysAsync(harness, channelId);
    }

    [Fact]
    public async Task Given_AnAccepterWithoutFundsThatFundedNothing_When_ItBumps_Then_RefusedAndNothingSent()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => harness.Bob.DualFund.BumpAsync(first.ChannelId, BumpFeeratePerKw,
                                                             TestContext.Current.CancellationToken));

        // Assert: the initiator's fees need an input; nothing was sent or reserved and the open is unchanged
        Assert.Contains("initiator's fees", error.Message);
        await harness.PumpAsync();
        Assert.DoesNotContain(harness.Transcript, t => t.Message is TxInitRbfMessage);
        Assert.Empty(harness.Bob.Wallet.ActiveReservations);
        await AssertOnFundingAsync(harness, first.ChannelId, first.FundingTxId!.Value, s_aliceShare,
                                   LightningMoney.Zero);
    }

    [Fact]
    public async Task Given_ChannelReadyExchanged_When_TheAccepterBumps_Then_Refused()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        await harness.ConfirmFundingAsync(first.ChannelId, first.FundingTxId!.Value);

        // Act / Assert: BOLT 2, the sender "MUST NOT have sent or received a channel_ready message"
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Bob.DualFund.BumpAsync(first.ChannelId, BumpFeeratePerKw,
                                                 TestContext.Current.CancellationToken));
        Assert.DoesNotContain(harness.Transcript, t => t.Message is TxInitRbfMessage);
    }

    [Fact]
    public async Task Given_AFeerateBelowTheFloor_When_TheAccepterBumps_Then_Refused()
    {
        // Arrange: the open is at 2,500 sat/kw, so IT-RBF-01 asks for max(2,604, 2,525)
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => harness.Bob.DualFund.BumpAsync(first.ChannelId, 2_603,
                                                             TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("IT-RBF-01", error.Message);
        Assert.DoesNotContain(harness.Transcript, t => t.Message is TxInitRbfMessage);
    }

    [Fact]
    public async Task Given_RbfNotAllowed_When_TheAccepterBumps_Then_Refused()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: false);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
                        () => harness.Bob.DualFund.BumpAsync(first.ChannelId, BumpFeeratePerKw,
                                                             TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("not enabled", error.Message);
    }

    [Fact]
    public async Task Given_AnOpenerThatRefusesRbf_When_TheAccepterBumps_Then_TxAbortAndItsFreshInputsReleased()
    {
        // Arrange: Bob funded nothing at the open; Alice no longer allows RBF (restarted with it off)
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        var channelId = first.ChannelId;
        harness.Alice.AllowRbfOverride = false;
        await harness.RestartAsync(harness.Alice);
        await harness.ReconnectAsync();
        await harness.PumpAsync();
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));

        // Act
        var bump = await harness.RunAsync(harness.Bob.DualFund.BumpAsync(channelId, BumpFeeratePerKw,
                                                                         LightningMoney.Satoshis(BobShareSat),
                                                                         TestContext.Current.CancellationToken));

        // Assert: Alice's tx_abort ends Bob's bump at once, his new reservation goes back, the open is unchanged
        Assert.NotNull(bump.FailureReason);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxAbortMessage });
        Assert.Empty(harness.Bob.Wallet.ActiveReservations);
        await AssertOnFundingAsync(harness, channelId, first.FundingTxId!.Value, s_aliceShare, LightningMoney.Zero);
        await harness.ConfirmFundingAsync(channelId, first.FundingTxId.Value);
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

    private static async Task<DualFundedOpenResult> BobBumpsAsync(DualFundHarness harness, ChannelId channelId,
                                                                  LightningMoney? bobShare = null)
    {
        var result = await harness.RunAsync(harness.Bob.DualFund.BumpAsync(channelId, BumpFeeratePerKw, bobShare,
                                                                           TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        return result;
    }

    private static async Task<InteractiveTxSessionModel> GetSessionAsync(DualFundNode node, ChannelId channelId,
                                                                         TxId txId)
    {
        var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository.GetByChannelIdAsync(channelId));
        return Assert.Single(sessions, s => s.State == InteractiveTxSessionState.Signed
                                         && s.ConstructedTx?.TxId == txId);
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