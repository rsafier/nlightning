using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// NL-810: a channel load made of several queries reads one snapshot of the database. A splice lock (the funding
/// columns, the capacity and the commitment rows move in one save) commits between the load's first query and the rest
/// of it; the load must return the channel entirely as before the lock (or entirely as after it), never the channel row
/// of one and the commitments of the other ("Balances add up to 1250000000 msat, not 1000000000"). Shared by the
/// SQLite (file database, one connection per context) and server-database tests.
/// </summary>
internal static class ChannelConsistentReadRoundTrip
{
    private const ulong CapacityBefore = 1_000_000;
    private const ulong CapacityAfter = 1_250_000;

    /// <summary>The first query of each load after the channel row: the write commits right before it runs.</summary>
    private const string TriggerTable = "channelfundings";

    /// <summary>How long the reader waits for the write before it goes on (the write cannot finish under a SQLite read
    /// transaction, it waits for the read).</summary>
    private static readonly TimeSpan s_writeWait = TimeSpan.FromSeconds(1);

    /// <param name="contextFactory">A new context on its own connection to the migrated database, with the given
    /// interceptors.</param>
    public static async Task AssertAsync(Func<IInterceptor[], NLightningDbContext> contextFactory,
                                         CancellationToken cancellationToken)
    {
        await AssertListedChannelIsConsistentAsync(contextFactory, cancellationToken);
        await AssertSigningInfoIsConsistentAsync(contextFactory, cancellationToken);
        await AssertStaleParamsFollowTheStoredFundingAsync(contextFactory, cancellationToken);
    }

