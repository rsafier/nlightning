namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using InteractiveTx.TestDoubles;

/// <summary>
/// NL-521: an RBF of a dual-funded open in which a contribution changes (BOLT 2 <c>tx_init_rbf</c>/<c>tx_ack_rbf</c>:
/// the sender "MAY set <c>funding_output_contribution</c> to a different value"; "Peers can use different values in
/// <c>tx_init_rbf.funding_output_contribution</c> and <c>tx_ack_rbf.funding_output_contribution</c> from the amounts
/// transmitted in <c>open_channel2</c> and <c>accept_channel2</c>"), on <see cref="DualFundHarness"/>: the new
/// attempt's funding output, both balances, the reserve (1% of the capacity) and the signatures follow the new
/// contributions in both roles; a contribution we cannot take gets <c>tx_abort</c>, and an attempt that ends before it
/// is signed leaves the channel on the signed funding with its old capacity.
/// </summary>
public class DualFundRbfContributionTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_AnAccepterThatFundedNothing_When_ItContributesInTxAckRbf_Then_TheOpenerBuildsTheNewFunding()
    {
        // Arrange: Bob's policy is 400,000 sat but his wallet is empty at the open (accept_channel2 says 0), as CLN's
        // funder did in the wave d13 run; then his wallet is funded
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        Assert.Equal(s_aliceShare, harness.Alice.Channel(channelId).FundingOutput!.Amount);
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));

        // Act
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                           TestContext.Current.CancellationToken));

        // Assert: Bob's tx_ack_rbf carries his 400,000 sat, and Alice's replacement pays 1,000,000 sat into the channel
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        var ack = (TxAckRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxAckRbfMessage).Message;
        Assert.Equal(BobShareSat, ack.FundingOutputContributionTlv?.Satoshis);
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxAddInputMessage });
        await AssertFundingAsync(harness, channelId, bump.FundingTxId!.Value, s_aliceShare,
                                 LightningMoney.Satoshis(BobShareSat));

        // ...and the channel carries payments both ways on the new capacity once it confirms
        await ConfirmAndPayBothWaysAsync(harness, channelId, bump.FundingTxId.Value);
    }

    [Fact]
    public async Task Given_AnUnconfirmedOpen_When_TheOpenerBumpsWithAnotherContribution_Then_TheAccepterFollowsIt()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        var aliceShare = LightningMoney.Satoshis(650_000);

        // Act: Alice adds 50,000 sat from the change of her first attempt's input
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000, aliceShare,
                                                                           TestContext.Current.CancellationToken));

        // Assert: tx_init_rbf carries 650,000 sat, Bob keeps his 400,000 sat and both sign the 1,050,000 sat funding
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        var init = (TxInitRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxInitRbfMessage).Message;
        Assert.Equal(650_000, init.FundingOutputContributionTlv?.Satoshis);
        var ack = (TxAckRbfMessage)Assert.Single(harness.Transcript, t => t.Message is TxAckRbfMessage).Message;
        Assert.Equal(BobShareSat, ack.FundingOutputContributionTlv?.Satoshis);
        await AssertFundingAsync(harness, channelId, bump.FundingTxId!.Value, aliceShare,
                                 LightningMoney.Satoshis(BobShareSat));
        await ConfirmAndPayBothWaysAsync(harness, channelId, bump.FundingTxId.Value);
    }

    [Fact]
    public async Task Given_OurTxInitRbf_When_TheAccepterAcksANegativeContribution_Then_TxAbortAndTheOpenStands()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        var bump = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxAckRbfMessage);
        Assert.IsType<TxAckRbfMessage>(harness.TakeNext(harness.Bob));

        // Act: the tx_ack_rbf Alice gets takes 5 sat out of a v2 open (an s64 on the wire)
        await harness.DeliverAsync(harness.Bob, new TxAckRbfMessage(new TxAckRbfPayload(channelId),
                                                                    new FundingOutputContributionTlv(-5L)));
        var abort = Assert.IsType<TxAbortMessage>(harness.TakeNext(harness.Alice));
        await harness.DeliverAsync(harness.Alice, abort);
        await harness.PumpAsync();
        var result = await bump;

        // Assert: Alice refused it with a reason, never started the attempt, and the first funding stands
        Assert.Contains("negative", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxAddInputMessage }
                                                    && harness.Transcript.IndexOf(t)
                                                     > harness.Transcript.FindIndex(x => x.Message is TxAckRbfMessage));
        foreach (var node in harness.Nodes)
        {
            Assert.Equal([first.FundingTxId!.Value], node.DualFund.GetSignedFundingTxIds(channelId));
            Assert.Equal(first.FundingTxId, node.Channel(channelId).FundingOutput!.TransactionId);
            Assert.Equal(LightningMoney.Satoshis(1_000_000), node.Channel(channelId).FundingOutput!.Amount);
        }

        // ...and a later bump with the right contribution goes through
        var second = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                             TestContext.Current.CancellationToken));
        Assert.True(second.FailureReason is null, $"{second.FailureReason}\n{harness.Describe()}");
        await AssertFundingAsync(harness, channelId, second.FundingTxId!.Value, s_aliceShare,
                                 LightningMoney.Satoshis(BobShareSat));
    }

    [Fact]
    public async Task Given_ATxInitRbfWhoseOpenerCannotPayTheCommitmentFee_When_Received_Then_TxAbortAndTheOpenStands()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var initRbf = new TxInitRbfMessage(new TxInitRbfPayload(first.ChannelId, 5_000, 500),
                                           new FundingOutputContributionTlv(LightningMoney.Satoshis(1)));

        // Act
        await harness.DeliverAsync(harness.Alice, initRbf);
        var abort = harness.TakeNext(harness.Bob);

        // Assert: Bob refused the RBF with the reason; the signed open is still the channel's funding
        var txAbort = Assert.IsType<TxAbortMessage>(abort);
        Assert.Contains("commitment's fee", System.Text.Encoding.ASCII.GetString(txAbort.Payload.Data));
        Assert.Equal([first.FundingTxId!.Value], harness.Bob.DualFund.GetSignedFundingTxIds(first.ChannelId));
        Assert.Equal(ChannelState.V1FundingSigned, harness.Bob.Channel(first.ChannelId).State);
        Assert.Equal(LightningMoney.Satoshis(1_000_000), harness.Bob.Channel(first.ChannelId).FundingOutput!.Amount);
    }

    [Fact]
    public async Task Given_AnRbfWithANewContributionPastBothCommitmentSigned_When_Aborted_Then_TheOldCapacityIsBack()
    {
        // Arrange: Bob funded nothing at the open; his RBF contribution is signed on both sides, then Bob aborts
        // before Alice's tx_signatures (Bob's input is the smaller one, so he signs first)
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        var signedParams = harness.Alice.Channel(channelId).ChannelParams;
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var bump = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxSignaturesMessage
                                                && harness.Transcript.Any(t => t.Message is TxInitRbfMessage));
        Assert.Equal(LightningMoney.Satoshis(1_000_000), harness.Alice.Channel(channelId).FundingOutput!.Amount);
        Assert.IsType<TxSignaturesMessage>(harness.TakeNext(harness.Bob));

        // Act
        await harness.DeliverAsync(harness.Bob,
                                   new TxAbortMessage(new TxAbortPayload(channelId, "changed my mind"u8.ToArray())));
        await harness.PumpAsync();
        var result = await bump;

        // Assert: Alice's channel is back on the 600,000 sat funding, her balance, reserve and signatures, in memory and
        // in the database
        Assert.NotNull(result.FailureReason);
        var alice = harness.Alice.Channel(channelId);
        Assert.Equal(first.FundingTxId, alice.FundingOutput!.TransactionId);
        Assert.Equal(s_aliceShare, alice.FundingOutput.Amount);
        Assert.Equal(s_aliceShare, alice.LocalBalance);
        Assert.True(alice.RemoteBalance.IsZero);
        Assert.Equal(signedParams.Local.ChannelReserveAmount, alice.ChannelParams.Local.ChannelReserveAmount);
        Assert.Equal(signedParams.Local.MaxHtlcValueInFlight, alice.ChannelParams.Local.MaxHtlcValueInFlight);
        var stored = await harness.Alice.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.Equal(s_aliceShare, stored!.FundingOutput!.Amount);
        Assert.Equal(s_aliceShare, stored.LocalBalance);
        Assert.Equal(signedParams.Remote.ChannelReserveAmount, stored.ChannelParams.Remote.ChannelReserveAmount);

        // And the signed funding still opens the channel
        await harness.ConfirmFundingAsync(channelId, first.FundingTxId!.Value);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
    }

    /// <summary>
    /// Both nodes: the channel is on <paramref name="txId"/> with the new capacity and balances, the reserve is 1% of
    /// it on both sides, the same in the database, and the initial funding row follows the replacement.
    /// </summary>
    private static async Task AssertFundingAsync(DualFundHarness harness, ChannelId channelId,
                                                 Domain.Bitcoin.ValueObjects.TxId txId, LightningMoney aliceShare,
                                                 LightningMoney bobShare)
    {
        var total = LightningMoney.MilliSatoshis(aliceShare.MilliSatoshi + bobShare.MilliSatoshi);
        var reserve = LightningMoney.Satoshis(total.Satoshi / 100);
        foreach (var node in harness.Nodes)
        {
            var (local, remote) = node == harness.Alice ? (aliceShare, bobShare) : (bobShare, aliceShare);
            var channel = node.Channel(channelId);
            Assert.Equal(txId, channel.FundingOutput!.TransactionId);
            Assert.Equal(total, channel.FundingOutput.Amount);
            Assert.Equal(local, channel.LocalBalance);
            Assert.Equal(remote, channel.RemoteBalance);
            Assert.Equal(reserve, channel.ChannelParams.Local.ChannelReserveAmount);
            Assert.Equal(reserve, channel.ChannelParams.Remote.ChannelReserveAmount);

            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.Equal(txId, stored!.FundingOutput!.TransactionId);
            Assert.Equal(total, stored.FundingOutput.Amount);
            Assert.Equal(local, stored.LocalBalance);
            Assert.Equal(reserve, stored.ChannelParams.Remote.ChannelReserveAmount);

            var fundings = await node.InScopeAsync(u => u.ChannelFundingDbRepository.GetByChannelIdAsync(channelId));
            var initial = Assert.Single(fundings);
            Assert.Equal(ChannelFundingKind.Initial, initial.Kind);
            Assert.Equal(txId, initial.FundingTxId);
            Assert.Equal((ulong)total.Satoshi, initial.CapacitySatoshis);

            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            Assert.Equal(2, sessions.Count(s => s.State == InteractiveTxSessionState.Signed));
        }
    }

    private static async Task ConfirmAndPayBothWaysAsync(DualFundHarness harness, ChannelId channelId,
                                                         Domain.Bitcoin.ValueObjects.TxId txId)
    {
        await harness.ConfirmFundingAsync(channelId, txId);
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

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness) =>
        await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                   new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                   TestContext.Current.CancellationToken));
}