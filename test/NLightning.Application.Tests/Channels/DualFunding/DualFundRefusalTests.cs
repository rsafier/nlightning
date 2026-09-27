namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using InteractiveTx.TestDoubles;

/// <summary>
/// The refusals of the dual-funded open (splicing plan wave DF, BOLT 2 "Channel Establishment v2") on
/// <see cref="DualFundHarness"/>: no <c>option_dual_fund</c>, an accepter that cannot fund its share, a first
/// <c>commitment_signed</c> with an HTLC signature, and an RBF that changes the peer's contribution.
/// </summary>
public class DualFundRefusalTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);

    [Fact]
    public async Task Given_NoDualFundNegotiated_When_OpenChannel2Arrives_Then_RefusedAndTheOpenForgotten()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(0, TimeSpan.FromSeconds(1));
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.NegotiatedFeatures = new FeatureOptions();

        // Act
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare),
                                                TestContext.Current.CancellationToken));

        // Assert: Bob sent an error for the temporary channel and kept nothing; Alice gave the open up
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Contains("option_dual_fund", error.PeerMessage);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
        Assert.False(harness.Alice.Memory.TryGetTemporaryChannelState(harness.Bob.NodeId, result.ChannelId, out _));
        Assert.False(harness.Alice.DualFund.IsOpening(result.ChannelId));
    }

    [Fact]
    public async Task Given_TheAccepterCannotFundItsShare_When_Opened_Then_TheChannelOpensWithTheOpenersFundsOnly()
    {
        // Arrange: Bob would contribute 400,000 sat but his wallet is empty
        await using var harness = await DualFundHarness.CreateAsync(400_000);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));

        // Act
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare),
                                                TestContext.Current.CancellationToken));

        // Assert: accept_channel2 said 0, Bob added no input, the funding output is Alice's share alone
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var (_, accept) = Assert.Single(harness.Transcript, t => t.Message is AcceptChannel2Message);
        Assert.True(((AcceptChannel2Message)accept).Payload.FundingAmount.IsZero);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Bob", Message: TxAddInputMessage });
        Assert.Equal(s_aliceShare, harness.Bob.Channel(result.ChannelId).FundingOutput!.Amount);
        Assert.True(harness.Bob.Channel(result.ChannelId).LocalBalance.IsZero);

        // Act: confirmed, then Alice pays Bob
        await harness.ConfirmFundingAsync(result.ChannelId, result.FundingTxId!.Value);
        await harness.Alice.PayAsync(harness.Bob, result.ChannelId, LightningMoney.Satoshis(25_000));
        await harness.PumpAsync();

        // Assert
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(LightningMoney.Satoshis(25_000), harness.Bob.Channel(result.ChannelId).LocalBalance);
    }

    [Fact]
    public async Task Given_AFirstCommitmentSignedWithAnHtlcSignature_When_Received_Then_TheNegotiationFailsWithTxAbort()
    {
        // Arrange: hold back the first commitment_signed and send one that carries an HTLC signature instead
        await using var harness = await DualFundHarness.CreateAsync(400_000, TimeSpan.FromSeconds(2));
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var open = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare),
                                                    TestContext.Current.CancellationToken);
        string? senderName = null;
        await harness.PumpAsync((from, message) =>
        {
            senderName = from;
            return message is CommitmentSignedMessage;
        });
        var sender = harness.Nodes.Single(n => n.Name == senderName);
        Assert.IsType<CommitmentSignedMessage>(harness.TakeNext(sender));
        var channelId = harness.Alice.Memory.FindChannels(_ => true).Single().ChannelId;
        var genuine = harness.Transcript.Count;
        var signature = new byte[64];
        signature[31] = 1;
        signature[63] = 1;
        var tampered = new CommitmentSignedMessage(new CommitmentSignedPayload(channelId, [signature], signature));

        // Act
        await harness.DeliverAsync(sender, tampered);
        await harness.PumpAsync();
        var result = await harness.RunAsync(open);

        // Assert: the receiver answered tx_abort (never a channel failure) and forgot the unsigned open: persisted
        // Stale, its negotiation Aborted, nothing published
        Assert.True(genuine < harness.Transcript.Count);
        var receiver = harness.Other(sender);
        Assert.Contains(harness.Transcript, t => t.From == receiver.Name && t.Message is TxAbortMessage);
        Assert.NotNull(result.FailureReason);
        Assert.False(receiver.Memory.TryGetChannel(channelId, out var kept),
                     $"{receiver.Name} kept {kept?.State}\n{harness.Describe()}");
        var stored = await receiver.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.True(stored is null or { State: ChannelState.Stale });
        var sessions = await receiver.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
        Assert.All(sessions, x => Assert.Equal(InteractiveTxSessionState.Aborted, x.State));
        Assert.Empty(receiver.Published);

        // The sender verified the receiver's genuine commitment_signed and, signing first, sent tx_signatures: from
        // then on it MUST keep the negotiation (IT-ABT-01) until an input of the transaction is spent
        var senderSessions = await sender.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                             .GetByChannelIdAsync(channelId));
        var senderSession = Assert.Single(senderSessions);
        if (senderSession.TxSignaturesSent)
        {
            Assert.Equal(ChannelState.V1FundingSigned, sender.Channel(channelId).State);
            Assert.NotEqual(InteractiveTxSessionState.Aborted, senderSession.State);
        }
        else
        {
            Assert.Equal(InteractiveTxSessionState.Aborted, senderSession.State);
        }

        Assert.Empty(sender.Published);
    }

    [Fact]
    public async Task Given_AnRbfThatChangesTheOpenersContribution_When_Received_Then_TxAbortAndTheOpenStands()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(400_000);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare),
                                                TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var initRbf = new TxInitRbfMessage(new TxInitRbfPayload(result.ChannelId, 5_000, 500),
                                           new FundingOutputContributionTlv(LightningMoney.Satoshis(650_000)));

        // Act
        await harness.DeliverAsync(harness.Alice, initRbf);
        var abort = harness.TakeNext(harness.Bob);

        // Assert: Bob refused the RBF; the signed open is still the channel's funding
        var txAbort = Assert.IsType<TxAbortMessage>(abort);
        Assert.Contains("contribution", System.Text.Encoding.ASCII.GetString(txAbort.Payload.Data));
        Assert.Equal([result.FundingTxId!.Value], harness.Bob.DualFund.GetSignedFundingTxIds(result.ChannelId));
        Assert.Equal(ChannelState.V1FundingSigned, harness.Bob.Channel(result.ChannelId).State);
    }
}