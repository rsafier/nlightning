using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.Hashes;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using InteractiveTx.TestDoubles;

/// <summary>
/// The in-process proof of the dual-funded open (splicing plan wave DF, DF1/DF2; BOLT 2 "Channel Establishment v2"):
/// two nodes, both contributing, through <c>open_channel2</c>/<c>accept_channel2</c>, the interactive funding
/// negotiation, the zero-HTLC first <c>commitment_signed</c> both ways, <c>tx_signatures</c>, the funding
/// confirmation and <c>channel_ready</c>, then payments both ways; RBF of the unconfirmed open; a restart between the
/// signatures resumed by <c>channel_reestablish</c> <c>next_funding</c>.
/// </summary>
public class DualFundHarnessTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_BothNodesContribute_When_AliceOpensDualFunded_Then_TheChannelOpensAndCarriesPayments()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));

        // Act: open_channel2 ... tx_signatures
        var result = await OpenAsync(harness);

        // Assert: one v2 channel id on both sides, derived from both revocation basepoints
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        var channelId = result.ChannelId;
        var alice = harness.Alice.Channel(channelId);
        var bob = harness.Bob.Channel(channelId);
        using (var sha256 = new Infrastructure.Crypto.Hashes.Sha256())
            Assert.Equal(Domain.Channels.DualFunding.ChannelIdV2.Derive(sha256,
                                                                        alice.LocalKeySet.RevocationCompactBasepoint,
                                                                        bob.LocalKeySet.RevocationCompactBasepoint),
                         channelId);
        Assert.Equal(ChannelVersion.V2, alice.Version);
        Assert.Equal(ChannelVersion.V2, bob.Version);
        Assert.True(alice.IsInitiator);
        Assert.False(bob.IsInitiator);

        // Both contributions are the channel's balances, the funding output is their sum
        Assert.Equal(s_aliceShare, alice.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(BobShareSat), alice.RemoteBalance);
        Assert.Equal(LightningMoney.Satoshis(BobShareSat), bob.LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(1_000_000), alice.FundingOutput!.Amount);
        Assert.Equal(result.FundingTxId, alice.FundingOutput.TransactionId);
        Assert.Equal(result.FundingTxId, bob.FundingOutput!.TransactionId);

        // Both added inputs (both contributed), the first commitment_signed had no HTLC signature, then tx_signatures
        var transcript = harness.Transcript;
        Assert.Contains(transcript, t => t is { From: "Alice", Message: TxAddInputMessage });
        Assert.Contains(transcript, t => t is { From: "Bob", Message: TxAddInputMessage });
        var commitments = transcript.Where(t => t.Message is CommitmentSignedMessage).ToList();
        Assert.Equal(2, commitments.Count);
        Assert.All(commitments, c => Assert.Empty(((CommitmentSignedMessage)c.Message).Payload.HtlcSignatures));
        Assert.Equal(2, transcript.Count(t => t.Message is TxSignaturesMessage));
        Assert.True(transcript.FindIndex(t => t.Message is CommitmentSignedMessage)
                  < transcript.FindIndex(t => t.Message is TxSignaturesMessage));

        // Persisted in V1FundingSigned with each side's signature of the other's first commitment, the negotiation
        // Signed, and the funding transaction published by both (BOLT 2: SHOULD broadcast)
        foreach (var node in harness.Nodes)
        {
            var stored = await node.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
            Assert.NotNull(stored);
            Assert.Equal(ChannelState.V1FundingSigned, stored.State);
            Assert.Equal(ChannelVersion.V2, stored.Version);
            Assert.NotNull(stored.LastReceivedSignature);
            Assert.NotNull(stored.LastSentSignature);
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            var session = Assert.Single(sessions);
            Assert.Equal(InteractiveTxSessionState.Signed, session.State);
            Assert.Equal(InteractiveTxPurpose.DualFund, session.Purpose);
            var published = Assert.Single(node.Published);
            Assert.Equal(BroadcastPurpose.Funding, published.Purpose);
            Assert.Equal(result.FundingTxId, published.TransactionId);
        }

        // Act: the funding confirms, channel_ready both ways
        await harness.ConfirmFundingAsync(channelId, result.FundingTxId!.Value);

        // Assert: Open with the first commitment snapshot on both sides
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        Assert.NotNull(harness.Alice.Channel(channelId).Commitments);
        Assert.NotNull(harness.Bob.Channel(channelId).Commitments);

        // Act: a payment each way
        var toBob = LightningMoney.Satoshis(50_000);
        var (bobHash, bobPreimage) = await harness.Alice.PayAsync(harness.Bob, channelId, toBob);
        await harness.PumpAsync();
        var toAlice = LightningMoney.Satoshis(20_000);
        var (aliceHash, alicePreimage) = await harness.Bob.PayAsync(harness.Alice, channelId, toAlice);
        await harness.PumpAsync();

        // Assert: both fulfilled, balances moved
        var fulfilledAtAlice = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(bobHash, fulfilledAtAlice.PaymentHash);
        Assert.Equal(bobPreimage, fulfilledAtAlice.PaymentPreimage);
        var fulfilledAtBob = Assert.Single(harness.Bob.PaymentHandler.Fulfilled);
        Assert.Equal(aliceHash, fulfilledAtBob.PaymentHash);
        Assert.Equal(alicePreimage, fulfilledAtBob.PaymentPreimage);
        Assert.Equal(s_aliceShare - toBob + toAlice, harness.Alice.Channel(channelId).LocalBalance);
        Assert.Equal(LightningMoney.Satoshis(BobShareSat) + toBob - toAlice,
                     harness.Bob.Channel(channelId).LocalBalance);
    }

    [Fact]
    public async Task Given_AnUnconfirmedOpen_When_AliceBumpsItWithRbf_Then_TheReplacementConfirmsAndCarriesPayments()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;

        // Act: the IT-RBF-01 floor is max(25/24 x 2,500, 2,500 + 25) = 2,604 sat/kw
        var belowFloor = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.BumpAsync(channelId, 2_600, TestContext.Current.CancellationToken));
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(channelId, 5_000,
                                                                           TestContext.Current.CancellationToken));

        // Assert: a new funding transaction that re-spends the first one's inputs, the same shares
        Assert.Contains("IT-RBF-01", belowFloor.Message);
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        Assert.NotEqual(first.FundingTxId, bump.FundingTxId);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxInitRbfMessage });
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxAckRbfMessage });
        foreach (var node in harness.Nodes)
        {
            Assert.Equal([first.FundingTxId!.Value, bump.FundingTxId!.Value],
                         node.DualFund.GetSignedFundingTxIds(channelId));
            Assert.Equal(bump.FundingTxId, node.Channel(channelId).FundingOutput!.TransactionId);
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            Assert.Equal(2, sessions.Count(x => x.State == InteractiveTxSessionState.Signed));
            Assert.Contains(sessions, x => x.Purpose == InteractiveTxPurpose.DualFundRbf && x.FeeratePerKw == 5_000);
            var inputs = sessions.Select(x => x.ConstructedTx!.Inputs.Select(i => (i.PrevTxId, i.PrevTxVout))
                                               .ToHashSet())
                                 .ToList();
            Assert.True(inputs[0].Overlaps(inputs[1]), "The replacement must double-spend the first attempt");

            // The first attempt is no longer sent after every block
            var broadcasts = await node.InScopeAsync(u => u.BroadcastTransactionDbRepository
                                                            .GetByChannelIdAsync(channelId));
            Assert.Equal(BroadcastState.Replaced,
                         Assert.Single(broadcasts, b => b.TransactionId == first.FundingTxId).State);
            Assert.Equal(BroadcastState.Pending,
                         Assert.Single(broadcasts, b => b.TransactionId == bump.FundingTxId).State);
        }

        // Act: the replacement confirms, then a payment
        await harness.ConfirmFundingAsync(channelId, bump.FundingTxId!.Value);
        await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(10_000));
        await harness.PumpAsync();

        // Assert
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
    }

    [Fact]
    public async Task Given_TheLinkDropsBeforeTheSecondCommitmentSigned_When_ANodeRestarts_Then_NextFundingFinishesIt()
    {
        // Arrange: stop right before the second commitment_signed is delivered (both sent theirs and stored them)
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        _ = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                             TestContext.Current.CancellationToken);
        var commitments = 0;
        await harness.PumpAsync((_, message) => message is CommitmentSignedMessage && ++commitments == 2);
        var channelId = harness.Transcript.First(t => t.Message is CommitmentSignedMessage).Message.Payload.ChannelId;
        var receiver = harness.Transcript.First(t => t.Message is CommitmentSignedMessage).From == "Alice"
                           ? harness.Alice
                           : harness.Bob;

        // Act: the link drops, the node whose peer's commitment_signed was lost restarts from its database, reconnect
        await harness.RestartAsync(receiver);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: its channel_reestablish asked for the commitment_signed again (next_funding, bit 0), and the open
        // completed on both sides with one funding transaction
        var askedAgain = harness.Transcript.Where(t => t.From == receiver.Name
                                                    && t.Message is ChannelReestablishMessage)
                                .Select(t => ((ChannelReestablishMessage)t.Message).NextFundingTlv)
                                .Single();
        Assert.NotNull(askedAgain);
        Assert.Equal(1, askedAgain.RetransmitFlags & 1);
        foreach (var node in harness.Nodes)
        {
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            Assert.True(Assert.Single(sessions).State == InteractiveTxSessionState.Signed, harness.Describe());
            Assert.Single(node.Published);
        }

        Assert.Equal(harness.Alice.Published[0].TransactionId, harness.Bob.Published[0].TransactionId);
    }

    [Fact]
    public async Task Given_TheLinkDropsBeforeTheFirstTxSignatures_When_TheOpenerRestarts_Then_TheyAreRetransmitted()
    {
        // Arrange: Bob contributes less, so he sends tx_signatures first (IT-SIG-01); stop before it arrives
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        _ = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                             TestContext.Current.CancellationToken);
        await harness.PumpAsync((_, message) => message is TxSignaturesMessage);
        var channelId = harness.Transcript.First(t => t.Message is CommitmentSignedMessage).Message.Payload.ChannelId;

        // Act: Alice restarts (her driver's memory is gone), reconnect
        await harness.RestartAsync(harness.Alice);
        await harness.ReconnectAsync();
        await harness.PumpAsync();

        // Assert: both channel_reestablish carry next_funding without the commitment_signed bit, Bob sent his
        // tx_signatures again and the open completed on both sides
        var reestablishes = harness.Transcript.Where(t => t.Message is ChannelReestablishMessage)
                                   .Select(t => ((ChannelReestablishMessage)t.Message).NextFundingTlv)
                                   .ToList();
        Assert.Equal(2, reestablishes.Count);
        Assert.All(reestablishes, tlv => Assert.Equal(0, tlv!.RetransmitFlags));
        Assert.Contains(harness.Transcript, t => t is { From: "Bob", Message: TxSignaturesMessage });
        foreach (var node in harness.Nodes)
        {
            var sessions = await node.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                          .GetByChannelIdAsync(channelId));
            Assert.True(Assert.Single(sessions).State == InteractiveTxSessionState.Signed, harness.Describe());
        }

        // And the channel opens once the funding confirms
        await harness.ConfirmFundingAsync(channelId, harness.Bob.Published[0].TransactionId);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness, uint feeratePerKw = 2_500)
    {
        var open = harness.Alice.DualFund.OpenAsync(
            new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, feeratePerKw),
            TestContext.Current.CancellationToken);
        return await harness.RunAsync(open);
    }
}