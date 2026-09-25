using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// Provider-agnostic proof for migration <c>AddCommitmentState</c> (BOLT2 plan N5-T1, NL-237), shared by the SQLite test
/// and the Docker Postgres/SQL Server tests: rows written with the schema before the migration are migrated forward
/// by its data steps, then the commitment state of a new channel round-trips through
/// <see cref="ChannelStateDbRepository"/>.
/// </summary>
internal static class CommitmentStateMigrationRoundTrip
{
    private const ulong FirstIndex = 281474976710655;

    private static readonly byte[] s_readyChannelId = Enumerable.Repeat((byte)0x07, 32).ToArray();
    private static readonly byte[] s_openingChannelId = Enumerable.Repeat((byte)0x08, 32).ToArray();

    /// <summary>The 1366-byte onion inside the seeded update_add_htlc.</summary>
    private static readonly byte[] s_onion = Enumerable.Range(0, 1366).Select(i => (byte)(i * 7)).ToArray();

    private const string SeededAddress = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    private static readonly byte[] s_utxoTxId = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    private static readonly byte[] s_spendingTxId = Enumerable.Repeat((byte)0x5B, 32).ToArray();

    /// <summary>The peer's second per-commitment point, stored by channel_ready on the remote key set.</summary>
    private static readonly byte[] s_remoteSecondPoint = [0x03, .. Enumerable.Repeat((byte)0x2B, 32)];

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddCommitmentState, with rows in it
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith("_AddCommitmentState", StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            await SeedAsync(context, databaseType, cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: data steps
        await using (var context = contextFactory())
        {
            var htlcs = (await context.Htlcs.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(h => h.HtlcId).ToList();
            Assert.Equal(2, htlcs.Count);
            Assert.Equal(s_onion, htlcs[0].OnionRoutingPacket);
            Assert.Empty(htlcs[1].OnionRoutingPacket);
            Assert.Equal(new byte[] { 0, 1 }, htlcs.Select(h => h.State));
            Assert.All(htlcs, h =>
            {
                Assert.Null(h.Sha256OfOnion);
                Assert.Null(h.FailReason);
                Assert.Null(h.RemovalKind);
                Assert.Null(h.OnionSharedSecret);
            });
            Assert.Equal(new byte[32].Select((_, i) => (byte)(i + 1)), htlcs[0].PaymentPreimage);

            var readyId = new ChannelId(s_readyChannelId);
            var openingId = new ChannelId(s_openingChannelId);
            var ready = await context.Channels.AsNoTracking()
                                     .SingleAsync(c => c.ChannelId == readyId, cancellationToken);
            Assert.Equal(s_remoteSecondPoint, (byte[]?)ready.RemoteNextPerCommitmentPoint);
            Assert.Null(ready.SentCommitDiff);
            Assert.Equal(0, ready.LastSentOrder);
            Assert.Null(ready.ErrorSent);
            Assert.False(ready.DataLossDetected);
            Assert.Equal(600_000_123, ready.LocalBalanceMsat);

            var opening = await context.Channels.AsNoTracking()
                                       .SingleAsync(c => c.ChannelId == openingId, cancellationToken);
            Assert.Null(opening.RemoteNextPerCommitmentPoint);
            Assert.Empty(await context.Commitments.ToListAsync(cancellationToken));
            Assert.Empty(await context.FeeUpdates.ToListAsync(cancellationToken));

            // Rows the migration does not touch survive it
            var keySets = await context.ChannelKeySets.AsNoTracking().ToListAsync(cancellationToken);
            Assert.Equal(4, keySets.Count);
            var remoteReady = keySets.Single(k => k.ChannelId == readyId && !k.IsLocal);
            Assert.Equal(FirstIndex - 1, remoteReady.CurrentPerCommitmentIndex);
            Assert.Equal(s_remoteSecondPoint, remoteReady.CurrentPerCommitmentPoint);

            var address = await context.WalletAddresses.AsNoTracking().SingleAsync(cancellationToken);
            Assert.Equal(SeededAddress, address.Address);
            Assert.Equal(AddressType.P2Wpkh, address.AddressType);
            var utxos = (await context.Utxos.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(u => u.Index)
                                                                                         .ToList();
            Assert.Equal(2, utxos.Count);
            Assert.All(utxos, u =>
            {
                Assert.Equal(new TxId(s_utxoTxId), u.TransactionId);
                Assert.Equal(AddressType.P2Wpkh, u.AddressType);
                Assert.Equal(7u, u.AddressIndex);
                Assert.False(u.IsAddressChange);
            });
            Assert.Equal(1_500_000, utxos[0].AmountSats);
            Assert.Equal(90u, utxos[0].BlockHeight);
            Assert.Equal(readyId, utxos[0].LockedToChannelId);
            Assert.Null(utxos[0].UsedInTransactionId);
            Assert.Equal(2_500, utxos[1].AmountSats);
            Assert.Null(utxos[1].LockedToChannelId);
            Assert.Equal(new TxId(s_spendingTxId), utxos[1].UsedInTransactionId);

            // NL-025: the legacy HTLC rows keep the old state and the channel is refused
            var anyParams = new CommitmentParams(true, 1_000_000, false, new CommitmentParty(546, 0, 1, 30, 0),
                                                 new CommitmentParty(546, 0, 1, 30, 0));
            var refused = await Assert.ThrowsAsync<LegacyHtlcStateException>(() =>
                new ChannelStateDbRepository(context).LoadAsync(readyId, anyParams));
            Assert.Contains("NL-025", refused.Message);
        }

        // Assert: the commitment state of a new channel round-trips on this provider
        await AssertStateRoundTripAsync(contextFactory, cancellationToken);
    }

    private static async Task AssertStateRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                        CancellationToken cancellationToken)
    {
        var sha256 = new Sha256();
        var channel = SqliteDbTestContext.CreateChannel(true);
        var @params = channel.ToCommitmentParams();
        var driver = new CommitmentDanceDriver(channel.ChannelId, @params, channel.LocalBalance.MilliSatoshi,
                                               channel.RemoteBalance.MilliSatoshi, seed: 11);
        await using (var context = contextFactory())
        {
            await new ChannelDbRepository(context, sha256).AddAsync(channel);
            await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
            await context.SaveChangesAsync(cancellationToken);
        }

        var sawUnackedCommit = false;
        for (var i = 0; i < 30; i++)
        {
            var result = driver.NextTransition();
            var sent = result.Outbound.OfType<OutboundCommitmentSigned>().FirstOrDefault();
            var extras = sent is null
                             ? null
                             : new ChannelStateExtras
                             {
                                 SentCommitDiff = CommitmentDanceDriver.DiffFor(sent.RemoteCommitmentNumber)
                             };
            await using (var context = contextFactory())
            {
                await new ChannelStateDbRepository(context).ApplyAsync(result.Next, result.Transition, extras);
                await context.SaveChangesAsync(cancellationToken);
            }

            await using (var context = contextFactory())
            {
                var state = await new ChannelStateDbRepository(context).LoadAsync(channel.ChannelId, @params);
                Assert.NotNull(state);
                CommitmentsAssert.Equal(driver.Us, state.Commitments);
                if (driver.Us.RemoteNextCommit is { } pending)
                {
                    sawUnackedCommit = true;
                    Assert.Equal(CommitmentDanceDriver.DiffFor(pending.Commit.Number), state.SentCommitDiff?.ToArray());
                }
            }
        }

        Assert.True(sawUnackedCommit);
        await using (var context = contextFactory())
        {
            var reloaded = await new ChannelDbRepository(context, sha256).GetByIdAsync(channel.ChannelId);
            Assert.NotNull(reloaded?.Commitments);
            CommitmentsAssert.Equal(driver.Us, reloaded.Commitments);
        }
    }

    private static async Task SeedAsync(NLightningDbContext context, DatabaseType databaseType,
                                        CancellationToken cancellationToken)
    {
        var sql = new MigrationSqlDialect(databaseType);
        var remoteNodeId = new byte[33];
        remoteNodeId[0] = 0x02;
        remoteNodeId[32] = 0x01;
        var point = new byte[33];
        point[0] = 0x02;

        foreach (var (channelId, remoteIndex) in new[]
                 {
                     (s_readyChannelId, FirstIndex - 1), (s_openingChannelId, FirstIndex)
                 })
        {
            await context.Database.ExecuteSqlRawAsync(
                sql.Insert("Channels",
                           ("ChannelId", "{0}"), ("FundingCreatedAtBlockHeight", "100"), ("FundingTxId", "{1}"),
                           ("FundingOutputIndex", "0"), ("FundingAmountSatoshis", "1000000"),
                           ("IsInitiator", sql.Bool(true)), ("RemoteNodeId", "{2}"), ("LocalNextHtlcId", "2"),
                           ("RemoteNextHtlcId", "0"), ("LocalRevocationNumber", "0"),
                           ("RemoteRevocationNumber", "0"), ("LocalCommitmentNumber", "0"),
                           ("RemoteCommitmentNumber", "0"), ("State", "40"), ("Version", "1"),
                           ("LocalBalanceMsat", "600000123"), ("RemoteBalanceMsat", "399999877")),
                [channelId, new byte[32], remoteNodeId], cancellationToken);

            foreach (var isLocal in new[] { true, false })
            {
                var currentPoint = !isLocal && remoteIndex < FirstIndex ? s_remoteSecondPoint : point;
                await context.Database.ExecuteSqlRawAsync(
                    sql.Insert("ChannelKeySets",
                               ("ChannelId", "{0}"), ("IsLocal", sql.Bool(isLocal)), ("FundingPubKey", "{1}"),
                               ("RevocationBasepoint", "{1}"), ("PaymentBasepoint", "{1}"),
                               ("DelayedPaymentBasepoint", "{1}"), ("HtlcBasepoint", "{1}"),
                               ("CurrentPerCommitmentIndex", isLocal ? $"{FirstIndex}" : $"{remoteIndex}"),
                               ("CurrentPerCommitmentPoint", "{2}"), ("LastRevealedPerCommitmentSecret", "{3}"),
                               ("KeyIndex", "0")),
                    [channelId, point, currentPoint, new byte[32]], cancellationToken);
            }
        }

        // Wallet rows: one UTXO locked to the ready channel's funding, one already spent
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("WalletAddresses",
                       ("Index", "7"), ("IsChange", sql.Bool(false)), ("AddressType", $"{(byte)AddressType.P2Wpkh}"),
                       ("Address", "{0}")),
            [SeededAddress], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Utxos",
                       ("TransactionId", "{0}"), ("Index", "0"), ("AmountSats", "1500000"), ("BlockHeight", "90"),
                       ("AddressIndex", "7"), ("IsAddressChange", sql.Bool(false)),
                       ("AddressType", $"{(byte)AddressType.P2Wpkh}"), ("LockedToChannelId", "{1}")),
            [s_utxoTxId, s_readyChannelId], cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Utxos",
                       ("TransactionId", "{0}"), ("Index", "1"), ("AmountSats", "2500"), ("BlockHeight", "91"),
                       ("AddressIndex", "7"), ("IsAddressChange", sql.Bool(false)),
                       ("AddressType", $"{(byte)AddressType.P2Wpkh}"), ("UsedInTransactionId", "{1}")),
            [s_utxoTxId, s_spendingTxId], cancellationToken);

