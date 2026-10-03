using System.Diagnostics;
using System.Text;
using Docker.DotNet;

namespace NLightning.Integration.Tests.Fixtures.Cashu;

using Docker.Utils;
using Tor;

/// <summary>
/// The Cashu mint proof's topology (Cashu plan C2, NL-903): its own bitcoind (<see cref="TorChainHost"/> on network
/// <see cref="NetworkName"/>), CDK's mint daemon <c>cdk-mintd</c> (<see cref="MintdImage"/>) and CDK's wallet CLI
/// <c>cdk-cli</c> (<see cref="CliImage"/>, built from <c>test/Docker/cdk-cli</c> when the tag is missing; CDK publishes
/// no image of it). It runs on Docker only and its tests skip under <c>NLTG_TEST_BACKEND=cluster</c>; run it with
/// <c>scripts/run-interop.sh cashu</c>.
/// </summary>
/// <remarks>
/// <para>The mint and the wallet run with the host's network (<c>--network host</c>): the mint reaches the in-process
/// node's CDK payment processor on <c>127.0.0.1</c> over plain HTTP/2, which the processor allows on loopback only,
/// and the wallet and the test reach the mint on <c>127.0.0.1</c>. Docker Desktop and OrbStack need host networking
/// turned on.</para>
/// <para>The mint is configured through <c>cdk-mintd config init</c> (CDK 0.18 keeps its configuration in its
/// database) with <c>backend = "grpcprocessor"</c> in sat. Each test starts its own mint (<see cref="StartMintAsync"/>)
/// because the processor's port belongs to the test's node.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class CashuMintFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-cashu-net";
    public const string BitcoinContainerName = "nltg-cashu-bitcoind";
    public const string MintdContainerName = "nltg-cashu-mintd";
    public const string WalletContainerPrefix = "nltg-cashu-wallet";

    /// <summary>The CDK release of the mint, the wallet and the vendored processor proto.</summary>
    public const string CdkVersion = "0.18.1";

    public const string MintdRepository = "cashubtc/mintd";
    public const string MintdImage = $"{MintdRepository}:{CdkVersion}";
    public const string CliImage = $"nltg-cdk-cli:{CdkVersion}";

    /// <summary>The BIP-39 test mnemonic of the mint's keysets (regtest only).</summary>
    private const string MintMnemonic =
        "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static readonly TimeSpan s_cliBuildTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan s_mintReadyTimeout = TimeSpan.FromMinutes(2);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly TorChainHost _chain;
    private readonly List<string> _directories = [];

    public CashuMintFixture()
    {
        _chain = new TorChainHost(_client, NetworkName, BitcoinContainerName);
    }

    public RegtestBitcoinEndpoint Bitcoin => _chain.Bitcoin;

    /// <summary>The mint's URL once <see cref="StartMintAsync"/> ran.</summary>
    public string MintUrl { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (TestBackend.IsCluster)
            return;

        await DockerContainerUtils.EnsureImageAsync(_client, MintdRepository, CdkVersion);
        if (!await DockerContainerUtils.ImageExistsAsync(_client, CliImage))
            await DockerContainerUtils.BuildImageAsync(_client, DockerContainerUtils.FindDockerDirectory("cdk-cli"),
                                                       CliImage, s_cliBuildTimeout);
        await _chain.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!TestBackend.IsCluster)
        {
            await StopMintAsync();
            await _chain.RemoveAsync();
        }

        foreach (var directory in _directories)
        {
            try
            {
                Directory.Delete(directory, true);
            }
            catch (IOException)
            {
                // best effort (files the containers wrote as root)
            }
            catch (UnauthorizedAccessException)
            {
                // best effort
            }
        }

        _client.Dispose();
    }

    /// <summary>Mines <paramref name="blocks"/> blocks to the miner wallet.</summary>
    public Task MineAsync(int blocks, CancellationToken cancellationToken) => _chain.MineAsync(blocks, cancellationToken);

    /// <summary>The chain tip.</summary>
    public Task<uint> GetTipAsync(CancellationToken cancellationToken) => _chain.GetTipAsync(cancellationToken);

    /// <summary>
    /// Starts <c>cdk-mintd</c> (replacing a running one) on <c>127.0.0.1:<paramref name="mintPort"/></c>, backed by
    /// the CDK payment processor on <c>127.0.0.1:<paramref name="processorPort"/></c>, and waits until it answers
    /// <c>/v1/info</c>. The mint's database is fresh.
    /// </summary>
    public async Task StartMintAsync(int processorPort, int mintPort, CancellationToken cancellationToken)
    {
        await StopMintAsync();
        var directory = CreateDirectory("mintd");
        MintUrl = $"http://127.0.0.1:{mintPort}";
        await File.WriteAllTextAsync(Path.Combine(directory, "config.toml"), $"""
            [info]
            url = "{MintUrl}/"
            listen_host = "127.0.0.1"
            listen_port = {mintPort}
            mnemonic = "env:CDK_MINTD_MNEMONIC"

            [database]
            engine = "sqlite"

            [payment_backend]
            backend = "grpcprocessor"
            unit = "sat"

            [grpc_processor]
            address = "127.0.0.1"
            port = {processorPort}
            supported_units = ["sat"]
            # cdk-mintd 0.18 refuses a processor without TLS unless told so, loopback included
            allow_insecure = true
            """, cancellationToken);

        // The configuration goes into the mint's database once, so a restart (RestartMintAsync) keeps the mint
        await DockerAsync(["run", "-d", "--name", MintdContainerName, "--network", "host",
                           "-v", $"{directory}:/data", "-e", $"CDK_MINTD_MNEMONIC={MintMnemonic}", MintdImage,
                           "sh", "-c",
                           "{ [ -f /data/.initialized ] || { cdk-mintd -w /data config init --file /data/config.toml "
                         + "--new-mint && touch /data/.initialized; }; } && exec cdk-mintd -w /data --enable-logging"],
                          cancellationToken);
        await WaitForMintAsync();
        Console.WriteLine($"[cashu] cdk-mintd {CdkVersion} up at {MintUrl}, processor 127.0.0.1:{processorPort}");
    }

    /// <summary>
    /// Stops <c>cdk-mintd</c> and starts it again over the same database (its quotes, keysets and the processor's
    /// address), and waits until it answers.
    /// </summary>
    public async Task RestartMintAsync(CancellationToken cancellationToken)
    {
        await DockerAsync(["stop", "-t", "10", MintdContainerName], cancellationToken);
        await DockerAsync(["start", MintdContainerName], cancellationToken);
        await WaitForMintAsync();
        Console.WriteLine($"[cashu] cdk-mintd restarted at {MintUrl}");
    }

    /// <summary>GETs <paramref name="path"/> on the mint (e.g. <c>/v1/melt/quote/onchain/{id}</c>).</summary>
    public async Task<string> GetFromMintAsync(string path, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        return await http.GetStringAsync($"{MintUrl}{path}", cancellationToken);
    }

    private async Task WaitForMintAsync()
    {
        using var http = new HttpClient();
        await DockerContainerUtils.WaitUntilReadyAsync(MintdContainerName, async ct =>
        {
            using var response = await http.GetAsync($"{MintUrl}/v1/info", ct);
            response.EnsureSuccessStatusCode();
        }, s_mintReadyTimeout);
    }

    /// <summary>Removes the mint container.</summary>
    public Task StopMintAsync() => DockerContainerUtils.RemoveContainerAsync(_client, MintdContainerName);

    /// <summary>The mint's log (its last <paramref name="lines"/> lines).</summary>
    public async Task<string> GetMintLogAsync(int lines, CancellationToken cancellationToken) =>
        (await DockerAsync(["logs", "--tail", lines.ToString(), MintdContainerName], cancellationToken,
                           throwOnError: false)).Output;

    /// <summary>A fresh wallet directory for <see cref="RunWalletAsync"/>.</summary>
    public string CreateWalletDirectory() => CreateDirectory("wallet");

    /// <summary>
    /// Runs <c>cdk-cli --work-dir /wallet <paramref name="arguments"/></c> to its end in a throwaway container on the
    /// host's network, with <paramref name="walletDirectory"/> as its wallet.
    /// </summary>
    /// <returns>Its standard output and error.</returns>
    public async Task<string> RunWalletAsync(string walletDirectory, IReadOnlyList<string> arguments,
                                             CancellationToken cancellationToken)
    {
        var (exitCode, output) = await DockerAsync(WalletRun(walletDirectory, arguments, detach: false),
                                                   cancellationToken, throwOnError: false);
        Console.WriteLine($"[cashu] cdk-cli {string.Join(' ', arguments)} (exit {exitCode}):\n{output}");
        if (exitCode != 0)
            throw new InvalidOperationException($"cdk-cli {string.Join(' ', arguments)} exited {exitCode}: {output}");
        return output;
    }

    /// <summary>
    /// Starts <c>cdk-cli --work-dir /wallet <paramref name="arguments"/></c> in the background (a command that waits,
    /// such as <c>mint</c> waiting for its invoice to be paid); read it with <see cref="GetWalletOutputAsync"/> and
    /// <see cref="WaitWalletExitAsync"/>.
    /// </summary>
    /// <returns>The container's name.</returns>
    public async Task<string> StartWalletAsync(string walletDirectory, IReadOnlyList<string> arguments,
                                               CancellationToken cancellationToken)
    {
        var name = $"{WalletContainerPrefix}-{Guid.NewGuid():N}"[..40];
        await DockerAsync(WalletRun(walletDirectory, arguments, detach: true, name), cancellationToken);
        return name;
    }

    /// <summary>What a background wallet command printed so far.</summary>
    public async Task<string> GetWalletOutputAsync(string container, CancellationToken cancellationToken) =>
        (await DockerAsync(["logs", container], cancellationToken, throwOnError: false)).Output;

    /// <summary>Waits for a background wallet command to end and removes its container.</summary>
    /// <returns>Its exit code and everything it printed.</returns>
    public async Task<(int ExitCode, string Output)> WaitWalletExitAsync(string container,
                                                                        CancellationToken cancellationToken)
    {
        var (_, status) = await DockerAsync(["wait", container], cancellationToken);
        var output = await GetWalletOutputAsync(container, cancellationToken);
        await DockerContainerUtils.RemoveContainerAsync(_client, container);
        return (int.Parse(status.Trim()), output);
    }

    private static List<string> WalletRun(string walletDirectory, IReadOnlyList<string> arguments, bool detach,
                                          string? name = null)
    {
        List<string> args = ["run"];
        if (detach)
            args.AddRange(["-d", "--name", name!]);
        else
            args.Add("--rm");
        args.AddRange(["--network", "host", "-v", $"{walletDirectory}:/wallet", CliImage, "--work-dir", "/wallet"]);
        args.AddRange(arguments);
        return args;
    }

    private string CreateDirectory(string kind)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nltg-cashu-{kind}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        _directories.Add(directory);
        return directory;
    }

    private static async Task<(int ExitCode, string Output)> DockerAsync(IReadOnlyList<string> arguments,
                                                                        CancellationToken cancellationToken,
                                                                        bool throwOnError = true)
    {
        var info = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start docker");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = new StringBuilder(await stdout).Append(await stderr).ToString();
        if (throwOnError && process.ExitCode != 0)
            throw new InvalidOperationException($"docker {arguments[0]} exited {process.ExitCode}: {output}");
        return (process.ExitCode, output);
    }
}