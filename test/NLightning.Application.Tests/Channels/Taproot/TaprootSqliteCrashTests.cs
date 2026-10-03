namespace NLightning.Application.Tests.Channels.Taproot;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Protocol.Messages;
using NLightning.Tests.Utils.Mocks;

/// <summary>
/// Plan D-T4's gate on SQLite (NL-956): a simple taproot channel opened with the v1 flow between two in-process nodes
/// (<see cref="TaprootOpenHarness"/>, production repositories and migrations), where one node's database dies at every
/// save of a payment each way (the persist points of update_add_htlc, commitment_signed, revoke_and_ack, the fulfill,
/// and the reestablish's nonce save and re-signing that follow a restart). The node starts again from what it saved,
/// both sides reestablish (with next_local_nonces) and the channel goes on. Over the whole run no public signing nonce
/// is ever sent twice, every partial signature a node accepted verified, and each local commitment number was accepted
/// for one commitment transaction only (its verification nonce signs one transaction for broadcast).
/// </summary>
public class TaprootSqliteCrashTests
{
    private static readonly LightningMoney s_fundingAmount = LightningMoney.Satoshis(1_000_000);

    [Theory]
    [InlineData("Alice")]
    [InlineData("Bob")]
    public async Task Given_ADatabaseCrashAtEverySave_When_TheNodeRestarts_Then_NoNonceIsReusedAndTheChannelGoesOn(
        string crashing)
    {
        var saves = await MeasureSavesAsync(crashing);
        Assert.True(saves >= 6, $"only {saves} saves");
        for (var crashAt = 1; crashAt <= saves; crashAt++)
        {
            // Arrange
            await using var harness = await TaprootOpenHarness.CreateAsync();
            var (channelId, funding) = await OpenAndConfirmAsync(harness);
            var node = crashing == "Alice" ? harness.Alice : harness.Bob;
            node.Crash.Arm(crashAt);

            // Act - a payment each way; a payer whose own save dies loses that payment
            await TryPayAsync(harness, harness.Alice, harness.Bob, channelId, 100_000);
            await harness.PumpAsync();
            await TryPayAsync(harness, harness.Bob, harness.Alice, channelId, 20_000);
            await harness.PumpAsync();

            // Assert
            var context = $"{crashing} crashed at save {crashAt}";
            Assert.True(harness.Restarts == 1, $"{context}: {harness.Restarts} restarts");
            AssertConverged(harness, channelId, funding, context);
        }
    }

    /// <summary>The saves of the crashing node in the undisturbed run.</summary>
    private static async Task<int> MeasureSavesAsync(string crashing)
    {
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (channelId, funding) = await OpenAndConfirmAsync(harness);
        var node = crashing == "Alice" ? harness.Alice : harness.Bob;
        node.Crash.Arm(null);
        await TryPayAsync(harness, harness.Alice, harness.Bob, channelId, 100_000);
        await harness.PumpAsync();
        await TryPayAsync(harness, harness.Bob, harness.Alice, channelId, 20_000);
        await harness.PumpAsync();
        AssertConverged(harness, channelId, funding, "measure");
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Single(harness.Bob.PaymentHandler.Fulfilled);
        return node.Crash.Saves;
    }

    private static async Task<(ChannelId, TxId)> OpenAndConfirmAsync(TaprootOpenHarness harness)
    {
        var (channelId, funding) = await harness.OpenAsync(s_fundingAmount);
        await harness.ConfirmFundingAsync(channelId, funding.TransactionId);
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);
        return (channelId, funding.TransactionId);
    }

    private static async Task TryPayAsync(TaprootOpenHarness harness, TaprootOpenNode payer, TaprootOpenNode payee,
                                          ChannelId channelId, long sat)
    {
        try
        {
            await payer.PayAsync(payee, channelId, LightningMoney.Satoshis(sat));
        }
        catch (Exception e) when (e is SimulatedCrashException or CommitmentRefusedException
                                   || payer.Crash.Crashed || payee.Crash.Crashed)
        {
            // The payer (or the payee, for its invoice) died, or the link is down until the restart: no payment
            await harness.RecoverAsync();
        }
    }

    private static void AssertConverged(TaprootOpenHarness harness, ChannelId channelId, TxId fundingTxId,
                                        string context)
    {
        var a = harness.Alice.Channel(channelId).Commitments!;
        var b = harness.Bob.Channel(channelId).Commitments!;
        Assert.True(a.Htlcs.IsEmpty && b.Htlcs.IsEmpty, $"{context}: HTLCs left {a.Htlcs.Count}/{b.Htlcs.Count}");
        Assert.True(a.RemoteNextCommit is null && b.RemoteNextCommit is null, $"{context}: a signature is unacked");
        Assert.False(a.HasPendingChangesForRemote || b.HasPendingChangesForRemote, $"{context}: changes pending");
        Assert.True(a.LocalCommit.Number == b.RemoteCommit.Number && b.LocalCommit.Number == a.RemoteCommit.Number,
                    $"{context}: numbers {a.LocalCommit.Number}/{a.RemoteCommit.Number} vs {b.LocalCommit.Number}/{b.RemoteCommit.Number}");
        Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
        Assert.Equal(a.RemoteBalanceMsat, b.LocalBalanceMsat);
        Assert.True(a.HasRemoteNoncesForActiveFundings && b.HasRemoteNoncesForActiveFundings,
                    $"{context}: a peer nonce is missing");
        Assert.Equal(ChannelState.Open, harness.Alice.Channel(channelId).State);
        Assert.Equal(ChannelState.Open, harness.Bob.Channel(channelId).State);

        // No signing nonce sent twice (delivered, lost with the link or dropped while it was down)
        foreach (var name in new[] { "Alice", "Bob" })
        {
            var nonces = harness.Sent.Where(s => s.From == name)
                                .Select(s => s.Message)
                                .OfType<CommitmentSignedMessage>()
                                .Select(m => m.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce)
                                .ToList();
            Assert.True(nonces.Count == nonces.Distinct().Count(), $"{context}: {name} sent a signing nonce twice");
        }

        // Each local commitment number was accepted for one transaction only
        foreach (var node in harness.Nodes)
        foreach (var group in node.Verified.GroupBy(v => v.Number))
            Assert.True(group.Select(v => v.TxId).Distinct().Count() == 1,
                        $"{context}: {node.Name} accepted two different commitments {group.Key}");

        // Every channel_reestablish carried the funding's nonce
        Assert.All(harness.Transcript.Select(t => t.Message).OfType<ChannelReestablishMessage>(),
                   m => Assert.True(m.NextLocalNoncesTlv!.Nonces.Contains(fundingTxId)));
    }
}
