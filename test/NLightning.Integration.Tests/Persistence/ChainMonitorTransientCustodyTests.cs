using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Infrastructure.Repositories.Database.Bitcoin;

public sealed class ChainMonitorTransientCustodyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_TransientParentAndChild_When_ConfirmedTogether_Then_OnlyUnspentChildIsPersistedAndHistoryRetainsBoth(bool failFirstCommit)
    {
        // Arrange: mempool custody is deliberately absent from SQLite before the confirming block.
        var ct = TestContext.Current.CancellationToken;
        await using var harness = new ChainMonitorHarness();
        using var key = new Key();
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var wallet = new WalletAddressModel(AddressType.P2Wpkh, 0, false, address.ToString());
        await using (var setup = harness.Context())
        {
            new WalletAddressesDbRepository(setup).AddRange([wallet]);
            await setup.SaveChangesAsync(ct);
        }
        await harness.StartAsync(95);
        var parent = Network.RegTest.CreateTransaction();
        parent.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat((byte)0x71, 32).ToArray()), 0));
        parent.Outputs.Add(Money.Satoshis(100_000), address.ScriptPubKey);
        var child = Network.RegTest.CreateTransaction();
        child.Inputs.Add(new OutPoint(parent.GetHash(), 0));
        child.Outputs.Add(Money.Satoshis(90_000), address.ScriptPubKey);
        var parentId = new TxId(parent.GetHash().ToBytes());
        var childId = new TxId(child.GetHash().ToBytes());
        var memory = harness.Services.GetRequiredService<IUtxoMemoryRepository>();
        memory.AddUnconfirmed(new UtxoModel(parentId, 0, LightningMoney.Satoshis(100_000), 0, wallet));
        memory.AddUnconfirmed(new UtxoModel(childId, 0, LightningMoney.Satoshis(90_000), 0, wallet));
        await using (var before = harness.Context()) Assert.Empty(await before.Utxos.ToListAsync(ct));

        // Act: promotion and same-block spend must not DELETE a transient row that never existed.
        harness.FailCommits = failFirstCommit;
        await harness.MineAndDeliverAsync(parent, child);
        if (failFirstCommit)
        {
            // A failed atomic commit must leave transient custody and the durable chain/history unchanged.
            Assert.True(harness.FailedCommits > 0);
            Assert.Equal(100u, harness.Monitor.LastProcessedBlockHeight);
            Assert.True(memory.TryGetUtxo(parentId, 0, out var stillTransient));
            Assert.Equal(0u, stillTransient.BlockHeight);
            Assert.Equal(2, memory.GetUnconfirmedUtxos().Count);
            await using (var failed = harness.Context())
            {
                Assert.Empty(await failed.Utxos.ToListAsync(ct));
                Assert.Null(await new WalletTransactionDbRepository(failed).GetByIdAsync(parentId, ct));
                Assert.Null(await new WalletTransactionDbRepository(failed).GetByIdAsync(childId, ct));
            }
            harness.FailCommits = false;
            await harness.DeliverTipAsync();
        }

        // Assert: the save advances the chain and replaces transient custody with the final confirmed child.
        Assert.Equal(harness.Chain.TipHeight, harness.Monitor.LastProcessedBlockHeight);
        Assert.False(memory.TryGetUtxo(parentId, 0, out _));
        Assert.True(memory.TryGetUtxo(childId, 0, out var confirmed));
        Assert.Equal(harness.Chain.TipHeight, confirmed.BlockHeight);
        Assert.Empty(memory.GetUnconfirmedUtxos());
        await using var read = harness.Context();
        var stored = Assert.Single(await new UtxoDbRepository(read).GetUnspentAsync());
        Assert.Equal(childId, stored.TxId);
        Assert.Equal(90_000, stored.Amount.Satoshi);
        Assert.Equal(harness.Chain.TipHeight, stored.BlockHeight);
        var history = new WalletTransactionDbRepository(read);
        var parentHistory = await history.GetByIdAsync(parentId, ct);
        var childHistory = await history.GetByIdAsync(childId, ct);
        Assert.NotNull(parentHistory);
        Assert.NotNull(childHistory);
        Assert.Equal(parent.ToBytes(), parentHistory.RawTransaction);
        Assert.Equal(child.ToBytes(), childHistory.RawTransaction);
        Assert.Equal(100_000, Assert.Single(childHistory.OurInputs).AmountSat);
        Assert.Equal(harness.Chain.TipHeight, childHistory.BlockHeight);
    }
}