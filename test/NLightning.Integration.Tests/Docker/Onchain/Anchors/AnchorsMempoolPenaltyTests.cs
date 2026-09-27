using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using OutPoint = NBitcoin.OutPoint;
using Transaction = NBitcoin.Transaction;

namespace NLightning.Integration.Tests.Docker.Onchain.Anchors;

using Abcd;
using Cheater;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Client.Requests;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Fixtures;
using Infrastructure.Bitcoin.Outputs;
using Utils;

/// <summary>
/// BOLT 5 plan O8 on an anchors channel (O7-T4; the mempool variant of <c>OnchainMempoolTests</c> (b)): two
/// NLightning nodes with <c>option_anchors</c>, the cheater's revoked commitment k sent to bitcoind and not mined. The
/// victim, running with the mempool reaction (<c>Bitcoin:WatchMempool</c>, on by default), must get the penalty of the
/// cheater's <c>to_local</c> into the mempool before any block. On an anchors channel the victim's <c>to_remote</c> in
/// k is <c>&lt;pubkey&gt; OP_CHECKSIGVERIFY 1 OP_CHECKSEQUENCEVERIFY</c> (BOLT 3): a spend of it needs nSequence 1
/// and is not BIP 68 final while k is unconfirmed, so bitcoind refuses any transaction that carries it together with
/// the penalty. The <c>to_remote</c> is swept only once k has a confirmation.
/// </summary>
/// <remarks>
/// <para>Fails on the pre-O7 <c>RevokedCommitResolver.PrepareUnconfirmedPenaltiesAsync</c>, which puts
/// <c>PaymentToRemote</c> into the mempool penalty batch (a new ledger entry; the fix belongs to the resolver's lane). The
/// confirmed-path breach on an anchors channel is <see cref="AnchorsO5Tests"/>.</para>
/// <para>Run with <c>ONCHAIN_SUITE=anchors scripts/run-onchain.sh</c>.</para>
/// </remarks>
[Collection(OnchainRegtestCollection.Name)]
[Trait("Category", AnchorsChannelTests.AnchorsCategory)]
public class AnchorsMempoolPenaltyTests : IAsyncLifetime
{
    private readonly AnchorsHarness _harness;

    public AnchorsMempoolPenaltyTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    {
        _harness = new AnchorsHarness(fixture);
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Given_RevokedAnchorsCommitmentInTheMempool_When_Seen_Then_ToLocalPenaltyInTheMempoolBeforeAnyBlockAndToRemoteSweptAfterOneConfirmation()
    {
        // Arrange: the O5 (b) breach on an anchors channel, up to the broadcast
        var ct = TestContext.Current.CancellationToken;
        var (victim, channelId, captured) = await PrepareCheaterBreachAsync(ct);
        var revoked = captured.Transaction;
        var revokedHash = revoked.GetHash();
        var model = AnchorsHarness.GetModel(victim, channelId);
        var (anchorA, anchorB) = AnchorsHarness.FindAnchors(model, revoked);
        var toRemoteScript = new ToRemoteOutput(LightningMoney.Zero, true,
                                                new PubKey(model.LocalKeySet.PaymentCompactBasepoint))
                            .ToTxOut().ScriptPubKey;
        var toRemoteVout = (uint)revoked.Outputs.FindIndex(o => o.ScriptPubKey == toRemoteScript);
        Assert.True(toRemoteVout < revoked.Outputs.Count, "no CSV-1 to_remote of the victim in the revoked commitment");
        var toLocalVout = Assert.Single(Enumerable.Range(0, revoked.Outputs.Count).Select(v => (uint)v),
                                        v => v != anchorA && v != anchorB && v != toRemoteVout);
        var tip = (uint)await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct);

        // Act: the cheater broadcasts k; no block is mined
        await _harness.Fixture.Bitcoin.SendRawTransactionAsync(revoked, ct);
        Console.WriteLine($"[o7-o8] revoked anchors commitment {revokedHash} sent at tip {tip}; to_local {toLocalVout}, "
                        + $"to_remote {toRemoteVout}, anchors {anchorA}/{anchorB}");
        var toLocal = new OutPoint(revokedHash, toLocalVout);
        var penalty = await Poll.ForAsync(() => _harness.FindMempoolSpenderAsync(toLocal, ct),
                                          AnchorsHarness.Timeout,
                                          "the penalty of the revoked to_local in the mempool before any block", ct);

        // Assert (before any block): the penalty is a pending Penalty broadcast; it spends no CSV-1 output of k (which
        // bitcoind would refuse), and nothing is recorded as a close
        Assert.Equal(tip, (uint)await _harness.Fixture.Bitcoin.GetBlockCountAsync(ct));
        Assert.DoesNotContain(penalty.Inputs, i => i.PrevOut == new OutPoint(revokedHash, toRemoteVout));
        TxId penaltyTxId = penalty.GetHash().ToBytes();
        var stored = await GetBroadcastAsync(victim, penaltyTxId);
        Assert.NotNull(stored);
        Assert.Equal(BroadcastPurpose.Penalty, stored.Purpose);
        Assert.Null(await GetCloseAsync(victim, channelId));

        // Act: one block
        await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [], [victim], ct);

