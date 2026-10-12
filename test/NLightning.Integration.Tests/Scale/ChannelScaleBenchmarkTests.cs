using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Scale;

using Application.Channels.Fees;
using Application.Channels.Safety.Interfaces;
using Application.Onchain.Interfaces;
using Docker.Mock;
using Docker.Utils;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Node.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Persistence;

/// <summary>
/// The channel-scale benchmark (NL-1357, <c>docs/agents/CHANNEL_SCALE.md</c>): one node with thousands of Open channels
/// across hundreds of peers, seeded into its database (<see cref="ChannelScaleSeeder"/>), started through the daemon's
/// composition and steps (<see cref="ScaleNode"/>) and measured: each start step, memory per channel, per-block work and
/// the channel memory repository's scans. The peers are offline (a closed loopback port), as most are right after a
/// restart. The <c>Explicit</c> <c>Long</c> test runs 1,000, 5,000 and 10,000 channels (<c>NLTG_SCALE_SIZES</c>
/// overrides, <c>NLTG_SCALE_POSTGRES</c> adds a Postgres run); the default test is a 200-channel smoke of the same path.
/// </summary>
public sealed class ChannelScaleBenchmarkTests : IAsyncDisposable
{
    private readonly List<string> _files = [];

    [Fact]
    public async Task Given_200SeededChannels_When_TheNodeStarts_Then_EveryChannelIsLoaded()
    {
        // Arrange / Act
        var result = await RunAsync(TestNodeDatabase.Sqlite(NewDbPath()), 200, 20, blocks: 2,
                                    TestContext.Current.CancellationToken);

        // Assert: every channel Open in memory, the HTLC rows reloaded, nothing failed to load
        Assert.Equal(200, result.LoadedChannels);
        Assert.Equal(result.SeededHtlcs, result.LoadedHtlcs);
        Assert.DoesNotContain(result.Warnings.Keys, k => k.StartsWith("Error", StringComparison.Ordinal)
                                                       || k.StartsWith("Critical", StringComparison.Ordinal));
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Long")]
    public async Task Given_1000To10000Channels_When_TheNodeStartsAndRuns_Then_TheCostsAreReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var sizes = (Environment.GetEnvironmentVariable("NLTG_SCALE_SIZES") ?? "1000,5000,10000")
                   .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Select(int.Parse).ToList();
        var results = new List<ScaleResult>();
        foreach (var size in sizes)
            results.Add(await RunAsync(TestNodeDatabase.Sqlite(NewDbPath()), size, Math.Max(10, size / 10), 5, ct));

        if (Environment.GetEnvironmentVariable("NLTG_SCALE_POSTGRES") is { Length: > 0 } postgres)
            foreach (var size in sizes)
                results.Add(await RunAsync(
                                TestNodeDatabase.Postgres($"{postgres};Database=nlscale_{size}_{Guid.NewGuid():N}"),
                                size, Math.Max(10, size / 10), 5, ct));

        Report(results);
        foreach (var result in results)
            Assert.Equal(result.Channels, result.LoadedChannels);
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
        var path = Path.Combine(Path.GetTempPath(), $"nltg-scale-{Guid.NewGuid():N}.db");
        _files.Add(path);
        return path;
    }

