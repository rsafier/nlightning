using System.Globalization;

namespace NLightning.Integration.Tests.Fixtures.Cashu;

using Cluster;
using Docker.Utils;
using Testing.Cluster.Images;
using Testing.Cluster.Kube;
using Testing.Cluster.Nodes;
using Testing.Cluster.Nodes.Cashu;
using Testing.Cluster.Reach;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;

/// <summary>
/// The Cashu mint proof's topology (Cashu plan C2, NL-993) on the Kubernetes harness, its only backend: a run namespace
/// (suite <c>cashu-mint</c>) with the harness's bitcoind (<c>miner</c>, Bitcoin Core 31.1, <c>emptyDir</c>), CDK's mint
/// daemon <c>cdk-mintd</c> (<see cref="ImageVersions.CdkMintd"/>, deployed per test by <see cref="StartMintAsync"/>)
/// and CDK's wallet CLI <c>cdk-cli</c> in an idle pod (<see cref="ImageVersions.CdkCli"/>, built locally from
/// <c>test/Docker/cdk-cli</c> and never pulled; CDK publishes no image of it). Without <c>NLTG_TEST_BACKEND=cluster</c>
/// the fixture starts nothing and its tests are skipped with the reason (<see cref="UnavailableReason"/>); with it, a
/// missing Kubernetes configuration fails the fixture (<see cref="ConfigurationError"/>). Run the suite with
/// <c>scripts/run-cluster.sh --matrix cashu</c>.
/// </summary>
/// <remarks>
/// <para>The mint reaches the in-process node's CDK payment processor, which listens on the host's loopback over plain
/// HTTP/2 (the processor allows that on loopback only), at <see cref="HostEndpoints.ForPods"/>
/// (<c>host.orb.internal</c>, which OrbStack forwards to the host's loopback). The wallet reaches the mint by its
/// Service name (<see cref="MintUrl"/>), the test process by its pod IP.</para>
/// <para>CDK 0.18 keeps the mint's configuration in its database, so the mint pod loads its <c>config.toml</c> with
/// <c>cdk-mintd config init --new-mint</c> when it starts (<see cref="CdkNodes.MintWorkload"/>). Each test starts its
/// own mint because the processor's port belongs to the test's node.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class CashuMintFixture : IAsyncLifetime
{
    /// <summary>The CDK release of the mint, the wallet and the vendored processor proto.</summary>
    public const string CdkVersion = "0.18.1";

    /// <summary>The BIP-39 test mnemonic of the mint's keysets (regtest only).</summary>
    private const string MintMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly ClusterAvailability _availability;
    private readonly CashuTopology? _topology;

    private RegtestBitcoinEndpoint? _bitcoin;
    private KubeNodeHandle? _wallet;
    private KubeNodeHandle? _mint;

    public CashuMintFixture()
        : this(Environment.GetEnvironmentVariable, ClusterAvailability.KubeConfigurationProbe)
    {
    }

    /// <param name="environment">Reads environment variables (<see cref="TestBackend.EnvironmentVariable"/>).</param>
    /// <param name="kubeConfiguration">Throws when no Kubernetes configuration can be built.</param>
    /// <param name="skip">Skips the current test with a reason (<see cref="Assert.Skip"/> when null).</param>
    internal CashuMintFixture(Func<string, string?> environment, Action kubeConfiguration,
                              Action<string>? skip = null)
    {
        _availability = new ClusterAvailability("the Cashu mint fixture", "NL-993",
                                                "scripts/run-cluster.sh --matrix cashu", environment,
                                                kubeConfiguration, skip);
        if (_availability.CanStart)
            _topology = new CashuTopology();
    }

    /// <summary>Why the fixture does not run in this process (the skip reason of its tests); null on the cluster.</summary>
    public string? UnavailableReason => _availability.UnavailableReason;

    /// <summary>Under <c>NLTG_TEST_BACKEND=cluster</c>, why no Kubernetes configuration could be built (NL-860).</summary>
    public string? ConfigurationError => _availability.ConfigurationError;

    public RegtestBitcoinEndpoint Bitcoin =>
        _bitcoin ?? throw new InvalidOperationException("The Cashu mint fixture is not running");

    /// <summary>The host the mint dials to reach the processor of an in-process node (listening on loopback).</summary>
    public string HostAddressForMint { get; } = HostEndpoints.ForPods();

    /// <summary>The mint's URL for the wallet (its Service name in the run's namespace).</summary>
    public string MintUrl => CdkNodes.MintServiceUrl;

    private CashuTopology Topology =>
        _topology ?? throw new InvalidOperationException("The Cashu mint fixture is not running");

    private TestRun Run => Topology.Run;

    private KubeNodeHandle Wallet =>
        _wallet ?? throw new InvalidOperationException("The Cashu mint fixture is not running");

    /// <summary>
    /// Skips the current test when the fixture does not run in this process (<see cref="UnavailableReason"/>); a test
    /// class calls it in its constructor.
    /// </summary>
    public void SkipIfUnavailable() => _availability.SkipIfUnavailable();

    public async ValueTask InitializeAsync()
    {
        if (UnavailableReason is not null)
        {
            Console.WriteLine($"[fixture] Cashu mint fixture not started: {UnavailableReason}");
            return;
        }

        _availability.ThrowIfMisconfigured();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ct = TestContext.Current.CancellationToken;
        await Topology.EnsureStartedAsync(ct);
        foreach (var line in Topology.StartLog)
            Console.WriteLine(line);

        _bitcoin = ClusterChainEndpoint.Create(Topology.Topology.Chain);
        var tip = await GetTipAsync(ct);
        if (tip < 101)
            await MineAsync(101 - (int)tip, ct);

        _wallet = await Run.DeployAsync(CdkNodes.WalletWorkload(), s_readyTimeout, ct);
        Console.WriteLine($"[fixture] Cashu mint fixture (cluster) ready in {watch.Elapsed.TotalSeconds:F1} s");
    }

    public async ValueTask DisposeAsync()
    {
        if (_topology is not null)
            await _topology.DisposeAsync();
    }

    /// <summary>Mines <paramref name="blocks"/> blocks to the miner wallet.</summary>
    public async Task MineAsync(int blocks, CancellationToken cancellationToken)
    {
        var rpc = Bitcoin.Rpc;
        await rpc.GenerateToAddressAsync(blocks, await rpc.GetNewAddressAsync(cancellationToken), cancellationToken);
    }

    /// <summary>The chain tip.</summary>
    public async Task<uint> GetTipAsync(CancellationToken cancellationToken) =>
        (uint)await Bitcoin.Rpc.GetBlockCountAsync(cancellationToken);

    /// <summary>
    /// Deploys <c>cdk-mintd</c> (replacing a running one) with a fresh database, backed by the CDK payment processor
    /// on this host's loopback port <paramref name="processorPort"/>, and waits until it answers <c>/v1/info</c>.
    /// </summary>
    public async Task StartMintAsync(int processorPort, CancellationToken cancellationToken)
    {
        await StopMintAsync(cancellationToken);
        var workload = CdkNodes.MintWorkload(CdkNodes.MintConfig(HostAddressForMint, processorPort), MintMnemonic);
        _mint = await Run.DeployAsync(workload, s_readyTimeout, cancellationToken);
        await WaitForMintAsync(cancellationToken);
        Console.WriteLine($"[cashu] cdk-mintd {CdkVersion} up at {MintUrl} ({MintHostUrl}), processor "
                        + $"{HostAddressForMint}:{processorPort}");
    }

    /// <summary>
    /// Restarts the mint's pod over the same database (its quotes, keysets and the processor's address) and waits
    /// until it answers.
    /// </summary>
    public async Task RestartMintAsync(CancellationToken cancellationToken)
    {
        var mint = _mint ?? throw new InvalidOperationException("No mint runs");
        await mint.RestartAsync(s_readyTimeout, cancellationToken);
        await WaitForMintAsync(cancellationToken);
        Console.WriteLine($"[cashu] cdk-mintd restarted ({MintHostUrl})");
    }

    /// <summary>GETs <paramref name="path"/> on the mint from this process (e.g. <c>/v1/melt/quote/onchain/{id}</c>).</summary>
    public async Task<string> GetFromMintAsync(string path, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        return await http.GetStringAsync($"{MintHostUrl}{path}", cancellationToken);
    }

    /// <summary>The mint's URL from this process (its pod IP).</summary>
    private string MintHostUrl =>
        string.Create(CultureInfo.InvariantCulture,
                      $"http://{(_mint ?? throw new InvalidOperationException("No mint runs")).PodIp}:{CdkNodes.MintPort}");

    private async Task WaitForMintAsync(CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        await Testing.Cluster.Poll.UntilDoneAsync(async ct =>
        {
            try
            {
                using var response = await http.GetAsync($"{MintHostUrl}/v1/info", ct);
                return response.IsSuccessStatusCode ? null : $"/v1/info answered {(int)response.StatusCode}";
            }
            catch (HttpRequestException e)
            {
                return e.Message;
            }
        }, s_readyTimeout, "cdk-mintd answers /v1/info", cancellationToken, TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Removes the mint's node, if one runs.</summary>
    public async Task StopMintAsync(CancellationToken cancellationToken)
    {
        if (_mint is null)
            return;

        await Run.RemoveNodeAsync(CdkNodes.MintName, cancellationToken);
        _mint = null;
    }

    /// <summary>The mint's log (its last <paramref name="lines"/> lines), or why it is unavailable.</summary>
    public async Task<string> GetMintLogAsync(int lines, CancellationToken cancellationToken)
    {
        if (_mint is null)
            return "(no mint)";

        try
        {
            return await _mint.ReadLogAsync(lines, cancellationToken);
        }
        catch (Exception e)
        {
            return $"(unavailable: {e.Message})";
        }
    }

    /// <summary>A fresh wallet work directory in the wallet pod for <see cref="RunWalletAsync"/>.</summary>
    public async Task<string> CreateWalletDirectoryAsync(CancellationToken cancellationToken)
    {
        var directory = $"{CdkNodes.WalletDataPath}/{Guid.NewGuid():N}";
        (await Wallet.ExecAsync(["mkdir", "-p", directory], cancellationToken)).EnsureSuccess($"mkdir {directory}");
        return directory;
    }

    /// <summary>
    /// Runs <c>cdk-cli --work-dir <paramref name="walletDirectory"/> <paramref name="arguments"/></c> to its end in the
    /// wallet pod.
    /// </summary>
    /// <returns>Its standard output and error.</returns>
    public async Task<string> RunWalletAsync(string walletDirectory, IReadOnlyList<string> arguments,
                                             CancellationToken cancellationToken)
    {
        var result = await Wallet.ExecAsync(WalletCommand(walletDirectory, arguments), cancellationToken);
        var output = result.StdOutText + result.StdErrText;
        Console.WriteLine($"[cashu] cdk-cli {string.Join(' ', arguments)} (exit {result.ExitCode}):\n{output}");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"cdk-cli {string.Join(' ', arguments)} exited {result.ExitCode}: {output}");
        return output;
    }

    /// <summary>
    /// Starts <c>cdk-cli</c> in the wallet pod for a command that waits (<c>mint</c> waits for its invoice to be paid),
    /// its output going to a log file next to the wallet; read it with <see cref="GetWalletOutputAsync"/> and
    /// <see cref="WaitWalletExitAsync"/>.
    /// </summary>
    public WalletRun StartWallet(string walletDirectory, IReadOnlyList<string> arguments,
                                 CancellationToken cancellationToken)
    {
        var log = $"{walletDirectory}-{Guid.NewGuid():N}.log";
        var command = string.Join(' ', WalletCommand(walletDirectory, arguments).Select(Quote));
        var exec = Wallet.ExecAsync(["sh", "-c", $"echo $$ > {log}.pid; exec {command} > {log} 2>&1"],
                                    cancellationToken);
        return new WalletRun(log, exec);
    }

    /// <summary>What a background wallet command printed so far (empty before its first line).</summary>
    public async Task<string> GetWalletOutputAsync(WalletRun run, CancellationToken cancellationToken)
    {
        var result = await Wallet.ExecAsync(["sh", "-c", $"cat {run.LogPath} 2>/dev/null || true"],
                                            cancellationToken);
        return result.StdOutText;
    }

    /// <summary>Waits for a background wallet command to end.</summary>
    /// <returns>Its exit code and everything it printed.</returns>
    public async Task<(int ExitCode, string Output)> WaitWalletExitAsync(WalletRun run,
                                                                        CancellationToken cancellationToken)
    {
        var result = await run.Exec.WaitAsync(cancellationToken);
        return (result.ExitCode, await GetWalletOutputAsync(run, cancellationToken));
    }

    /// <summary>Stops a background wallet command that is still running.</summary>
    public async Task StopWalletAsync(WalletRun run, CancellationToken cancellationToken)
    {
        await Wallet.ExecAsync(["sh", "-c", $"kill $(cat {run.LogPath}.pid) 2>/dev/null || true"], cancellationToken);
        try
        {
            await run.Exec.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (Exception e) when (e is TimeoutException or KubeExecException)
        {
            // Best effort: the wallet pod goes with the namespace
        }
    }

    private static List<string> WalletCommand(string walletDirectory, IReadOnlyList<string> arguments)
    {
        List<string> command = ["cdk-cli", "--work-dir", walletDirectory];
        command.AddRange(arguments);
        return command;
    }

    /// <summary>One shell word in single quotes.</summary>
    private static string Quote(string word) => $"'{word.Replace("'", "'\\''")}'";

    /// <summary>A <c>cdk-cli</c> command running in the background in the wallet pod.</summary>
    public sealed record WalletRun(string LogPath, Task<ExecResult> Exec);

    /// <summary>The collection's topology: bitcoind <c>miner</c> (31.1, <c>emptyDir</c>), nothing else.</summary>
    private sealed class CashuTopology : ClusterTopologyFixture
    {
        protected override string Suite => "cashu-mint";

        protected override void Configure(TopologyBuilder builder)
        {
            builder.Storage = NodeStorage.Ephemeral;
            builder.AddBitcoinCore("miner", ImageVersions.BitcoinCore31);
        }
    }
}