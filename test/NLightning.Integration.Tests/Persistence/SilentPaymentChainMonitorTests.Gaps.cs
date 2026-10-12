using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Accounting.Enums;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed partial class SilentPaymentChainMonitorTests
{
    [Fact]
    public async Task Given_ReceiveWasDisabledAcrossWalletBlocks_When_Reenabled_Then_OnlySilentPaymentRecoveryIsQueuedAndTheGlobalCursorDoesNotRewind()
    {
        // Arrange: an ordinary wallet receipt must retain its committed accounting while SP discovery is off.
        using var keys = new ReceiverKeys();
        var options = EnabledOptions();
        await using var harness = CreateHarness(keys.Manager, options);
        using var ordinaryKey = new Key(Enumerable.Repeat((byte)0x42, 32).ToArray());
        var wallet = new WalletAddressModel(AddressType.P2Wpkh, 7, false,
            ordinaryKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString());
        await using (var context = harness.Context())
        {
            new WalletAddressesDbRepository(context).AddRange([wallet]);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await harness.StartAsync(95);
        options.Receive = false;
        await harness.RestartAsync();
        var receipt = Receipt(keys.Manager, 40, [AmountSat]);
        var ordinary = Network.RegTest.CreateTransaction();
        ordinary.Inputs.Add(new TxIn(new OutPoint(new uint256(Enumerable.Repeat((byte)0x43, 32).ToArray()), 0)));
        ordinary.Outputs.Add(new TxOut(Money.Satoshis(5_000), ordinaryKey.PubKey.WitHash.ScriptPubKey));
        await harness.MineAndDeliverAsync(receipt, ordinary);
        await harness.MineAndDeliverAsync();
        Assert.Equal(102u, harness.Monitor.LastProcessedBlockHeight);
        var committed = Assert.Single(await EventsAsync(harness));
        Assert.Equal(AccountingEventKind.WalletReceived, committed.Kind);
        Assert.Equal(Id(ordinary), committed.TxId);

        // Act: re-enabling arms an SP-only rescan behind the unchanged ordinary wallet cursor.
        options.Receive = true;
        await harness.RestartAsync();

        // Assert: recovery starts after the last SP block; channel/ordinary wallet facts are never replayed.
        Assert.False(harness.Monitor.IsChainProcessingHalted);
        Assert.Equal(102u, harness.Monitor.LastProcessedBlockHeight);
        await using var read = harness.Context();
        var repository = new SilentPaymentDbRepository(read);
        var state = await repository.GetScanStateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(100u, state!.BirthdayHeight);
        Assert.Equal(102u, state.LiveFromHeight);
        Assert.Equal(100u, state.RescanCursorHeight);
        Assert.Equal(new Hash(harness.Chain[100].GetHash().ToBytes()), state.RescanCursorHash);
        Assert.Equal(101u, state.RescanTargetHeight);
        Assert.Empty(await repository.GetOutputsAsync(TestContext.Current.CancellationToken));
        var after = Assert.Single(await EventsAsync(harness));
        Assert.Equal(committed.EventKey, after.EventKey);
        Assert.Equal(5_000_000, await WalletBalanceAsync(harness));
        Assert.False(Memory(harness).TryGetUtxo(Id(receipt), 0, out _));
        Assert.True(Memory(harness).TryGetUtxo(Id(ordinary), 0, out _));
    }
}