    /// <summary><c>listchannels</c>'s load (<c>ChannelDbRepository.GetAllAsync</c>), as in the Eclair splice run.</summary>
    public static async Task AssertListedChannelIsConsistentAsync(
        Func<IInterceptor[], NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange
        await MigrateAsync(contextFactory, cancellationToken);
        var fixture = await SplicedChannel.CreateAsync(contextFactory, 0x71);
        var trigger = new WriteBeforeQueryInterceptor(TriggerTable, fixture.LockAsync);

        // Act
        ChannelModel loaded;
        await using (var context = contextFactory([trigger]))
        {
            using var sha256 = new Sha256();
            loaded = Assert.Single(await new ChannelDbRepository(context, sha256).GetAllAsync());
        }

        await trigger.WaitForWriteAsync();

        // Assert: the load is all before or all after the lock
        var capacity = (ulong)loaded.FundingOutput!.Amount.Satoshi;
        Assert.Contains(capacity, new[] { CapacityBefore, CapacityAfter });
        AssertSnapshotOn(loaded, capacity);

        // The lock is stored: a later load is all after it
        await using (var context = contextFactory([]))
        {
            using var sha256 = new Sha256();
            var reloaded = await new ChannelDbRepository(context, sha256).GetByIdAsync(fixture.ChannelId);
            Assert.Equal(CapacityAfter, (ulong)reloaded!.FundingOutput!.Amount.Satoshi);
            Assert.Equal(fixture.Funding.FundingTxId, reloaded.FundingOutput.TransactionId);
            AssertSnapshotOn(reloaded, CapacityAfter);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>The signer's view (<c>IChannelSigningInfoSource</c>): the channel row and its funding rows.</summary>
    public static async Task AssertSigningInfoIsConsistentAsync(
        Func<IInterceptor[], NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange
        await MigrateAsync(contextFactory, cancellationToken);
        var fixture = await SplicedChannel.CreateAsync(contextFactory, 0x72);
        var trigger = new WriteBeforeQueryInterceptor(TriggerTable, fixture.LockAsync);

        // Act
        ChannelSigningInfo info;
        await using (var context = contextFactory([trigger]))
            info = (await new ChannelSigningInfoDbRepository(context).GetAsync(fixture.ChannelId))!.Value;

        await trigger.WaitForWriteAsync();

        // Assert: before the lock the splice is the pending other funding; after it the initial funding is the
        // replaced one. A torn read had the old funding current and the locked splice among the others.
        if (info.FundingTxId == fixture.Funding.FundingTxId)
        {
            Assert.Equal(CapacityAfter * 1_000, info.FundingSatoshis);
            Assert.All(info.Fundings!, f => Assert.Equal(ChannelFundingStatus.Replaced, f.Status));
        }
        else
        {
            Assert.Equal(CapacityBefore * 1_000, info.FundingSatoshis);
            Assert.All(info.Fundings!, f => Assert.Equal(ChannelFundingStatus.Pending, f.Status));
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The switch's and the resolvers' lookups of archived HTLCs load the state with the parameters of a model in
    /// memory, which a lock saved since leaves behind: the stored funding wins.
    /// </summary>
    public static async Task AssertStaleParamsFollowTheStoredFundingAsync(
        Func<IInterceptor[], NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange
        await MigrateAsync(contextFactory, cancellationToken);
        var fixture = await SplicedChannel.CreateAsync(contextFactory, 0x73);
        var staleParams = CommitmentParams.FromChannel(fixture.Channel);
        await fixture.LockAsync();

        // Act
        PersistedChannelState? state;
        await using (var context = contextFactory([]))
            state = await new ChannelStateDbRepository(context).LoadAsync(fixture.ChannelId, staleParams);

        // Assert
        Assert.NotNull(state);
        Assert.Equal(CapacityAfter, state.Commitments.Params.FundingSatoshis);
        Assert.Equal(fixture.Funding.FundingTxId, state.Commitments.Params.Funding!.FundingTxId);
        Assert.Equal(CapacityAfter * 1_000, state.Commitments.LocalBalanceMsat + state.Commitments.RemoteBalanceMsat);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Migrates the database (once is enough; later calls find nothing to apply) and, on SQL Server, allows
    /// the snapshot isolation the loads read under, as the daemon's migration step does.</summary>
    private static async Task MigrateAsync(Func<IInterceptor[], NLightningDbContext> contextFactory,
                                           CancellationToken cancellationToken)
    {
        await using var context = contextFactory([]);
        await context.Database.MigrateAsync(cancellationToken);
        await context.EnableSqlServerSnapshotIsolationAsync(cancellationToken);
    }

    private static void AssertSnapshotOn(ChannelModel channel, ulong capacitySatoshis)
    {
        var commitments = channel.Commitments!;
        Assert.Equal(capacitySatoshis, commitments.Params.FundingSatoshis);
        Assert.Equal(capacitySatoshis * 1_000, commitments.LocalBalanceMsat + commitments.RemoteBalanceMsat);
        Assert.Equal(capacitySatoshis * 1_000, commitments.LocalCommit.Spec.LocalMsat
                                             + commitments.LocalCommit.Spec.RemoteMsat);
    }

    /// <summary>A channel with its commitment snapshot and a splice pending in its engine, ready to lock.</summary>
    private sealed class SplicedChannel
    {
        private readonly Func<IInterceptor[], NLightningDbContext> _contextFactory;
        private readonly ChannelCommitments _pending;

        public ChannelModel Channel { get; }
        public ChannelId ChannelId => Channel.ChannelId;
        public ChannelFunding Funding { get; }

        private SplicedChannel(Func<IInterceptor[], NLightningDbContext> contextFactory, ChannelModel channel,
                               ChannelFunding funding, ChannelCommitments pending)
        {
            _contextFactory = contextFactory;
            Channel = channel;
            Funding = funding;
            _pending = pending;
        }

        public static async Task<SplicedChannel> CreateAsync(Func<IInterceptor[], NLightningDbContext> contextFactory,
                                                             byte tag)
        {
            var channel = SqliteDbTestContext.CreateChannel(true, channelTag: tag);
            var driver = new CommitmentDanceDriver(channel.ChannelId, CommitmentParams.FromChannel(channel),
                                                   channel.LocalBalance.MilliSatoshi,
                                                   channel.RemoteBalance.MilliSatoshi);

            // A splice-in of 250,000 sat by us (key index 1), pending in our engine with the peer's signatures
            var funding = new ChannelFunding(new TxId(Enumerable.Repeat(tag, 32).ToArray()), 1, CapacityAfter,
                                             SqliteDbTestContext.LocalPaymentBasepoint,
                                             SqliteDbTestContext.RemotePaymentBasepoint, 1,
                                             (long)(CapacityAfter - CapacityBefore) * 1_000, 0,
                                             ChannelFundingKind.Splice, ChannelFundingStatus.Pending, 2_000, 812_000);
            await using (var context = contextFactory([]))
            {
                using var unitOfWork = CreateUnitOfWork(context);
                await unitOfWork.ChannelDbRepository.AddAsync(channel);
                await unitOfWork.ChannelStateDbRepository.InitializeAsync(driver.Us);
                await unitOfWork.SaveChangesAsync();
            }

            var received = driver.UsReceiveSplice(funding);
            await using (var context = contextFactory([]))
            {
                using var unitOfWork = CreateUnitOfWork(context);
                await unitOfWork.ChannelFundingDbRepository.UpsertAsync(channel.ChannelId, funding);
                await unitOfWork.ChannelStateDbRepository.ApplyAsync(received.Next, received.Transition);
                await unitOfWork.SaveChangesAsync();
            }

            return new SplicedChannel(contextFactory, channel, funding, received.Next);
        }

        /// <summary>The splice lock's save, as <c>EngineSpliceStatePort.StageFundingsAsync</c> stages it.</summary>
        public async Task LockAsync()
        {
            var (next, retired) = _pending.Fundings!.Lock(Funding.FundingTxId);
            var result = _pending.LockFunding(Funding.FundingTxId);
            await using var context = _contextFactory([]);
            using var unitOfWork = CreateUnitOfWork(context);
            await unitOfWork.ChannelFundingDbRepository.ApplyLockAsync(ChannelId, next.Current, retired);
            await unitOfWork.ChannelStateDbRepository.ApplyAsync(result.Next, result.Transition);
            await unitOfWork.SaveChangesAsync();
        }

        private static UnitOfWork CreateUnitOfWork(NLightningDbContext context) =>
            new(context, NullLogger<UnitOfWork>.Instance, new Sha256(), new UtxoMemoryRepository());
    }

    /// <summary>
    /// Starts <c>write</c> on its own context right before the first query on <c>table</c> runs, and lets the query go
    /// on once the write committed or after <see cref="s_writeWait"/> (a write that waits for the reader).
    /// </summary>
    private sealed class WriteBeforeQueryInterceptor(string table, Func<Task> write) : DbCommandInterceptor
    {
        private Task? _write;

        public async Task WaitForWriteAsync()
        {
            Assert.NotNull(_write);
            await _write;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_write is null && command.CommandText.Replace("_", string.Empty)
                                         .Contains(table, StringComparison.OrdinalIgnoreCase))
            {
                _write = Task.Run(write, CancellationToken.None);
                await Task.WhenAny(_write, Task.Delay(s_writeWait, CancellationToken.None));
            }

            return result;
        }
    }
}