    private static async Task<ScaleResult> RunAsync(TestNodeDatabase database, int channelCount, int peerCount,
                                                    int blocks, CancellationToken ct)
    {
        var keyManager = new FakeSecureKeyManager();
        var chain = new ScaleChain(ChannelScaleSeeder.FirstFundingHeight - 1);
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var closedPort = await PortPoolUtil.GetAvailablePortAsync(); // nobody listens: every dial is refused at once
        var result = new ScaleResult(database.ConfigurationProviderName, channelCount, peerCount);
        try
        {
            // Seed through a first service graph (migrations, the hub's signer, the repositories)
            var seedWatch = Stopwatch.StartNew();
            await using (var seedNode = new ScaleNode("seed", database, keyManager, chain, port))
            {
                await seedNode.BuildAsync(ct);
                var signer = seedNode.Services.GetRequiredService<ILightningSigner>();
                var offline = new RandomKeySource();
                var peers = Enumerable.Range(0, peerCount)
                                      .Select(_ => new ScalePeer(RandomKeySource.NewKey(), "127.0.0.1",
                                                                 (uint)closedPort, offline))
                                      .ToList();
                var seeder = new ChannelScaleSeeder();
                var channels = seeder.Build(keyManager.GetNodePubKey(), new SignerKeySource(signer), peers,
                                            channelCount, chain,
                                            seedNode.Services.GetRequiredService<IChannelIdFactory>());
                result.SeededHtlcs = await seeder.WriteAsync(
                                         seedNode.Services, channels, hubSide: true,
                                         s => new PeerModel(s.Peer.NodeId, s.Peer.Host, s.Peer.Port, "IPv4"), ct);
            }

            result.SeedTime = seedWatch.Elapsed;
            // The tip a few blocks past the last funding (every channel well past its depth)
            for (var i = 0; i < 6; i++)
                chain.Mine([]);

            // The first start adopts the seeded database into the signing enrollment (NL-1340) and writes the
            // accounting cutover: one-time costs, reported apart
            await using (var first = new ScaleNode("hub", database, keyManager, chain, port))
            {
                var firstWatch = Stopwatch.StartNew();
                await first.StartAsync(ct);
                result.FirstStartTotal = firstWatch.Elapsed;
                result.FirstStartSteps.AddRange(first.StartSteps);
            }

            // A restart (a fresh service graph, as a process start), measured
            ForceGc();
            var managedBefore = GC.GetTotalMemory(true);
            var rssBefore = Process.GetCurrentProcess().WorkingSet64;
            await using var node = new ScaleNode("hub", database, keyManager, chain, port);
            var startWatch = Stopwatch.StartNew();
            await node.StartAsync(ct);
            result.StartTotal = startWatch.Elapsed;
            result.StartSteps.AddRange(node.StartSteps);

            result.LoadedChannels = node.Channels.FindChannels(c => c.State == ChannelState.Open).Count;
            result.LoadedHtlcs = node.Channels.FindChannels(c => c.Commitments is not null)
                                     .Sum(c => c.Commitments!.Htlcs.Count);

            // The signer loads a channel's signing data from the database on first use (NL-067): every channel once
            var signerWatch = Stopwatch.StartNew();
            var hubSigner = node.Services.GetRequiredService<ILightningSigner>();
            foreach (var channel in node.Channels.FindChannels(_ => true))
                hubSigner.TryGetBroadcastSignedCommitment(channel.ChannelId, out _);
            result.SignerLoad = signerWatch.Elapsed;

            ForceGc();
            result.ManagedBytes = GC.GetTotalMemory(true) - managedBefore;
            result.RssBytes = Process.GetCurrentProcess().WorkingSet64 - rssBefore;

            // Diagnostics of the peer manager step: the repository load it starts with, and the state reload that each
            // channel's registration repeats (its pending HTLC events, ChannelManager.QueuePendingDomainEventsAsync)
            using (var scope = node.Services.CreateScope())
            {
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var load = Stopwatch.StartNew();
                _ = await uow.GetPeersForStartupAsync();
                result.ChannelLoad = load.Elapsed;
            }

            var reload = Stopwatch.StartNew();
            foreach (var channel in node.Channels.FindChannels(c => c.Commitments is not null))
            {
                using var scope = node.Services.CreateScope();
                _ = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelStateDbRepository
                               .LoadAsync(channel.ChannelId, channel.Commitments!.Params);
            }

            result.StateReload = reload.Elapsed;

            // The channel memory repository's linear scans (per call)
            var peerIds = node.Channels.FindChannels(_ => true).Select(c => c.RemoteNodeId).Distinct().ToList();
            var scanWatch = Stopwatch.StartNew();
            const int scans = 1_000;
            for (var i = 0; i < scans; i++)
            {
                var peerId = peerIds[i % peerIds.Count];
                _ = node.Channels.FindChannels(c => c.RemoteNodeId == peerId);
            }

            result.FindChannelsPerCall = scanWatch.Elapsed / scans;

            // Per block: the monitor's block round, the HTLC deadline round and the update_fee round
            var expiry = node.Services.GetRequiredService<IHtlcExpiryMonitor>();
            var fees = node.Services.GetRequiredService<IFeeUpdateScheduler>();
            for (var b = 0; b < blocks; b++)
            {
                var (_, height) = chain.Mine(RandomTransactions(500));
                result.BlockTimes.Add(await node.DeliverBlockAsync(height));
                // The block's on-chain resolution round runs in the background (ChannelManager schedules it)
                var executorRound = Stopwatch.StartNew();
                await node.Services.GetRequiredService<IOnchainResolutionExecutor>().WhenIdleAsync();
                result.ExecutorTimes.Add(executorRound.Elapsed);
                var check = Stopwatch.StartNew();
                await expiry.CheckAsync(height, ct);
                result.ExpiryTimes.Add(check.Elapsed);
                var round = Stopwatch.StartNew();
                await fees.RunOnceAsync(ct);
                result.FeeRoundTimes.Add(round.Elapsed);
            }

            var stopWatch = Stopwatch.StartNew();
            await node.StopAsync();
            result.StopTime = stopWatch.Elapsed;
            foreach (var (key, count) in node.WarningCounts)
                result.Warnings[key] = count;
        }
        finally
        {
            PortPoolUtil.ReleasePort(port);
            PortPoolUtil.ReleasePort(closedPort);
        }

        TestContext.Current.TestOutputHelper?.WriteLine(result.ToString());
        return result;
    }

