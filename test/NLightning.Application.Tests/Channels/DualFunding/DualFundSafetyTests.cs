namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Events;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using InteractiveTx.TestDoubles;

/// <summary>
/// The safety rules of the dual-funded open (wave sp1 review of lane SP1-F) on <see cref="DualFundHarness"/>: an open
/// is never forgotten once our <c>tx_signatures</c> went out (the peer can broadcast the funding transaction), and is
/// aborted through the driver before that; RBF is off by default, refused for public channels, and an RBF attempt
/// that ends before it is signed leaves the channel on the signed funding; the accepter's open times out before our
/// <c>commitment_signed</c>; mismatched <c>next_funding</c> values fail the channel.
/// </summary>
public class DualFundSafetyTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private const long BobShareSat = 400_000;

    [Fact]
    public async Task Given_WeSignedFirst_When_TheOpenTimesOutWithoutThePeersTxSignatures_Then_TheChannelIsKept()
    {
        // Arrange: Alice's input is the smaller one, so she sends tx_signatures first (IT-SIG-01); Bob's never arrives
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, TimeSpan.FromSeconds(2));
        FundAliceSigningFirst(harness);
        var open = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxSignaturesMessage);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxSignaturesMessage });
        var channelId = SingleChannel(harness.Alice);

        // Act
        var result = await open;

        // Assert: the open failed but the channel, its negotiation, its reservation and its funding watches stay
        Assert.NotNull(result.FailureReason);
        Assert.Contains("kept", result.FailureReason);
        await AssertKeptWithFundingWatchedAsync(harness, channelId);

        // Act: Bob's tx_signatures arrive after all
        await harness.PumpAsync();

        // Assert: the open completes and the funding transaction is published
        var published = Assert.Single(harness.Alice.Published);
        Assert.Equal(harness.Alice.Channel(channelId).FundingOutput!.TransactionId, published.TransactionId);
    }

    [Fact]
    public async Task Given_WeSignedFirst_When_ThePeerSendsAnError_Then_TheChannelIsKept()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, withPeerServices: true);
        FundAliceSigningFirst(harness);
        var open = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxSignaturesMessage);
        Assert.Contains(harness.Transcript, t => t is { From: "Alice", Message: TxSignaturesMessage });
        var channelId = SingleChannel(harness.Alice);

        // Act: a hostile peer takes our tx_signatures and sends an error (a warning looks the same to the open)
        harness.Alice.PeerService.Raise(p => p.OnAttentionMessageReceived += null, harness.Alice.PeerService.Object,
                                        new AttentionMessageEventArgs("bye", harness.Bob.NodeId, channelId));
        var result = await open;

        // Assert
        Assert.NotNull(result.FailureReason);
        Assert.Contains("kept", result.FailureReason);
        await AssertKeptWithFundingWatchedAsync(harness, channelId);

        // Act: its tx_signatures arrive later
        await harness.PumpAsync();

        // Assert
        Assert.Single(harness.Alice.Published);
    }

    [Fact]
    public async Task Given_OurCommitmentSignedSentButNotOurTxSignatures_When_ThePeerSendsAnError_Then_AbortedFirst()
    {
        // Arrange: Bob's input is the smaller one, so Alice waits for his tx_signatures before sending hers
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, withPeerServices: true);
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
        var open = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxSignaturesMessage);
        var channelId = SingleChannel(harness.Alice);
        Assert.Equal(ChannelState.V1FundingSigned, harness.Alice.Channel(channelId).State);

        // Act
        harness.Alice.PeerService.Raise(p => p.OnAttentionMessageReceived += null, harness.Alice.PeerService.Object,
                                        new AttentionMessageEventArgs("bye", harness.Bob.NodeId, channelId));
        var result = await open;

        // Assert: our tx_abort went out through the driver (the reservation released, the negotiation Aborted), then
        // the channel was forgotten (persisted Stale)
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain("kept", result.FailureReason);
        Assert.IsType<TxAbortMessage>(harness.TakeNext(harness.Alice));
        Assert.NotEmpty(harness.Alice.Wallet.Released);
        Assert.False(harness.Alice.Memory.TryGetChannel(channelId, out _));
        var stored = await harness.Alice.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.Equal(ChannelState.Stale, stored!.State);
        var sessions = await harness.Alice.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                               .GetByChannelIdAsync(channelId));
        Assert.Equal(InteractiveTxSessionState.Aborted, Assert.Single(sessions).State);
    }

    [Fact]
    public void Given_TheDefaultOptions_When_Read_Then_RbfIsAllowed()
    {
        // Arrange, Act, Assert: lane dfrbf (owner decision 2026-09-28; NL-528 made it safe)
        Assert.True(new Application.Channels.DualFunding.DualFundingOptions().AllowRbf);
    }

    [Fact]
    public async Task Given_RbfTurnedOff_When_AnRbfIsRequested_Then_RefusedBothWays()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: false);
        FundDefault(harness);
        var result = await OpenAsync(harness);
        var initRbf = new TxInitRbfMessage(new TxInitRbfPayload(result.ChannelId, 5_000, 500),
                                           new FundingOutputContributionTlv(s_aliceShare));

        // Act
        var bump = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Alice.DualFund.BumpAsync(result.ChannelId, 5_000, TestContext.Current.CancellationToken));
        await harness.DeliverAsync(harness.Alice, initRbf);
        var reply = harness.TakeNext(harness.Bob);

        // Assert
        Assert.Contains("not enabled", bump.Message);
        var abort = Assert.IsType<TxAbortMessage>(reply);
        Assert.Contains("not enabled", System.Text.Encoding.ASCII.GetString(abort.Payload.Data));
        Assert.Equal([result.FundingTxId!.Value], harness.Bob.DualFund.GetSignedFundingTxIds(result.ChannelId));
    }

    [Fact]
    public async Task Given_APublicDualFundedOpen_When_Bumped_Then_TheReplacementOpensTheChannel()
    {
        // Arrange
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        FundDefault(harness);
        var result = await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                                new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500,
                                                                          IsPublic: true),
                                                TestContext.Current.CancellationToken));
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");

        // Act: NL-528 lifted the refusal (the announcement is built from the attempt the channel follows)
        var bump = await harness.RunAsync(harness.Alice.DualFund.BumpAsync(result.ChannelId, 5_000,
                                                                           TestContext.Current.CancellationToken));
        await harness.ConfirmFundingAsync(result.ChannelId, bump.FundingTxId!.Value);

        // Assert
        Assert.True(bump.FailureReason is null, $"{bump.FailureReason}\n{harness.Describe()}");
        foreach (var node in harness.Nodes)
        {
            var channel = node.Channel(result.ChannelId);
            Assert.True(channel.AnnounceChannel);
            Assert.Equal(ChannelState.Open, channel.State);
            Assert.Equal(bump.FundingTxId, channel.FundingOutput!.TransactionId);
        }
    }

    [Fact]
    public async Task Given_AnRbfAttemptPastBothCommitmentSigned_When_ThePeerAborts_Then_TheSignedFundingIsRestored()
    {
        // Arrange: a signed open; Bob's input is the smaller one, so Alice's tx_signatures come after his
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, allowRbf: true);
        FundDefault(harness);
        var first = await OpenAsync(harness);
        Assert.True(first.FailureReason is null, $"{first.FailureReason}\n{harness.Describe()}");
        var channelId = first.ChannelId;
        var alice = harness.Alice.Channel(channelId);
        var signedIndex = alice.FundingOutput!.Index;
        var sent = alice.LastSentSignature;
        var received = alice.LastReceivedSignature;

        var bump = harness.Alice.DualFund.BumpAsync(channelId, 5_000, TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxSignaturesMessage
                                                && harness.Transcript.Any(t => t.Message is TxInitRbfMessage));
        Assert.NotEqual(first.FundingTxId, alice.FundingOutput.TransactionId);
        Assert.IsType<TxSignaturesMessage>(harness.TakeNext(harness.Bob));

        // Act: the peer aborts the attempt after both commitment_signed, before Alice's tx_signatures
        await harness.DeliverAsync(harness.Bob,
                                   new TxAbortMessage(new TxAbortPayload(channelId, "changed my mind"u8.ToArray())));
        await harness.PumpAsync();
        var result = await bump;

        // Assert: the channel is back on the signed funding with its signatures, in memory and in the database
        Assert.NotNull(result.FailureReason);
        Assert.Equal(first.FundingTxId, alice.FundingOutput.TransactionId);
        Assert.Equal(signedIndex, alice.FundingOutput.Index);
        Assert.Equal(sent, alice.LastSentSignature);
        Assert.Equal(received, alice.LastReceivedSignature);
        var stored = await harness.Alice.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.Equal(first.FundingTxId, stored!.FundingOutput!.TransactionId);
        Assert.Equal(received, stored.LastReceivedSignature);
        Assert.Equal([first.FundingTxId!.Value], harness.Alice.DualFund.GetSignedFundingTxIds(channelId));

        // And the signed funding still opens the channel
        await harness.ConfirmFundingAsync(channelId, first.FundingTxId!.Value);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
    }

    [Fact]
    public async Task Given_APeerSilentAfterAcceptChannel2_When_TheOpenTimeoutPasses_Then_TheAccepterAbortsAndForgets()
    {
        // Arrange: Bob accepts, then nothing more reaches him
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat, TimeSpan.FromSeconds(1));
        FundDefault(harness);
        var open = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                                    TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is AcceptChannel2Message);
        var channelId = SingleChannel(harness.Bob);
        Assert.True(harness.Bob.DualFund.IsOpening(channelId));

        // Act
        for (var i = 0; i < 100 && harness.Bob.DualFund.IsOpening(channelId); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        await open;

        // Assert: Bob sent tx_abort after his accept_channel2 and kept nothing
        Assert.False(harness.Bob.DualFund.IsOpening(channelId));
        Assert.False(harness.Bob.Memory.TryGetChannel(channelId, out _));
        Assert.IsType<AcceptChannel2Message>(harness.TakeNext(harness.Bob));
        Assert.IsType<TxAbortMessage>(harness.TakeNext(harness.Bob));
    }

    [Fact]
    public async Task Given_BothSendNextFunding_When_TheValuesDiffer_Then_TheChannelFails()
    {
        // Arrange: Bob signs first; the link drops before his tx_signatures reach Alice, so both owe next_funding
        await using var harness = await DualFundHarness.CreateAsync(BobShareSat);
        FundDefault(harness);
        _ = harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                             TestContext.Current.CancellationToken);
        await harness.PumpAsync((from, message) => from == "Bob" && message is TxSignaturesMessage);
        var channelId = SingleChannel(harness.Alice);
        await harness.DisconnectAsync();
        await harness.ReconnectAsync();
        var ours = Assert.IsType<ChannelReestablishMessage>(harness.TakeNext(harness.Alice));
        Assert.NotNull(ours.NextFundingTlv);
        var other = new byte[32];
        Array.Fill(other, (byte)0x42);

        // Act: Alice's channel_reestablish arrives with another next_funding_txid
        await harness.DeliverAsync(harness.Alice,
                                   new ChannelReestablishMessage(ours.Payload, new NextFundingTlv(other, 0)));

        // Assert: Bob sent an error and failed the channel (BOLT 2)
        Assert.Contains(harness.Bob.Errors, e => e is ChannelFailedException);
        var stored = await harness.Bob.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.Equal(ChannelState.Failed, stored!.State);
    }

    private static void FundDefault(DualFundHarness harness)
    {
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(700_000));
    }

    private static void FundAliceSigningFirst(DualFundHarness harness)
    {
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(620_000));
        harness.Bob.Wallet.Utxos.Add(WalletUtxo.Create(2_000_000));
    }

    private static ChannelId SingleChannel(DualFundNode node) => node.Memory.FindChannels(_ => true).Single().ChannelId;

    private static async Task AssertKeptWithFundingWatchedAsync(DualFundHarness harness, ChannelId channelId)
    {
        var alice = harness.Alice;
        Assert.True(alice.DualFund.IsOpening(channelId));
        Assert.Equal(ChannelState.V1FundingSigned, alice.Channel(channelId).State);
        var stored = await alice.InScopeAsync(u => u.ChannelDbRepository.GetByIdAsync(channelId));
        Assert.Equal(ChannelState.V1FundingSigned, stored!.State);
        var session = Assert.Single(await alice.InScopeAsync(u => u.InteractiveTxSessionDbRepository
                                                                    .GetByChannelIdAsync(channelId)));
        Assert.Equal(InteractiveTxSessionState.TxSignaturesSent, session.State);
        var txId = session.ConstructedTx!.TxId;
        Assert.NotNull(await alice.InScopeAsync(u => u.WatchedTransactionDbRepository.GetByTransactionIdAsync(txId)));
        Assert.NotNull(await alice.InScopeAsync(u => u.WatchedOutpointDbRepository.GetAsync(
                                                     txId, alice.Channel(channelId).FundingOutput!.Index!.Value)));
        Assert.Empty(alice.Wallet.Released);
        Assert.Empty(alice.Published);
    }

    private static async Task<DualFundedOpenResult> OpenAsync(DualFundHarness harness) =>
        await harness.RunAsync(harness.Alice.DualFund.OpenAsync(
                                   new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare, 2_500),
                                   TestContext.Current.CancellationToken));
}