        // Assert: k and the penalty confirmed in that block; the revoked close recorded
        var commitmentInfo = await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(revokedHash, ct);
        Assert.Equal(1u, commitmentInfo.Confirmations);
        Assert.Equal(1u, (await _harness.Fixture.Bitcoin.GetRawTransactionInfoAsync(penalty.GetHash(), ct))
                        .Confirmations);
        var close = await AnchorsHarness.WaitForCloseAsync(victim, channelId, ct);
        Assert.Equal(ChannelCloseKind.RevokedCommitment, close.Kind);

        // The to_remote is swept with nSequence 1 once k has its confirmation, and confirms
        var toRemote = new OutPoint(revokedHash, toRemoteVout);
        var sweep = await _harness.MineUntilAsync(victim, [], async () =>
        {
            var spender = await _harness.FindMempoolSpenderAsync(toRemote, ct);
            return spender ?? await FindConfirmedSpenderAsync(victim, channelId, revoked, toRemoteVout, ct);
        }, "the CSV-1 to_remote swept", ct);
        var input = Assert.Single(sweep.Inputs, i => i.PrevOut == toRemote);
        Assert.Equal(1u, input.Sequence.Value);
        Assert.True(AnchorsHarness.IsAnchorsToRemoteSpend(input.WitScript), "not the anchors to_remote witness");
        await _harness.MineUntilConfirmedAsync(victim, [], sweep.GetHash(), ct);
        Console.WriteLine($"[o7-o8] penalty {penalty.GetHash()} at tip {tip}, to_remote sweep {sweep.GetHash()}");
    }

    public async ValueTask DisposeAsync()
    {
        await _harness.DisposeNodesAsync([]);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The breach of Proof O5 (b) on an anchors channel: cheater → victim, both with <c>option_anchors</c>, 1,000,000
    /// sat with 100,000 pushed; one payment, the cheater's commitment k captured, three more payments (the cheater
    /// revokes k), the cheater stopped.
    /// </summary>
    private async Task<(NLightningTestNode Victim, ChannelId ChannelId, CapturedCommitment Captured)>
        PrepareCheaterBreachAsync(CancellationToken ct)
    {
        var cheater = await _harness.CreateNodeAsync("anchors-o8-cheater", ct);
        var victim = await _harness.CreateNodeAsync("anchors-o8-victim", ct);
        await cheater.FundWalletAsync(LightningMoney.Satoshis(1_500_000), AddressType.P2Wpkh, ct);
        // NL-379: as fundee of an anchors channel the victim keeps the on-chain anchors reserve (10,000 sat)
        await victim.FundWalletAsync(LightningMoney.Satoshis(100_000), AddressType.P2Wpkh, ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [cheater, victim], ct);
        await cheater.ConnectToAsync(victim, ct);
        var opened = await cheater.OpenChannelAsync(new OpenChannelClientRequest(victim.Address,
                                                                                 AnchorsHarness.Capacity)
        {
            PushAmount = LightningMoney.Satoshis(100_000),
            FeeRatePerKw = AnchorsHarness.EstimateFeeRatePerKw
        }, ct);
        var channelId = opened.ChannelId;
        await Poll.UntilAsync(async () =>
        {
            var usable = (await cheater.GetChannelAsync(channelId, ct)).IsUsable()
                      && (await victim.GetChannelAsync(channelId, ct)).IsUsable();
            if (!usable)
                await ChainSync.MineAndWaitAsync(_harness.Fixture, 1, [], [cheater, victim], ct);
            return usable;
        }, AnchorsHarness.Timeout, $"channel {channelId} usable", ct);
        await ChainSync.WaitAllAtTipAsync(_harness.Fixture, [cheater, victim], ct);
        Assert.True(AnchorsHarness.GetModel(victim, channelId).ChannelParams.OptionAnchorOutputs,
                    "the victim's channel is not an anchors channel");
        Assert.True(AnchorsHarness.GetModel(cheater, channelId).ChannelParams.OptionAnchorOutputs,
                    "the cheater's channel is not an anchors channel");

        await PayAsync(cheater, victim, channelId, 20_000, ct);
        var captured = await StaleCommitmentCapture.CaptureAsync(cheater, channelId);
        for (var i = 0; i < 3; i++)
            await PayAsync(cheater, victim, channelId, 50_000, ct);
        var now = await victim.GetChannelAsync(channelId, ct);
        Assert.True(now.RemoteCommitmentNumber > captured.Number + 1,
                    $"the cheater's commitment {captured.Number} is not revoked yet ({now.Describe()})");

        await cheater.StopAsync();
        return (victim, channelId, captured);
    }

    /// <summary><paramref name="payer"/> pays <paramref name="payee"/>'s invoice over their channel, and both sides
    /// settle.</summary>
    private static async Task PayAsync(NLightningTestNode payer, NLightningTestNode payee, ChannelId channelId,
                                       long amountSat, CancellationToken ct)
    {
        var invoice = await payee.CreateInvoiceAsync(LightningMoney.Satoshis(amountSat), "o7 o8 payment", ct);
        var payment = await payer.PayInvoiceAsync(invoice.Bolt11!, ct);
        Assert.Equal(PaymentStatus.Succeeded, payment.Status);
        await Poll.UntilAsync(async () =>
        {
            var ours = await payer.GetChannelAsync(channelId, ct);
            var theirs = await payee.GetChannelAsync(channelId, ct);
            return ours is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && theirs is { OfferedHtlcCount: 0, ReceivedHtlcCount: 0 }
                && ours.LocalBalance == theirs.RemoteBalance
                && ours.LocalCommitmentNumber == theirs.RemoteCommitmentNumber
                && ours.RemoteCommitmentNumber == theirs.LocalCommitmentNumber;
        }, AnchorsHarness.Timeout, "the payment settled on both sides", ct);
    }

    /// <summary>The confirmed transaction the victim recorded as resolving the output, when it spends it.</summary>
    private async Task<Transaction?> FindConfirmedSpenderAsync(NLightningTestNode victim, ChannelId channelId,
                                                               Transaction revoked, uint vout, CancellationToken ct)
    {
        TxId revokedTxId = revoked.GetHash().ToBytes();
        var row = (await AnchorsHarness.GetRowsAsync(victim, channelId))
                 .FirstOrDefault(r => r.TransactionId == revokedTxId && r.OutputIndex == vout);
        if (row?.ResolvingTransactionId is not { } txId)
            return null;

        try
        {
            var tx = await _harness.Fixture.Bitcoin.GetRawTransactionAsync(new uint256((byte[])txId), true, ct);
            return tx.Inputs.Any(i => i.PrevOut == new OutPoint(revoked, vout)) ? tx : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // Not broadcast yet or replaced
        }
    }

    private static async Task<ChannelCloseModel?> GetCloseAsync(NLightningTestNode node, ChannelId channelId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().OnchainResolutionDbRepository
                          .GetCloseAsync(channelId);
    }

    private static async Task<BroadcastTransactionModel?> GetBroadcastAsync(NLightningTestNode node, TxId txId)
    {
        using var scope = node.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BroadcastTransactionDbRepository
                          .GetByTransactionIdAsync(txId);
    }
}