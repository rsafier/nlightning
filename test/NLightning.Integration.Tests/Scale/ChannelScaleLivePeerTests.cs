using System.Diagnostics;
using System.Text;
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
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Protocol.Interfaces;
using Persistence;

/// <summary>
/// The live part of the channel-scale benchmark (NL-1357, <c>docs/agents/CHANNEL_SCALE.md</c>): a hub with thousands of
/// Open channels, most to offline peers and a share to one live peer node (both databases seeded with the two ends of
/// the same channels, real keys on both sides), started together: the time until every channel with the live peer is
/// reestablished over one connection, then payments from the hub to the peer over those channels while the rest stay
/// idle. <c>NLTG_SCALE_LIVE_TOTAL</c> (10,000), <c>NLTG_SCALE_LIVE_CHANNELS</c> (1,000) and
/// <c>NLTG_SCALE_PAYMENTS</c> (200) size it.
/// </summary>
public sealed class ChannelScaleLivePeerTests : IAsyncDisposable
{
    private static readonly TimeSpan s_reestablishTimeout = TimeSpan.FromMinutes(10);
    private readonly List<string> _files = [];

    [Fact]
    public Task Given_SeededChannelsWithALivePeer_When_BothStart_Then_TheyReestablishAndCarryPayments() =>
        // The default-run variant (about 10 s): both seeded ends agree, so every channel reestablishes and pays
        RunAsync(100, 20, 5, blackhole: false, TestContext.Current.CancellationToken);

    [Fact(Explicit = true)]
    [Trait("Category", "Long")]
    public Task Given_ThousandsOfChannelsAndALivePeer_When_BothStart_Then_ReestablishAndPaymentsAreTimed() =>
        // "blackhole": the offline peers' addresses never answer (each dial waits Node:NetworkTimeout), as dead peers
        // behind a firewall do; otherwise a closed loopback port refuses every dial at once
        RunAsync(EnvInt("NLTG_SCALE_LIVE_TOTAL", 10_000), EnvInt("NLTG_SCALE_LIVE_CHANNELS", 1_000),
                 EnvInt("NLTG_SCALE_PAYMENTS", 200),
                 Environment.GetEnvironmentVariable("NLTG_SCALE_UNREACHABLE") == "blackhole",
                 TestContext.Current.CancellationToken);

