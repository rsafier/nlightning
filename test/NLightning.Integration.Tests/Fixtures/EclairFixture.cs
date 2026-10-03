using Docker.DotNet;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;

/// <summary>
/// An Eclair regtest node for the interop tests (NL-180), on its own bitcoind in its own Docker network
/// (<see cref="InteropChainHost"/>), so it never shares containers, names or chain state with the LND or CLN
/// fixtures.
/// </summary>
/// <remarks>
/// <para>ACINQ publishes no pinned multi-arch image of a recent release (NL-553), so the fixture builds
/// <see cref="EclairImage"/> from <c>test/Docker/eclair</c> (the v0.14.3 release zip, sha256-checked, on a pinned
/// Temurin 21 JRE) when the tag is missing (about 15 s).</para>
/// <para>Eclair funds channels from the bitcoind wallet <c>eclair</c>, follows blocks over ZMQ <c>hashblock</c> and
/// answers its JSON API on <c>127.0.0.1</c> (password <see cref="ApiPassword"/>). Its p2p and API ports are published
/// on fixed ports from <see cref="PortPoolUtil"/> (Docker gives a restarted container new random ones), so its address
/// survives <see cref="RestartEclairAsync"/>. Eclair runs with its default channel policy, the <c>to_self_delay</c> of
/// <see cref="EclairDefaultToRemoteDelayBlocks"/> it asks of us included (accepted since NL-550).</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class EclairFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-eclair-net";
    public const string BitcoinContainerName = "nltg-eclair-bitcoind";
    public const string EclairContainerName = "nltg-eclair";

    /// <summary>
    /// The second Eclair of the fixture, configured as a liquidity seller (<see cref="GetSellerAsync"/>, NL-771); it
    /// shares the chain and is removed with the fixture.
    /// </summary>
    public const string SellerContainerName = "nltg-eclair-seller";

    public const string EclairImage = "nltg-eclair";
    public const string EclairTag = "0.14.3";
    public const string ApiPassword = "nltg";

    /// <summary>
    /// Eclair's default <c>eclair.channel.to-remote-delay-blocks</c>: the <c>to_self_delay</c> it asks of us, within
    /// our <c>Node:MaxAcceptedToSelfDelay</c> (2016) since NL-550.
    /// </summary>
    public const int EclairDefaultToRemoteDelayBlocks = 720;

    private const int P2PPort = 9735;
    private const int ApiPort = 8080;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly SharedObjectCache _shared = new();
    private readonly InteropChainHost _chain;

    private readonly SemaphoreSlim _sellerGate = new(1, 1);

    private EclairClient? _eclair;
    private EclairEndpoint? _seller;
    private int _p2pHostPort;
    private int _apiHostPort;
    private int _sellerP2PHostPort;
    private int _sellerApiHostPort;

    public EclairFixture()
    {
        _chain = new InteropChainHost(_client, NetworkName, BitcoinContainerName);
    }

    public RegtestBitcoinEndpoint Bitcoin => _chain.Bitcoin;

    public InteropChainHost Chain => _chain;

    public EclairClient Eclair => _eclair ?? throw new InvalidOperationException("The Eclair fixture is not running");

    /// <summary>Eclair's node id (hex, lower case).</summary>
    public string EclairNodeId { get; private set; } = string.Empty;

    /// <summary>Eclair's p2p port on <c>127.0.0.1</c> (fixed for the fixture's lifetime).</summary>
    public int EclairHostPort => _p2pHostPort;

    /// <summary>The <c>pubkey@127.0.0.1:port</c> an in-process node connects to.</summary>
    public string EclairAddress => $"{EclairNodeId}@127.0.0.1:{_p2pHostPort}";

    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory) where T : class =>
        _shared.GetOrCreateAsync(key, factory);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The fixture's liquidity seller (NL-771): a second Eclair 0.14.3 on the same chain (container
    /// <see cref="SellerContainerName"/>, wallet <c>eclair-seller</c>) whose <c>eclair.liquidity-ads</c> sells at
    /// <see cref="SellerRates"/>, paid from the channel balance only. Started on first use, then shared by the tests of
    /// the collection; <see cref="WaitAllAtTipAsync"/> waits for it too once it runs. Its wallet is funded with
    /// <paramref name="walletSat"/> on first use (Eclair needs confirmed coins to contribute).
    /// </summary>
    public async Task<EclairEndpoint> GetSellerAsync(CancellationToken cancellationToken, long walletSat = 10_000_000)
    {
        await _sellerGate.WaitAsync(cancellationToken);
        try
        {
            if (_seller is not null)
                return _seller;

            _sellerP2PHostPort = await PortPoolUtil.GetAvailablePortAsync();
            _sellerApiHostPort = await PortPoolUtil.GetAvailablePortAsync();
            await DockerContainerUtils.RemoveContainerAsync(_client, SellerContainerName);
            await _chain.CreateWalletAsync("eclair-seller");
            var (client, nodeId) = await StartEclairContainerAsync(SellerContainerName, "eclair-seller",
                                                                   _sellerP2PHostPort, _sellerApiHostPort,
                                                                   SellerRates);
            var seller = new EclairEndpoint(client, nodeId, $"{nodeId}@127.0.0.1:{_sellerP2PHostPort}");
            Console.WriteLine($"[eclair-seller] {seller.Address}: {(await client.GetInfoAsync(cancellationToken))
               .ToJsonString()}");
            _seller = seller;
            await FundEclairWalletAsync(LightningMoney.Satoshis((ulong)walletSat), [], cancellationToken, client);
            return seller;
        }
        finally
        {
            _sellerGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shared.DisposeAll();
        _eclair?.Dispose();
        _seller?.Client.Dispose();
        await DockerContainerUtils.RemoveContainerAsync(_client, SellerContainerName);
        if (_sellerP2PHostPort != 0)
            PortPoolUtil.ReleasePort(_sellerP2PHostPort);
        if (_sellerApiHostPort != 0)
            PortPoolUtil.ReleasePort(_sellerApiHostPort);
        await DockerContainerUtils.RemoveContainerAsync(_client, EclairContainerName);
        await _chain.RemoveAsync();
        if (_p2pHostPort != 0)
            PortPoolUtil.ReleasePort(_p2pHostPort);
        if (_apiHostPort != 0)
            PortPoolUtil.ReleasePort(_apiHostPort);
        _client.Dispose();
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
            var tip = await _chain.GetTipAsync(cancellationToken);
            var eclairHeights = new List<long>();
            var eclairs = extra.Prepend(Eclair).ToList();
            if (_seller is { } seller && !eclairs.Contains(seller.Client))
                eclairs.Add(seller.Client);
            foreach (var eclair in eclairs)
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
        await _chain.MineAsync(blocks, cancellationToken);
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
        await _chain.SendToAddressAsync(address, (long)amount.Satoshi, cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken, eclair == Eclair || eclair == _seller?.Client ? [] : [eclair]);
        await Poll.UntilAsync(async () =>
                                  (await eclair.OnchainBalanceAsync(cancellationToken))["confirmed"]!
                                 .GetValue<long>() >= before + (long)amount.Satoshi,
                              TimeSpan.FromSeconds(60), "Eclair sees its deposit confirmed", cancellationToken);
    }

    /// <summary>
    /// Restarts the Eclair container (its <c>/data</c> is kept) on the same host ports and waits until it is at the tip.
    /// </summary>
    public async Task RestartEclairAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _chain.RestartContainerAsync(EclairContainerName);
        await WaitReadyAsync(EclairContainerName, Eclair);
    }

    private async Task StartAsync()
    {
        await EnsureEclairImageAsync();
        await DockerContainerUtils.RemoveContainerAsync(_client, EclairContainerName);
        await _chain.StartAsync();
        await _chain.CreateWalletAsync("eclair");

        _p2pHostPort = await PortPoolUtil.GetAvailablePortAsync();
        _apiHostPort = await PortPoolUtil.GetAvailablePortAsync();
        var (client, nodeId) = await StartEclairContainerAsync(EclairContainerName, "eclair", _p2pHostPort,
                                                               _apiHostPort);
        _eclair = client;
        EclairNodeId = nodeId;
        Console.WriteLine($"[eclair] {EclairAddress}: {(await client.GetInfoAsync(CancellationToken.None))
           .ToJsonString()}");
    }

    private async Task<(EclairClient Client, string NodeId)> StartEclairContainerAsync(
        string containerName, string wallet, int p2pHostPort, int apiHostPort, EclairSellerRate? sellerRate = null)
    {
        // Both host ports fixed: Docker gives a restarted container new random ones
        var ports = await _chain.StartContainerAsync(
                        $"{EclairImage}:{EclairTag}", containerName, ["JAVA_OPTS=-Xmx512m -Declair.printToConsole=true"], [],
                        [P2PPort, ApiPort],
                        new Dictionary<int, int> { [P2PPort] = p2pHostPort, [ApiPort] = apiHostPort },
                        new Dictionary<string, string>
                        {
                            ["/data/eclair.conf"] = BuildConfig(containerName, wallet, sellerRate)
                        });
        var client = new EclairClient(ports[ApiPort], ApiPassword);
        try
        {
            await WaitReadyAsync(containerName, client);
            var nodeId = (await client.GetInfoAsync(CancellationToken.None))["nodeId"]!.GetValue<string>();
            return (client, nodeId);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task WaitReadyAsync(string containerName, EclairClient client)
    {
        await DockerContainerUtils.WaitUntilReadyAsync(containerName, async ct =>
        {
            var height = await client.GetBlockHeightAsync(ct);
            var tip = await _chain.GetTipAsync(ct);
            if (height != tip)
                throw new InvalidOperationException($"Eclair at {height}, tip {tip}");
        }, s_readyTimeout);
    }

    /// <summary>
    /// The rate the seller Eclair (<see cref="GetSellerAsync"/>) sells at: 10,000 to 5,000,000 sat, a funding weight
    /// of 400, 500 sat + 100 basis points, and 1,000 sat more for a new channel.
    /// </summary>
    public static readonly EclairSellerRate SellerRates = new(10_000, 5_000_000, 400, 500, 100, 1_000);

    /// <summary>
    /// Eclair's configuration, with a liquidity ads seller section when <paramref name="sellerRate"/> is given
    /// (<c>eclair.liquidity-ads</c> of Eclair 0.14.3's <c>reference.conf</c>: <c>funding-rates</c> entries with
    /// <c>min-funding-amount-satoshis</c>, <c>max-funding-amount-satoshis</c>, <c>funding-weight</c>,
    /// <c>fee-base-satoshis</c>, <c>fee-basis-points</c> and <c>channel-creation-fee-satoshis</c>; <c>payment-types</c>;
    /// <c>lock-utxos-during-funding</c>).
    /// </summary>
    internal static string BuildConfig(string containerName, string wallet, EclairSellerRate? sellerRate = null) =>
        BuildBaseConfig(containerName, wallet) + (sellerRate is { } rate ? BuildSellerConfig(rate) : string.Empty);

    internal static string BuildSellerConfig(EclairSellerRate rate) =>
        $$"""

          eclair.liquidity-ads {
            funding-rates = [
              {
                min-funding-amount-satoshis = {{rate.MinFundingSat}}
                max-funding-amount-satoshis = {{rate.MaxFundingSat}}
                funding-weight = {{rate.FundingWeight}}
                fee-base-satoshis = {{rate.FeeBaseSat}}
                fee-basis-points = {{rate.FeeBasisPoints}}
                channel-creation-fee-satoshis = {{rate.ChannelCreationFeeSat}}
              }
            ]
            payment-types = ["from_channel_balance"]
            lock-utxos-during-funding = true
          }
          """;

    private static string BuildBaseConfig(string containerName, string wallet) =>
        $"""
         eclair.chain = "regtest"
         eclair.server.port = {P2PPort}
         eclair.api.enabled = true
         eclair.api.binding-ip = "0.0.0.0"
         eclair.api.port = {ApiPort}
         eclair.api.password = "{ApiPassword}"
         eclair.bitcoind.host = "{BitcoinContainerName}"
         eclair.bitcoind.rpcport = {InteropChainHost.RpcPort}
         eclair.bitcoind.rpcuser = "{InteropChainHost.RpcUser}"
         eclair.bitcoind.rpcpassword = "{InteropChainHost.RpcPassword}"
         eclair.bitcoind.wallet = "{wallet}"
         eclair.bitcoind.zmqblock = "tcp://{BitcoinContainerName}:{InteropChainHost.ZmqHashBlockPort}"
         eclair.bitcoind.zmqtx = "tcp://{BitcoinContainerName}:{InteropChainHost.ZmqTxPort}"
         eclair.node-alias = "{containerName}"
         eclair.channel.min-depth-blocks = 6
         """;

    private async Task EnsureEclairImageAsync()
    {
        if (await InteropChainHost.ImageExistsAsync(_client, $"{EclairImage}:{EclairTag}"))
            return;

        var dockerfileDir = FindDockerDirectory("eclair");
        await BuildImageAsync(_client, dockerfileDir, $"{EclairImage}:{EclairTag}", TimeSpan.FromMinutes(15));
    }

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

        if (!await InteropChainHost.ImageExistsAsync(client, tag))
            throw new InvalidOperationException($"docker build {tag} did not produce the image");
    }
}

/// <summary>An Eclair reachable from the tests: its API client, its node id and its <c>pubkey@127.0.0.1:port</c>.</summary>
public sealed record EclairEndpoint(EclairClient Client, string NodeId, string Address);

/// <summary>One <c>eclair.liquidity-ads.funding-rates</c> entry (NL-771).</summary>
public sealed record EclairSellerRate(
    uint MinFundingSat,
    uint MaxFundingSat,
    ushort FundingWeight,
    uint FeeBaseSat,
    ushort FeeBasisPoints,
    uint ChannelCreationFeeSat);