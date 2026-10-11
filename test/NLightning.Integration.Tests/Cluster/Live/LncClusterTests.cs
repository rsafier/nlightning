using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
using Testing.Cluster.Run;
using Testing.Cluster.Runner;
using Testing.Lnd.Lnrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using ClusterPoll = Testing.Cluster.Poll;

/// <summary>Upstream LNC Noise transport through a local Aperture relay into our real regtest node.</summary>
[Trait("Category", "Cluster")]
public sealed class LncClusterTests
{
    private static void Log(string value) => TestContext.Current.TestOutputHelper?.WriteLine(value);

    [Fact(Explicit = true)]
    public async Task Given_UpstreamLnc_When_PairingPayingRestartingAndRevoking_Then_RealNodeHonorsSessions()
    {
        var ct = TestContext.Current.CancellationToken;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")))
        {
            await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("lnc"), ct);
            var job = new TestRunnerJob
            {
                Image = RunnerImage.FromEnvironment(),
                Assembly = "NLightning.Integration.Tests.dll",
                ActiveDeadline = TimeSpan.FromMinutes(15)
            };
            job.Env["NLTG_TEST_BACKEND"] = "cluster";
            job.Env["NLTG_LNC_BIN"] = "/lnc-bin";
            foreach (var argument in new[] { "-explicit", "only", "-class", GetType().FullName!, "-showLiveOutput", "-noColor" })
                job.TestArguments.Add(argument);
            var result = await InClusterTestRunner.RunAsync(run, job, Log, ct);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains("LNC_REGTEST_ALL_OK", result.Log);
            return;
        }

