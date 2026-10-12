using System.Diagnostics;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Npgsql;
using ClusterPoll = NLightning.Testing.Cluster.Poll;

namespace NLightning.Integration.Tests.Cluster.Live;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Infrastructure.RemoteSigning;
using RemoteSigning.Tests;
using Signer;
using Testing.Cluster.Nodes.BitcoinCore;
using Testing.Cluster.Run;
using Testing.Cluster.Topology;
using SignedTransaction = Domain.Bitcoin.ValueObjects.SignedTransaction;
using TxId = Domain.Bitcoin.ValueObjects.TxId;
using WireRequest = Signing.Contracts.SigningRequest;

[Trait("Category", "Cluster")]
[Trait("Database", "Postgres")]
public sealed class NativeWalletAuthorityProcessClusterTests
{
    private const string WriterA = "native-owner-installed-writer-a-000000000000000000";
    private const string WriterB = "native-owner-installed-writer-b-000000000000000000";
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(3);

    [Theory(Explicit = true)]
    [InlineData(AddressType.P2Wpkh)]
    [InlineData(AddressType.P2Tr)]
    public async Task ProductionWalletAuthorityUsesVerifiedTlsPostgresAndCoreBeforeNativeSigning(AddressType type)
    {
        var ct = TestContext.Current.CancellationToken;
        void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("native-wallet-authority")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        using var topology = await new TopologyBuilder { Log = Log, ReadyTimeout = s_timeout }
            .AddBitcoinCore("miner").BuildAsync(run, ct);
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        var rpc = chain.Bitcoin.CreateRpc(RpcRoute.ServiceDns);
        await using var original = new SignerDaemonFixture(injected: true);
        await original.InitializeAsync();
        var directory = original.DirectoryPath;
        var state = Path.Combine(directory, "state");
        var locator = new NativeWalletKeyLocator(0, false, type);
        var changeLocator = new NativeWalletKeyLocator(1, true, type);
        var registryBinding = new NativeSignerBinding(Domain.Signing.NodeSigningContext.DefaultNodeId,
            Domain.Signing.NodeSigningContext.DefaultOwnerId, Domain.Signing.NodeSigningContext.DefaultSignerId,
            "regtest", original.LocalKeys.GetNodePubKey().ToString());
        var registry = new NativeSignerWalletScriptRegistry(registryBinding, original.LocalKeys, [locator, changeLocator]);
        var inputScript = registry.GetScript(registryBinding, locator);
        var deposit = new Script(inputScript).GetDestinationAddress(Network.RegTest)!.ToString();
        var fundingId = await rpc.SendToAddressAsync(deposit, 100_000, 2, ct);
        await chain.Chain.MineAsync(1, ct);
        var fundingHex = (await rpc.CallAsync("getrawtransaction", new Dictionary<string, object?> { ["txid"] = fundingId }, ct)).Value<string>()!;
        var funding = Transaction.Parse(fundingHex, Network.RegTest);
        var inputIndex = (uint)funding.Outputs.FindIndex(output => output.ScriptPubKey.ToBytes().AsSpan().SequenceEqual(inputScript));
        Assert.Equal(100_000, funding.Outputs[(int)inputIndex].Value.Satoshi);
        var height = (uint)(await rpc.GetTipAsync(ct)).Height;
        var destination = BitcoinAddress.Create(await rpc.GetNewAddressAsync(ct), Network.RegTest).ScriptPubKey.ToBytes();
        var transaction = Transaction.Create(Network.RegTest);
        transaction.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(fundingId), inputIndex)));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(50_000), new Script(destination)));
        transaction.Outputs.Add(new TxOut(Money.Satoshis(49_000), new Script(registry.GetScript(registryBinding, changeLocator))));
        var reservation = Guid.NewGuid();
        var inputId = new TxId(uint256.Parse(fundingId).ToBytes());
        var snapshot = new WalletSnapshot([new UtxoModel(inputId, inputIndex, LightningMoney.Satoshis(100_000), height,
            new WalletAddressModel(type, locator.Index, locator.IsChange, deposit))],
            [new FeeReservation(inputId, inputIndex, reservation)]);
        var request = RemoteSignerConnection.Prepare(SignerOperations.SignWalletTransaction3,
            new SignedTransaction(new TxId(transaction.GetHash().ToBytes()), transaction.ToBytes()),
            reservation, Array.Empty<SpentOutput>(), snapshot);
        await original.StopAsync();
        var manifest = await ReadManifestAsync(original, ct);
        Assert.Equal(registryBinding, manifest.Binding);
        var postgres = await NativeWalletAuthorityTlsPostgres.DeployAsync(run, directory, ct);
        var owner = new NativeSignerAuthority(() => new NpgsqlConnection(postgres.OwnerConnectionString));
        owner.Initialize();
        owner.Enroll(manifest.Binding, manifest.SignerCheckpoint);
        var execution = owner.AcquireWriter(manifest.Binding, 0, "writer-a");
        owner.ApproveWalletIntent(manifest.Binding, NativeSignerIntent.FromRequest(request),
            new NativeWalletApproval(reservation, [new NativeWalletApprovedInput(fundingId, inputIndex, locator)],
                new NativeWalletSpendingIntent([new NativeApprovedOutput(destination, 50_000)], 1000)),
            DateTimeOffset.UtcNow.AddMinutes(20));
        await postgres.GrantRuntimeAsync(ct);
        var core = new NativeCoreChainEvidenceOptions(new Uri($"http://{chain.Bitcoin.Handle.ServiceDnsName}:{chain.RpcPort}/"),
            Network.RegTest.GetGenesis().GetHash().ToString(), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15),
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(2), AllowTrustedPlainHttp: true);
        WritePrivate(Path.Combine(directory, "authority-postgres"), postgres.RuntimeConnectionString);
        WritePrivate(Path.Combine(directory, "authority-writer"), WriterA);
        WritePrivate(Path.Combine(directory, "authority-core"), JsonSerializer.Serialize(new
        { Username = chain.RpcUser, Password = chain.RpcPassword }));
        void InstallProfile(NativeSignerExecution current) => WritePrivate(Path.Combine(directory, "authority.json"),
            JsonSerializer.Serialize(new
            {
                Mode = "NativeWalletAuthorityV1",
                Binding = manifest.Binding,
                Execution = current,
                PostgreSqlConnectionFile = Path.Combine(directory, "authority-postgres"),
                WriterCredentialFile = Path.Combine(directory, "authority-writer"),
                CoreCredentialsFile = Path.Combine(directory, "authority-core"),
                Core = core,
                Derivations = new[] { locator, changeLocator }
            }));
        InstallProfile(execution);
        await using var process = new RestrictedSignerProcess(original);
        await process.StartAsync(ct);
        using var writerA = new RemoteSignerConnection(ConnectionOptions(original, "writer-a", 1, WriterA));
        using var nodeOnly = new RemoteSignerConnection(original.Options());
        var before = Histories(state);
        AssertDenied(nodeOnly, request, StatusCode.PermissionDenied);
        AssertHistories(before);
        var unsupported = RemoteSignerConnection.Prepare(SignerOperations.MarkDataLoss, new ChannelId(new byte[32]));
        owner.Approve(manifest.Binding, NativeSignerIntent.FromRequest(unsupported), DateTimeOffset.UtcNow.AddMinutes(20));
        AssertDenied(writerA, unsupported, StatusCode.FailedPrecondition);
        AssertHistories(before);
        var altered = request.Clone();
        var alteredTx = transaction.Clone();
        alteredTx.Outputs[0].Value = Money.Satoshis(50_001);
        altered.Payload = ByteString.CopyFrom(SignerWire.Encode([new SignedTransaction(new TxId(alteredTx.GetHash().ToBytes()),
            alteredTx.ToBytes()), reservation, Array.Empty<SpentOutput>(), snapshot]));
        AssertDenied(writerA, altered, StatusCode.PermissionDenied);
        AssertHistories(before);
        var originalTip = await rpc.GetTipAsync(ct);
        await chain.Bitcoin.Handle.RestartAsync(s_timeout, (_, _) =>
        {
            AssertDenied(writerA, request, StatusCode.FailedPrecondition);
            AssertHistories(before);
            return Task.CompletedTask;
        }, ct);
        using (var restoredEvidence = new AuthenticatedNativeCoreChainEvidence(manifest.Binding, core, registry,
            chain.RpcUser, chain.RpcPassword))
        {
            await ClusterPoll.UntilDoneAsync(_ =>
            {
                try
                {
                    restoredEvidence.RequireFresh(manifest.Binding);
                    return Task.FromResult<string?>(null);
                }
                catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
                {
                    // This adapter emits fixed sanitized reasons, never source credentials or response bodies.
                    return Task.FromResult<string?>(exception.Message);
                }
            }, s_timeout, "restored independently configured Core evidence after its stopped window", ct);
        }
        Assert.Equal(originalTip, await rpc.GetTipAsync(ct));
        var restoredInput = await rpc.CallAsync("gettxout", new Dictionary<string, object?>
        { ["txid"] = fundingId, ["n"] = inputIndex, ["include_mempool"] = true }, ct);
        Assert.NotEqual(JTokenType.Null, restoredInput.Type);
        Assert.Equal(100_000m, restoredInput["value"]!.Value<decimal>() * 100_000_000m);
        Assert.Equal(Convert.ToHexString(inputScript).ToLowerInvariant(),
            restoredInput["scriptPubKey"]!["hex"]!.Value<string>());
        execution = owner.AcquireWriter(manifest.Binding, 1, "writer-b");
        AssertDenied(writerA, request, StatusCode.FailedPrecondition);
        AssertHistories(before);
        await process.StopAsync(ct);
        WritePrivate(Path.Combine(directory, "authority-writer"), WriterB);
        InstallProfile(execution);
        await process.StartAsync(ct);
        using var writerB = new RemoteSignerConnection(ConnectionOptions(original, "writer-b", 2, WriterB));
        AssertDenied(writerA, request, StatusCode.PermissionDenied);
        AssertHistories(before);
        var signedArgs = writerB.Execute(request);
        Assert.True(SignerWire.Read<bool>(signedArgs[0]));
        var signed = SignerWire.Read<SignedTransaction>(signedArgs[1]);
        var signedTx = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        Assert.Null(signedTx.CreateValidator([new TxOut(Money.Satoshis(100_000), new Script(inputScript))]).ValidateInput(0).Error);
        var raw = Convert.ToHexString(signed.RawTxBytes).ToLowerInvariant();
        var published = (await rpc.CallAsync("sendrawtransaction", new Dictionary<string, object?> { ["hexstring"] = raw }, ct)).Value<string>();
        Assert.Equal(signed.TxId.ToString(), published);
        var signedHistory = Histories(state);
        var replay = writerB.Execute(request);
        Assert.Equal(signed.RawTxBytes, SignerWire.Read<SignedTransaction>(replay[1]).RawTxBytes);
        AssertHistories(signedHistory);
        await chain.Chain.MineAsync(1, ct);
        var confirmation = await rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = published, ["verbose"] = true }, ct);
        Assert.True(confirmation.Value<long>("confirmations") > 0);
        Assert.NotNull(confirmation.Value<string>("blockhash"));
        var accepted = (await rpc.CallAsync("getrawtransaction", new Dictionary<string, object?> { ["txid"] = published }, ct)).Value<string>();
        Assert.Equal(raw, accepted);
        Assert.False(before[state].AsSpan().SequenceEqual(ReadHistoryDigest(state)));
        Log($"{run.Namespace}: actual signer Program authorized {type} op29 through verified TLS PostgreSQL and authenticated Core; "
            + "node-only, altered intent, Core outage and stale writers did not mutate safety history; writer transfer restarted and Core confirmed exact signed bytes");
    }

    private static RemoteSignerOptions ConnectionOptions(SignerDaemonFixture fixture, string writer, long epoch, string token)
    {
        var options = fixture.Options();
        options.WriterId = writer;
        options.WriterEpoch = epoch;
        options.WriterCredential = token;
        options.ExpectedNodePublicKey = fixture.LocalKeys.GetNodePubKey().ToString();
        return options;
    }

    private static void AssertDenied(RemoteSignerConnection connection, WireRequest request, StatusCode expected)
    {
        if (expected == StatusCode.FailedPrecondition)
        {
            Assert.Throws<SignerException>(() => connection.Execute(request));
            return;
        }
        var failure = Assert.Throws<RemoteSignerTransportException>(() => connection.Execute(request));
        Assert.Equal(expected, Assert.IsType<RpcException>(failure.InnerException).StatusCode);
    }

    private static Dictionary<string, byte[]> Histories(string state) => new[] { state, state + ".nonces",
        state + ".swap-sessions", state + ".key-index", state + ".enrollment" }
        .ToDictionary(path => path, ReadHistoryDigest);

    private static void AssertHistories(Dictionary<string, byte[]> before)
    {
        foreach (var (path, digest) in before) Assert.Equal(digest, ReadHistoryDigest(path));
    }

    private static byte[] ReadHistoryDigest(string path)
    {
        // An external read observes bytes without participating in .NET's exclusive advisory writer locks.
        var start = new ProcessStartInfo("/usr/bin/sha256sum")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("History digest observer failed to start.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            _ = process.WaitForExit(5000);
            throw new TimeoutException("History digest observer did not exit within its deadline.");
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (process.ExitCode != 0 || error.Length != 0 || output.Length < 65
         || output[64] != ' ' || !output[..64].All(Uri.IsHexDigit))
            throw new InvalidOperationException("History digest observer did not return a SHA256 digest.");
        return Convert.FromHexString(output[..64]);
    }

    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The signer process proof requires Unix private files.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private sealed record Manifest(NativeSignerBinding Binding, string SignerCheckpoint);

    private static async Task<Manifest> ReadManifestAsync(SignerDaemonFixture fixture, CancellationToken ct)
    {
        using var process = Process.Start(StartInfo(fixture, manifest: true))
            ?? throw new InvalidOperationException("Public manifest process failed to start.");
        await process.StandardInput.WriteLineAsync((new string('0', 63) + "1").AsMemory(), ct);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await errors);
        return JsonSerializer.Deserialize<Manifest>(await output)
            ?? throw new InvalidDataException("Signer public manifest was missing.");
    }

    private static ProcessStartInfo StartInfo(SignerDaemonFixture fixture, bool manifest)
    {
        var start = new ProcessStartInfo("dotnet")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(SignerAssemblyMarker).Assembly.Location, "--socket", fixture.SocketPath,
            "--seed-stdin", "--state-file", Path.Combine(fixture.DirectoryPath, "state"), "--auth-token-file",
            Path.Combine(fixture.DirectoryPath, "token"), "--network", "regtest" }) start.ArgumentList.Add(arg);
        if (manifest) start.ArgumentList.Add("--authority-manifest");
        else
        {
            start.ArgumentList.Add("--authority-config");
            start.ArgumentList.Add(Path.Combine(fixture.DirectoryPath, "authority.json"));
        }
        return start;
    }

    private sealed class RestrictedSignerProcess(SignerDaemonFixture fixture) : IAsyncDisposable
    {
        private Process? _process;
        private Task<string>? _errors;
        private Task<string>? _output;
        public async Task StartAsync(CancellationToken ct)
        {
            _process = Process.Start(StartInfo(fixture, manifest: false))
                ?? throw new InvalidOperationException("Restricted signer process failed to start.");
            _errors = _process.StandardError.ReadToEndAsync(ct);
            await _process.StandardInput.WriteLineAsync((new string('0', 63) + "1").AsMemory(), ct);
            _process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("SIGNER_READY", StringComparison.Ordinal)) continue;
                _output = _process.StandardOutput.ReadToEndAsync(ct);
                return;
            }
            throw new InvalidOperationException("Restricted signer exited before authenticated readiness: " + await _errors);
        }
        public async Task StopAsync(CancellationToken ct)
        {
            if (_process is null) return;
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (_errors is not null) _ = await _errors;
            if (_output is not null) _ = await _output;
            _process.Dispose();
            _process = null;
            File.Delete(fixture.SocketPath);
        }
        public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
    }
}