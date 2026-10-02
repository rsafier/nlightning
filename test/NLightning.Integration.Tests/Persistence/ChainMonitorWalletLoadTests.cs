using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-600: <see cref="Infrastructure.Bitcoin.Wallet.BlockchainMonitorService.LoadWalletAsync"/> over the real SQLite
/// schema. The hosts start the peer manager before the chain monitor, so a splice a peer resumed right after a restart
/// signed its reserved wallet inputs against an empty UTXO set ("The signer found no wallet input in the
/// transaction"); the wallet load the hosts now run before the peers fills the UTXO set and the fee reservations
/// without asking bitcoind, and the start that follows does not load it again.
/// </summary>
public class ChainMonitorWalletLoadTests
{
    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f"));

    [Fact]
    public async Task Given_AReservedWalletOutputAfterARestart_When_TheWalletIsLoadedBeforeTheStart_Then_TheSignerSignsIt()
    {
        // Arrange: a reserved wallet output and the last processed height stored by the previous process
        await using var harness = new ChainMonitorHarness(tipHeight: 100);
        var (utxo, txOut) = await SeedWalletOutputAsync(harness, 0, 500_000);
        var reservationId = await SeedReservationAsync(harness, utxo);
        await SeedStateAsync(harness, 95);
        var memory = harness.Services.GetRequiredService<IUtxoMemoryRepository>();
        var signer = CreateSigner(memory);
        var spend = CreateSpend(utxo);

        // Assert (the NL-600 failure): before the wallet is loaded the signer finds no wallet input
        Assert.False(signer.SignWalletTransaction(ToSigned(spend), reservationId, []));

        // Act
        await harness.Monitor.LoadWalletAsync(TestContext.Current.CancellationToken);

        // Assert: the output, its address and its reservation are in memory, and the stored height is known
        Assert.True(memory.TryGetUtxo(utxo.TxId, utxo.Index, out var loaded));
        Assert.NotNull(loaded.WalletAddress);
        Assert.True(memory.TryGetFeeReservation(utxo.TxId, utxo.Index, out var loadedReservation));
        Assert.Equal(reservationId, loadedReservation);
        Assert.Equal(95u, harness.Monitor.LastProcessedBlockHeight);

        var signed = ToSigned(spend);
        Assert.True(signer.SignWalletTransaction(signed, reservationId, []));
        var result = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        Assert.True(result.Inputs.FindIndexedInput(0).VerifyScript(txOut, out var error), $"our input: {error}");

        // Act: the chain monitor starts after the peers
        await harness.StartAsync(0);

        // Assert: nothing was loaded twice, and the start caught up to the tip
        Assert.Equal(LightningMoney.Satoshis(500_000), memory.GetLockedBalance());
        Assert.Equal(LightningMoney.Satoshis(500_000), memory.GetConfirmedBalance(100));
        Assert.True(memory.TryGetFeeReservation(utxo.TxId, utxo.Index, out _));
        Assert.Equal(100u, harness.Monitor.LastProcessedBlockHeight);
    }

    [Fact]
    public async Task Given_AReservationWhoseInputsAreAllSpent_When_TheWalletIsLoaded_Then_ItEndsInTheLoadsOwnSave()
    {
        // Arrange: a reservation whose only input a processed block spent (its caller crashed before confirming it)
        await using var harness = new ChainMonitorHarness(tipHeight: 100);
        var (spent, _) = await SeedWalletOutputAsync(harness, 0, 80_000);
        var (kept, _) = await SeedWalletOutputAsync(harness, 1, 90_000);
        var endedId = await SeedReservationAsync(harness, spent);
        var keptId = await SeedReservationAsync(harness, kept);
        using (var uow = CreateSeedUnitOfWork(harness, out var previousMemory))
        {
            previousMemory.Load([spent]);
            uow.TrySpendUtxo(spent.TxId, spent.Index);
            await uow.SaveChangesAsync();
        }

        // Act
        await harness.Monitor.LoadWalletAsync(TestContext.Current.CancellationToken);

        // Assert: the ended reservation's rows are gone without a start, the other one is restored
        await using var context = harness.Context();
        var reservations = await context.FeeInputReservations.ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(keptId, Assert.Single(reservations).Id);
        var memory = harness.Services.GetRequiredService<IUtxoMemoryRepository>();
        Assert.False(memory.TryGetUtxo(spent.TxId, spent.Index, out _));
        Assert.True(memory.TryGetFeeReservation(kept.TxId, kept.Index, out var id));
        Assert.Equal(keptId, id);
        Assert.NotEqual(endedId, keptId);
    }

