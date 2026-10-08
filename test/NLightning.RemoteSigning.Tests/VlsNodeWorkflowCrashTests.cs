using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Entities.Node;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Actual node process kills with real SQLite and an independent actual Rust VLS gateway.</summary>
public sealed class VlsNodeWorkflowCrashTests
{
    private const string ChildEnvironment = "NLTG_VLS_WORKFLOW_CHILD";

    [Theory(Explicit = true)]
    [InlineData("prepared")]
    [InlineData("completed")]
    [InlineData("consumed")]
    [InlineData("release")]
    [InlineData("revoked")]
    [InlineData("holder-prepared")]
    [InlineData("holder-completed")]
    [InlineData("peer-revoke-prepared")]
    [InlineData("peer-revoke-completed")]
    public async Task Given_DurableVlsWorkflow_When_NodeIsKilled_Then_OriginalRequestsRecoverAndPeersConverge(string phase)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var signer = new VlsGatewayFixture();
        await signer.InitializeAsync(ct);
        var directory = Path.Combine(signer.DirectoryPath, "node");
        Directory.CreateDirectory(directory);
        List<StoredRequest> before;
        await using (var child = StartChild(directory, signer.SocketPath, phase, signer.TokenFile,
                                            signer.ApprovalSocketPath, signer.ApprovalTokenFile))
        {
            await WaitForGateAsync(child, Path.Combine(directory, "gate"));
            before = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
            Assert.Contains(before, r => r.Operation == VlsOperations.SignRemote);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(ct);
        }
        // Both process boundaries restart. Redb and SQLite retain their original durable state.
        await signer.RestartAsync(ct);
        await using (var recovered = StartChild(directory, signer.SocketPath, "recover", signer.TokenFile,
                                                signer.ApprovalSocketPath, signer.ApprovalTokenFile))
        {
            await recovered.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(120), ct);
            Assert.True(recovered.ExitCode == 0, await ReadOutputAsync(recovered));
        }
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile });
        var after = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
        foreach (var request in before)
        {
            var restored = Assert.Single(after, r => r.RequestId == request.RequestId);
            Assert.Equal(request.Envelope, restored.Envelope);
            Assert.Equal(3, restored.State);
            Assert.NotNull(restored.Response);
            if (request.Response is not null)
                Assert.Equal(request.Response, restored.Response);
            var receipt = connection.ReconcileEnvelope(restored.Envelope);
            Assert.Equal(RemoteSigningRequestOutcome.Completed, receipt.Outcome);
            Assert.Equal(restored.Response, receipt.Response);
        }
        Assert.Equal("converged", File.ReadAllText(Path.Combine(directory, "result")));
    }

    [Fact(Explicit = true)]
    public async Task ChildEntrypoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Environment.GetEnvironmentVariable(ChildEnvironment)
            ?? throw new InvalidOperationException("Only the owning crash proof may start this worker.");
        var phase = Environment.GetEnvironmentVariable("NLTG_VLS_WORKFLOW_PHASE")!;
        var connection = new VlsSignerConnection(new VlsSignerOptions
        {
            SocketPath = Environment.GetEnvironmentVariable("NLTG_VLS_WORKFLOW_SOCKET")!,
            TokenFile = Environment.GetEnvironmentVariable("NLTG_VLS_TOKEN")!,
            TimeoutSeconds = 120
        });
        var approval = new VlsPaymentApprovalClient(Environment.GetEnvironmentVariable("NLTG_VLS_APPROVAL_SOCKET")!,
                                                     Environment.GetEnvironmentVariable("NLTG_VLS_APPROVAL_TOKEN")!);
        var gate = new WorkflowGate(directory, phase);
        await using var harness = await TaprootOpenHarness.CreateAsync(new PersistentHarnessDatabase(directory),
            (node, services) =>
            {
                services.AddLogging(builder => builder.AddProvider(new WorkerLoggerProvider()));
                if (node.Name != "Alice") return;
                node.Options.MaxDustHtlcExposureMsat = 0;
                node.Options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
                node.Options.Routing.FeeBaseMsat = 1_000;
                node.Options.Routing.FeeProportionalMillionths = 0;
                node.PublicKeyManager = new VlsSecureKeyManager(connection);
                services.AddSingleton<ISecureKeyManager>(node.PublicKeyManager);
                services.AddSingleton(connection);
                services.AddSingleton<VlsChannelMappingRegistry>();
                services.AddSingleton<ILightningSigner>(sp => new VlsLightningSigner(connection,
                    sp.GetRequiredService<VlsChannelMappingRegistry>(), sp.GetRequiredService<IChannelSigningInfoSource>(),
                    sp.GetRequiredService<IUtxoMemoryRepository>()));
                services.AddSingleton<IVlsChannelSigner>(sp => (IVlsChannelSigner)sp.GetRequiredService<ILightningSigner>());
                services.AddSingleton<IVlsGossipSigner>(sp => (IVlsGossipSigner)sp.GetRequiredService<ILightningSigner>());
                services.AddSingleton<VlsSigningWorkflowCoordinator>();
                services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp => sp.GetRequiredService<VlsSigningWorkflowCoordinator>());
                services.ConfigureDbContext<NLightningDbContext>(o => o.AddInterceptors(gate));
            }, simpleTaproot: false);
        if (phase == "recover")
        {
            foreach (var node in harness.Nodes) await node.LoadStoredChannelsAsync();
            await harness.ReconnectAsync();
            await harness.PumpAsync();
        }
        else
        {
            var (channelId, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000));
            await harness.ConfirmFundingAsync(channelId, funding.TransactionId);
            await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(300_000),
                invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
            await harness.PumpAsync();
            gate.Armed = true;
            await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(50_000),
                invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
            await harness.Alice.Scheduler.WhenIdleAsync();
            await harness.Alice.Scheduler.SignNowAsync(channelId, ct);
            await harness.PumpAsync();
            throw new InvalidOperationException("Requested VLS process killpoint was never reached.");
        }
        var channel = Assert.Single(harness.Alice.Memory.FindChannels(c => c.State == ChannelState.Open));
        await harness.Alice.PayAsync(harness.Bob, channel.ChannelId, LightningMoney.Satoshis(20_000),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.PumpAsync();
        await harness.Bob.PayAsync(harness.Alice, channel.ChannelId, LightningMoney.Satoshis(10_000));
        await harness.PumpAsync();
        var a = harness.Alice.Channel(channel.ChannelId).Commitments!;
        var b = harness.Bob.Channel(channel.ChannelId).Commitments!;
        Assert.True(a.Htlcs.IsEmpty && b.Htlcs.IsEmpty);
        Assert.Null(a.RemoteNextCommit);
        Assert.Null(b.RemoteNextCommit);
        Assert.Equal(a.LocalCommit.Number, b.RemoteCommit.Number);
        Assert.Equal(a.RemoteCommit.Number, b.LocalCommit.Number);
        Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
        Assert.Equal(a.RemoteBalanceMsat, b.LocalBalanceMsat);
        File.WriteAllText(Path.Combine(directory, "result"), "converged");
    }

    private static OwnedChild StartChild(string directory, string socketPath, string phase, string tokenFile, string approvalSocket, string approvalTokenFile)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(VlsNodeWorkflowCrashTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(VlsNodeWorkflowCrashTests).FullName + ".ChildEntrypoint");
        start.ArgumentList.Add("-explicit"); start.ArgumentList.Add("on");
        start.ArgumentList.Add("-noColor");
        start.Environment[ChildEnvironment] = directory;
        start.Environment["NLTG_VLS_WORKFLOW_PHASE"] = phase;
        start.Environment["NLTG_VLS_WORKFLOW_SOCKET"] = socketPath;
        start.Environment["NLTG_VLS_TOKEN"] = tokenFile;
        start.Environment["NLTG_VLS_APPROVAL_SOCKET"] = approvalSocket;
        start.Environment["NLTG_VLS_APPROVAL_TOKEN"] = approvalTokenFile;
        return new OwnedChild(Process.Start(start) ?? throw new InvalidOperationException("Failed to start node worker."));
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
        private readonly Process _process;
        public Task<string> Output { get; }
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public OwnedChild(Process process)
        {
            _process = process;
            // Drain both pipes immediately, including while a process is parked at a killpoint.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Output = JoinOutputAsync(stdout, stderr);
        }
        private static async Task<string> JoinOutputAsync(Task<string> stdout, Task<string> stderr)
        { await Task.WhenAll(stdout, stderr); return await stdout + await stderr; }
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
        private void Before(DbContext? context)
        {
            _matches = false;
            if (!Armed || context is null) return;
            var requests = context.ChangeTracker.Entries<SigningRequestEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity);
            var workflows = context.ChangeTracker.Entries<SigningWorkflowEntity>()
                .Where(e => e.State is EntityState.Added or EntityState.Modified).Select(e => e.Entity);
            _matches = phase switch
            {
                "holder-prepared" => requests.Any(r => r.Operation == VlsOperations.ValidateHolder && r.State == 1),
                "holder-completed" => requests.Any(r => r.Operation == VlsOperations.ValidateHolder && r.State == 2),
                "peer-revoke-prepared" => requests.Any(r => r.Operation == VlsOperations.ValidateRevocation && r.State == 1),
                "peer-revoke-completed" => requests.Any(r => r.Operation == VlsOperations.ValidateRevocation && r.State == 2),
                "prepared" => requests.Any(r => r.Operation == VlsOperations.SignRemote && r.State == 1),
                "completed" => requests.Any(r => r.Operation == VlsOperations.SignRemote && r.State == 2),
                "consumed" => workflows.Any(w => w.Kind == 1 && w.State == 2),
                "release" => workflows.Any(w => w.Kind == 2 && w.State == 1),
                "revoked" => workflows.Any(w => w.Kind == 2 && w.State == 2),
                _ => false
            };
        }
        private void After()
        {
            if (!_matches) return;
            File.WriteAllText(Path.Combine(directory, "gate"), phase);
            if (!new ManualResetEventSlim(false).Wait(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Owning proof did not kill its parked VLS node worker.");
        }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Before(eventData.Context); return ValueTask.FromResult(result); }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        { After(); return ValueTask.FromResult(result); }
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

}