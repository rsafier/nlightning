using Docker.DotNet;

namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;
using Eclair;

/// <summary>
/// An Eclair regtest node for the interop tests (NL-180), on its own bitcoind, never sharing containers, names or chain
/// state with the LND or CLN fixtures. Where they run is <see cref="TestBackend"/>'s (<c>NLTG_TEST_BACKEND</c>): Docker
/// by default (<see cref="DockerEclairBackend"/>: its own Docker network, <see cref="InteropChainHost"/>, ports
/// published on <c>127.0.0.1</c>), or a run namespace of the Kubernetes harness (<see cref="ClusterEclairBackend"/>,
/// test harness phase 4). The tests see the same members either way.
/// </summary>
/// <remarks>
/// <para>ACINQ publishes no pinned multi-arch image of a recent release (NL-553), so the Docker backend builds
/// <see cref="EclairImage"/> from <c>test/Docker/eclair</c> (the v0.14.3 release zip, sha256-checked, on a pinned
/// Temurin 21 JRE) when the tag is missing (about 15 s); the cluster backend runs the same local tag (never pulled).</para>
/// <para>Eclair funds channels from the bitcoind wallet <c>eclair</c>, follows blocks over ZMQ <c>hashblock</c> and
/// answers its JSON API (password <see cref="ApiPassword"/>). Its p2p address (<see cref="EclairAddress"/>) survives
/// <see cref="RestartEclairAsync"/> on both backends. Eclair runs with its default channel policy, the
/// <c>to_self_delay</c> of <see cref="EclairDefaultToRemoteDelayBlocks"/> it asks of us included (accepted since
/// NL-550).</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class EclairFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-eclair-net";
    public const string BitcoinContainerName = "nltg-eclair-bitcoind";

    /// <summary>The fixture Eclair's container (Docker) or node (cluster) name, also its alias.</summary>
    public const string EclairContainerName = "nltg-eclair";

    public const string EclairImage = "nltg-eclair";
    public const string EclairTag = "0.14.3";
    public const string ApiPassword = "nltg";

    /// <summary>
    /// Eclair's default <c>eclair.channel.to-remote-delay-blocks</c>: the <c>to_self_delay</c> it asks of us, within
    /// our <c>Node:MaxAcceptedToSelfDelay</c> (2016) since NL-550.
    /// </summary>
    public const int EclairDefaultToRemoteDelayBlocks = 720;

    private readonly IEclairBackend _backend =
        TestBackend.Current == TestBackendKind.Cluster ? new ClusterEclairBackend() : new DockerEclairBackend();

    private readonly SharedObjectCache _shared = new();

    /// <summary>Where the fixture runs (<see cref="TestBackend.Current"/> when xunit created it).</summary>
    public TestBackendKind Backend => _backend.Kind;

    public RegtestBitcoinEndpoint Bitcoin => _backend.Bitcoin;

    public EclairClient Eclair => _backend.Eclair;

    /// <summary>Eclair's node id (hex, lower case).</summary>
    public string EclairNodeId => _backend.EclairNodeId;

    /// <summary>
    /// The host this process dials Eclair at (<c>127.0.0.1</c> for Docker, Eclair's stable ClusterIP name for the
    /// cluster); fixed for the fixture's lifetime.
    /// </summary>
    public string EclairHost => _backend.EclairHost;

    /// <summary>Eclair's p2p port at <see cref="EclairHost"/> (fixed for the fixture's lifetime).</summary>
    public int EclairHostPort => _backend.EclairPort;

    /// <summary>The <c>pubkey@host:port</c> an in-process node connects to.</summary>
    public string EclairAddress => $"{EclairNodeId}@{EclairHost}:{EclairHostPort}";

    /// <summary>
    /// The host Eclair dials to reach a listener of this process (<see cref="ClnFixture.HostAddressFromContainers"/>
    /// for Docker, <c>host.orb.internal</c> on OrbStack's cluster); listen on every interface.
    /// </summary>
    public string HostAddressForEclair => _backend.HostAddressForPeers;

    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _backend.StartAsync(TestContext.Current.CancellationToken);
            await WaitAllAtTipAsync([], CancellationToken.None);
            // One comparable line per backend (test harness: fixture start, Docker against the cluster)
            Console.WriteLine($"[fixture] Eclair fixture ({Backend}) ready in {watch.Elapsed.TotalSeconds:F1} s");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shared.DisposeAll();
        await _backend.DisposeAsync();
    }

    /// <summary>
    /// Writes the last <paramref name="tail"/> lines of Eclair's log to <see cref="Console"/> (the test output). On the
    /// cluster backend a failed test also gets a full dump of the namespace under <c>TestResults/cluster/</c>.
    /// </summary>
    public Task DumpEclairLogAsync(int tail = 300) => _backend.DumpEclairLogAsync(tail);

    /// <summary>Mines <paramref name="blocks"/> blocks to the miner wallet.</summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Waits until Eclair (and <paramref name="extra"/> Eclair clients) and every node in <paramref name="nodes"/> have
    /// processed bitcoind's tip. Eclair has no <c>waitblockheight</c>, so <c>getinfo.blockHeight</c> is polled.
    /// </summary>
    public async Task<uint> WaitAllAtTipAsync(IEnumerable<NLightningTestNode> nodes,
                                              CancellationToken cancellationToken, TimeSpan? timeout = null,
                                              params EclairClient[] extra)
    {
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var tip = (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);
            var eclairHeights = new List<long>();
            foreach (var eclair in extra.Prepend(Eclair))
                eclairHeights.Add(await eclair.GetBlockHeightAsync(cancellationToken));
            var ours = nodeList.Select(n => (n.Name,
                                             Height: n.IsRunning ? n.BlockchainMonitor.LastProcessedBlockHeight : 0))
                               .ToList();
            if (eclairHeights.All(h => h == tip) && ours.All(o => o.Height == tip))
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Not at the tip {tip} in time: eclair {string.Join("/", eclairHeights)}, "
                  + string.Join(", ", ours.Select(o => $"{o.Name} {o.Height}")));

            await Task.Delay(200, cancellationToken);
        }
    }

    public async Task<uint> MineAndWaitAsync(int blocks, IEnumerable<NLightningTestNode> nodes,
                                             CancellationToken cancellationToken, params EclairClient[] extra)
    {
        await MineAsync(blocks, cancellationToken);
        return await WaitAllAtTipAsync(nodes, cancellationToken, null, extra);
    }

    /// <summary>
    /// Sends <paramref name="amount"/> from the miner to a new address of <paramref name="eclair"/> (the fixture's
    /// Eclair by default), mines 6 blocks and waits until Eclair's <c>onchainbalance.confirmed</c> grew.
    /// </summary>
    public async Task FundEclairWalletAsync(LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                            CancellationToken cancellationToken, EclairClient? eclair = null)
    {
        eclair ??= Eclair;
        var before = (await eclair.OnchainBalanceAsync(cancellationToken))["confirmed"]!.GetValue<long>();
        var address = await eclair.GetNewAddressAsync(cancellationToken);
        await Bitcoin.Rpc.SendToAddressAsync(NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                                             NBitcoin.Money.Satoshis((long)amount.Satoshi),
                                             cancellationToken: cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken, eclair == Eclair ? [] : [eclair]);
        await Poll.UntilAsync(async () =>
                                  (await eclair.OnchainBalanceAsync(cancellationToken))["confirmed"]!
                                 .GetValue<long>() >= before + (long)amount.Satoshi,
                              TimeSpan.FromSeconds(60), "Eclair sees its deposit confirmed", cancellationToken);
    }

    /// <summary>
    /// Restarts Eclair (its data is kept) at the same address (<see cref="EclairAddress"/>) and waits until it is at
    /// the tip.
    /// </summary>
    public Task RestartEclairAsync(CancellationToken cancellationToken) =>
        _backend.RestartEclairAsync(cancellationToken);

    /// <summary>
    /// <c>test/Docker/&lt;name&gt;</c>, found by walking up from the test assembly.
    /// </summary>
    internal static string FindDockerDirectory(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "test", "Docker", name);
            if (File.Exists(Path.Combine(candidate, "Dockerfile")))
                return candidate;

            candidate = Path.Combine(dir.FullName, "Docker", name);
            if (File.Exists(Path.Combine(candidate, "Dockerfile")))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"test/Docker/{name}/Dockerfile not found above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// <c>docker build -t <paramref name="tag"/> <paramref name="directory"/></c> through the Docker CLI (BuildKit), with
    /// <paramref name="timeout"/>.
    /// </summary>
    internal static async Task BuildImageAsync(DockerClient client, string directory, string tag, TimeSpan timeout,
                                               params string[] buildArgs)
    {
        var info = new System.Diagnostics.ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = directory
        };
        info.ArgumentList.Add("build");
        info.ArgumentList.Add("-t");
        info.ArgumentList.Add(tag);
        foreach (var arg in buildArgs)
        {
            info.ArgumentList.Add("--build-arg");
            info.ArgumentList.Add(arg);
        }

        info.ArgumentList.Add(".");
        using var process = System.Diagnostics.Process.Start(info)
                         ?? throw new InvalidOperationException("Could not start docker build");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"docker build {tag} took more than {timeout}");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"docker build {tag} failed:\n{await stdout}\n{await stderr}");

        if (!await DockerContainerUtils.ImageExistsAsync(client, tag))
            throw new InvalidOperationException($"docker build {tag} did not produce the image");
    }
}