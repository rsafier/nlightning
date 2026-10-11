using Microsoft.Extensions.DependencyInjection;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Scale;

using Docker.Mock;
using Docker.Utils;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Reestablish;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Persistence;

/// <summary>
/// Per-channel lanes (<c>Node:ChannelLanes</c>) between two real nodes with many channels over one connection: payments
/// sent many at once over different channels all settle and both ends agree on every channel, and a restart of the
/// peer while payments are in flight loses or doubles no HTLC. SQLite files by default (lanes stay correct there);
/// <c>NLTG_SCALE_POSTGRES</c> runs them on Postgres as <see cref="ChannelScaleLivePeerTests"/> does.
/// </summary>
public sealed class ChannelLanesLivePeerTests : IAsyncDisposable
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private const ulong PaymentSat = 1_000;

    private readonly List<string> _files = [];
    private readonly List<string> _postgresDatabases = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_ManyChannelsToOnePeer_When_PaymentsRunAtOnce_Then_AllSettleAndBothEndsAgree(bool lanes)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var pair = await StartPairAsync(20, lanes, ct);
        var before = Snapshot(pair);

        // Act (16 at once over 20 channels, so most run on different channels' lanes at the same time)
        var outcomes = await PayAsync(pair, 48, 16, ct);

        // Assert
        Assert.All(outcomes, o => Assert.Equal(PaymentStatus.Succeeded, o));
        await WaitSettledAsync(pair, ct);
        var after = Snapshot(pair);
        AssertBothEndsAgree(after);
        Assert.Equal(48UL * PaymentSat * 1_000, before.HubLocalMsat - after.HubLocalMsat);
    }

    [Fact]
    public async Task Given_PaymentsInFlightOnManyLanes_When_ThePeerRestarts_Then_NoHtlcIsLostOrDoubled()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var pair = await StartPairAsync(20, lanes: true, ct);
        var before = Snapshot(pair);
        var invoices = pair.Peer.Services.GetRequiredService<IInvoiceService>();
        var bolt11s = new List<string>();
        for (var i = 0; i < 60; i++)
            bolt11s.Add((await invoices.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), $"restart {i}", 3_600,
                                                           ct)).Bolt11!);

        // Act: 16 at once; after a few settled the peer stops (a graceful stop: its lanes finish the items they are
        // handling, queued work is dropped) and starts again on the same database while the hub keeps paying
        var payer = pair.Hub.Services.GetRequiredService<IPaymentService>();
        var gate = new SemaphoreSlim(16);
        var settled = 0;
        var payments = bolt11s.Select(async bolt11 =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var payment = await payer.PayInvoiceAsync(bolt11, null, TimeSpan.FromSeconds(60), ct);
                if (payment.Status == PaymentStatus.Succeeded)
                    Interlocked.Increment(ref settled);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Poll.UntilAsync(() => Volatile.Read(ref settled) >= 10, s_timeout, "10 payments settled", ct);
        await pair.RestartPeerAsync(ct);
        await Task.WhenAll(payments).WaitAsync(s_timeout, ct);

        // Assert: once every HTLC is resolved, both ends agree on every channel and what left the hub is exactly what
        // the peer's settled invoices received (no payment stays in flight, none is counted twice)
        await WaitSettledAsync(pair, ct);
        var after = Snapshot(pair);
        AssertBothEndsAgree(after);
        var hubPayments = await ListPaymentsAsync(pair.Hub.Services);
        Assert.DoesNotContain(hubPayments, p => p.Status == PaymentStatus.InFlight);
        var succeeded = hubPayments.Count(p => p.Status == PaymentStatus.Succeeded);
        var settledInvoices = (await ListInvoicesAsync(pair.Peer.Services)).Where(i => i.Status == InvoiceStatus.Settled)
                                                                            .ToList();
        Assert.Equal(succeeded, settledInvoices.Count);
        var received = settledInvoices.Aggregate(0UL, (sum, i) => sum + i.AmountReceived!.MilliSatoshi);
        Assert.Equal(received, before.HubLocalMsat - after.HubLocalMsat);
        Assert.Equal(received, after.PeerLocalMsat - before.PeerLocalMsat);
    }

    private async Task<LanePair> StartPairAsync(int channelCount, bool lanes, CancellationToken ct)
    {
        var hubKeys = new FakeSecureKeyManager();
        var peerKeys = new FakeSecureKeyManager();
        var chain = new ScaleChain(ChannelScaleSeeder.FirstFundingHeight - 1);
        var hubPort = await PortPoolUtil.GetAvailablePortAsync();
        var peerPort = await PortPoolUtil.GetAvailablePortAsync();
        var hubDb = NewDatabase("hub");
        var peerDb = NewDatabase("peer");

        await using (var hubSeed = new ScaleNode("hub-seed", hubDb, hubKeys, chain, hubPort))
        await using (var peerSeed = new ScaleNode("peer-seed", peerDb, peerKeys, chain, peerPort))
        {
            await hubSeed.BuildAsync(ct);
            await peerSeed.BuildAsync(ct);
            var livePeer = new ScalePeer(peerKeys.GetNodePubKey(), "127.0.0.1", (uint)peerPort,
                                         new SignerKeySource(peerSeed.Services.GetRequiredService<ILightningSigner>()),
                                         Live: true);
            var seeder = new ChannelScaleSeeder();
            var live = seeder.Build(hubKeys.GetNodePubKey(),
                                    new SignerKeySource(hubSeed.Services.GetRequiredService<ILightningSigner>()),
                                    [livePeer], channelCount, chain,
                                    hubSeed.Services.GetRequiredService<IChannelIdFactory>());
            await seeder.WriteAsync(hubSeed.Services, live, hubSide: true,
                                    c => new PeerModel(c.Peer.NodeId, c.Peer.Host, c.Peer.Port, "IPv4"), ct);
            await seeder.WriteAsync(peerSeed.Services, live, hubSide: false,
                                    _ => new PeerModel(hubKeys.GetNodePubKey(), "127.0.0.1", (uint)hubPort, "IPv4"),
                                    ct);
        }

        for (var i = 0; i < 6; i++)
            chain.Mine([]);

        void Configure(NodeOptions options)
        {
            options.ChannelLanes = new ChannelLaneOptions { Enabled = lanes };
            options.ReconnectInitialDelay = TimeSpan.FromMilliseconds(500);
            options.ReconnectMaxDelay = TimeSpan.FromSeconds(2);
        }

        var pair = new LanePair(hubDb, peerDb, hubKeys, peerKeys, chain, hubPort, peerPort, Configure);
        await pair.StartAsync(ct);
        return pair;
    }

    private static async Task<List<PaymentStatus>> PayAsync(LanePair pair, int count, int parallel,
                                                            CancellationToken ct)
    {
        var invoices = pair.Peer.Services.GetRequiredService<IInvoiceService>();
        var payer = pair.Hub.Services.GetRequiredService<IPaymentService>();
        var bolt11s = new List<string>();
        for (var i = 0; i < count; i++)
            bolt11s.Add((await invoices.CreateInvoiceAsync(LightningMoney.Satoshis(PaymentSat), $"lanes {i}", 3_600,
                                                           ct)).Bolt11!);
        var gate = new SemaphoreSlim(parallel);
        var outcomes = new List<PaymentStatus>();
        await Task.WhenAll(bolt11s.Select(async bolt11 =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var payment = await payer.PayInvoiceAsync(bolt11, null, TimeSpan.FromSeconds(60), ct);
                lock (outcomes)
                    outcomes.Add(payment.Status);
            }
            finally
            {
                gate.Release();
            }
        }));
        return outcomes;
    }

    /// <summary>Waits until neither end holds an HTLC on any channel (every update signed and revoked).</summary>
    private static Task WaitSettledAsync(LanePair pair, CancellationToken ct) =>
        Poll.UntilAsync(() => pair.Hub.Channels.FindChannels(_ => true).Concat(pair.Peer.Channels.FindChannels(_ => true))
                                  .All(c => c.Commitments is { } commitments && commitments.Htlcs.Count == 0
                                         && commitments.RemoteNextCommit is null),
                        s_timeout, "no HTLC left on either end", ct);

    private static LaneSnapshot Snapshot(LanePair pair)
    {
        var hub = pair.Hub.Channels.FindChannels(_ => true).ToDictionary(c => c.ChannelId);
        var peer = pair.Peer.Channels.FindChannels(_ => true).ToDictionary(c => c.ChannelId);
        return new LaneSnapshot(hub.ToDictionary(c => c.Key, c => (c.Value.LocalBalance.MilliSatoshi,
                                                                   c.Value.RemoteBalance.MilliSatoshi)),
                                peer.ToDictionary(c => c.Key, c => (c.Value.LocalBalance.MilliSatoshi,
                                                                    c.Value.RemoteBalance.MilliSatoshi)));
    }

    private static void AssertBothEndsAgree(LaneSnapshot snapshot)
    {
        Assert.Equal(snapshot.Hub.Keys.OrderBy(k => k.ToString()), snapshot.Peer.Keys.OrderBy(k => k.ToString()));
        foreach (var (channelId, (hubLocal, hubRemote)) in snapshot.Hub)
        {
            var (peerLocal, peerRemote) = snapshot.Peer[channelId];
            Assert.Equal(hubLocal, peerRemote);
            Assert.Equal(hubRemote, peerLocal);
        }
    }

    private static async Task<IReadOnlyList<Domain.Payments.Models.PaymentModel>> ListPaymentsAsync(
        IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().PaymentDbRepository.ListAsync(0, 10_000);
    }

    private static async Task<IReadOnlyList<Domain.Payments.Models.InvoiceModel>> ListInvoicesAsync(
        IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().InvoiceDbRepository.ListAsync(0, 10_000);
    }

    private TestNodeDatabase NewDatabase(string name)
    {
        var postgres = Environment.GetEnvironmentVariable("NLTG_SCALE_POSTGRES");
        if (!string.IsNullOrEmpty(postgres))
        {
            var database = $"nltg_lanes_{name}_{Guid.NewGuid():N}"[..40];
            _postgresDatabases.Add(database);
            return TestNodeDatabase.Postgres($"{postgres.TrimEnd(';')};Database={database}");
        }

        var path = Path.Combine(Path.GetTempPath(), $"nltg-lanes-{name}-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return TestNodeDatabase.Sqlite(path);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var file in _files.SelectMany(f => new[] { f, f + "-wal", f + "-shm" }))
            try
            {
                SqliteTestPools.Clear(file);
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }

        var postgres = Environment.GetEnvironmentVariable("NLTG_SCALE_POSTGRES");
        foreach (var database in _postgresDatabases)
            try
            {
                Npgsql.NpgsqlConnection.ClearAllPools();
                await using var connection = new Npgsql.NpgsqlConnection($"{postgres!.TrimEnd(';')};Database=postgres");
                await connection.OpenAsync();
                await using var command =
                    new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", connection);
                await command.ExecuteNonQueryAsync();
            }
            catch (Npgsql.NpgsqlException)
            {
                // A leftover database is harmless
            }
    }

    private sealed record LaneSnapshot(
        Dictionary<ChannelId, (ulong Local, ulong Remote)> Hub,
        Dictionary<ChannelId, (ulong Local, ulong Remote)> Peer)
    {
        public ulong HubLocalMsat => Hub.Values.Aggregate(0UL, (sum, b) => sum + b.Local);
        public ulong PeerLocalMsat => Peer.Values.Aggregate(0UL, (sum, b) => sum + b.Local);
    }

    /// <summary>The hub and its live peer, both started; the peer can be restarted on its database.</summary>
    private sealed class LanePair(
        TestNodeDatabase hubDb,
        TestNodeDatabase peerDb,
        FakeSecureKeyManager hubKeys,
        FakeSecureKeyManager peerKeys,
        ScaleChain chain,
        int hubPort,
        int peerPort,
        Action<NodeOptions> configure) : IAsyncDisposable
    {
        public ScaleNode Hub { get; private set; } = null!;
        public ScaleNode Peer { get; private set; } = null!;

        public async Task StartAsync(CancellationToken ct)
        {
            // The first starts adopt the seeded databases (signing enrollment, accounting cutover), then both start for
            // real: the peer first, then the hub, which dials it
            await using (var first = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort, configure))
                await first.StartAsync(ct);
            await using (var first = new ScaleNode("hub", hubDb, hubKeys, chain, hubPort, configure))
                await first.StartAsync(ct);

            Peer = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort, configure);
            await Peer.StartAsync(ct);
            Hub = new ScaleNode("hub", hubDb, hubKeys, chain, hubPort, configure);
            await Hub.StartAsync(ct);
            await WaitReestablishedAsync(ct);
        }

        public async Task RestartPeerAsync(CancellationToken ct)
        {
            await Peer.DisposeAsync();
            Peer = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort, configure);
            await Peer.StartAsync(ct);
            await WaitReestablishedAsync(ct);
        }

        private Task WaitReestablishedAsync(CancellationToken ct)
        {
            var hubTracker = Hub.Services.GetRequiredService<IReestablishTracker>();
            var peerTracker = Peer.Services.GetRequiredService<IReestablishTracker>();
            var ids = Hub.Channels.FindChannels(c => c.RemoteNodeId == peerKeys.GetNodePubKey())
                         .Select(c => c.ChannelId).ToList();
            return Poll.UntilAsync(() => ids.All(hubTracker.IsReestablished) && ids.All(peerTracker.IsReestablished),
                                   s_timeout, "every channel reestablished on both ends", ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Hub is not null)
                await Hub.DisposeAsync();
            if (Peer is not null)
                await Peer.DisposeAsync();
            PortPoolUtil.ReleasePort(hubPort);
            PortPoolUtil.ReleasePort(peerPort);
        }
    }
}