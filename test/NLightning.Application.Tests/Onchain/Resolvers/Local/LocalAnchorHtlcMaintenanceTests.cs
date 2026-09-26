using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Tests.Onchain.Resolvers.Local;

using Application.Onchain.Resolvers;
using Application.Onchain.Resolvers.Local;
using Channels.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Protocol.Interfaces;

/// <summary>
/// NL-314 review (O7-T3): a broadcast anchors HTLC transaction is funded by wallet inputs, so the resolver keeps it
/// confirmable: RBF on the <see cref="SweepFeePolicy.ShouldBump"/> schedule with a BIP 125 fee, rebuilt with other inputs
/// when a wallet input is spent elsewhere, the owner's reservation released once the output is spent, and one warning
/// per output while the wallet cannot pay.
/// </summary>
public sealed class LocalAnchorHtlcMaintenanceTests
{
    private const ulong ReceivedMsat = 30_000_000;
    private const uint ReceivedCltv = 1_020;

    private static readonly Secret s_preimage = RealSigningCommitmentPair.Preimage(2);

    [Fact]
    public async Task Given_AnchorsHtlcSuccessUnconfirmed_When_RbfIntervalPasses_Then_ReplacedWithBip125FeeAndOldMarked()
    {
        // Arrange: the estimate never moves, so only the BIP 125 minimum raises the fee
        var wallet = new AnchorTestWallet(60_000, 50_000);
        using var harness = CreateHarness(wallet);
        harness.HoldMempool = true;
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var first = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        var firstId = new TxId(first.GetHash().ToBytes());

        // Act: one block is not enough (RbfIntervalBlocks = 2), the second is
        await harness.MineAsync();
        Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        await harness.MineAsync();

        // Assert: a replacement of the same HTLC input, script-valid, paying the BIP 125 minimum over the old one
        var transactions = harness.Broadcast(BroadcastPurpose.HtlcTransaction);
        Assert.Equal(2, transactions.Count);
        var second = transactions[1];
        var secondId = new TxId(second.GetHash().ToBytes());
        Assert.Equal(firstId, harness.Broadcasts[secondId].ReplacesTransactionId);
        Assert.Equal(harness.Height, harness.Broadcasts[secondId].FirstBroadcastHeight);
        Assert.Equal(BroadcastState.Replaced, harness.Broadcasts[firstId].State);
        Assert.Equal(secondId, harness.CommitmentRow(vout).ResolvingTransactionId);
        Assert.Equal(first.Inputs[0].PrevOut, second.Inputs[0].PrevOut);
        Assert.Equal(first.Outputs[0].Value, second.Outputs[0].Value);
        harness.AssertAllInputsVerify(second);

        var oldFee = FeeOf(first, wallet);
        var newFee = FeeOf(second, wallet);
        var newVsize = (3L * second.GetSerializedSize(TransactionOptions.None) + second.GetSerializedSize() + 3) / 4;
        Assert.True(newFee >= oldFee + (ulong)newVsize, $"old {oldFee} new {newFee} vsize {newVsize}");
        Assert.True(newFee * 1000 >= oldFee * 1250, $"old {oldFee} new {newFee}");

        // One reservation, for this output, replaced by each selection
        var owner = new AnchorFeeInputOwner(harness.Channel.ChannelId, harness.CommitmentTxId, vout);
        Assert.All(wallet.Selections, s => Assert.Equal(owner, s.Owner));
        Assert.Equal(owner, Assert.Single(wallet.Reservations.Keys));
        Assert.Empty(wallet.Released);

        // Act: the replacement confirms, then more blocks
        harness.HoldMempool = false;
        await harness.MineAsync();
        await harness.MineAsync();

        // Assert: the output is resolved by the replacement and the reservation released once
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(vout).State);
        Assert.Equal(secondId, harness.CommitmentRow(vout).ResolvingTransactionId);
        Assert.Equal(owner, Assert.Single(wallet.Released));
    }

    [Fact]
    public async Task Given_AnchorsHtlcSuccessUnconfirmed_When_ItsWalletInputIsSpentElsewhere_Then_AbandonedAndRebuilt()
    {
        // Arrange
        var wallet = new AnchorTestWallet(60_000, 50_000);
        using var harness = CreateHarness(wallet);
        harness.HoldMempool = true;
        await harness.ResolveAsync();
        var vout = harness.VoutOf(OutputDescriptorKind.LocalReceivedHtlc);
        var first = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        var firstId = new TxId(first.GetHash().ToBytes());
        Assert.Equal(new OutPoint(wallet.FundingTransactions[0], 0), first.Inputs[1].PrevOut);

        // The wallet's output is spent by another transaction, which confirms
        wallet.MarkSpent(wallet.Output(0));
        var conflict = Transaction.Create(Network.Main);
        conflict.Inputs.Add(new OutPoint(wallet.FundingTransactions[0], 0));
        conflict.Outputs.Add(new TxOut(Money.Satoshis(59_000), new Script(wallet.ChangeScript)));

        // Act
        await harness.MineAsync(conflict);

        // Assert: the old transaction is abandoned (never rebroadcast) and a new one with the other output replaces it
        // on the row in the same round
        Assert.Equal(BroadcastState.Abandoned, harness.Broadcasts[firstId].State);
        var second = harness.Broadcast(BroadcastPurpose.HtlcTransaction).Last();
        var secondId = new TxId(second.GetHash().ToBytes());
        Assert.NotEqual(firstId, secondId);
        Assert.Null(harness.Broadcasts[secondId].ReplacesTransactionId);
        Assert.Equal(new OutPoint(wallet.FundingTransactions[1], 0), second.Inputs[1].PrevOut);
        Assert.Equal(secondId, harness.CommitmentRow(vout).ResolvingTransactionId);
        harness.AssertAllInputsVerify(second);

        // Act: it confirms
        harness.HoldMempool = false;
        await harness.MineAsync();

        // Assert
        Assert.Equal(OutputResolutionState.Resolved, harness.CommitmentRow(vout).State);
        Assert.Equal(BroadcastState.Confirmed, harness.Broadcasts[secondId].State);
    }

    [Fact]
    public async Task Given_AnchorsHtlcSuccessUnconfirmed_When_WalletInputsStayUnspent_Then_NotAbandoned()
    {
        // Arrange
        var wallet = new AnchorTestWallet(60_000, 50_000);
        using var harness = CreateHarness(wallet);
        harness.HoldMempool = true;
        await harness.ResolveAsync();
        var first = Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));

        // Act: a block without it (not yet time to bump)
        await harness.MineAsync();

        // Assert
        Assert.Equal(BroadcastState.Pending, harness.Broadcasts[new TxId(first.GetHash().ToBytes())].State);
        Assert.Single(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
        Assert.Single(wallet.Selections);
    }

    [Fact]
    public async Task Given_NoFeeInputProvider_When_ResolvedEveryBlock_Then_OneWarningPerOutput()
    {
        // Arrange: the default registration, which selects nothing
        var logger = new LevelRecordingLogger<LocalCommitResolver>();
        using var harness = new LocalCommitResolutionHarness(Setup, hasAnchors: true, resolverLogger: logger);

        // Act: three rounds
        await harness.ResolveAsync();
        await harness.MineAsync();
        await harness.MineAsync();

        // Assert: one warning, then debug only; nothing broadcast
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("fee-input provider"));
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Debug
                                               && e.Message.Contains("fee-input provider")));
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
    }

    [Fact]
    public async Task Given_WalletCannotPay_When_ResolvedEveryBlock_Then_OneWarningUntilItPays()
    {
        // Arrange: too small at first
        var logger = new LevelRecordingLogger<LocalCommitResolver>();
        var wallet = new AnchorTestWallet(300);
        LocalCommitResolutionHarness? harness = null;
        harness = new LocalCommitResolutionHarness(Setup, hasAnchors: true, feeInputProvider: wallet,
                                                   wrapSigner: signer => CombinedHtlcSigningProxy.Create(
                                                       signer, () => (harness!.Pair.Alice,
                                                                      harness.GetService<IKeyDerivationService>())),
                                                   resolverLogger: logger);
        using var disposable = harness;

        // Act
        await harness.ResolveAsync();
        await harness.MineAsync();
        await harness.MineAsync();

        // Assert
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("cannot pay"));
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Debug && e.Message.Contains("cannot pay")));
        Assert.Equal(3, wallet.Selections.Count);
        Assert.Empty(harness.Broadcast(BroadcastPurpose.HtlcTransaction));
    }

    private static void Setup(RealSigningCommitmentPair pair)
    {
        var id = pair.Add(pair.Bob, ReceivedMsat, s_preimage, ReceivedCltv);
        pair.Settle(pair.Bob);
        pair.Alice.Apply("fulfill", pair.Alice.State.SendFulfill(id, s_preimage,
                                                                 new Infrastructure.Crypto.Hashes.Sha256()));
    }

    private static LocalCommitResolutionHarness CreateHarness(AnchorTestWallet wallet)
    {
        LocalCommitResolutionHarness? harness = null;
        harness = new LocalCommitResolutionHarness(Setup, hasAnchors: true, feeInputProvider: wallet,
                                                   wrapSigner: signer => CombinedHtlcSigningProxy.Create(
                                                       signer, () => (harness!.Pair.Alice,
                                                                      harness.GetService<IKeyDerivationService>())));
        foreach (var funding in wallet.FundingTransactions)
            harness.AddKnownTransaction(funding);
        return harness;
    }

    /// <summary>The fee of a combined HTLC transaction: its wallet inputs minus its change (the HTLC pair pays none).</summary>
    private static ulong FeeOf(Transaction tx, AnchorTestWallet wallet)
    {
        var inputs = tx.Inputs.Skip(1)
                       .Sum(i => wallet.FundingTransactions.First(f => f.GetHash() == i.PrevOut.Hash)
                                       .Outputs[i.PrevOut.N].Value.Satoshi);
        var change = tx.Outputs.Count > 1 ? tx.Outputs[1].Value.Satoshi : 0;
        return (ulong)(inputs - change);
    }

    private sealed class LevelRecordingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}