    private static IEnumerable<Transaction> RandomTransactions(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var tx = Network.RegTest.CreateTransaction();
            tx.Inputs.Add(new OutPoint(RandomUtils.GetUInt256(), 0));
            tx.Outputs.Add(Money.Satoshis(10_000), new Key().PubKey.WitHash.ScriptPubKey);
            yield return tx;
        }
    }

    private static void ForceGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
    }

    private static void Report(IEnumerable<ScaleResult> results)
    {
        var text = new StringBuilder("\n=== channel scale summary ===\n");
        foreach (var r in results)
            text.AppendLine(r.ToString());
        Console.WriteLine(text);
        TestContext.Current.TestOutputHelper?.WriteLine(text.ToString());
    }

    private sealed class ScaleResult(string provider, int channels, int peers)
    {
        public string Provider { get; } = provider;
        public int Channels { get; } = channels;
        public int Peers { get; } = peers;
        public int SeededHtlcs { get; set; }
        public int LoadedChannels { get; set; }
        public int LoadedHtlcs { get; set; }
        public TimeSpan SeedTime { get; set; }
        public TimeSpan FirstStartTotal { get; set; }
        public TimeSpan StartTotal { get; set; }
        public TimeSpan SignerLoad { get; set; }
        public TimeSpan StopTime { get; set; }
        public TimeSpan FindChannelsPerCall { get; set; }
        public long ManagedBytes { get; set; }
        public TimeSpan ChannelLoad { get; set; }
        public TimeSpan StateReload { get; set; }
        public long RssBytes { get; set; }
        public List<(string Step, TimeSpan Elapsed)> FirstStartSteps { get; } = [];
        public List<(string Step, TimeSpan Elapsed)> StartSteps { get; } = [];
        public List<TimeSpan> BlockTimes { get; } = [];
        public List<TimeSpan> ExecutorTimes { get; } = [];
        public List<TimeSpan> ExpiryTimes { get; } = [];
        public List<TimeSpan> FeeRoundTimes { get; } = [];
        public Dictionary<string, int> Warnings { get; } = [];

        public override string ToString()
        {
            var text = new StringBuilder();
            text.AppendLine($"--- {Provider}: {Channels} channels, {Peers} peers, {SeededHtlcs} HTLCs "
                          + $"(loaded {LoadedChannels} channels, {LoadedHtlcs} HTLCs); seed {SeedTime.TotalSeconds:F1} s");
            text.AppendLine($"{"start step",-40} {"first start",12} {"restart",12}");
            for (var i = 0; i < StartSteps.Count; i++)
                text.AppendLine($"  {StartSteps[i].Step,-38} {FirstStartSteps[i].Elapsed.TotalMilliseconds,9:F1} ms "
                              + $"{StartSteps[i].Elapsed.TotalMilliseconds,9:F1} ms");
            text.AppendLine($"  {"total",-38} {FirstStartTotal.TotalMilliseconds,9:F1} ms "
                          + $"{StartTotal.TotalMilliseconds,9:F1} ms");
            text.AppendLine($"signer lazy load of every channel {SignerLoad.TotalMilliseconds:F0} ms");
            text.AppendLine($"managed heap growth over the restart (after a full GC) {ManagedBytes / 1048576.0:F1} MiB, "
                          + $"RSS {RssBytes / 1048576.0:+0.0;-0.0} MiB (compare sizes for the per-channel slope)");
            text.AppendLine($"diagnostics: channel load (GetPeersForStartupAsync) {ChannelLoad.TotalMilliseconds:F0} ms, "
                          + $"state reload of every channel {StateReload.TotalMilliseconds:F0} ms");
            text.AppendLine($"FindChannels(by peer) {FindChannelsPerCall.TotalMicroseconds:F1} us/call");
            text.AppendLine($"block (500 txs) monitor ms: {Join(BlockTimes)}");
            text.AppendLine($"on-chain executor round ms: {Join(ExecutorTimes)}");
            text.AppendLine($"HTLC deadline round ms:     {Join(ExpiryTimes)}");
            text.AppendLine($"update_fee round ms:        {Join(FeeRoundTimes)}");
            text.AppendLine($"stop {StopTime.TotalMilliseconds:F0} ms");
            foreach (var (key, count) in Warnings.OrderByDescending(w => w.Value))
                text.AppendLine($"  log {key}: {count}");
            return text.ToString();
        }

        private static string Join(IEnumerable<TimeSpan> times) =>
            string.Join(", ", times.Select(t => t.TotalMilliseconds.ToString("F1")));
    }
}