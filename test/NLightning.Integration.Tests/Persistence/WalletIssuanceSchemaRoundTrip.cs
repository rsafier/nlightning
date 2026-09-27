using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Bitcoin;
using Infrastructure.Repositories.Database.Onchain;

/// <summary>
/// Provider-agnostic proof of the wallet's address issuance (NL-280, over the <c>WalletAddresses.IsReserved</c> column
/// of migration <c>AddShutdownHtlcBoundaryAndAddressReservation</c>) and of the abandoned-broadcast listing (NL-294),
/// shared by the SQLite test and the Docker Postgres/SQL Server tests: a reserved address and an address that ever
/// held a UTXO are never returned by the unused-address lookup, nor is any address below them, and only abandoned
/// broadcasts are listed, oldest first.
/// </summary>
internal static class WalletIssuanceSchemaRoundTrip
{
    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the whole schema, six receive addresses and two change addresses
        await using (var context = contextFactory())
        {
            await context.Database.MigrateAsync(cancellationToken);
            var addresses = new WalletAddressesDbRepository(context);
            addresses.AddRange(Enumerable.Range(0, 6).Select(i => Address((uint)i, false)).ToList());
            addresses.AddRange([Address(0, true), Address(1, true)]);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act / Assert: nothing used yet, the lowest index of each chain
        Assert.Equal(0u, (await GetUnusedAsync(contextFactory, false))!.Index);
        Assert.Equal(0u, (await GetUnusedAsync(contextFactory, true))!.Index);

        // Act: legacy rows (handed out before reservations): index 0 was spent (no row left), index 2 holds a UTXO
        await using (var context = contextFactory())
        {
            new UtxoDbRepository(context).Add(new UtxoModel(new TxId(Enumerable.Repeat((byte)0x71, 32).ToArray()), 0,
                                                            LightningMoney.Satoshis(10_000), 100, 2, false,
                                                            AddressType.P2Wpkh));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: never below index 2
        Assert.Equal(3u, (await GetUnusedAsync(contextFactory, false))!.Index);

        // Act: index 3 reserved (handed out)
        await using (var context = contextFactory())
        {
            await new WalletAddressesDbRepository(context).ReserveAsync(Address(3, false));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: the next one, the other chain untouched; with the last one reserved nothing is left
        Assert.Equal(4u, (await GetUnusedAsync(contextFactory, false))!.Index);
        Assert.Equal(0u, (await GetUnusedAsync(contextFactory, true))!.Index);
        await using (var context = contextFactory())
        {
            await new WalletAddressesDbRepository(context).ReserveAsync(Address(5, false));
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.Null(await GetUnusedAsync(contextFactory, false));

        // Arrange (NL-294): a pending, an abandoned funding and an abandoned wallet send
        var funding = Transaction(0x72);
        var withdraw = Transaction(0x73);
        var pending = Transaction(0x74);
        await using (var context = contextFactory())
        {
            var broadcasts = new BroadcastTransactionDbRepository(context);
            broadcasts.Add(new BroadcastTransactionModel(funding, BroadcastPurpose.Funding,
                                                         new ChannelId(Enumerable.Repeat((byte)0x75, 32).ToArray()),
                                                         100));
            await context.SaveChangesAsync(cancellationToken);
            broadcasts.Add(new BroadcastTransactionModel(withdraw, BroadcastPurpose.WalletSend, null, 101));
            broadcasts.Add(new BroadcastTransactionModel(pending, BroadcastPurpose.Sweep, null, 102));
            await context.SaveChangesAsync(cancellationToken);
            Assert.True(await broadcasts.MarkAbandonedAsync(withdraw.TxId));
            Assert.True(await broadcasts.MarkAbandonedAsync(funding.TxId));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Act
        IReadOnlyList<BroadcastTransactionModel> abandoned;
        await using (var context = contextFactory())
            abandoned = await new BroadcastTransactionDbRepository(context).GetAbandonedAsync();

        // Assert
        Assert.Equal([funding.TxId, withdraw.TxId], abandoned.Select(b => b.TransactionId));
        Assert.All(abandoned, b => Assert.Equal(BroadcastState.Abandoned, b.State));
    }

    private static async Task<WalletAddressModel?> GetUnusedAsync(Func<NLightningDbContext> contextFactory,
                                                                  bool isChange)
    {
        await using var context = contextFactory();
        return await new WalletAddressesDbRepository(context).GetUnusedAddressAsync(AddressType.P2Wpkh, isChange);
    }

    private static WalletAddressModel Address(uint index, bool isChange) =>
        new(AddressType.P2Wpkh, index, isChange, $"bcrt1qissuance{(isChange ? "c" : "r")}{index}");

    private static SignedTransaction Transaction(byte seed)
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat(seed, 32).ToArray()), 0));
        transaction.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
        return new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes());
    }
}