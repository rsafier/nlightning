using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Accounting;
using Application.Accounting.Books;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Wallet.SilentPayments;
using Infrastructure.Repositories.Database.Accounting;
using Infrastructure.Repositories.Database.Bitcoin;

/// <summary>Production BIP 352 crypto, key source, scanner, chain monitor and books over real SQLite saves.</summary>
public sealed partial class SilentPaymentChainMonitorTests
{
    private const long AmountSat = 75_000;

    [Fact]
    public async Task Given_ASilentPaymentAndAFailedBlockSave_When_RestartedAndRetried_Then_CoinCursorAndAccountingCommitTogether()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        var options = EnabledOptions();
        await using var harness = CreateHarness(keys.Manager, options);
        await harness.StartAsync(95);
        SilentPaymentScanState initialState;
        await using (var initial = harness.Context())
            initialState = (await new SilentPaymentDbRepository(initial).GetScanStateAsync(TestContext.Current.CancellationToken))!;
        var receipt = Receipt(keys.Manager, 1, [AmountSat]);
        harness.FailCommits = true;

        // Act: every SQL write succeeds, then commit fails before discovery and accounting become durable.
        await harness.MineAndDeliverAsync(receipt);

        // Assert: a failed attempt has no durable or in-memory monetary effect.
        Assert.True(harness.Monitor.IsChainProcessingHalted);
        Assert.True(harness.FailedCommits > 0);
        await using (var context = harness.Context())
        {
            var repo = new SilentPaymentDbRepository(context);
            Assert.Empty(await repo.GetOutputsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(initialState, await repo.GetScanStateAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await new UtxoDbRepository(context).GetUnspentAsync(true));
        }
        Assert.Empty(await EventsAsync(harness));
        Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));

        // Act: restart with the committed old cursor, then retry and replay the same tip.
        harness.FailCommits = false;
        await harness.RestartAsync();
        await harness.DeliverTipAsync();
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: one receipt, one spendable coin and the cursor of its committed block.
        await using (var context = harness.Context())
        {
            var repo = new SilentPaymentDbRepository(context);
            Assert.Single(await repo.GetOutputsAsync(TestContext.Current.CancellationToken));
            var state = await repo.GetScanStateAsync(TestContext.Current.CancellationToken);
            Assert.Equal(101u, state!.LiveCursorHeight);
            Assert.Equal(new Domain.Crypto.ValueObjects.Hash(harness.Chain[101].GetHash().ToBytes()), state.LiveCursorHash);
            Assert.NotNull((await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0, true))!.SilentPayment);
        }
        Assert.True(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        var received = Assert.Single(await EventsAsync(harness));
        Assert.Equal(AccountingEventKind.WalletReceived, received.Kind);
        Assert.Equal(AmountSat * 1_000, received.AmountMsat);
        Assert.Equal(AmountSat * 1_000, await WalletBalanceAsync(harness));
    }

    [Fact]
    public async Task Given_AReceiptSpentInItsOwnBlock_When_ProcessedAndRestarted_Then_HistoricalMetadataAndBothAccountingFactsSurvive()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 2, [AmountSat]);
        var spender = Spend(receipt, 0);

        // Act
        await harness.MineAndDeliverAsync(receipt, spender);
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: the coin never becomes spendable, but its receipt and confirmed spend remain auditable.
        await using var context = harness.Context();
        var metadata = await new SilentPaymentDbRepository(context).GetOutputAsync(Id(receipt), 0,
            TestContext.Current.CancellationToken);
        Assert.NotNull(metadata);
        Assert.Equal(Id(spender), metadata.SpentByTransactionId);
        Assert.Equal(101u, metadata.SpentAtHeight);
        Assert.Null(await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0));
        Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        var events = await EventsAsync(harness);
        Assert.Equal(3, events.Count);
        Assert.Equal(AmountSat * 1_000, Assert.Single(events, e => e.Kind == AccountingEventKind.WalletReceived).AmountMsat);
        Assert.Equal(-AmountSat * 1_000, Assert.Single(events, e => e.Kind == AccountingEventKind.WalletOutputSpent).AmountMsat);
        Assert.Equal(0, await WalletBalanceAsync(harness));
        await AssertSimpleSpendSettlementAsync(harness, 0, 0);
    }

    [Fact]
    public async Task Given_AReceiptAndItsSpendInOneBlock_When_ThatBlockIsDisconnected_Then_BothFactsReverseWithoutRestoringACoin()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 7, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt, Spend(receipt, 0));
        Assert.Equal(0, await WalletBalanceAsync(harness));

        // Act
        harness.Chain.Reorg(100, 2);
        await harness.DeliverTipAsync();
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: reversing only the receipt would create a negative wallet balance.
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        await using var context = harness.Context();
        Assert.Empty(await new SilentPaymentDbRepository(context).GetOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await new UtxoDbRepository(context).GetUnspentAsync(true));
        Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        var events = await EventsAsync(harness);
        Assert.Equal(6, events.Count);
        Assert.Equal(3, events.Count(e => e.Kind == AccountingEventKind.Reversal));
        Assert.Equal(0, await WalletBalanceAsync(harness));
        await AssertSimpleSpendSettlementAsync(harness, 3, 0);
    }

    [Fact]
    public async Task Given_ASilentPaymentSpend_When_ItsBlockIsDisconnected_Then_TheCoinAndBooksAreRestoredExactlyOnce()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 3, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        await harness.MineAndDeliverAsync(Spend(receipt, 0));
        Assert.Equal(0, await WalletBalanceAsync(harness));

        // Act: replace the spend with an empty branch, then restart and replay its tip.
        harness.Chain.Reorg(101, 2);
        await harness.DeliverTipAsync();
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        await using var context = harness.Context();
        var metadata = await new SilentPaymentDbRepository(context).GetOutputAsync(Id(receipt), 0,
            TestContext.Current.CancellationToken);
        Assert.Null(metadata!.SpentByTransactionId);
        Assert.Null(metadata.SpentAtHeight);
        Assert.NotNull(await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0));
        Assert.True(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        Assert.Equal(5, (await EventsAsync(harness)).Count);
        Assert.Equal(2, (await EventsAsync(harness)).Count(e => e.Kind == AccountingEventKind.Reversal));
        Assert.Equal(AmountSat * 1_000, await WalletBalanceAsync(harness));
        await AssertSimpleSpendSettlementAsync(harness, 2, AmountSat * 1_000);
    }

    [Fact]
    public async Task Given_ASilentPaymentReceipt_When_ItIsDisconnectedAndReconfirmed_Then_OwnershipAndWalletBalancesFollowTheActiveBranch()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 4, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);

        // Act: disconnect its creating block without reconfirming it.
        harness.Chain.Reorg(100, 2);
        await harness.DeliverTipAsync();

        // Assert: metadata, selection and books all lose the disconnected receipt.
        await using (var context = harness.Context())
            Assert.Empty(await new SilentPaymentDbRepository(context).GetOutputsAsync(TestContext.Current.CancellationToken));
        Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        Assert.Equal(0, await WalletBalanceAsync(harness));

        // Act: the same transaction confirms on the replacement branch.
        await harness.MineAndDeliverAsync(receipt);
        await harness.RestartAsync();
        await harness.DeliverTipAsync();

        // Assert: one active receipt, with its new confirmation height and compensated history.
        await using var read = harness.Context();
        var metadata = Assert.Single(await new SilentPaymentDbRepository(read).GetOutputsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(103u, metadata.BlockHeight);
        Assert.True(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        var events = await EventsAsync(harness);
        Assert.Equal(2, events.Count(e => e.Kind == AccountingEventKind.WalletReceived));
        Assert.Single(events, e => e.Kind == AccountingEventKind.Reversal);
        Assert.Equal(AmountSat * 1_000, await WalletBalanceAsync(harness));
    }

    [Fact]
    public async Task Given_DustAtKZeroAndASpendableOutputAtKOne_When_Scanned_Then_DustAdvancesKWithoutCreatingMoney()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        await using var harness = CreateHarness(keys.Manager, EnabledOptions());
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 5, [1, AmountSat]);

        // Act
        await harness.MineAndDeliverAsync(receipt);
        await harness.RestartAsync();

        // Assert
        await using var context = harness.Context();
        var metadata = await new SilentPaymentDbRepository(context).GetOutputsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, metadata.Count);
        Assert.True(Assert.Single(metadata, o => o.Index == 0).Ignored);
        Assert.False(Assert.Single(metadata, o => o.Index == 1).Ignored);
        Assert.Null(await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 0));
        Assert.NotNull(await new UtxoDbRepository(context).GetByIdAsync(Id(receipt), 1));
        Assert.Equal(1u, Assert.Single(await EventsAsync(harness)).OutputIndex);
        Assert.Equal(AmountSat * 1_000, await WalletBalanceAsync(harness));
    }

    [Fact]
    public async Task Given_StoredSilentPaymentsAndReceiveTurnedOff_When_SpentThenReorged_Then_OwnedCoinsAndAccountingStillRecover()
    {
        // Arrange
        using var keys = new ReceiverKeys();
        var options = EnabledOptions();
        await using var harness = CreateHarness(keys.Manager, options);
        await harness.StartAsync(95);
        var receipt = Receipt(keys.Manager, 6, [AmountSat]);
        await harness.MineAndDeliverAsync(receipt);
        options.Receive = false;
        await harness.RestartAsync();

        // Act: disabling discovery must not disable custody of an existing receipt.
        await harness.MineAndDeliverAsync(Spend(receipt, 0));
        Assert.Equal(0, await WalletBalanceAsync(harness));
        harness.Chain.Reorg(101, 2);
        await harness.DeliverTipAsync();
        await harness.RestartAsync();

        // Assert
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        Assert.True(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        Assert.Equal(AmountSat * 1_000, await WalletBalanceAsync(harness));
        Assert.Equal(5, (await EventsAsync(harness)).Count);
        await AssertSimpleSpendSettlementAsync(harness, 2, AmountSat * 1_000);
    }

    private static SilentPaymentsOptions EnabledOptions() => new()
    {
        Enabled = true,
        Receive = true,
        RecoveryLabelCount = 0,
        MinReceiveSat = 1_000
    };

    private static ChainMonitorHarness CreateHarness(SecureKeyManager keys, SilentPaymentsOptions options,
                                                    SenderPrevoutSource? source = null) =>
        new(configureServices: services =>
        {
            services.AddSingleton(Options.Create(options));
            services.AddSingleton<ISilentPaymentKeySource>(keys);
            services.AddSingleton<ISilentPaymentCrypto, SilentPaymentCrypto>();
            services.AddSingleton<IBlockPrevoutSource>(source ?? new SenderPrevoutSource());
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<SilentPaymentScanner>>(
                NullLogger<SilentPaymentScanner>.Instance);
            services.AddSingleton<SilentPaymentScanner>();
        });

    private static Transaction Receipt(SecureKeyManager keys, byte seed, IReadOnlyList<long> amounts)
    {
        using var sender = new Key(Enumerable.Repeat((byte)0x21, 32).ToArray());
        var previous = new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 0);
        var bytes = new byte[36];
        previous.Hash.ToBytes().CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), previous.N);
        var input = new SilentPaymentSenderInput(bytes, sender.ToBytes(), false);
        try
        {
            var crypto = new SilentPaymentCrypto();
            var derived = crypto.DeriveOutputs([input], amounts.Select(_ =>
                new SilentPaymentRecipient(keys.ScanPubKey, keys.SpendPubKey)).ToArray());
            var transaction = Network.RegTest.CreateTransaction();
            transaction.Inputs.Add(new TxIn(previous) { WitScript = new WitScript([new byte[71], sender.PubKey.ToBytes()]) });
            for (var index = 0; index < amounts.Count; index++)
                transaction.Outputs.Add(new TxOut(Money.Satoshis(amounts[index]), new Script([0x51, 0x20, .. derived[index].OutputKey32])));
            return transaction;
        }
        finally { CryptographicOperations.ZeroMemory(input.PrivateKey32!); }
    }

    private static Transaction Spend(Transaction receipt, uint output)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(new OutPoint(receipt.GetHash(), output)) { WitScript = new WitScript([new byte[64]]) });
        using var destination = new Key(Enumerable.Repeat((byte)0x22, 32).ToArray());
        transaction.Outputs.Add(new TxOut(receipt.Outputs[(int)output].Value - Money.Satoshis(500), destination.PubKey.WitHash.ScriptPubKey));
        return transaction;
    }

    private static IUtxoMemoryRepository Memory(ChainMonitorHarness harness) =>
        harness.Services.GetRequiredService<IUtxoMemoryRepository>();

    private static TxId Id(Transaction transaction) => new(transaction.GetHash().ToBytes());

    private static async Task<IReadOnlyList<AccountingEventModel>> EventsAsync(ChainMonitorHarness harness)
    {
        await using var context = harness.Context();
        return await new AccountingEventDbRepository(context).GetAtOrAboveHeightAsync(0,
            Enum.GetValues<AccountingEventKind>(), TestContext.Current.CancellationToken);
    }

    private static async Task<long> WalletBalanceAsync(ChainMonitorHarness harness)
    {
        var scopes = harness.Services.GetRequiredService<IServiceScopeFactory>();
        using var sealer = new AccountingEventSealerService(scopes, NullLogger<AccountingEventSealerService>.Instance);
        await using var books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance, sealer: sealer);
        await books.ProjectNowAsync(TestContext.Current.CancellationToken);
        await using var context = harness.Context();
        return (await new AccountingBooksDbRepository(context).GetBalancesAsync(TestContext.Current.CancellationToken))
            .GetValueOrDefault(AccountRole.Wallet);
    }

    private sealed class ReceiverKeys : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "nltg-sp-monitor-" + Guid.NewGuid().ToString("N"));
        public SecureKeyManager Manager { get; }
        public ReceiverKeys()
        {
            Directory.CreateDirectory(_directory);
            Manager = SecureKeyManager.FromMnemonic(
                "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about",
                string.Empty, Domain.Protocol.ValueObjects.BitcoinNetwork.Regtest, Path.Combine(_directory, "keys.json"));
        }
        public void Dispose()
        {
            Manager.Dispose();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class SenderPrevoutSource : IBlockPrevoutSource
    {
        private readonly Dictionary<TxId, IReadOnlyList<BitcoinPrevout>> _overrides = [];
        public void Add(Transaction transaction, params BitcoinPrevout[] prevouts) => _overrides[Id(transaction)] = prevouts;
        public SilentPaymentPrevoutSource Source => SilentPaymentPrevoutSource.GetBlock;
        public Task ProbeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ValidateHeightAsync(uint height, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>>> GetPrevoutsAsync(BitcoinBlock block,
            uint height, CancellationToken cancellationToken = default)
        {
            using var sender = new Key(Enumerable.Repeat((byte)0x21, 32).ToArray());
            var script = new BitcoinScript(sender.PubKey.WitHash.ScriptPubKey.ToBytes());
            IReadOnlyDictionary<TxId, IReadOnlyList<BitcoinPrevout>> previous = Block.Load(block.BlockData, Network.RegTest)
                .Transactions.Where(t => !t.IsCoinBase).ToDictionary(Id,
                    t => _overrides.GetValueOrDefault(Id(t)) ??
                        (IReadOnlyList<BitcoinPrevout>)t.Inputs.Select(_ => new BitcoinPrevout(200_000, script)).ToArray());
            return Task.FromResult(previous);
        }
    }
}