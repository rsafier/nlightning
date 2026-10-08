using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NBitcoin;
using NBitcoin.RPC;
using NLightning.Domain.Accounting.Labels;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Interfaces;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Entities.Node;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Integration.Tests.Docker.Utils;
using NLightning.RemoteSigning.Tests;

namespace NLightning.Integration.Tests.Cluster.Live;

public sealed partial class NativeWithdrawalProcessKillClusterTests
{
    /// <summary>Starts only when the owning live proof explicitly selects this subprocess entrypoint.</summary>
    [Fact(Explicit = true)]
    public async Task WithdrawalChildEntrypoint()
    {
        var directory = Environment.GetEnvironmentVariable(ChildEnvironment)
            ?? throw new InvalidOperationException("The owning withdrawal proof must configure this worker.");
        var phase = Environment.GetEnvironmentVariable("NLTG_NATIVE_WITHDRAWAL_PHASE")
            ?? throw new InvalidOperationException("Withdrawal worker phase is missing.");
        var ct = TestContext.Current.CancellationToken;
        var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(
            await File.ReadAllTextAsync(Path.Combine(directory, "worker.json"), ct))
            ?? throw new InvalidOperationException("Withdrawal worker configuration is missing.");
        var gate = new WithdrawalGate(directory, phase, configuration.PsbtPublication);
        var options = new RemoteSignerOptions
        {
            SocketPath = configuration.SocketPath,
            AuthToken = SignerDaemonFixture.Token,
            Network = "regtest",
            TimeoutSeconds = 120
        };
        using var connection = new RemoteSignerConnection(options, async cancellation =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(options.SocketPath), cancellation);
            return new WithheldWithdrawalReplyStream(new NetworkStream(socket, ownsSocket: true), gate);
        });
        var rpc = new RPCClient($"{configuration.RpcUser}:{configuration.RpcPassword}",
            configuration.RpcUrl, Network.RegTest);
        var endpoint = new RegtestBitcoinEndpoint(rpc, configuration.ZmqHost,
            configuration.ZmqBlockPort, configuration.ZmqTxPort);
        // The parent owns this database. Stop services on success, but do not call DisposeAsync: it deletes files.
        var node = await NLightningTestNode.CreateAsync(endpoint, "withdrawal-worker",
            TestNodeDatabase.Sqlite(Path.Combine(directory, "node.db")),
            secureKeyManager: new RemoteSecureKeyManager(connection));
        node.RemoteSignerConnection = connection;
        node.ChainNotifications = "Poll";
        node.ExtraConfiguration["Gossip:Enabled"] = "false";
        node.ExtraConfiguration["Gossip:SyncEnabled"] = "false";
        node.ExtraConfiguration["Gossip:RelayEnabled"] = "false";
        node.ExtraConfiguration["Node:Bootstrap:Enabled"] = "false";
        node.ExtraConfiguration["Signing:Mode"] = "RemoteNative";
        node.ExtraConfiguration["Signing:SocketPath"] = configuration.SocketPath;
        node.ExtraConfiguration["Signing:AuthTokenFile"] = Path.Combine(Path.GetDirectoryName(directory)!, "token");
        WithdrawalCapture? capture = null;
        node.ConfigureServices = services =>
        {
            services.ConfigureDbContext<NLightningDbContext>(o => o.AddInterceptors(gate));
            services.Replace(ServiceDescriptor.Singleton<IRemoteSigningWorkflowCoordinator>(provider =>
            {
                var scopes = provider.GetRequiredService<IServiceScopeFactory>();
                var coordinator = new RemoteSigningWorkflowCoordinator(connection, scopes, attachCapture: false);
                capture = new WithdrawalCapture(coordinator, gate);
                connection.AttachWorkflowCapture(capture);
                return phase == "synthetic-unknown"
                    ? new SyntheticUnknownWithdrawalRecovery(coordinator, scopes)
                    : coordinator;
            }));
        };
        ReportPhase($"starting {phase}");
        await node.StartAsync(ct);
        if (phase is "recover" or "synthetic-unknown" or "lost-receipt")
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "recovered"), phase, ct);
            var deadline = DateTime.UtcNow + s_timeout;
            while (DateTime.UtcNow < deadline)
            {
                var saved = await ReadSnapshotAsync(Path.Combine(directory, "node.db"));
                if (saved.Broadcasts is [{ State: 1 }])
                {
                    await node.StopAsync();
                    // Confirmed withdrawal reservations are cleaned on startup or the next withdrawal.
                    // Rebuild the actual node services against the retained database to prove startup cleanup.
                    if (capture is not null) connection.DetachWorkflowCapture(capture);
                    await node.StartAsync(ct);
                    var restarted = await ReadSnapshotAsync(Path.Combine(directory, "node.db"));
                    Assert.Null(restarted.ReservationId);
                    Assert.Empty(restarted.Inputs);
                    Assert.Equal(saved.WorkflowId, restarted.WorkflowId);
                    Assert.Equal(saved.WorkflowState, restarted.WorkflowState);
                    Assert.Equal(saved.Intent, restarted.Intent);
                    Assert.Equal(saved.Fingerprint, restarted.Fingerprint);
                    Assert.Equal(Assert.Single(saved.Broadcasts).Raw, Assert.Single(restarted.Broadcasts).Raw);
                    Assert.Equal(1, Assert.Single(restarted.Broadcasts).State);
                    var receipt = Assert.Single(saved.Requests);
                    var retained = Assert.Single(restarted.Requests);
                    Assert.Equal(receipt.RequestId, retained.RequestId);
                    Assert.Equal(receipt.Envelope, retained.Envelope);
                    Assert.Equal(receipt.Response, retained.Response);
                    Assert.Equal(receipt.State, retained.State);
                    await node.StopAsync();
                    ReportPhase("confirmed exact recovered withdrawal; restart retained its receipt and released its reservation");
                    return;
                }
                await Task.Delay(100, ct);
            }
            throw new TimeoutException("The recovered withdrawal did not confirm and end its reservation.");
        }

        ReportPhase("funding real wallet through Core");
        await node.FundWalletAsync(LightningMoney.Satoshis(200_000), configuration.Taproot ? NLightning.Domain.Bitcoin.Enums.AddressType.P2Tr : NLightning.Domain.Bitcoin.Enums.AddressType.P2Wpkh, ct);
        var destination = await rpc.GetNewAddressAsync(ct);
        gate.Armed = true;
        ReportPhase($"withdrawing at {phase}");
        if (configuration.PsbtPublication)
        {
            await node.Services.GetRequiredService<IWalletSpendService>().SendOutputsAsync(
                [(new BitcoinScript(destination.ScriptPubKey.ToBytes()), LightningMoney.Satoshis(40_000)),
                 (new BitcoinScript((await rpc.GetNewAddressAsync(ct)).ScriptPubKey.ToBytes()), LightningMoney.Satoshis(5_123))],
                1_000, 1, "killed PSBT publication", ct);
            throw new InvalidOperationException("The requested PSBT publication killpoint was never reached.");
        }
        await node.Services.GetRequiredService<IWalletSpendService>().WithdrawAsync(
            new WalletWithdrawRequest(destination.ToString(), LightningMoney.Satoshis(40_000), LightningMoney.Satoshis(2_500))
            { Labels = SourceLabels.Create("killed withdrawal", ["proof=native-withdrawal"]) }, ct);
        throw new InvalidOperationException("The requested withdrawal killpoint was never reached.");
    }

    private static void ReportPhase(string phase) => Console.Error.WriteLine($"{DateTime.UtcNow:O} native withdrawal worker: {phase}");

    private static WithdrawalWorker StartWorker(string directory, string phase)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[]
        {
            typeof(NativeWithdrawalProcessKillClusterTests).Assembly.Location, "-method",
            typeof(NativeWithdrawalProcessKillClusterTests).FullName + ".WithdrawalChildEntrypoint",
            "-explicit", "on", "-noColor"
        }) start.ArgumentList.Add(argument);
        start.Environment[ChildEnvironment] = directory;
        start.Environment["NLTG_NATIVE_WITHDRAWAL_PHASE"] = phase;
        return new WithdrawalWorker(Process.Start(start) ?? throw new InvalidOperationException("Withdrawal worker failed to start."), phase);
    }

    private sealed class WithdrawalWorker : IAsyncDisposable
    {
        private const int MaximumOutputCharacters = 32 * 1024;
        private readonly Process _process;
        private readonly string _phase;
        private readonly object _outputGate = new();
        private readonly StringBuilder _output = new();
        private readonly Task _draining;
        public int ExitCode => _process.ExitCode;
        public string OutputSnapshot { get { lock (_outputGate) return _output.ToString(); } }
        public WithdrawalWorker(Process process, string phase)
        {
            _process = process; _phase = phase;
            _draining = Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
        }
        private async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[1024];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
                lock (_outputGate)
                {
                    _output.Append(buffer, 0, count);
                    if (_output.Length > MaximumOutputCharacters)
                        _output.Remove(0, _output.Length - MaximumOutputCharacters);
                }
        }
        public async Task WaitForFileAsync(string path)
        {
            var deadline = DateTime.UtcNow + s_timeout;
            while (!File.Exists(path) && !_process.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(25, TestContext.Current.CancellationToken);
            if (!File.Exists(path))
                throw new InvalidOperationException($"Withdrawal worker did not reach {Path.GetFileName(path)} ({_phase}, pid={_process.Id}):\n{OutputSnapshot}");
        }
        public void Kill() { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        public async Task WaitForExitAsync()
        {
            try
            {
                await _process.WaitForExitAsync(TestContext.Current.CancellationToken)
                    .WaitAsync(s_timeout, TestContext.Current.CancellationToken);
                await _draining.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            catch (TimeoutException)
            {
                Kill();
                throw new TimeoutException($"Withdrawal worker timed out ({_phase}, pid={_process.Id}):\n{OutputSnapshot}");
            }
        }
        public async ValueTask DisposeAsync()
        {
            Kill();
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await _draining.WaitAsync(TimeSpan.FromSeconds(10));
            _process.Dispose();
        }
    }

    private sealed class WithdrawalGate(string directory, string phase, bool psbtPublication = false) : SaveChangesInterceptor
    {
        private readonly ConcurrentDictionary<Guid, bool> _matches = new();
        public bool Armed { get; set; }
        public bool BlockReply { get; private set; }
        private void Before(DbContext? context)
        {
            if (!Armed || context is null) return;
            var requests = context.ChangeTracker.Entries<SigningRequestEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity);
            var workflows = context.ChangeTracker.Entries<SigningWorkflowEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity);
            var matches = phase switch
            {
                "intent" => workflows.Any(w => w.Kind == ((int)(psbtPublication ? SigningWorkflowKind.WalletPsbtPublication : SigningWorkflowKind.WalletWithdrawal)) && w.State == 1),
                "prepared" => requests.Any(r => r.Operation == (psbtPublication ? SignerOperations.SignWalletTransaction2 : SignerOperations.SignWalletTransaction3) && r.State == 1),
                "completed" => requests.Any(r => r.Operation == (psbtPublication ? SignerOperations.SignWalletTransaction2 : SignerOperations.SignWalletTransaction3) && r.State == 2),
                "consumed" => workflows.Any(w => w.Kind == ((int)(psbtPublication ? SigningWorkflowKind.WalletPsbtPublication : SigningWorkflowKind.WalletWithdrawal)) && w.State == 2),
                _ => false
            };
            if (matches) _matches[context.ContextId.InstanceId] = true;
        }
        private void After(DbContext? context)
        {
            if (context is not null && _matches.TryRemove(context.ContextId.InstanceId, out _)) BlockForever();
        }
        public void BeforeExecute(uint operation)
        {
            if (Armed && phase == "reply" && operation == (psbtPublication ? SignerOperations.SignWalletTransaction2 : SignerOperations.SignWalletTransaction3)) BlockReply = true;
        }
        public void BlockForever()
        {
            ReportPhase($"parked after commit at {phase}");
            File.WriteAllText(Path.Combine(directory, "gate"), phase);
            if (!new ManualResetEventSlim(false).Wait(TimeSpan.FromMinutes(3)))
                throw new TimeoutException("The parent did not kill its parked withdrawal worker.");
        }
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        { Before(eventData.Context); return result; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Before(eventData.Context); return ValueTask.FromResult(result); }
        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        { After(eventData.Context); return result; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        { After(eventData.Context); return ValueTask.FromResult(result); }
    }

    private sealed class WithdrawalCapture(IRemoteSigningRequestCapture inner, WithdrawalGate gate) : IRemoteSigningRequestCapture
    {
        public byte[] Execute(uint operation, byte[] proposedEnvelope, byte[] argumentFingerprint,
            Func<byte[], RemoteSigningRequestStatus> reconcile, Func<byte[], byte[]> execute) =>
            inner.Execute(operation, proposedEnvelope, argumentFingerprint, reconcile, saved =>
            { gate.BeforeExecute(operation); return execute(saved); });
    }

    /// <summary>Explicit synthetic reconciliation outcome; this is not claimed as a natural wallet signing crash result.</summary>
    private sealed class SyntheticUnknownWithdrawalRecovery(RemoteSigningWorkflowCoordinator inner,
        IServiceScopeFactory scopes) : IRemoteSigningWorkflowCoordinator, INativeWalletSigningRecovery, INativeWalletPsbtSigningRecovery, IDisposable
    {
        public Task<ISigningWorkflowScope> BeginAsync(SigningWorkflowDescriptor descriptor) => inner.BeginAsync(descriptor);
        public Task StageAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork unitOfWork) => inner.StageAsync(descriptor, unitOfWork);
        public Task<IReadOnlyList<SigningWorkflow>> GetPendingAsync(ChannelId channelId) => inner.GetPendingAsync(channelId);
        public SignedTransaction? ReplayWithdrawal(ISigningWorkflowScope workflow) => ReplayUnknown(workflow);
        public SignedTransaction? ReplayPsbtPublication(ISigningWorkflowScope workflow) => ReplayUnknown(workflow);
        private SignedTransaction? ReplayUnknown(ISigningWorkflowScope workflow)
        {
            using var scope = scopes.CreateScope();
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var request = Assert.Single(unit.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId).GetAwaiter().GetResult());
            inner.Execute(request.Operation, request.Envelope, request.ArgumentFingerprint,
                _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.Unknown),
                _ => throw new InvalidOperationException("Unknown receipts must not execute another signing request."));
            throw new InvalidOperationException("Unknown withdrawal recovery must remain blocked.");
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class WithheldWithdrawalReplyStream(Stream inner, WithdrawalGate gate) : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await inner.ReadAsync(buffer, cancellationToken);
            if (count > 0 && gate.BlockReply) gate.BlockForever();
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}