        // A serialized update_add_htlc as the old HtlcDbRepository stored it: type (2) + channel_id (32) + id (8) +
        // amount_msat (8) + payment_hash (32) + cltv_expiry (4) + onion (1366) + an extension TLV
        byte[] addMessage = [0x00, 0x80, .. s_readyChannelId, .. new byte[8], .. new byte[8], .. new byte[32],
                             .. new byte[4], .. s_onion, 0x01, 0x00];
        var preimage = new byte[32].Select((_, i) => (byte)(i + 1)).ToArray();
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Htlcs",
                       ("ChannelId", "{0}"), ("HtlcId", "0"), ("Direction", "1"), ("AmountMsat", "10000"),
                       ("PaymentHash", "{1}"), ("PaymentPreimage", "{2}"), ("CltvExpiry", "500"), ("State", "0"),
                       ("ObscuredCommitmentNumber", "42"), ("AddMessageBytes", "{3}"), ("Signature", "{4}")),
            [s_readyChannelId, new byte[32], preimage, addMessage, Enumerable.Repeat((byte)0x30, 64).ToArray()],
            cancellationToken);

        // A row from before the onion was mandatory (NL-025): too short to hold one
        await context.Database.ExecuteSqlRawAsync(
            sql.Insert("Htlcs",
                       ("ChannelId", "{0}"), ("HtlcId", "1"), ("Direction", "0"), ("AmountMsat", "20000"),
                       ("PaymentHash", "{1}"), ("CltvExpiry", "501"), ("State", "1"),
                       ("ObscuredCommitmentNumber", "43"), ("AddMessageBytes", "{2}")),
            [s_readyChannelId, new byte[32], new byte[] { 0x00, 0x80, 0x01 }], cancellationToken);
    }
}