    private async Task RunAsync(int total, int liveCount, int paymentCount, bool blackhole, CancellationToken ct)
    {
        var offlinePeerCount = Math.Max(10, (total - liveCount) / 10);
        var report = new StringBuilder($"\n=== live peer: {total} hub channels, {liveCount} with the live peer, "
                                     + $"{offlinePeerCount} offline peers{(blackhole ? " (blackholed)" : "")} ===\n");

        var hubKeys = new FakeSecureKeyManager();
        var peerKeys = new FakeSecureKeyManager();
        var chain = new ScaleChain(ChannelScaleSeeder.FirstFundingHeight - 1);
        var hubPort = await PortPoolUtil.GetAvailablePortAsync();
        var peerPort = await PortPoolUtil.GetAvailablePortAsync();
        var closedPort = await PortPoolUtil.GetAvailablePortAsync();
        var hubDb = TestNodeDatabase.Sqlite(NewDbPath());
        var peerDb = TestNodeDatabase.Sqlite(NewDbPath());
        try
        {
            // Seed both databases: the hub's every channel, the peer's end of the live ones
            var seedWatch = Stopwatch.StartNew();
            await using (var hubSeed = new ScaleNode("hub-seed", hubDb, hubKeys, chain, hubPort))
            await using (var peerSeed = new ScaleNode("peer-seed", peerDb, peerKeys, chain, peerPort))
            {
                await hubSeed.BuildAsync(ct);
                await peerSeed.BuildAsync(ct);
                var hubSource = new SignerKeySource(hubSeed.Services.GetRequiredService<ILightningSigner>());
                var livePeer = new ScalePeer(peerKeys.GetNodePubKey(), "127.0.0.1", (uint)peerPort,
                                             new SignerKeySource(peerSeed.Services.GetRequiredService<ILightningSigner>()),
                                             Live: true);
                var offline = new RandomKeySource();
                var offlinePeers = Enumerable.Range(0, offlinePeerCount)
                                             .Select(_ => new ScalePeer(RandomKeySource.NewKey(),
                                                                        blackhole ? "10.255.255.1" : "127.0.0.1",
                                                                        blackhole ? 9735u : (uint)closedPort, offline))
                                             .ToList();
                var seeder = new ChannelScaleSeeder();
                var idFactory = hubSeed.Services.GetRequiredService<IChannelIdFactory>();
                var live = seeder.Build(hubKeys.GetNodePubKey(), hubSource, [livePeer], liveCount, chain, idFactory);
                var idle = seeder.Build(hubKeys.GetNodePubKey(), hubSource, offlinePeers, total - liveCount, chain,
                                        idFactory);
                // The dead peers' rows first (the order the database lists them in), last seen a month ago; the live
                // peer seen an hour ago
                await seeder.WriteAsync(hubSeed.Services, [.. idle, .. live], hubSide: true,
                                        c => new PeerModel(c.Peer.NodeId, c.Peer.Host, c.Peer.Port, "IPv4")
                                        {
                                            LastSeenAt = DateTime.UtcNow - (c.Peer.Live
                                                                                ? TimeSpan.FromHours(1)
                                                                                : TimeSpan.FromDays(30))
                                        }, ct);
                await seeder.WriteAsync(peerSeed.Services, live, hubSide: false,
                                        _ => new PeerModel(hubKeys.GetNodePubKey(), "127.0.0.1", (uint)hubPort,
                                                           "IPv4"), ct);
            }

            for (var i = 0; i < 6; i++)
                chain.Mine([]);
            report.AppendLine($"seed {seedWatch.Elapsed.TotalSeconds:F1} s");

            // First starts (signing enrollment adoption, accounting cutover), one node at a time
            await using (var first = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort))
                await first.StartAsync(ct);
            await using (var first = new ScaleNode("hub", hubDb, hubKeys, chain, hubPort))
                await first.StartAsync(ct);

            // The measured restart: the peer first (its dial to the hub fails and backs off), then the hub, which
            // dials the peer during its start
            // The peer never redials the hub on its own here (a long backoff), so the hub's own dial is what is timed
            await using var peer = new ScaleNode("peer", peerDb, peerKeys, chain, peerPort,
                                                 o =>
                                                 {
                                                     o.ReconnectMaxDelay = TimeSpan.FromHours(2);
                                                     o.ReconnectInitialDelay = TimeSpan.FromHours(1);
                                                 });
            await peer.StartAsync(ct);
            await using var hub = new ScaleNode("hub", hubDb, hubKeys, chain, hubPort);
            var liveIds = new List<ChannelId>();
            var startWatch = Stopwatch.StartNew();
            await hub.StartAsync(ct);
            var started = startWatch.Elapsed;
            var hubTracker = hub.Services.GetRequiredService<IReestablishTracker>();
            liveIds.AddRange(hub.Channels.FindChannels(c => c.RemoteNodeId == peerKeys.GetNodePubKey())
                                .Select(c => c.ChannelId));
            var peerTracker = peer.Services.GetRequiredService<IReestablishTracker>();
            var firstUsable = TimeSpan.Zero;
            while (true)
            {
                var done = liveIds.Count(hubTracker.IsReestablished);
                if (done > 0 && firstUsable == TimeSpan.Zero)
                    firstUsable = startWatch.Elapsed;
                if (done == liveIds.Count && liveIds.All(peerTracker.IsReestablished))
                    break;
                if (startWatch.Elapsed > s_reestablishTimeout)
                    throw new TimeoutException($"{done} of {liveIds.Count} channels reestablished after "
                                             + $"{startWatch.Elapsed}");
                await Task.Delay(50, ct);
            }

            var allUsable = startWatch.Elapsed;
            report.AppendLine($"hub start {started.TotalMilliseconds:F0} ms; first live channel usable at "
                            + $"{firstUsable.TotalMilliseconds:F0} ms, all {liveIds.Count} usable on both ends at "
                            + $"{allUsable.TotalMilliseconds:F0} ms from the hub's start");
            foreach (var (step, elapsed) in hub.StartSteps)
                report.AppendLine($"  {step,-40} {elapsed.TotalMilliseconds,10:F1} ms");

            // Payments from the hub to the peer: one at a time, then 8 at once
            var invoices = peer.Services.GetRequiredService<IInvoiceService>();
            var payer = hub.Services.GetRequiredService<IPaymentService>();
            foreach (var parallel in new[] { 1, 8 })
            {
                var bolt11s = new List<string>();
                var invoicing = Stopwatch.StartNew();
                for (var i = 0; i < paymentCount; i++)
                    bolt11s.Add((await invoices.CreateInvoiceAsync(LightningMoney.Satoshis(1_000), $"scale {i}", 3_600,
                                                                   ct)).Bolt11!);
                report.AppendLine($"invoices x{paymentCount} on the peer: "
                                + $"{invoicing.Elapsed.TotalMilliseconds / paymentCount:F1} ms each");
                var latencies = new List<TimeSpan>();
                var failed = 0;
                var gate = new SemaphoreSlim(parallel);
                var watch = Stopwatch.StartNew();
                await Task.WhenAll(bolt11s.Select(async bolt11 =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var one = Stopwatch.StartNew();
                        var payment = await payer.PayInvoiceAsync(bolt11, null, TimeSpan.FromSeconds(60), ct);
                        lock (latencies)
                        {
                            latencies.Add(one.Elapsed);
                            if (payment.Status != PaymentStatus.Succeeded)
                                failed++;
                        }
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
                var elapsed = watch.Elapsed;
                latencies.Sort();
                report.AppendLine($"payments x{paymentCount}, {parallel} at once: {elapsed.TotalSeconds:F1} s, "
                                + $"{paymentCount / elapsed.TotalSeconds:F1}/s, failed {failed}, latency p50 "
                                + $"{latencies[latencies.Count / 2].TotalMilliseconds:F0} ms, p95 "
                                + $"{latencies[latencies.Count * 95 / 100].TotalMilliseconds:F0} ms");
                Assert.Equal(0, failed);
            }

            foreach (var (key, count) in hub.WarningCounts.OrderByDescending(w => w.Value))
                report.AppendLine($"  hub log {key}: {count}");
            foreach (var (key, count) in peer.WarningCounts.OrderByDescending(w => w.Value))
                report.AppendLine($"  peer log {key}: {count}");
        }
        finally
        {
            Console.WriteLine(report);
            TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
            PortPoolUtil.ReleasePort(hubPort);
            PortPoolUtil.ReleasePort(peerPort);
            PortPoolUtil.ReleasePort(closedPort);
        }
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

        await Task.CompletedTask;
    }

    private string NewDbPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nltg-scale-live-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}