        var bin = Environment.GetEnvironmentVariable("NLTG_LNC_BIN") ?? throw new InvalidOperationException("NLTG_LNC_BIN required");
        var directory = Path.Combine(Path.GetTempPath(), "nltg-lnc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var fixture = new LightningRegtestNetworkFixture();
        NLightningTestNode? node = null;
        LndGrpcHost? host = null;
        Child? relay = null;
        Child? bridge = null;
        try
        {
            await fixture.InitializeAsync();
            var alice = fixture.GetLndNode("alice");
            node = await NLightningTestNode.CreateAsync(fixture, "lnc");
            var grpc = Path.Combine(directory, "grpc");
            node.ExtraConfiguration["LndGrpc:Enabled"] = "true";
            node.ExtraConfiguration["LndGrpc:Port"] = (await PortPoolUtil.GetAvailablePortAsync()).ToString(CultureInfo.InvariantCulture);
            node.ExtraConfiguration["LndGrpc:ListenAddress"] = "127.0.0.1";
            node.ExtraConfiguration["LndGrpc:DataDirectory"] = grpc;
            node.ConfigureServices = services =>
            {
                services.AddLndGrpcHost(grpc);
                services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Information)
                    .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning));
            };
            await node.StartAsync(ct);
            host = node.Services.GetServices<IHostedService>().OfType<LndGrpcHost>().Single();
            await host.StartAsync(ct);
            await node.FundWalletAsync(LightningMoney.Satoshis(4_000_000), AddressType.P2Wpkh, ct);
            await node.ConnectToAsync(alice, ct);
            await node.OpenChannelAsync(new OpenChannelClientRequest($"{alice.LocalNodePubKey}@{await fixture.GetLndPeerEndpointAsync(alice, ct)}", LightningMoney.Satoshis(2_000_000))
            {
                ForceV1 = true,
                PushAmount = LightningMoney.Satoshis(1_000_000)
            }, cancellationToken: ct);
            await ChainSync.MineAndWaitAsync(fixture, 6, [alice], [node], ct);
            await ClusterPoll.UntilAsync(async c => (await alice.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: c))
                .Channels.Any(ch => ch.Active && ch.RemotePubkey == node.NodeIdHex), TimeSpan.FromMinutes(2), ClusterPoll.DefaultInterval, "LNC channel active", ct);

            var relayDirectory = Path.Combine(directory, "relay");
            Directory.CreateDirectory(relayDirectory);
            var relayAddress = $"127.0.0.1:{await PortPoolUtil.GetAvailablePortAsync()}";
            await File.WriteAllTextAsync(Path.Combine(relayDirectory, "aperture.yaml"), $"""
                listenaddr: '{relayAddress}'
                servername: localhost
                autocert: false
                insecure: false
                debuglevel: info
                writetimeout: 0s
                dbbackend: sqlite
                authenticator:
                  disable: true
                hashmail:
                  enabled: true
                  messagerate: 1ms
                  messageburstallowance: 100000
                """, ct);
            relay = new Child(bin, "aperture", directory, $"--basedir={relayDirectory}", $"--configfile={relayDirectory}/aperture.yaml", $"--sqlite.dbfile={relayDirectory}/aperture.db");
            await ClusterPoll.UntilAsync(_ => { relay.CheckRunning(); return Task.FromResult(File.Exists(Path.Combine(relayDirectory, "tls.cert"))); },
                TimeSpan.FromMinutes(1), ClusterPoll.DefaultInterval, "local LNC mailbox relay TLS", ct);
            var state = Path.Combine(directory, "sessions");
            string[] common = [$"--state-dir={state}", $"--backend=127.0.0.1:{host.BoundPort}", $"--tls-cert={grpc}/tls.cert", $"--admin-macaroon={grpc}/admin.macaroon", $"--relay={relayAddress}"];
            async Task<JsonElement> Create(string profile)
            {
                var output = await Child.Run(bin, "nltg-lnc", directory, ["create", .. common, $"--profile={profile}", $"--name=regtest-{profile}"], ct);
                return JsonDocument.Parse(output).RootElement.Clone();
            }
            var readonlySession = await Create("readonly");
            var walletSession = await Create("wallet");
            string[] serve = ["serve", .. common, $"--relay-tls-cert={relayDirectory}/tls.cert"];
            bridge = new Child(bin, "nltg-lnc", directory, serve);
            string[] Proof(JsonElement session, string mode) => [$"--relay={relayAddress}", $"--relay-cert={relayDirectory}/tls.cert",
                $"--state={directory}/client-{session.GetProperty("id").GetString()}.json", $"--phrase={session.GetProperty("pairing_phrase").GetString()}", $"--mode={mode}"];
            Assert.Contains("LNC_READONLY_OK", await Child.Run(bin, "lnc-proof", directory, Proof(readonlySession, "readonly"), ct));
            Log("Read-only LNC session paired; write permission refused by real node");
            var info = await Child.Run(bin, "lnc-proof", directory, Proof(walletSession, "info"), ct);
            Assert.Contains(node.NodeIdHex, info);
            Assert.Contains(node.NodeIdHex, await Child.Run(bin, "lnc-proof", directory,
                [.. Proof(walletSession, "info"), "--transport=websocket"], ct));
            Log("Upstream LNC gRPC and browser websocket relay transports both reached our node");
            var invoicePath = Path.Combine(directory, "invoice.txt");
            using (var receive = new Child(bin, "lnc-proof", directory, [.. Proof(walletSession, "receive"), $"--invoice-output={invoicePath}"]))
            {
                await ClusterPoll.UntilAsync(_ => { bridge.CheckRunning(); receive.CheckRunning(); return Task.FromResult(File.Exists(invoicePath)); },
                    TimeSpan.FromMinutes(2), ClusterPoll.DefaultInterval, "invoice stream ready through LNC", ct);
                using var incomingPayment = alice.RouterClient.SendPaymentV2(new Testing.Lnd.Routerrpc.SendPaymentRequest
                {
                    PaymentRequest = await File.ReadAllTextAsync(invoicePath, ct),
                    TimeoutSeconds = 30,
                    FeeLimitSat = 100
                }, cancellationToken: ct);
                Payment? finalPayment = null;
                while (await incomingPayment.ResponseStream.MoveNext(ct).WaitAsync(TimeSpan.FromMinutes(1), ct))
                    finalPayment = incomingPayment.ResponseStream.Current;
                Assert.NotNull(finalPayment);
                Assert.Equal(Payment.Types.PaymentStatus.Succeeded, finalPayment.Status);
                Assert.Contains("LNC_INVOICE_STREAM_OK", await receive.Complete(ct));
            }
            var aliceInvoice = await alice.LightningClient.AddInvoiceAsync(new Invoice { Value = 1000 }, cancellationToken: ct);
            Assert.Contains("LNC_PAYMENT_STREAM_OK", await Child.Run(bin, "lnc-proof", directory,
                [.. Proof(walletSession, "pay"), $"--payment-request={aliceInvoice.PaymentRequest}"], ct));
            Log("LNC invoice subscription settled and SendPaymentV2 succeeded on real regtest channel");
            bridge.Dispose();
            bridge = new Child(bin, "nltg-lnc", directory, serve);
            Assert.Contains(node.NodeIdHex, await Child.Run(bin, "lnc-proof", directory, Proof(walletSession, "info"), ct));
            Log("LNC paired client and bridge identities survived restart without pairing again");
            var watchPath = Path.Combine(directory, "watch-ready.txt");
            using (var watch = new Child(bin, "lnc-proof", directory, [.. Proof(walletSession, "watch"), $"--invoice-output={watchPath}"]))
            {
                await ClusterPoll.UntilAsync(_ => { watch.CheckRunning(); return Task.FromResult(File.Exists(watchPath)); },
                    TimeSpan.FromMinutes(2), ClusterPoll.DefaultInterval, "active LNC stream", ct);
                await Child.Run(bin, "nltg-lnc", directory, ["revoke", .. common, $"--id={walletSession.GetProperty("id").GetString()}"], ct);
                Assert.Contains("LNC_ACTIVE_REVOKED_OK", await watch.Complete(ct));
            }
            Assert.Contains("LNC_REVOKED_OK", await Child.Run(bin, "lnc-proof", directory, Proof(walletSession, "revoked"), ct));
            var expiryOutput = await Child.Run(bin, "nltg-lnc", directory, ["create", .. common, "--profile=readonly", "--name=expiry", "--ttl=30s"], ct);
            var expirySession = JsonDocument.Parse(expiryOutput).RootElement.Clone();
            Assert.Contains(node.NodeIdHex, await Child.Run(bin, "lnc-proof", directory, Proof(expirySession, "info"), ct));
            var expiresAt = expirySession.GetProperty("expires_at").GetDateTimeOffset();
            var expiryDelay = expiresAt.AddSeconds(1) - DateTimeOffset.UtcNow;
            if (expiryDelay > TimeSpan.Zero) await Task.Delay(expiryDelay, ct);
            Assert.Contains("LNC_REVOKED_OK", await Child.Run(bin, "lnc-proof", directory, Proof(expirySession, "revoked"), ct));
            Log("Active session stream closed on revocation; expired paired session could not reconnect");
            Assert.Contains("LNC_READONLY_OK", await Child.Run(bin, "lnc-proof", directory, Proof(readonlySession, "readonly"), ct));
            Log("LNC_REGTEST_ALL_OK: pairing, reads, denied writes, invoice and payment streams, restart, active revocation, expiry, isolated sessions");
        }
        finally
        {
            bridge?.Dispose(); relay?.Dispose();
            if (host is not null) await host.StopAsync(CancellationToken.None);
            if (node is not null) await node.DisposeAsync();
            await fixture.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Child : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stdout;
        private readonly Task<string> _stderr;
        private readonly string _name;
        public Child(string bin, string name, string directory, params string[] arguments)
        {
            _name = name;
            var start = new ProcessStartInfo(Path.Combine(bin, name))
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            _process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {name}");
            _stdout = _process.StandardOutput.ReadToEndAsync();
            _stderr = _process.StandardError.ReadToEndAsync();
        }
        public void CheckRunning()
        {
            if (_process.HasExited) throw new InvalidOperationException($"{_name} exited {_process.ExitCode}: {Tail()}");
        }
        private string Tail() => string.Join('\n', (_stdout.IsCompletedSuccessfully ? _stdout.Result : "")
            .Split('\n').Concat((_stderr.IsCompletedSuccessfully ? _stderr.Result : "").Split('\n')).TakeLast(80));
        public async Task<string> Complete(CancellationToken ct)
        {
            await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(110), ct);
            if (_process.ExitCode != 0) throw new InvalidOperationException($"{_name}: {Tail()}");
            return await _stdout;
        }
        public void Dispose()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(10_000);
            Task.WhenAll(_stdout, _stderr).Wait(TimeSpan.FromSeconds(10));
            if (_process.ExitCode != 0) Log($"{_name}: {Tail()}");
            _process.Dispose();
        }
        public static async Task<string> Run(string bin, string name, string directory, string[] arguments, CancellationToken ct)
        {
            using var child = new Child(bin, name, directory, arguments);
            return await child.Complete(ct);
        }
    }
}