    [Fact]
    public async Task Given_ANewNode_When_TheWalletIsLoaded_Then_NothingIsStoredAndTheStartCreatesTheStateAtItsBirth()
    {
        // Arrange
        await using var harness = new ChainMonitorHarness(tipHeight: 100);

        // Act
        await harness.Monitor.LoadWalletAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0u, harness.Monitor.LastProcessedBlockHeight);
        await using (var context = harness.Context())
            Assert.Empty(await context.BlockchainStates.ToListAsync(TestContext.Current.CancellationToken));

        // Act
        await harness.StartAsync(100);

        // Assert
        Assert.Equal(100u, harness.Monitor.LastProcessedBlockHeight);
    }

    private static UnitOfWork CreateSeedUnitOfWork(ChainMonitorHarness harness, out UtxoMemoryRepository memory)
    {
        // The previous process's memory: the harness's own UTXO set must stay empty until the monitor loads it
        memory = new UtxoMemoryRepository();
        return new UnitOfWork(harness.Db.CreateContext(), NullLogger<UnitOfWork>.Instance, new Sha256(), memory);
    }

    private static async Task<(UtxoModel Utxo, TxOut TxOut)> SeedWalletOutputAsync(ChainMonitorHarness harness,
                                                                                   uint index, long amountSat)
    {
        var address = GetP2WpkhExtKey(index).Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, index, false, address.ToString());
        var utxo = new UtxoModel(new TxId(RandomUtils.GetBytes(32)), 1, LightningMoney.Satoshis(amountSat), 90,
                                 walletAddress);
        using var uow = CreateSeedUnitOfWork(harness, out _);
        uow.WalletAddressesDbRepository.AddRange([walletAddress]);
        uow.AddUtxo(utxo);
        await uow.SaveChangesAsync();
        return (utxo, new TxOut(Money.Satoshis(amountSat), address.ScriptPubKey));
    }

    private static async Task<Guid> SeedReservationAsync(ChainMonitorHarness harness, UtxoModel utxo)
    {
        var script = GetP2WpkhExtKey(utxo.WalletAddress!.Index).Neuter().PubKey.WitHash.ScriptPubKey;
        var reservation = new FeeInputReservation(Guid.NewGuid(), "itx:splice:nl600",
                                                  [
                                                      new WalletInput(utxo.TxId, utxo.Index, utxo.Amount,
                                                                      AddressType.P2Wpkh, script.ToBytes(), 272)
                                                  ], LightningMoney.Satoshis(500), LightningMoney.Zero, null);
        using var uow = CreateSeedUnitOfWork(harness, out _);
        uow.FeeInputReservationDbRepository.Add(reservation, DateTimeOffset.UtcNow);
        await uow.SaveChangesAsync();
        return reservation.Id;
    }

    private static async Task SeedStateAsync(ChainMonitorHarness harness, uint height)
    {
        using var uow = CreateSeedUnitOfWork(harness, out _);
        uow.BlockchainStateDbRepository.Add(new BlockchainState(height, Hash.Empty, DateTime.UtcNow));
        await uow.SaveChangesAsync();
    }

    private static LocalLightningSigner CreateSigner(IUtxoMemoryRepository memory)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()))
                  .Returns((uint index, bool _) => GetP2WpkhExtKey(index).ToBytes());
        return new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
                                        NullLogger<LocalLightningSigner>.Instance,
                                        new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest },
                                        keyManager.Object, memory);
    }

    private static Transaction CreateSpend(UtxoModel utxo)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])utxo.TxId), utxo.Index)) { Sequence = 0xFFFFFFFD });
        tx.Outputs.Add(Money.Satoshis(utxo.Amount.Satoshi - 1_000), new Key().PubKey.WitHash.ScriptPubKey);
        return tx;
    }

    private static SignedTransaction ToSigned(Transaction tx) => new(tx.GetHash().ToBytes(), tx.ToBytes());

    private static ExtKey GetP2WpkhExtKey(uint index) => s_masterKey.Derive(0u).Derive(index);
}