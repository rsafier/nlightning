using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Application.Channels.Services;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Entities.Node;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Kills the process owning the node's real SQLite databases; the signer stays alive.</summary>
public sealed class NodeWorkflowCrashTests
{
    private const string ChildEnvironment = "NLTG_REMOTE_WORKFLOW_CHILD";

    [Theory]
    [InlineData(RemoteSigningRequestOutcome.Unknown)]
    [InlineData(RemoteSigningRequestOutcome.Invalidated)]
    [InlineData(RemoteSigningRequestOutcome.Unsupported)]
    public async Task IndeterminateReceiptPolicyBlocksDurableWorkflowWithoutDispatch(RemoteSigningRequestOutcome outcome)
    {
        // Supplementary policy proof: synthetic receipt outcomes over a real node database, no process-kill claim.
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (channelId, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000));
        await harness.ConfirmFundingAsync(channelId, funding.TransactionId);
        var channel = harness.Alice.Channel(channelId);
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var scope = await coordinator.BeginAsync(SigningWorkflowSnapshot.Create(channel, channel.Commitments!,
            SigningWorkflowKind.SendCommit));
        scope.Activate();
        var envelope = RemoteSignerConnection.Prepare(SignerOperations.RegisterChannel, channel.ChannelId,
            channel.GetSigningInfo()).ToByteArray();
        var parsed = WireRequest.Parser.ParseFrom(envelope);
        var material = new byte[sizeof(uint) + parsed.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, parsed.Operation);
        parsed.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        var dispatched = 0;
        Assert.Throws<RemoteSigningWorkflowException>(() => coordinator.Execute(parsed.Operation, envelope,
            SHA256.HashData(material), _ => new RemoteSigningRequestStatus(outcome), _ =>
            { dispatched++; return "[]"u8.ToArray(); }));
        Assert.Equal(0, dispatched);
        await harness.Alice.InScopeAsync(async uow =>
        {
            var workflow = await uow.SigningWorkflowDbRepository.GetAsync(scope.WorkflowId);
            Assert.NotNull(workflow);
            Assert.Equal(SigningWorkflowState.Blocked, workflow.State);
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(scope.WorkflowId));
            Assert.Equal(SigningRequestState.Blocked, request.State);
            Assert.Equal(envelope, request.Envelope);
            return 0;
        });
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("funding")]
    [InlineData("data-loss")]
    [InlineData("invalidated")]
    [InlineData("missing-receipt")]
    [InlineData("response-mismatch")]
    public async Task RestartRefusesDriftOrLostSafetyHistoryBeforeExecutingAnotherRequest(string mutation)
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        var directory = Path.Combine(signer.DirectoryPath, "node");
        Directory.CreateDirectory(directory);
        await using (var child = StartChild(directory, signer.SocketPath, "completed"))
        {
            await WaitForGateAsync(child, Path.Combine(directory, "gate"));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }
        var channelId = new NLightning.Domain.Channels.ValueObjects.ChannelId(
            Convert.FromHexString(File.ReadAllText(Path.Combine(directory, "channel"))));
        if (mutation == "invalidated")
        {
            using var observer = new RemoteSignerConnection(signer.Options());
            observer.Invoke(SignerOperations.MarkDataLoss, channelId);
        }
        else if (mutation == "missing-receipt")
        {
            var pending = Assert.Single(await ReadRequestsAsync(Path.Combine(directory, "alice.db")),
                request => request.Operation == SignerOperations.SignRemoteCommitmentPartial && request.State == 2);
            await signer.StopAsync();
            // Lose one public signature receipt; preserve registrations, safety history and the allocation journal.
            RemoveCompletedPartialReceipt(Path.Combine(signer.DirectoryPath, "state"), pending.RequestId);
            await signer.RestartAsync();
        }
        else
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "alice.db")};Pooling=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = mutation switch
            {
                "snapshot" => "UPDATE SigningWorkflows SET SnapshotFingerprint=zeroblob(32) WHERE State=1",
                "funding" => "UPDATE Channels SET FundingTxId=zeroblob(32)",
                "data-loss" => "UPDATE Channels SET DataLossDetected=1",
                "response-mismatch" => "UPDATE SigningRequests SET Response=x'5B5D' WHERE Operation=37 AND State=2",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation))
            };
            Assert.True(await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken) > 0);
        }
        var journal = File.ReadAllBytes(Path.Combine(signer.DirectoryPath, "state"));
        var before = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
        await using var recovered = StartChild(directory, signer.SocketPath, "recover");
        await recovered.WaitForExitWithDiagnosticsAsync();
        Assert.NotEqual(0, recovered.ExitCode);
        var failure = await ReadOutputAsync(recovered);
        Assert.False(File.Exists(Path.Combine(directory, "result")));
        // Authentication/identity and read-only reconciliation may happen; execution must not change the journal.
        Assert.Equal(journal, File.ReadAllBytes(Path.Combine(signer.DirectoryPath, "state")));
        var after = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
        Assert.Equal(before.Select(r => Convert.ToHexString(r.Envelope)), after.Select(r => Convert.ToHexString(r.Envelope)));
        var expected = mutation switch
        {
            "snapshot" => "Pending signing workflow belongs to a different channel snapshot",
            // A replaced funding ID prevents loading its persisted commitment engine, so the state guard rejects first.
            "funding" or "data-loss" => "Cannot resume signing workflow for this channel state",
            _ => "durable receipt no longer matches the node"
        };
        Assert.True(failure.Contains(expected, StringComparison.Ordinal), failure);
    }

    private static void RemoveCompletedPartialReceipt(string path, Guid requestId)
    {
        var journal = File.ReadAllBytes(path);
        // Native v2 journal: magic, compressed node identity and chain identity, then length-prefixed JSON records.
        const int headerLength = 8 + 33 + 32;
        using var remaining = new MemoryStream();
        remaining.Write(journal.AsSpan(0, headerLength));
        var position = headerLength;
        var removed = 0;
        while (position < journal.Length)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(position, sizeof(int)));
            Assert.True(size > 0 && position + sizeof(int) + size <= journal.Length);
            using var entry = JsonDocument.Parse(journal.AsMemory(position + sizeof(int), size));
            var root = entry.RootElement;
            var matches = root.GetProperty("Operation").GetUInt32() == SignerOperations.SignRemoteCommitmentPartial
                && root.TryGetProperty("RequestId", out var storedId)
                && storedId.ValueKind == JsonValueKind.String
                && storedId.GetString() == requestId.ToString("N");
            if (matches) removed++;
            else remaining.Write(journal.AsSpan(position, sizeof(int) + size));
            position += sizeof(int) + size;
        }
        Assert.Equal(1, removed);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        remaining.Position = 0;
        remaining.CopyTo(file);
        file.Flush(flushToDisk: true);
    }

    public static IEnumerable<TheoryDataRow<string>> Killpoints()
    {
        string[] killpoints =
        [
            "prepared", "reply", "completed", "consumed", "release", "revoked", "incoming-before-commit",
            "resign-prepared", "funding-prepared", "funding-reply", "funding-completed", "funding-consumed"
        ];
        foreach (var killpoint in killpoints)
            yield return new TheoryDataRow<string>(killpoint).WithTrait("killpoint", killpoint);
    }

    [Theory]
    [MemberData(nameof(Killpoints))]
    public async Task KilledNodeResumesExactSigningWorkflowFromItsDatabase(string killpoint)
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        var directory = Path.Combine(signer.DirectoryPath, "node");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "original-phase"), killpoint);
        await using (var child = StartChild(directory, signer.SocketPath, killpoint))
        {
            await WaitForGateAsync(child, Path.Combine(directory, "gate"));
            var before = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
            if (killpoint.StartsWith("funding-", StringComparison.Ordinal))
                Assert.Equal(killpoint == "funding-consumed" ? 0L : 1L,
                    await CountFundingReservationsAsync(Path.Combine(directory, "alice.db")));
            using var observer = new RemoteSignerConnection(signer.Options());
            if (killpoint is "reply" or "funding-reply")
            {
                var replyOperation = killpoint == "funding-reply" ? SignerOperations.SignFundingTransaction
                    : SignerOperations.SignRemoteCommitmentPartial;
                var pending = Assert.Single(before, r => r.Operation == replyOperation && r.State == 1);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                var receipt = observer.Reconcile(WireRequest.Parser.ParseFrom(pending.Envelope));
                while (receipt.Outcome == RequestOutcome.NotFound && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25, TestContext.Current.CancellationToken);
                    receipt = observer.Reconcile(WireRequest.Parser.ParseFrom(pending.Envelope));
                }
                Assert.Equal(RequestOutcome.Completed, receipt.Outcome);
                File.WriteAllBytes(Path.Combine(directory, "expected-response"), receipt.Response.Payload.ToByteArray());
            }
            if (killpoint is "prepared" or "resign-prepared" or "funding-prepared")
            {
                var preparedOperation = killpoint == "funding-prepared" ? SignerOperations.SignFundingTransaction
                    : SignerOperations.SignRemoteCommitmentPartial;
                var pending = Assert.Single(before, r => r.Operation == preparedOperation && r.State == 1);
                Assert.Equal(RequestOutcome.NotFound, observer.Reconcile(WireRequest.Parser.ParseFrom(pending.Envelope)).Outcome);
                if (killpoint == "resign-prepared")
                {
                    var resignedArguments = SignerWire.Decode(WireRequest.Parser.ParseFrom(pending.Envelope).Payload.ToByteArray());
                    var original = before.Last(r => r.Operation == SignerOperations.SignRemoteCommitmentPartial && r.State == 3);
                    var originalArguments = SignerWire.Decode(WireRequest.Parser.ParseFrom(original.Envelope).Payload.ToByteArray());
                    // The reconnect consumes a new signing nonce but retains the original commitment transaction.
                    Assert.Equal(originalArguments[2].GetRawText(), resignedArguments[2].GetRawText());
                    Assert.NotEqual(original.RequestId, pending.RequestId);
                }
            }
            if (killpoint is "release" or "incoming-before-commit")
            {
                var channelId = new NLightning.Domain.Channels.ValueObjects.ChannelId(
                    Convert.FromHexString(File.ReadAllText(Path.Combine(directory, "channel"))));
                // Whether the new local commitment was committed or interrupted, no secret precedes its advancement.
                Assert.Throws<SignerException>(() => observer.Invoke(SignerOperations.RevealPerCommitmentSecret,
                    channelId, 0UL));
                await using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "alice.db")};Pooling=False");
                await db.OpenAsync(TestContext.Current.CancellationToken);
                await using var query = db.CreateCommand();
                query.CommandText = "SELECT LocalCommitmentNumber FROM Channels";
                Assert.Equal(killpoint == "release" ? 1L : 0L,
                    Convert.ToInt64(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
            }
            if (killpoint == "consumed")
            {
                await using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "alice.db")};Pooling=False");
                await db.OpenAsync(TestContext.Current.CancellationToken);
                await using var query = db.CreateCommand();
                query.CommandText = "SELECT SentCommitDiff FROM Channels WHERE SentCommitDiff IS NOT NULL";
                var diff = Assert.IsType<byte[]>(await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
                Assert.NotEmpty(diff);
                File.WriteAllBytes(Path.Combine(directory, "expected-diff"), diff);
            }
            if (killpoint == "revoked")
                File.WriteAllBytes(Path.Combine(directory, "expected-revoke-response"),
                    Assert.Single(before, r => r.Operation == SignerOperations.RevealPerCommitmentSecret).Response!);
            // No Dispose or graceful shutdown can flush volatile node state before this kill.
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(0, child.ExitCode);
            await using var recovered = StartChild(directory, signer.SocketPath, "recover");
            await recovered.WaitForExitWithDiagnosticsAsync();
            Assert.True(recovered.ExitCode == 0, await ReadOutputAsync(recovered));
            var after = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
            if (killpoint.StartsWith("funding-", StringComparison.Ordinal))
                Assert.Equal(0L, await CountFundingReservationsAsync(Path.Combine(directory, "alice.db")));
            Assert.NotEmpty(before);
            foreach (var request in before)
            {
                var restored = Assert.Single(after, r => r.RequestId == request.RequestId);
                Assert.Equal(request.Envelope, restored.Envelope);
                Assert.Equal(3, restored.State); // Consumed with the channel transition, never replaced.
                Assert.NotNull(restored.Response);
                if (request.Response is not null)
                    Assert.Equal(request.Response, restored.Response);
                Assert.Equal(restored.Response, observer.Reconcile(WireRequest.Parser.ParseFrom(restored.Envelope))
                    .Response.Payload.ToByteArray());
            }
            if (killpoint is "reply" or "funding-reply")
                Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "expected-response")),
                    Assert.Single(after, r => r.Operation == (killpoint == "funding-reply"
                        ? SignerOperations.SignFundingTransaction : SignerOperations.SignRemoteCommitmentPartial) &&
                        before.Any(b => b.RequestId == r.RequestId)).Response);
            if (killpoint == "resign-prepared")
            {
                var original = before.Last(r => r.Operation == SignerOperations.SignRemoteCommitmentPartial && r.State == 3);
                var pending = Assert.Single(before, r => r.Operation == SignerOperations.SignRemoteCommitmentPartial && r.State == 1);
                var resumed = Assert.Single(after, r => r.RequestId == pending.RequestId);
                Assert.NotEqual(SignerWire.Read<MusigPartialSignatureWithNonce>(SignerWire.Decode(original.Response!)[0]).PublicNonce,
                    SignerWire.Read<MusigPartialSignatureWithNonce>(SignerWire.Decode(resumed.Response!)[0]).PublicNonce);
            }
        }
        Assert.Equal("converged", File.ReadAllText(Path.Combine(directory, "result")));
    }

    /// <summary>Test-only subprocess entrypoint, explicitly selected by the parent proof.</summary>
    [Fact(Explicit = true)]
    public async Task ChildEntrypoint()
    {
        var directory = Environment.GetEnvironmentVariable(ChildEnvironment)
            ?? throw new InvalidOperationException("Only the owning crash proof may start this worker.");
        var phase = Environment.GetEnvironmentVariable("NLTG_REMOTE_WORKFLOW_PHASE")!;
        ReportWorkerPhase($"starting {phase}");
        var options = new RemoteSignerOptions
        {
            SocketPath = Environment.GetEnvironmentVariable("NLTG_REMOTE_WORKFLOW_SOCKET")!,
            Network = "regtest",
            AuthToken = SignerDaemonFixture.Token,
            TimeoutSeconds = 120
        };
        var gate = new WorkflowGate(directory, phase);
        using var connection = new RemoteSignerConnection(options, async cancellation =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), cancellation);
            return new WithheldReplyStream(new NetworkStream(socket, ownsSocket: true), gate);
        });
        ReportWorkerPhase("creating persistent harness");
        await using var harness = await TaprootOpenHarness.CreateAsync(new PersistentHarnessDatabase(directory),
            (node, services) =>
            {
                services.AddLogging(builder => builder.AddProvider(new WorkerLoggerProvider()));
                if (node.Name != "Alice") return;
                node.PublicKeyManager = new RemoteSecureKeyManager(connection);
                services.AddSingleton<ISecureKeyManager>(node.PublicKeyManager);
                services.AddSingleton<ILightningSigner>(sp => new RemoteLightningSigner(connection,
                    sp.GetRequiredService<IChannelSigningInfoSource>(), sp.GetRequiredService<IUtxoMemoryRepository>()));
                services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp =>
                {
                    var coordinator = new RemoteSigningWorkflowCoordinator(connection,
                        sp.GetRequiredService<IServiceScopeFactory>(), attachCapture: false);
                    connection.AttachWorkflowCapture(new GatedCapture(coordinator, gate));
                    return coordinator;
                });
                services.ConfigureDbContext<NLightningDbContext>(o => o.AddInterceptors(gate));
            });
        if (phase == "recover")
        {
            foreach (var node in harness.Nodes)
            {
                ReportWorkerPhase($"loading stored channels for {node.Name}");
                await node.LoadStoredChannelsAsync();
            }
            if (File.Exists(Path.Combine(directory, "original-phase"))
             && File.ReadAllText(Path.Combine(directory, "original-phase")).StartsWith("funding-", StringComparison.Ordinal))
            {
                var funded = Assert.Single(harness.Alice.Memory.FindChannels(c => c.State == ChannelState.V1FundingSigned));
                ReportWorkerPhase("confirming recovered funding");
                await harness.ConfirmFundingAsync(funded.ChannelId, funded.FundingOutput!.TransactionId!.Value);
            }
            if (File.Exists(Path.Combine(directory, "expected-diff")))
                Assert.Equal(File.ReadAllBytes(Path.Combine(directory, "expected-diff")),
                    Assert.Single(harness.Alice.Memory.FindChannels(c => c.State == ChannelState.Open)).SentCommitDiff!.Value.ToArray());
            ReportWorkerPhase("reconnecting recovered peers");
            await harness.ReconnectAsync();
            ReportWorkerPhase("pumping recovered peer retransmissions");
            await harness.PumpAsync();
            ReportWorkerPhase("recovered peer retransmissions drained");
            if (File.Exists(Path.Combine(directory, "expected-revoke-response")))
            {
                var response = SignerWire.Decode(File.ReadAllBytes(Path.Combine(directory, "expected-revoke-response")));
                var secret = SignerWire.Read<Secret>(response[0]);
                Assert.Contains(harness.Sent.Where(s => s.From == "Alice").Select(s => s.Message)
                    .OfType<RevokeAndAckMessage>(), message => message.Payload.PerCommitmentSecret.Span.SequenceEqual((byte[])secret));
            }
        }
        else
        {
            gate.Armed = phase.StartsWith("funding-", StringComparison.Ordinal);
            var (channelId, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000), LightningMoney.Satoshis(300_000));
            await harness.ConfirmFundingAsync(channelId, funding.TransactionId);
            File.WriteAllText(Path.Combine(directory, "channel"), channelId.ToString());
            gate.Armed = phase != "resign-prepared";
            await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(50_000));
            await harness.Alice.Scheduler.WhenIdleAsync();
            // Async scheduler logs failures. A direct round surfaces its original exception to the owning proof.
            if (phase != "resign-prepared")
                await harness.Alice.Scheduler.SignNowAsync(channelId, TestContext.Current.CancellationToken);
            await harness.PumpAsync();
            if (phase == "resign-prepared")
            {
                await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(25_000));
                await harness.Alice.Scheduler.WhenIdleAsync();
                Assert.NotNull(harness.Alice.Channel(channelId).Commitments!.RemoteNextCommit);
                await harness.DisconnectAsync();
                gate.Armed = true;
                await harness.ReconnectAsync();
                await harness.PumpAsync();
            }
            throw new InvalidOperationException("Requested process killpoint was never reached.");
        }
        var channel = Assert.Single(harness.Alice.Memory.FindChannels(c => c.State == ChannelState.Open));
        ReportWorkerPhase("sending subsequent Alice payment");
        await harness.Alice.PayAsync(harness.Bob, channel.ChannelId, LightningMoney.Satoshis(20_000));
        ReportWorkerPhase("pumping subsequent Alice payment");
        await harness.PumpAsync();
        ReportWorkerPhase("sending subsequent Bob payment");
        await harness.Bob.PayAsync(harness.Alice, channel.ChannelId, LightningMoney.Satoshis(10_000));
        ReportWorkerPhase("pumping subsequent Bob payment");
        await harness.PumpAsync();
        ReportWorkerPhase("checking commitment and balance convergence");
        var a = harness.Alice.Channel(channel.ChannelId).Commitments!;
        var b = harness.Bob.Channel(channel.ChannelId).Commitments!;
        Assert.True(a.Htlcs.IsEmpty && b.Htlcs.IsEmpty);
        Assert.Null(a.RemoteNextCommit);
        Assert.Null(b.RemoteNextCommit);
        Assert.Equal(a.LocalCommit.Number, b.RemoteCommit.Number);
        Assert.Equal(a.RemoteCommit.Number, b.LocalCommit.Number);
        Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
        Assert.Equal(a.RemoteBalanceMsat, b.LocalBalanceMsat);
        foreach (var node in harness.Nodes)
            foreach (var accepted in node.Verified.GroupBy(v => v.Number))
                Assert.Single(accepted.Select(v => v.TxId).Distinct());
        File.WriteAllText(Path.Combine(directory, "result"), "converged");
        ReportWorkerPhase("converged; disposing harness");
    }

    private static void ReportWorkerPhase(string phase) =>
        Console.Error.WriteLine($"{DateTime.UtcNow:O} node workflow phase: {phase}");

    private static OwnedChild StartChild(string directory, string socketPath, string phase)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(NodeWorkflowCrashTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(NodeWorkflowCrashTests).FullName + ".ChildEntrypoint");
        start.ArgumentList.Add("-explicit"); start.ArgumentList.Add("on");
        start.ArgumentList.Add("-noColor");
        start.Environment[ChildEnvironment] = directory;
        start.Environment["NLTG_REMOTE_WORKFLOW_PHASE"] = phase;
        start.Environment["NLTG_REMOTE_WORKFLOW_SOCKET"] = socketPath;
        return new OwnedChild(Process.Start(start) ?? throw new InvalidOperationException("Failed to start node worker."),
            phase, File.Exists(Path.Combine(directory, "original-phase"))
                ? File.ReadAllText(Path.Combine(directory, "original-phase")) : "negative restart");
    }

    private static async Task WaitForGateAsync(OwnedChild process, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (!File.Exists(path) && !process.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        if (!File.Exists(path))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Fail("Node never reached killpoint: " + await ReadOutputAsync(process));
        }
    }

    private static Task<string> ReadOutputAsync(OwnedChild process) => process.Output;

    private sealed class OwnedChild : IAsyncDisposable
    {
        private const int MaximumOutputCharacters = 32 * 1024;
        private readonly Process _process;
        private readonly string _phase;
        private readonly string _killpoint;
        private readonly object _outputLock = new();
        private readonly StringBuilder _output = new();
        public Task<string> Output { get; }
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public OwnedChild(Process process, string phase, string killpoint)
        {
            _process = process;
            _phase = phase;
            _killpoint = killpoint;
            // Drain both pipes immediately, including while a process is parked at a killpoint.
            var stdout = DrainOutputAsync(process.StandardOutput);
            var stderr = DrainOutputAsync(process.StandardError);
            Output = JoinOutputAsync(stdout, stderr);
        }
        private async Task DrainOutputAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
                lock (_outputLock)
                {
                    _output.Append(buffer, 0, count);
                    if (_output.Length > MaximumOutputCharacters)
                        _output.Remove(0, _output.Length - MaximumOutputCharacters);
                }
        }
        private string OutputSnapshot()
        {
            lock (_outputLock) return _output.ToString();
        }
        private async Task<string> JoinOutputAsync(Task stdout, Task stderr)
        { await Task.WhenAll(stdout, stderr); return OutputSnapshot(); }
        public async Task WaitForExitWithDiagnosticsAsync()
        {
            try
            {
                await _process.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                var shutdown = "child killed and output drained";
                try
                {
                    if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await Output.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception exception)
                {
                    shutdown = $"child shutdown/output drain failed: {exception.Message}";
                }
                Assert.Fail($"Node worker timed out after 90 seconds (phase={_phase}, killpoint={_killpoint}, " +
                    $"pid={_process.Id}; {shutdown}). Last {MaximumOutputCharacters} output characters:\n{OutputSnapshot()}");
            }
        }
        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);
        public Task WaitForExitAsync(CancellationToken cancellation) => _process.WaitForExitAsync(cancellation);
        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            _process.Dispose();
        }
    }

    private sealed record StoredRequest(Guid RequestId, uint Operation, byte[] Envelope, int State, byte[]? Response);

    private static async Task<List<StoredRequest>> ReadRequestsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestId,Operation,Envelope,State,Response FROM SigningRequests ORDER BY CreatedAtTicks";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var result = new List<StoredRequest>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            result.Add(new StoredRequest(Guid.Parse(reader.GetString(0)), (uint)reader.GetInt64(1),
                (byte[])reader[2], reader.GetInt32(3), reader.IsDBNull(4) ? null : (byte[])reader[4]));
        return result;
    }

    private static async Task<long> CountFundingReservationsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM FeeInputReservations WHERE Purpose LIKE 'native-funding:%'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private sealed class PersistentHarnessDatabase(string directory) : ITaprootHarnessDatabase
    {
        public string Provider => "sqlite";
        public bool CreatesMigrated => false;
        public Task<string> CreateAsync(string name) => Task.FromResult($"Data Source={Path.Combine(directory, name + ".db")};Pooling=False");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WorkflowGate(string directory, string phase) : SaveChangesInterceptor
    {
        private bool _matches;
        public bool Armed { get; set; }
        public bool BlockReply { get; private set; }
        private void Before(DbContext? context)
        {
            if (!Armed || context is null) return;
            var requests = context.ChangeTracker.Entries<SigningRequestEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity);
            var workflows = context.ChangeTracker.Entries<SigningWorkflowEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity);
            if (phase == "incoming-before-commit" && workflows.Any(w => w.Kind == 2 && w.State == 1))
                BlockForever();
            _matches = phase switch
            {
                "prepared" or "resign-prepared" => requests.Any(r => r.Operation == SignerOperations.SignRemoteCommitmentPartial && r.State == 1),
                "funding-prepared" => requests.Any(r => r.Operation == SignerOperations.SignFundingTransaction && r.State == 1),
                "funding-completed" => requests.Any(r => r.Operation == SignerOperations.SignFundingTransaction && r.State == 2),
                "funding-consumed" => workflows.Any(w => w.Kind == (int)SigningWorkflowKind.Funding && w.State == 2),
                "completed" => requests.Any(r => r.Operation == SignerOperations.SignRemoteCommitmentPartial && r.State == 2),
                "consumed" => workflows.Any(w => w.Kind == 1 && w.State == 2),
                "release" => workflows.Any(w => w.Kind == 2 && w.State == 1),
                "revoked" => workflows.Any(w => w.Kind == 2 && w.State == 2),
                _ => false
            };
        }
        private void After()
        {
            if (!_matches) return;
            BlockForever();
        }
        public void BeforeExecute(uint operation)
        {
            if (Armed && (phase == "reply" && operation == SignerOperations.SignRemoteCommitmentPartial
                || phase == "funding-reply" && operation == SignerOperations.SignFundingTransaction))
                BlockReply = true;
        }
        public void BlockForever()
        {
            ReportWorkerPhase($"parked at killpoint {phase}");
            File.WriteAllText(Path.Combine(directory, "gate"), phase);
            // The parent kills this process; it cannot release the gate or flush another save.
            if (!new ManualResetEventSlim(false).Wait(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Owning proof did not kill its parked node worker.");
        }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Before(eventData.Context); return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Before(eventData.Context); return ValueTask.FromResult(result); }
        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        { After(); return result; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        { After(); return ValueTask.FromResult(result); }
    }

    private sealed class GatedCapture(IRemoteSigningRequestCapture inner, WorkflowGate gate) : IRemoteSigningRequestCapture
    {
        public byte[] Execute(uint operation, byte[] envelope, byte[] fingerprint,
            Func<byte[], RemoteSigningRequestStatus> reconcile, Func<byte[], byte[]> execute)
        {
            ReportWorkerPhase($"capturing signer operation {operation}");
            var response = inner.Execute(operation, envelope, fingerprint, saved =>
            {
                ReportWorkerPhase($"reconciling signer operation {operation}");
                var status = reconcile(saved);
                ReportWorkerPhase($"reconciled signer operation {operation}: {status.Outcome}");
                return status;
            }, saved =>
            {
                gate.BeforeExecute(operation);
                ReportWorkerPhase($"dispatching signer operation {operation}");
                return execute(saved);
            });
            ReportWorkerPhase($"captured signer operation {operation}");
            return response;
        }
    }

    private sealed class WorkerLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new WorkerLogger(categoryName);
        public void Dispose() { }
        private sealed class WorkerLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) Console.Error.WriteLine($"{category}: {formatter(state, exception)}\n{exception}");
            }
        }
    }

    private sealed class WithheldReplyStream(Stream inner, WorkflowGate gate) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            if (count > 0 && gate.BlockReply) gate.BlockForever();
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}