using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLightning.Tests.Utils;

namespace NLightning.Integration.Tests.Cluster.Live;

using Docker.Utils;
using Domain.Client.Requests;
using Domain.Money;
using Fixtures;
using LndGrpc;
using Testing.Cluster.Images;
using Testing.Cluster.Run;
using Testing.Cluster.Runner;
using Testing.Lnd.Lnrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>Real Loop PR 1222, Aperture L402 and LND 0.21.4 against our TLS/macaroon gRPC listener.</summary>
[Trait("Category", "Cluster")]
public partial class LoopClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);
    private static void Log(string value) => TestContext.Current.TestOutputHelper?.WriteLine(value);

    [Fact(Explicit = true)]
    public async Task Given_RealLoopAndOurGrpc_When_SwappingOutInAndStaticIn_Then_AllThreeSwapsSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")))
        {
            // Use the harness's owned namespace and RBAC; no host-to-pod route is required.
            await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("loop"), ct);
            var image = Environment.GetEnvironmentVariable("NLTG_LOOP_RUNNER_IMAGE") ?? "nltg-loop-runner:latest";
            var job = new TestRunnerJob
            {
                Image = new ImageRef(image[..image.LastIndexOf(':')], image[(image.LastIndexOf(':') + 1)..], PullPolicy: ImagePullPolicy.Never),
                Assembly = "NLightning.Integration.Tests.dll",
                ActiveDeadline = TimeSpan.FromMinutes(25)
            };
            job.Env["NLTG_TEST_BACKEND"] = "cluster";
            job.Env["NLTG_LOOP_BIN"] = "/loop-bin";
            if (Environment.GetEnvironmentVariable("NLTG_KEEP_NAMESPACE") is { } keep)
                job.Env["NLTG_KEEP_NAMESPACE"] = keep;
            foreach (var arg in new[] { "-explicit", "only", "-class", GetType().FullName!, "-showLiveOutput", "-noColor" })
                job.TestArguments.Add(arg);
            var result = await InClusterTestRunner.RunAsync(run, job, Log, ct);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("All three real Loop swap flows succeeded", result.Log);
            return;
        }

        var bin = Environment.GetEnvironmentVariable("NLTG_LOOP_BIN") ?? throw new InvalidOperationException("NLTG_LOOP_BIN required");
        foreach (var name in new[] { "loop", "loopd", "loopserver-regtest", "aperture" })
            Assert.True(File.Exists(Path.Combine(bin, name)), $"missing {name}; build the pinned Loop runner image");
        var directory = Path.Combine(Path.GetTempPath(), "nltg-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var processes = new List<Child>();
        var fixture = new LightningRegtestNetworkFixture();
        NLightningTestNode? node = null;
        LndGrpcHost? host = null;
        try
        {
            await fixture.InitializeAsync();
            var alice = fixture.GetLndNode("alice");
            node = await NLightningTestNode.CreateAsync(fixture, "loop");
            var grpc = Path.Combine(directory, "grpc");
            node.ExtraConfiguration["LndGrpc:Enabled"] = "true";
            node.ExtraConfiguration["LndGrpc:EnableSigner"] = "true";
            node.ExtraConfiguration["LndGrpc:Port"] = (await PortPoolUtil.GetAvailablePortAsync()).ToString(System.Globalization.CultureInfo.InvariantCulture);
            node.ExtraConfiguration["LndGrpc:ListenAddress"] = "127.0.0.1";
            node.ExtraConfiguration["LndGrpc:DataDirectory"] = grpc;
            node.ConfigureServices = services =>
            {
                services.AddLndGrpcHost(grpc);
                // Thousands of expiry blocks should exercise chain processing without logging every SQL query.
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information)
                    .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
            };
            await node.StartAsync(ct);
            host = node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
            await host.StartAsync(ct);
            await node.FundWalletAsync(LightningMoney.Satoshis(8_000_000), AddressType.P2Wpkh, ct);
            await node.ConnectToAsync(alice, ct);
            await node.OpenChannelAsync(new OpenChannelClientRequest($"{alice.LocalNodePubKey}@{await fixture.GetLndPeerEndpointAsync(alice, ct)}", LightningMoney.Satoshis(5_000_000))
            {
                ForceV1 = true,
                PushAmount = LightningMoney.Satoshis(2_000_000)
            }, cancellationToken: ct);
            await ChainSync.MineAndWaitAsync(fixture, 6, [alice], [node], ct);
            await ClusterPoll.UntilAsync(async c => (await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: c))
                .Channels.Any(ch => ch.Active && ch.RemotePubkey == node.NodeIdHex), s_timeout, ClusterPoll.DefaultInterval, "Loop channel active", ct);

            await AssertClientPaymentGapsAsync(alice, node, host, grpc, ct);

            var lnd = Path.Combine(directory, "lnd");
            Directory.CreateDirectory(lnd);
            await File.WriteAllBytesAsync(Path.Combine(lnd, "tls.cert"), alice.Settings.TlsCert!, ct);
            await File.WriteAllBytesAsync(Path.Combine(lnd, "admin.macaroon"), alice.Settings.Macaroon!, ct);
            await File.WriteAllBytesAsync(Path.Combine(lnd, "invoice.macaroon"), alice.Settings.Macaroon!, ct);
            var serverPort = await PortPoolUtil.GetAvailablePortAsync();
            var aperturePort = await PortPoolUtil.GetAvailablePortAsync();
            var loopPort = await PortPoolUtil.GetAvailablePortAsync();
            var aliceHost = $"alice:{new Uri(alice.Settings.GrpcEndpoint!).Port}";
            processes.Add(new Child(bin, "loopserver-regtest", directory,
                $"--listen=127.0.0.1:{serverPort}", $"--tls.certpath={grpc}/tls.cert", $"--tls.keypath={grpc}/tls.key",
                $"--lnd.host={aliceHost}",
                $"--lnd.tlspath={lnd}/tls.cert", $"--lnd.macaroonpath={lnd}/admin.macaroon",
                $"--bitcoin.host={fixture.Bitcoin.Address.Authority}",
                $"--bitcoin.user={fixture.Bitcoin.CredentialString.UserPassword.UserName}",
                $"--bitcoin.password={fixture.Bitcoin.CredentialString.UserPassword.Password}"));
            var aperture = Path.Combine(directory, "aperture");
            Directory.CreateDirectory(aperture);
            await File.WriteAllTextAsync(Path.Combine(aperture, "aperture.yaml"), $"""
                listenaddr: '127.0.0.1:{aperturePort}'
                servername: localhost
                autocert: false
                insecure: false
                debuglevel: info
                writetimeout: 0s
                dbbackend: sqlite
                authenticator:
                  lndhost: '{aliceHost}'
                  tlspath: '{lnd}/tls.cert'
                  macdir: '{lnd}'
                  network: regtest
                services:
                  - name: loop
                    hostregexp: '^.*$'
                    pathregexp: '^/looprpc.*$'
                    address: '127.0.0.1:{serverPort}'
                    protocol: https
                    tlscertpath: '{grpc}/tls.cert'
                    price: 1000
                    authwhitelistpaths:
                      - '^/looprpc.SwapServer/LoopOutTerms.*$'
                      - '^/looprpc.SwapServer/LoopOutQuote.*$'
                      - '^/looprpc.SwapServer/LoopInTerms.*$'
                      - '^/looprpc.SwapServer/LoopInQuote.*$'
                """, ct);
            processes.Add(new Child(bin, "aperture", directory, $"--basedir={aperture}",
                $"--configfile={aperture}/aperture.yaml", $"--sqlite.dbfile={aperture}/aperture.db"));
            await ClusterPoll.UntilAsync(_ =>
            {
                foreach (var child in processes) child.CheckRunning();
                return Task.FromResult(File.Exists(Path.Combine(aperture, "tls.cert")));
            },
                s_timeout, ClusterPoll.DefaultInterval, "Aperture TLS certificate", ct);
            var loop = Path.Combine(directory, "loop");
            processes.Add(new Child(bin, "loopd", directory, $"--loopdir={loop}", "--network=regtest", "--experimental",
                $"--rpclisten=127.0.0.1:{loopPort}", $"--restlisten=127.0.0.1:{await PortPoolUtil.GetAvailablePortAsync()}",
                $"--server.host=127.0.0.1:{aperturePort}", $"--server.tlspath={aperture}/tls.cert",
                $"--lnd.host=127.0.0.1:{host.BoundPort}", $"--lnd.tlspath={grpc}/tls.cert",
                $"--lnd.macaroonpath={grpc}/admin.macaroon", "--debuglevel=debug"));
            var cliArgs = new[] { $"--loopdir={loop}", "--network=regtest", $"--rpcserver=127.0.0.1:{loopPort}" };
            async Task<string> Cli(params string[] args) => await Child.Run(bin, "loop", directory, [.. cliArgs, .. args], ct);
            async Task<bool> Ready(CancellationToken c)
            {
                foreach (var child in processes) child.CheckRunning();
                try { await Cli("getinfo"); return true; }
                catch (InvalidOperationException) { return false; }
            }
            await ClusterPoll.UntilAsync(Ready, s_timeout, ClusterPoll.DefaultInterval, "real loopd startup", ct);
            Log("Real loopd connected to NLightning with TLS and macaroons");

            async Task MineWhenPublished()
            {
                await ClusterPoll.UntilAsync(async c => (await fixture.Bitcoin.GetRawMempoolAsync(c)).Length > 0,
                    s_timeout, ClusterPoll.DefaultInterval, "swap publication", ct);
                await ChainSync.MineAndWaitAsync(fixture, 3, [alice], [node], ct);
            }
            async Task RestartBothDuringSwap(string phase)
            {
                // Preserve the node database/key manager and Loop's database, TLS and macaroons.
                await host.StopAsync(ct);
                await node.StopAsync();
                await node.StartAsync(ct);
                host = node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
                await host.StartAsync(ct);
                var previous = processes[^1];
                processes.RemoveAt(processes.Count - 1);
                processes.Add(previous.Restart(bin, directory));
                Log(previous.Tail());
                await ClusterPoll.UntilAsync(Ready, s_timeout, ClusterPoll.DefaultInterval, "loopd resumed after both restarts", ct);
                await ClusterPoll.UntilAsync(async c => (await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: c))
                    .Channels.Any(ch => ch.Active && ch.RemotePubkey == node.NodeIdHex), s_timeout, ClusterPoll.DefaultInterval,
                    "Loop channel reestablished", ct);
                Log($"NLightning and real loopd restarted during {phase}");
            }
            async Task Traditional(string command)
            {
                var output = await Cli([command, "--amt", "500000", "--force", .. command == "out" ? new[] { "--fast" } : []]);
                var hash = IdPattern().Match(output).Groups[1].Value;
                Assert.NotEmpty(hash);
                if (command == "in")
                    await ClusterPoll.UntilAsync(async c => (await fixture.Bitcoin.GetRawMempoolAsync(c)).Length > 0,
                        s_timeout, ClusterPoll.DefaultInterval, "Loop In funding published before restart", ct);
                await RestartBothDuringSwap($"outstanding Loop {command}");
                await MineWhenPublished();
                await MineWhenPublished();
                await ClusterPoll.UntilAsync(async _ =>
                {
                    using var info = JsonDocument.Parse(await Cli("swapinfo", hash));
                    var state = info.RootElement.GetProperty("state").GetString();
                    Assert.NotEqual("FAILED", state);
                    return state == "SUCCESS";
                }, s_timeout, ClusterPoll.DefaultInterval, $"Loop {command} success", ct);
                Log($"Real Loop {command} succeeded");
            }
            await Traditional("out");
            await Traditional("in");
            // Lose the real server after it accepted a Loop In, before its HTLC confirms.
            // The client must recover its own on-chain funds, even across both local restarts.
            var failedOutput = await Cli("in", "--amt", "500000", "--force");
            var failedHash = IdPattern().Match(failedOutput).Groups[1].Value;
            Assert.NotEmpty(failedHash);
            using var failedInfo = JsonDocument.Parse(await Cli("swapinfo", failedHash));
            var refundAddress = failedInfo.RootElement.GetProperty("htlc_address_p2tr").GetString();
            if (string.IsNullOrEmpty(refundAddress))
                refundAddress = failedInfo.RootElement.GetProperty("htlc_address_p2wsh").GetString();
            Assert.NotNull(refundAddress);
            Assert.NotEmpty(refundAddress);
            var refundScript = NBitcoin.BitcoinAddress.Create(refundAddress, NBitcoin.Network.RegTest).ScriptPubKey;
            NBitcoin.uint256? fundingId = null;
            await ClusterPoll.UntilAsync(async c =>
            {
                foreach (var id in await fixture.Bitcoin.GetRawMempoolAsync(c))
                {
                    var tx = await fixture.Bitcoin.GetRawTransactionAsync(id, cancellationToken: c);
                    if (tx.Outputs.Any(o => o.ScriptPubKey == refundScript)) { fundingId = id; return true; }
                }
                return false;
            }, s_timeout, ClusterPoll.DefaultInterval, "failed Loop In HTLC published", ct);
            var failedServer = processes[0];
            processes.RemoveAt(0);
            failedServer.Stop();
            try
            {
                await ChainSync.MineAndWaitAsync(fixture, 3, [alice], [node], ct);
                await RestartBothDuringSwap("server-unavailable Loop In");
                // Refuse the first refund signature, then recover from the persisted funded swap.
                var signerOptions = node.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LndGrpcOptions>>().Value;
                signerOptions.EnableSigner = false;
                // The fixture's classic Loop In deadline is 100 blocks from initiation.
                for (var remaining = 105; remaining > 0; remaining -= Math.Min(remaining, 32))
                    await ChainSync.MineAndWaitAsync(fixture, Math.Min(remaining, 32), [alice], [node], ct, s_timeout);
                await ClusterPoll.UntilAsync(async _ =>
                {
                    using var info = JsonDocument.Parse(await Cli("swapinfo", failedHash));
                    return info.RootElement.GetProperty("state").GetString() == "FAILED"
                        && info.RootElement.GetProperty("failure_reason").GetString() == "FAILURE_REASON_TEMPORARY";
                }, s_timeout, ClusterPoll.DefaultInterval, "refund signer refusal surfaced by real Loop", ct);
                Assert.Empty(await fixture.Bitcoin.GetRawMempoolAsync(ct));
                signerOptions.EnableSigner = true;
                var interruptedLoop = processes[^1];
                processes.RemoveAt(processes.Count - 1);
                processes.Add(interruptedLoop.Restart(bin, directory));
                Log(interruptedLoop.Tail());
                await ClusterPoll.UntilAsync(Ready, s_timeout, ClusterPoll.DefaultInterval,
                    "loopd recovered after signer opt-in restored", ct);
                NBitcoin.uint256? refundId = null;
                await ClusterPoll.UntilAsync(async c =>
                {
                    foreach (var id in await fixture.Bitcoin.GetRawMempoolAsync(c))
                    {
                        var tx = await fixture.Bitcoin.GetRawTransactionAsync(id, cancellationToken: c);
                        if (tx.Inputs.Any(input => input.PrevOut.Hash == fundingId)) { refundId = id; return true; }
                    }
                    return false;
                }, s_timeout, ClusterPoll.DefaultInterval, "real Loop In timeout refund published", ct);
                await ChainSync.MineAndWaitAsync(fixture, 3, [alice], [node], ct);
                Assert.NotNull(refundId);
                await ClusterPoll.UntilAsync(async _ =>
                {
                    using var info = JsonDocument.Parse(await Cli("swapinfo", failedHash));
                    return info.RootElement.GetProperty("state").GetString() == "FAILED"
                        && info.RootElement.GetProperty("failure_reason").GetString() == "FAILURE_REASON_TIMEOUT";
                }, s_timeout, ClusterPoll.DefaultInterval, "confirmed failed Loop In refund", ct);
                await RestartBothDuringSwap("confirmed failed Loop In refund");
                using var recoveredFailure = JsonDocument.Parse(await Cli("swapinfo", failedHash));
                Assert.Equal("FAILED", recoveredFailure.RootElement.GetProperty("state").GetString());
                Assert.Equal("FAILURE_REASON_TIMEOUT", recoveredFailure.RootElement.GetProperty("failure_reason").GetString());
                Log("Server loss, signer refusal and daemon restarts recovered a failed Loop In with a confirmed timeout refund");
            }
            finally
            {
                processes.Insert(0, failedServer.Restart(bin, directory));
                Log(failedServer.Tail());
            }
            await ClusterPoll.UntilAsync(async _ =>
            {
                foreach (var child in processes) child.CheckRunning();
                try { await Cli("terms"); return true; }
                catch (InvalidOperationException) { return false; }
            }, s_timeout, ClusterPoll.DefaultInterval, "restarted swap server serves terms through Aperture", ct);
            var addressOutput = await Child.Run(bin, "loop", directory, [.. cliArgs, "static", "new"], ct, "y\n");
            var address = AddressPattern().Match(addressOutput).Groups[1].Value;
            Assert.NotEmpty(address);
            var staticDeposit = await fixture.Bitcoin.SendToAddressAsync(NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                NBitcoin.Money.Satoshis(500_000), cancellationToken: ct);
            await ChainSync.MineAndWaitAsync(fixture, 6, [alice], [node], ct);
            var depositHeight = node.BlockchainMonitor.LastProcessedBlockHeight - 5;
            await ClusterPoll.UntilAsync(async _ =>
            {
                using var deposits = JsonDocument.Parse(await Cli("static", "listdeposits", "--filter", "deposited"));
                return deposits.RootElement.GetProperty("filtered_deposits").GetArrayLength() > 0;
            }, s_timeout, ClusterPoll.DefaultInterval, "imported static deposit", ct);
            // Loop's pre-signing FSM intentionally aborts on recovery (its MuSig nonces are ephemeral).
            // Restart at the confirmed deposit checkpoint, then initiate a fresh signing round.
            await RestartBothDuringSwap("confirmed static deposit awaiting Loop In");
            using var staticSwap = JsonDocument.Parse(await Cli("static", "in", "--all", "--fast", "--force"));
            var staticHash = staticSwap.RootElement.GetProperty("swap_hash").GetString();
            await ClusterPoll.UntilAsync(async _ =>
            {
                using var swaps = JsonDocument.Parse(await Cli("static", "listswaps"));
                var swap = swaps.RootElement.GetProperty("swaps").EnumerateArray()
                    .FirstOrDefault(s => s.GetProperty("swap_hash").GetString() == staticHash);
                if (swap.ValueKind == JsonValueKind.Undefined) return false;
                var state = swap.GetProperty("state").GetString();
                Assert.NotEqual("FAILED_STATIC_ADDRESS_SWAP", state);
                return state == "SUCCEEDED";
            }, s_timeout, ClusterPoll.DefaultInterval, "static Loop in success", ct);
            await MineWhenPublished();
            await AssertNotifierParityAsync(fixture, alice, host, grpc, staticDeposit, depositHeight, address, ct);
            await AssertSignerParityAsync(alice, node, host, grpc, ct);
            var withdrawalDeposit = await fixture.Bitcoin.SendToAddressAsync(
                NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest), NBitcoin.Money.Satoshis(250_000),
                cancellationToken: ct);
            await ChainSync.MineAndWaitAsync(fixture, 6, [alice], [node], ct);
            await ClusterPoll.UntilAsync(async _ =>
            {
                using var deposits = JsonDocument.Parse(await Cli("static", "listdeposits", "--filter", "deposited"));
                return deposits.RootElement.GetProperty("filtered_deposits").EnumerateArray().Any(d =>
                    d.GetProperty("outpoint").GetString()?.StartsWith(withdrawalDeposit + ":", StringComparison.Ordinal) == true);
            }, s_timeout, ClusterPoll.DefaultInterval, "cooperative withdrawal deposit", ct);
            await RestartBothDuringSwap("imported deposit awaiting cooperative withdrawal");
            var previousLoop = processes[^1];
            processes.RemoveAt(processes.Count - 1);
            processes.Add(previousLoop.Restart(bin, directory));
            Log(previousLoop.Tail());
            await ClusterPoll.UntilAsync(Ready, s_timeout, ClusterPoll.DefaultInterval,
                "loopd-only restart with a persisted static deposit", ct);
            Log("Loopd-only restart restored the confirmed imported static deposit");
            await Cli("static", "withdraw", "--all", "--sat_per_vbyte", "2");
            NBitcoin.uint256? withdrawalId = null;
            await ClusterPoll.UntilAsync(async c =>
            {
                foreach (var id in await fixture.Bitcoin.GetRawMempoolAsync(c))
                {
                    var tx = await fixture.Bitcoin.GetRawTransactionAsync(id, cancellationToken: c);
                    if (tx.Inputs.Any(input => input.PrevOut.Hash == withdrawalDeposit)) { withdrawalId = id; return true; }
                }
                return false;
            }, s_timeout, ClusterPoll.DefaultInterval, "cooperative withdrawal publication", ct);
            await ChainSync.MineAndWaitAsync(fixture, 3, [alice], [node], ct);
            await AssertImportedSpendReorgAsync(fixture, alice, node, host, grpc, withdrawalDeposit, withdrawalId!, ct);
            Log("Cooperative static withdrawal confirmed after daemon restarts and spend reorg");
            // A second confirmed deposit exercises unilateral signing when its CSV lifetime expires.
            var csvDeposit = await fixture.Bitcoin.SendToAddressAsync(NBitcoin.BitcoinAddress.Create(address, NBitcoin.Network.RegTest),
                NBitcoin.Money.Satoshis(500_000), cancellationToken: ct);
            await ChainSync.MineAndWaitAsync(fixture, 6, [alice], [node], ct);
            await ClusterPoll.UntilAsync(async _ =>
            {
                using var deposits = JsonDocument.Parse(await Cli("static", "listdeposits", "--filter", "deposited"));
                return deposits.RootElement.GetProperty("filtered_deposits").EnumerateArray().Any(d =>
                    d.GetProperty("outpoint").GetString()?.StartsWith(csvDeposit.ToString() + ":", StringComparison.Ordinal) == true
                    && long.TryParse(d.GetProperty("confirmation_height").GetString(), out var height) && height > 0);
            }, s_timeout, ClusterPoll.DefaultInterval, "second imported static deposit", ct);
            await AssertDepositReorgAsync(fixture, alice, node, host, grpc, csvDeposit, ct);
            var replacementHeight = node.BlockchainMonitor.LastProcessedBlockHeight - 5;
            await ClusterPoll.UntilAsync(async _ =>
            {
                using var deposits = JsonDocument.Parse(await Cli("static", "listdeposits", "--filter", "deposited"));
                return deposits.RootElement.GetProperty("filtered_deposits").EnumerateArray().Any(d =>
                    d.GetProperty("outpoint").GetString()?.StartsWith(csvDeposit.ToString() + ":", StringComparison.Ordinal) == true
                    && long.TryParse(d.GetProperty("confirmation_height").GetString(), out var height) && height == replacementHeight);
            }, s_timeout, ClusterPoll.DefaultInterval, "Loop reconciled the replacement deposit height", ct);
            // The pinned server's defaultStaticAddressExpiry is 4320 blocks (regtest/server/server.go).
            for (var remaining = 4321; remaining > 0; remaining -= Math.Min(remaining, 64))
                await ChainSync.MineAndWaitAsync(fixture, Math.Min(remaining, 64), [alice], [node], ct, s_timeout);
            // Deliver a fresh epoch after deposit reconciliation has observed the stable expiry tip.
            await ChainSync.MineAndWaitAsync(fixture, 1, [alice], [node], ct);
            await ClusterPoll.UntilAsync(async c =>
            {
                if ((await fixture.Bitcoin.GetRawMempoolAsync(c)).Length > 0) return true;
                using var deposits = JsonDocument.Parse(await Cli("static", "listdeposits", "--filter", "expired"));
                return deposits.RootElement.GetProperty("filtered_deposits").GetArrayLength() > 0;
            }, s_timeout, ClusterPoll.DefaultInterval, "CSV sweep publication or confirmation", ct);
            if ((await fixture.Bitcoin.GetRawMempoolAsync(ct)).Length > 0)
                await ChainSync.MineAndWaitAsync(fixture, 3, [alice], [node], ct);
            await ClusterPoll.UntilAsync(async _ =>
            {
                using var deposits = JsonDocument.Parse(await Cli("static", "listdeposits", "--filter", "expired"));
                return deposits.RootElement.GetProperty("filtered_deposits").GetArrayLength() > 0;
            }, s_timeout, ClusterPoll.DefaultInterval, "confirmed static CSV timeout sweep", ct);
            Log("Real static deposit CSV timeout sweep confirmed");
            Log("All three real Loop swap flows succeeded");
        }
        finally
        {
            foreach (var child in processes.AsEnumerable().Reverse()) { child.Dispose(); Log(child.Tail()); }
            if (host is not null) await host.StopAsync(CancellationToken.None);
            if (node is not null) await node.DisposeAsync();
            await fixture.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    [GeneratedRegex(@"(?m)^ID:\s*(\S+)")]
    private static partial Regex IdPattern();
    [GeneratedRegex("\"address\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex AddressPattern();

    private sealed class Child : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly string _name;
        private readonly string[] _arguments;
        public Child(string bin, string name, string directory, params string[] args)
        {
            _name = name;
            _arguments = args.ToArray();
            var start = new ProcessStartInfo(Path.Combine(bin, name))
            {

                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            _process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {name}");
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
        }
        public void Stop()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
        }
        public Child Restart(string bin, string directory)
        {
            Dispose();
            return new Child(bin, _name, directory, _arguments);
        }
        public void CheckRunning()
        {
            if (_process.HasExited) throw new InvalidOperationException($"{_name} exited {_process.ExitCode}: {Tail()}");
        }
        public string Tail() => $"{_name}: " + string.Join('\n', (_stdout.IsCompletedSuccessfully ? _stdout.Result : "")
            .Split('\n').Concat((_stderr.IsCompletedSuccessfully ? _stderr.Result : "").Split('\n')).TakeLast(80));
        public void Dispose()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
            Task.WhenAll(_stdout, _stderr).Wait(TimeSpan.FromSeconds(10));
            _process.Dispose();
        }
        public static async Task<string> Run(string bin, string name, string directory, string[] args,
                                               CancellationToken ct, string? input = null)
        {
            using var child = new Child(bin, name, directory, args);
            if (input is not null) await child._process.StandardInput.WriteAsync(input.AsMemory(), ct);
            child._process.StandardInput.Close();
            await child._process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
            if (child._process.ExitCode != 0) throw new InvalidOperationException(child.Tail());
            return await child._stdout;
        }
    }
}