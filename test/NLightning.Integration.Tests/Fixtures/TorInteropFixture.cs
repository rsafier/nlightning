using System.Net;
using System.Net.Sockets;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace NLightning.Integration.Tests.Fixtures;

using Docker.Utils;
using Domain.Money;
using Domain.Node.Options;
using Infrastructure.Transport.Tor;

/// <summary>
/// The Tor interop topology (NL-572): its own bitcoind, a C Tor client on the public Tor network and a Core Lightning
/// node reachable only through its onion service, in a Docker network of their own (no container, name or chain
/// state shared with the other fixtures).
/// </summary>
/// <remarks>
/// <para>Tor (<see cref="TorContainerName"/>, built from <c>test/Docker/tor</c> as <see cref="TorImage"/> when the tag
/// is missing) publishes its SOCKS5 port and its control port (password <see cref="ControlPassword"/>) on
/// <c>127.0.0.1</c> for the in-process NLightning nodes, and hosts CLN's onion service from <c>torrc</c>
/// (<c>HiddenServiceDir</c>, port 9735 to <c>127.0.0.1:9735</c>). CLN (<see cref="ClnContainerName"/>, the image of
/// <see cref="ClnFixture"/>) shares the Tor container's network namespace and listens on <c>127.0.0.1:9735</c> only,
/// so the only way in is the onion service; it dials onions through the same Tor (<c>--proxy=127.0.0.1:9050</c>).
/// The onion services live on the public Tor network, so the fixture needs Internet access (a private Tor network,
/// chutney, would make it hermetic; not done).</para>
/// <para>Tor runs as a child of the container's shell, so <see cref="RestartTorAsync"/> kills it and the shell starts
/// it again without stopping the container (CLN keeps its network namespace) and with the onion service's key kept.</para>
/// </remarks>
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class TorInteropFixture : IAsyncLifetime
{
    public const string NetworkName = "nltg-tor-net";
    public const string BitcoinContainerName = "nltg-tor-bitcoind";
    public const string TorContainerName = "nltg-tor";
    public const string ClnContainerName = "nltg-tor-cln";

    public const string TorImage = "nltg-tor";
    public const string TorImageTag = "alpine3.22";
    public const string ControlPassword = "nltg-tor-control";

    /// <summary>The virtual port of CLN's onion service and of ours.</summary>
    public const int OnionPort = 9735;

    private const int SocksPort = 9050;
    private const int ControlPort = 9051;

    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a fresh onion service may take to become reachable (descriptor published to the HSDirs, introduction
    /// points up): usually under a minute on the public network.
    /// </summary>
    private static readonly TimeSpan s_onionReachableTimeout = TimeSpan.FromMinutes(4);

    private readonly DockerClient _client = new DockerClientConfiguration().CreateClient();
    private readonly SharedObjectCache _shared = new();
    private readonly InteropChainHost _chain;

    private ClnClient? _cln;

    public TorInteropFixture()
    {
        _chain = new InteropChainHost(_client, NetworkName, BitcoinContainerName);
    }

    public RegtestBitcoinEndpoint Bitcoin => _chain.Bitcoin;

    public ClnClient Cln => _cln ?? throw new InvalidOperationException("The Tor interop fixture is not running");

    /// <summary>CLN's node id (hex, lower case).</summary>
    public string ClnNodeId { get; private set; } = string.Empty;

    /// <summary>CLN's onion service (<c>&lt;56 chars&gt;.onion</c>).</summary>
    public string ClnOnionHost { get; private set; } = string.Empty;

    /// <summary>The <c>pubkey@onion:9735</c> an in-process node dials through Tor.</summary>
    public string ClnOnionAddress => $"{ClnNodeId}@{ClnOnionHost}:{OnionPort}";

    /// <summary>Tor's SOCKS5 port on <c>127.0.0.1</c>.</summary>
    public int SocksHostPort { get; private set; }

    /// <summary>Tor's control port on <c>127.0.0.1</c>.</summary>
    public int ControlHostPort { get; private set; }

    /// <summary>
    /// The address the Tor container reaches the host at (<c>host.docker.internal</c> resolved inside it): the target of
    /// our onion service, since Tor's own loopback is not the host's.
    /// </summary>
    public string HostAddressFromTor { get; private set; } = string.Empty;

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

    public async ValueTask DisposeAsync()
    {
        _shared.DisposeAll();
        await DockerContainerUtils.RemoveContainerAsync(_client, ClnContainerName);
        await DockerContainerUtils.RemoveContainerAsync(_client, TorContainerName);
        await _chain.RemoveAsync();
        _client.Dispose();
    }

    /// <summary>
    /// Points <paramref name="options"/> at the fixture's Tor in <paramref name="mode"/>. With
    /// <paramref name="onionService"/> the node also registers its onion service through the control port (password),
    /// targeted at <see cref="HostAddressFromTor"/>:<paramref name="listenPort"/>, and listens on every interface so the
    /// Tor container can reach it (<c>AllowClearnetListen</c>: in <see cref="TorMode.TorOnly"/> a non-loopback
    /// listener is refused without it). The reconnect backoff is capped at 10 s, so a redial after a Tor restart does
    /// not wait minutes.
    /// </summary>
    public void ConfigureTor(NodeOptions options, TorMode mode, bool onionService, int listenPort,
                             string onionKeyFile)
    {
        options.Tor.Mode = mode;
        options.Tor.SocksProxy = $"127.0.0.1:{SocksHostPort}";
        options.Tor.Control = $"127.0.0.1:{ControlHostPort}";
        options.Tor.ControlPassword = ControlPassword;
        options.Tor.ConnectTimeout = TimeSpan.FromSeconds(90);
        options.Tor.OnionServiceEnabled = onionService;
        options.Tor.OnionServicePort = OnionPort;
        options.Tor.OnionServiceKeyFile = onionKeyFile;
        options.ReconnectMaxDelay = TimeSpan.FromSeconds(10);
        if (!onionService)
            return;

        options.Tor.OnionServiceTarget = $"{HostAddressFromTor}:{listenPort}";
        options.Tor.AllowClearnetListen = true;
        options.ListenAddresses = options.ListenAddresses.Select(a => a.Replace("127.0.0.1", "0.0.0.0")).ToList();
    }

    /// <summary>Waits until CLN and every node in <paramref name="nodes"/> have processed bitcoind's tip.</summary>
    public async Task<uint> WaitAllAtTipAsync(IEnumerable<NLightningTestNode> nodes,
                                              CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var nodeList = nodes.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (true)
        {
            var tip = await _chain.GetTipAsync(cancellationToken);
            var clnHeight = (uint)(await Cln.GetInfoAsync(cancellationToken))["blockheight"]!.GetValue<long>();
            var ours = nodeList.Select(n => (n.Name,
                                             Height: n.IsRunning ? n.BlockchainMonitor.LastProcessedBlockHeight : 0))
                               .ToList();
            if (clnHeight == tip && ours.All(o => o.Height == tip))
                return tip;

            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Not at the tip {tip} in time: cln {clnHeight}, "
                                         + string.Join(", ", ours.Select(o => $"{o.Name} {o.Height}")));

            await Task.Delay(200, cancellationToken);
        }
    }

    public async Task<uint> MineAndWaitAsync(int blocks, IEnumerable<NLightningTestNode> nodes,
                                             CancellationToken cancellationToken)
    {
        await _chain.MineAsync(blocks, cancellationToken);
        return await WaitAllAtTipAsync(nodes, cancellationToken);
    }

    /// <summary>
    /// Sends <paramref name="amount"/> to a new CLN address, mines 6 blocks and waits until CLN lists it confirmed.
    /// </summary>
    public async Task FundClnWalletAsync(LightningMoney amount, IEnumerable<NLightningTestNode> nodes,
                                         CancellationToken cancellationToken)
    {
        var address = (await Cln.CallAsync("newaddr", cancellationToken, ("addresstype", "bech32")))["bech32"]!
           .GetValue<string>();
        var txId = await _chain.SendToAddressAsync(address, (long)amount.Satoshi, cancellationToken);
        await MineAndWaitAsync(6, nodes, cancellationToken);
        await Poll.UntilAsync(async () =>
        {
            var outputs = (await Cln.CallAsync("listfunds", cancellationToken))["outputs"]!.AsArray();
            return outputs.Any(o => o?["txid"]?.GetValue<string>() == txId.ToString()
                                 && o["status"]?.GetValue<string>() == "confirmed");
        }, TimeSpan.FromSeconds(60), "CLN sees its deposit confirmed", cancellationToken);
    }

    /// <summary>
    /// Kills the Tor process (SIGKILL: every circuit, SOCKS stream and control connection drops at once, as in a crash)
    /// and waits until the restarted Tor has bootstrapped and CLN's onion service is reachable again.
    /// </summary>
    public async Task RestartTorAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [tor] killing tor");
        await ExecAsync(TorContainerName, ["pkill", "-KILL", "-x", "tor"], cancellationToken);
        // The container's shell starts it again after 1 s
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        await WaitTorReadyAsync(cancellationToken);
    }

    /// <summary>
    /// Opens a SOCKS5 stream through Tor to <paramref name="onionHost"/>:<see cref="OnionPort"/> and closes it: true
    /// when Tor could build the rendezvous (the service is published and its target accepted the connection).
    /// </summary>
    public async Task<bool> IsOnionReachableAsync(string onionHost, CancellationToken cancellationToken)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, SocksHostPort, attempt.Token);
            // Fresh credentials: a stream of its own (IsolateSOCKSAuth), never a circuit a failed attempt left
            var isolation = Guid.NewGuid().ToString("N");
            await Socks5Client.ConnectAsync(tcp.GetStream(), onionHost, OnionPort, (isolation, isolation),
                                            attempt.Token);
            return true;
        }
        catch (Exception e) when (e is Socks5Exception or SocketException or IOException
                                       or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [tor] {onionHost} not reachable yet: {e.Message}");
            return false;
        }
    }

    /// <summary>Waits until <paramref name="onionHost"/> is reachable through Tor (<see cref="IsOnionReachableAsync"/>).</summary>
    public Task WaitOnionReachableAsync(string onionHost, CancellationToken cancellationToken) =>
        Poll.UntilAsync(() => IsOnionReachableAsync(onionHost, cancellationToken), s_onionReachableTimeout,
                        $"{onionHost} reachable through Tor", cancellationToken, TimeSpan.FromSeconds(2));

    /// <summary>The last <paramref name="lines"/> lines of Tor's log (for failure messages).</summary>
    public async Task<string> GetTorLogAsync(int lines, CancellationToken cancellationToken)
    {
        try
        {
            return await GetContainerLogTailAsync(TorContainerName, lines, cancellationToken);
        }
        catch (Exception e)
        {
            return $"(tor log unavailable: {e.Message})";
        }
    }

    private async Task StartAsync()
    {
        await EnsureTorImageAsync();
        await InteropChainHost.EnsureImageAsync(_client, ClnFixture.ClnImage, ClnFixture.ClnTag);
        await DockerContainerUtils.RemoveContainerAsync(_client, ClnContainerName);
        await DockerContainerUtils.RemoveContainerAsync(_client, TorContainerName);
        await _chain.StartAsync();

        // Tor
        var torPorts = await _chain.StartContainerAsync($"{TorImage}:{TorImageTag}", TorContainerName,
                                                        [$"TOR_CONTROL_PASSWORD={ControlPassword}"], [],
                                                        [SocksPort, ControlPort]);
        SocksHostPort = torPorts[SocksPort];
        ControlHostPort = torPorts[ControlPort];
        await WaitTorBootstrappedAsync(CancellationToken.None);
        ClnOnionHost = (await ExecAsync(TorContainerName, ["cat", "/tor/peer/hostname"], CancellationToken.None))
           .Trim();
        HostAddressFromTor = (await ExecAsync(TorContainerName, ["getent", "hosts", "host.docker.internal"],
                                              CancellationToken.None))
                            .Split([' ', '\t', '\n'], StringSplitOptions.RemoveEmptyEntries)
                            .FirstOrDefault()
                          ?? throw new InvalidOperationException("host.docker.internal does not resolve in the Tor "
                                                               + "container");
        Console.WriteLine($"[tor] up: SOCKS 127.0.0.1:{SocksHostPort}, control 127.0.0.1:{ControlHostPort}, "
                        + $"CLN onion {ClnOnionHost}, host from Tor {HostAddressFromTor}");

        // CLN, in Tor's network namespace: it reaches bitcoind by name, Tor at 127.0.0.1, and nothing reaches it but
        // its onion service
        var cln = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = $"{ClnFixture.ClnImage}:{ClnFixture.ClnTag}",
            Name = ClnContainerName,
            Env = ["LIGHTNINGD_NETWORK=regtest"],
            Cmd =
            [
                $"--bitcoin-rpcconnect={BitcoinContainerName}",
                $"--bitcoin-rpcport={InteropChainHost.RpcPort}",
                $"--bitcoin-rpcuser={InteropChainHost.RpcUser}",
                $"--bitcoin-rpcpassword={InteropChainHost.RpcPassword}",
                $"--bind-addr=127.0.0.1:{OnionPort}",
                $"--proxy=127.0.0.1:{SocksPort}",
                "--alias=nltg-tor-cln",
                "--log-level=debug",
                "--developer",
                "--dev-bitcoind-poll=1",
                "--ignore-fee-limits=false"
            ],
            HostConfig = new HostConfig { NetworkMode = $"container:{TorContainerName}" }
        });
        await _client.Containers.StartContainerAsync(cln.ID, new ContainerStartParameters());
        var client = new ClnClient(_client, ClnContainerName);
        await DockerContainerUtils.WaitUntilReadyAsync(ClnContainerName, async ct => await client.GetInfoAsync(ct),
                                                       s_readyTimeout);
        _cln = client;
        ClnNodeId = (await client.GetInfoAsync(CancellationToken.None))["id"]!.GetValue<string>();
        await WaitAllAtTipAsync([], CancellationToken.None);

        // CLN's onion service is published once Tor is up, and reaches CLN once CLN listens
        await WaitOnionReachableAsync(ClnOnionHost, CancellationToken.None);
        Console.WriteLine($"[tor] CLN {ClnOnionAddress} reachable through Tor");
    }

    /// <summary>Waits until Tor answers on its control port and its bootstrap is done, then until CLN's onion answers.</summary>
    private async Task WaitTorReadyAsync(CancellationToken cancellationToken)
    {
        await WaitTorBootstrappedAsync(cancellationToken);
        await WaitOnionReachableAsync(ClnOnionHost, cancellationToken);
    }

    private async Task WaitTorBootstrappedAsync(CancellationToken cancellationToken)
    {
        var phase = string.Empty;
        try
        {
            await Poll.UntilAsync(async () =>
            {
                try
                {
                    await using var control = await TorControlClient.ConnectAsync(
                                                  new IPEndPoint(IPAddress.Loopback, ControlHostPort),
                                                  cancellationToken);
                    var info = await control.GetProtocolInfoAsync(cancellationToken);
                    await control.AuthenticateAsync(info, ControlPassword, null, false, cancellationToken);
                    phase = await control.GetInfoAsync("status/bootstrap-phase", cancellationToken);
                    return phase.Contains("PROGRESS=100", StringComparison.Ordinal);
                }
                catch (Exception e) when (e is SocketException or IOException or TorControlException)
                {
                    phase = e.Message;
                    return false;
                }
            }, s_readyTimeout, "Tor bootstrapped", cancellationToken, TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message}; last phase: {phase}\n"
                                     + await GetContainerLogTailAsync(TorContainerName, 40, cancellationToken), e);
        }
    }

    private async Task EnsureTorImageAsync()
    {
        if (await InteropChainHost.ImageExistsAsync(_client, $"{TorImage}:{TorImageTag}"))
            return;

        await EclairFixture.BuildImageAsync(_client, EclairFixture.FindDockerDirectory("tor"),
                                            $"{TorImage}:{TorImageTag}", TimeSpan.FromMinutes(5));
    }

    private async Task<string> ExecAsync(string container, IList<string> cmd, CancellationToken cancellationToken)
    {
        var exec = await _client.Exec.ExecCreateContainerAsync(container, new ContainerExecCreateParameters
        {
            Cmd = cmd,
            AttachStdout = true,
            AttachStderr = true
        }, cancellationToken);
        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(exec.ID, false, cancellationToken);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);
        var inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, cancellationToken);
        if (inspect.ExitCode != 0)
            throw new InvalidOperationException($"{container}: {string.Join(' ', cmd)} exited {inspect.ExitCode}: "
                                              + $"{stdout} {stderr}".Trim());

        return stdout;
    }

    private async Task<string> GetContainerLogTailAsync(string container, int lines,
                                                        CancellationToken cancellationToken)
    {
        using var stream = await _client.Containers.GetContainerLogsAsync(
                               container, false,
                               new ContainerLogsParameters
                               {
                                   ShowStdout = true,
                                   ShowStderr = true,
                                   Tail = lines.ToString(System.Globalization.CultureInfo.InvariantCulture)
                               }, cancellationToken);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);
        return stdout + stderr;
    }
}