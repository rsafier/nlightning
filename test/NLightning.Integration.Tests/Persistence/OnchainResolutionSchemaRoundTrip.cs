using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Database.Onchain;

/// <summary>
/// Provider-agnostic proof for migration <c>AddOnchainResolution</c> (BOLT 5 plan O1-T3), shared by the SQLite test and
/// the Docker Postgres/SQL Server tests: channels stored before the migration get <c>RevocationLogFromNumber</c> from
/// its data step when their peer already revoked commitments, and the new tables (revocation log, channel closes,
/// output resolutions) round-trip through their repositories, including state 37.
/// </summary>
internal static class OnchainResolutionSchemaRoundTrip
{
    private const string MigrationName = "_AddOnchainResolution";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, DatabaseType databaseType,
                                         CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddOnchainResolution, with channels at several peer commitment numbers
        var channels = new (byte Seed, ChannelState State, ulong RemoteCommitmentNumber)[]
        {
            (0x41, ChannelState.Open, 0), (0x42, ChannelState.Open, 5), (0x43, ChannelState.Failed, 12),
            (0x44, ChannelState.V1FundingSigned, 0)
        };
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);
            foreach (var (seed, state, remoteNumber) in channels)
                await ChainWatchSchemaRoundTrip.SeedChannelAsync(context, databaseType,
                                                                 ChainWatchSchemaRoundTrip.ChannelIdOf(seed),
                                                                 ChainWatchSchemaRoundTrip.TxIdOf(seed), 0, state,
                                                                 cancellationToken, remoteNumber);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        // Assert: the log of a channel with revoked commitments starts at its current peer commitment number
        await using (var context = contextFactory())
        {
            var rows = await context.Channels.AsNoTracking().ToListAsync(cancellationToken);
            var repository = new RevokedCommitmentDbRepository(context);
            foreach (var (seed, _, remoteNumber) in channels)
            {
                var channelId = ChainWatchSchemaRoundTrip.ChannelIdOf(seed);
                var row = rows.Single(r => r.ChannelId == channelId);
                Assert.Equal(remoteNumber > 0 ? remoteNumber : null, row.RevocationLogFromNumber);
                Assert.Equal(remoteNumber, await repository.GetLogStartAsync(channelId));
            }
        }

        // Assert: the new tables round-trip on this provider
        await AssertTablesRoundTripAsync(contextFactory, cancellationToken);
    }

    /// <summary>
    /// Writes and reads back a revocation log produced by the engine, a channel close and output resolutions through
    /// every state, and a channel in state 37, each save read from a fresh context.
    /// </summary>
    public static async Task AssertTablesRoundTripAsync(Func<NLightningDbContext> contextFactory,
                                                        CancellationToken cancellationToken)
    {
        // Revocation log: a channel whose peer revokes a commitment with an HTLC
        var sha256 = new Sha256();
        var channel = SqliteDbTestContext.CreateChannel(true);
        var driver = new CommitmentDanceDriver(channel.ChannelId, CommitmentParams.FromChannel(channel),
                                               channel.LocalBalance.MilliSatoshi, channel.RemoteBalance.MilliSatoshi);
        await using (var context = contextFactory())
        {
            await new ChannelDbRepository(context, sha256).AddAsync(channel);
            await new ChannelStateDbRepository(context).InitializeAsync(driver.Us);
            await context.SaveChangesAsync(cancellationToken);
        }

        CommitmentsResult?[] dance =
        [
            driver.TryUsAdd(5_000_000), driver.TryPeerAdd(), driver.TryUsCommit(), driver.TryDeliverRevokeToUs(),
            driver.TryUsAdd(7_000_000), driver.TryUsCommit()
        ];
        var revoked = driver.Us.RemoteCommit;
        var steps = dance.Append(driver.TryDeliverRevokeToUs()).ToList();
        foreach (var step in steps)
        {
            Assert.NotNull(step);
            await using var context = contextFactory();
            await new ChannelStateDbRepository(context).ApplyAsync(step.Next, step.Transition);
            await context.SaveChangesAsync(cancellationToken);
        }

        Assert.NotEmpty(revoked.Spec.Htlcs);
        await using (var context = contextFactory())
        {
            var repository = new RevokedCommitmentDbRepository(context);
            var entry = Assert.Single(await repository.GetByChannelIdAsync(channel.ChannelId));
            Assert.Equal(revoked.Number, entry.Number);
            Assert.Equal(revoked.Spec, entry.Spec);
            Assert.Equal(revoked.Spec, (await repository.GetAsync(channel.ChannelId, revoked.Number))?.Spec);
            Assert.Null(await repository.GetAsync(channel.ChannelId, revoked.Number + 1));
            Assert.Equal(0UL, await repository.GetLogStartAsync(channel.ChannelId));
        }

        // Channel close: added, replaced (a reorg can change the spender), deleted and added again
        var channelId = channel.ChannelId;
        var blockHash = new Hash(Enumerable.Repeat((byte)0x51, 32).ToArray());
        var createdAt = new DateTimeOffset(2026, 9, 26, 5, 6, 7, TimeSpan.FromHours(2)).AddTicks(1_234);
        var close = new ChannelCloseModel(channelId, ChannelCloseKind.RevokedCommitment,
                                          ChainWatchSchemaRoundTrip.TxIdOf(0x52), revoked.Number, 901, blockHash,
                                          createdAt);
        await SaveAsync(contextFactory, c => new OnchainResolutionDbRepository(c).UpsertCloseAsync(close),
                        cancellationToken);
        await AssertCloseAsync(contextFactory, close);

        var replaced = close with
        {
            Kind = ChannelCloseKind.Unknown,
            CommitmentNumber = null,
            SpentAtHeight = 902,
            CommitmentTransactionId = ChainWatchSchemaRoundTrip.TxIdOf(0x53)
        };
        await SaveAsync(contextFactory, c => new OnchainResolutionDbRepository(c).UpsertCloseAsync(replaced),
                        cancellationToken);
        await AssertCloseAsync(contextFactory, replaced);

        await SaveAsync(contextFactory, async c =>
        {
            var repository = new OnchainResolutionDbRepository(c);
            await repository.DeleteCloseAsync(channelId);
            Assert.Null(await repository.GetCloseAsync(channelId));
            await repository.UpsertCloseAsync(close);
        }, cancellationToken);
        await AssertCloseAsync(contextFactory, close);

        // Output resolutions: every descriptor field, then through the states to irrevocable
        var output = new OutputResolutionModel
        {
            TransactionId = close.CommitmentTransactionId,
            OutputIndex = 2,
            ChannelId = channelId,
            Descriptor = OutputDescriptorKind.RevokedHtlc,
            DescriptorData = [0x01, 0x02, 0x03],
            HtlcDirection = HtlcDirection.Outgoing,
            HtlcId = 7,
            State = OutputResolutionState.Pending,
            DeadlineHeight = 1_045,
            CreatedAt = createdAt
        };
        var toLocal = output with
        {
            OutputIndex = 0,
            Descriptor = OutputDescriptorKind.RevokedToLocal,
            DescriptorData = [],
            HtlcDirection = null,
            HtlcId = null,
            State = OutputResolutionState.Ignored,
            DeadlineHeight = null
        };
        await SaveAsync(contextFactory, async c =>
        {
            var repository = new OnchainResolutionDbRepository(c);
            await repository.UpsertOutputAsync(output);
            await repository.UpsertOutputAsync(toLocal);
        }, cancellationToken);
        await AssertOutputsAsync(contextFactory, channelId, [toLocal, output], [output]);

        var states = new[]
        {
            output with { State = OutputResolutionState.Waiting, WaitUntilHeight = 1_000 },
            output with
            {
                State = OutputResolutionState.Broadcast, ResolvingTransactionId = ChainWatchSchemaRoundTrip.TxIdOf(0x54)
            },
            output with
            {
                State = OutputResolutionState.Resolved, ResolvingTransactionId = ChainWatchSchemaRoundTrip.TxIdOf(0x54),
                ResolvedHeight = 1_010
            },
            output with
            {
                State = OutputResolutionState.Irrevocable,
                ResolvingTransactionId = ChainWatchSchemaRoundTrip.TxIdOf(0x54), ResolvedHeight = 1_010
            }
        };
        foreach (var state in states)
        {
            await SaveAsync(contextFactory, c => new OnchainResolutionDbRepository(c).UpsertOutputAsync(state),
                            cancellationToken);
            await AssertOutputsAsync(contextFactory, channelId, [toLocal, state],
                                     state.State == OutputResolutionState.Irrevocable ? [] : [state]);
        }

        // State 37 round-trips on the channel row
        await using (var context = contextFactory())
        {
            var repository = new ChannelDbRepository(context, sha256);
            var stored = await repository.GetByIdAsync(channelId);
            Assert.NotNull(stored);
            stored.UpdateState(ChannelState.OnchainResolving);
            await repository.UpdateAsync(stored);
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var stored = await new ChannelDbRepository(context, sha256).GetByIdAsync(channelId);
            Assert.Equal(ChannelState.OnchainResolving, stored?.State);
        }

        // Deleting the channel row cascades to its log, close and outputs
        await using (var context = contextFactory())
        {
            await context.Channels.Where(c => c.ChannelId == channelId).ExecuteDeleteAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            Assert.False(await context.RevokedCommitments.AnyAsync(r => r.ChannelId == channelId, cancellationToken));
            Assert.False(await context.ChannelCloses.AnyAsync(r => r.ChannelId == channelId, cancellationToken));
            Assert.False(await context.OutputResolutions.AnyAsync(r => r.ChannelId == channelId, cancellationToken));
        }
    }

    private static async Task AssertCloseAsync(Func<NLightningDbContext> contextFactory, ChannelCloseModel expected)
    {
        await using var context = contextFactory();
        var repository = new OnchainResolutionDbRepository(context);
        AssertClose(expected, await repository.GetCloseAsync(expected.ChannelId));
        AssertClose(expected, Assert.Single(await repository.GetClosesAsync(), c => c.ChannelId == expected.ChannelId));
    }

    private static void AssertClose(ChannelCloseModel expected, ChannelCloseModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.CommitmentTransactionId, actual.CommitmentTransactionId);
        Assert.Equal(expected.CommitmentNumber, actual.CommitmentNumber);
        Assert.Equal(expected.SpentAtHeight, actual.SpentAtHeight);
        Assert.Equal(expected.BlockHash, actual.BlockHash);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
    }

    private static async Task AssertOutputsAsync(Func<NLightningDbContext> contextFactory, ChannelId channelId,
                                                 OutputResolutionModel[] expected,
                                                 OutputResolutionModel[] expectedUnresolved)
    {
        await using var context = contextFactory();
        var repository = new OnchainResolutionDbRepository(context);
        var outputs = await repository.GetOutputsByChannelIdAsync(channelId);
        Assert.Equal(expected.Length, outputs.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            AssertOutput(expected[i], outputs[i]);
            AssertOutput(expected[i],
                         await repository.GetOutputAsync(expected[i].TransactionId, expected[i].OutputIndex));
        }

        var unresolved = (await repository.GetUnresolvedOutputsAsync()).Where(o => o.ChannelId == channelId).ToList();
        Assert.Equal(expectedUnresolved.Length, unresolved.Count);
        for (var i = 0; i < expectedUnresolved.Length; i++)
            AssertOutput(expectedUnresolved[i], unresolved[i]);
    }

    private static void AssertOutput(OutputResolutionModel expected, OutputResolutionModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.TransactionId, actual.TransactionId);
        Assert.Equal(expected.OutputIndex, actual.OutputIndex);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.Descriptor, actual.Descriptor);
        Assert.Equal(expected.DescriptorData, actual.DescriptorData);
        Assert.Equal(expected.HtlcDirection, actual.HtlcDirection);
        Assert.Equal(expected.HtlcId, actual.HtlcId);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.ResolvingTransactionId, actual.ResolvingTransactionId);
        Assert.Equal(expected.WaitUntilHeight, actual.WaitUntilHeight);
        Assert.Equal(expected.DeadlineHeight, actual.DeadlineHeight);
        Assert.Equal(expected.ResolvedHeight, actual.ResolvedHeight);
        Assert.Equal(expected.CreatedAt.UtcTicks, actual.CreatedAt.UtcTicks);
    }

    private static async Task SaveAsync(Func<NLightningDbContext> contextFactory,
                                        Func<NLightningDbContext, Task> stage, CancellationToken cancellationToken)
    {
        await using var context = contextFactory();
        await stage(context);
        await context.SaveChangesAsync(cancellationToken);
    }
}