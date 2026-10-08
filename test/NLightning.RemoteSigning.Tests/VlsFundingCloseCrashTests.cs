using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NBitcoin;
using NLightning.Application.Channels.Close;
using NLightning.Application.Channels.Handlers;
using NLightning.Application.Channels.Handlers.Interfaces;
using NLightning.Application.Channels.Safety;
using NLightning.Application.Channels.Safety.Interfaces;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Onchain.Enums;
using NLightning.Domain.Onchain.Models;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Persistence.Entities.Node;
using NLightning.Infrastructure.VlsSigning;
using AddressType = NLightning.Domain.Bitcoin.Enums.AddressType;

namespace NLightning.RemoteSigning.Tests;

/// <summary>
/// Funding, mutual close and force close through VLS as node workflows (NL-1330): the node process is killed at each
/// durable boundary (intent with its envelope, gateway committed before the reply, receipt, consumed with the
/// transition, before and after publication) against a real SQLite node and an actual Rust VLS gateway, then restarted.
/// </summary>
public sealed class VlsFundingCloseCrashTests
{
    private const string ChildEnvironment = "NLTG_VLS_FUNDCLOSE_CHILD";

    [Theory(Explicit = true)]
    [InlineData("funding-prepared")]
    [InlineData("funding-committed")]
    [InlineData("funding-completed")]
    [InlineData("funding-consumed")]
    [InlineData("funding-published")]
    [InlineData("close-prepared")]
    [InlineData("close-committed")]
    [InlineData("close-final-committed")]
    [InlineData("close-final-completed")]
    [InlineData("close-consumed")]
    [InlineData("close-published")]
    [InlineData("force-prepared")]
    [InlineData("force-committed")]
    [InlineData("force-completed")]
    [InlineData("force-consumed")]
    [InlineData("force-published")]
    public async Task Given_VlsFundingOrClose_When_NodeIsKilledAtABoundary_Then_OriginalRequestsRecoverAndPeersConverge(
        string phase)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var flow = phase[..phase.IndexOf('-')];
        var operation = OperationOf(flow);
        await using var signer = new VlsGatewayFixture();
        await signer.InitializeAsync(ct);
        var directory = Path.Combine(signer.DirectoryPath, "node");
        Directory.CreateDirectory(directory);

        // Act
        var before = await KillAtAsync(signer, directory, phase, ct);
        await signer.RestartAsync(ct);
        await RunToEndAsync(signer, directory, "recover-" + flow, ct);

        // Assert
        Assert.Equal("converged", File.ReadAllText(Path.Combine(directory, "result")));
        var after = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile });
        var interrupted = before.Where(r => r.Operation == operation).ToList();
        Assert.NotEmpty(interrupted);
        foreach (var request in interrupted)
        {
            // The same request ID and envelope, its exact receipt, consumed with the node transition
            var restored = Assert.Single(after, r => r.RequestId == request.RequestId);
            Assert.Equal(request.Envelope, restored.Envelope);
            Assert.Equal((int)SigningRequestState.Consumed, restored.State);
            Assert.NotNull(restored.Response);
            if (request.Response is not null)
                Assert.Equal(request.Response, restored.Response);
            var receipt = connection.ReconcileEnvelope(restored.Envelope);
            Assert.Equal(RemoteSigningRequestOutcome.Completed, receipt.Outcome);
            Assert.Equal(restored.Response, receipt.Response);
        }

        // Funding and force close never ask VLS twice; after the transition a mutual close is retransmitted from the
        // saved transaction, not signed again
        if (flow is "funding" or "force" || phase is "close-consumed" or "close-published")
            Assert.Equal(interrupted.Count, after.Count(r => r.Operation == operation));
        Assert.All(await ReadWorkflowsAsync(Path.Combine(directory, "alice.db")),
                   w => Assert.Equal((int)SigningWorkflowState.Consumed, w.State));
    }

    [Theory(Explicit = true)]
    [InlineData("funding-completed", "alter-response")]
    [InlineData("funding-completed", "lose-request")]
    [InlineData("close-prepared", "alter-envelope")]
    [InlineData("force-prepared", "alter-envelope")]
    [InlineData("force-completed", "lose-request")]
    public async Task Given_AlteredOrLostRecoveryHistory_When_TheNodeRestarts_Then_RecoveryRefusesWithoutSigning(
        string phase, string damage)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var flow = phase[..phase.IndexOf('-')];
        var operation = OperationOf(flow);
        await using var signer = new VlsGatewayFixture();
        await signer.InitializeAsync(ct);
        var directory = Path.Combine(signer.DirectoryPath, "node");
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "alice.db");
        var before = await KillAtAsync(signer, directory, phase, ct);
        var target = Assert.Single(before, r => r.Operation == operation);
        await DamageAsync(database, target, damage);
        await signer.RestartAsync(ct);

        // Act
        await RunToEndAsync(signer, directory, "refuse-" + flow, ct);

        // Assert
        Assert.Equal("refused", File.ReadAllText(Path.Combine(directory, "result")));
        var after = await ReadRequestsAsync(database);
        // No replacement request ID was created and nothing was dispatched for an envelope that was altered
        Assert.Equal(damage == "lose-request" ? 0 : 1, after.Count(r => r.Operation == operation));
        var workflow = Assert.Single(await ReadWorkflowsAsync(database), w => w.Kind == KindOf(flow));
        Assert.NotEqual((int)SigningWorkflowState.Consumed, workflow.State);
        if (damage == "alter-envelope")
        {
            var connection = new VlsSignerConnection(new VlsSignerOptions
            { SocketPath = signer.SocketPath, TokenFile = signer.TokenFile });
            var original = connection.ReconcileEnvelope(target.Envelope);
            Assert.Equal(RemoteSigningRequestOutcome.NotFound, original.Outcome);
        }
    }

    [Fact(Explicit = true)]
    public async Task ChildEntrypoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Environment.GetEnvironmentVariable(ChildEnvironment)
                     ?? throw new InvalidOperationException("Only the owning crash proof may start this worker.");
        var phase = Environment.GetEnvironmentVariable("NLTG_VLS_FUNDCLOSE_PHASE")!;
        var connection = new VlsSignerConnection(new VlsSignerOptions
        {
            SocketPath = Environment.GetEnvironmentVariable("NLTG_VLS_FUNDCLOSE_SOCKET")!,
            TokenFile = Environment.GetEnvironmentVariable("NLTG_VLS_TOKEN")!,
            TimeoutSeconds = 120
        });
        var approval = new VlsPaymentApprovalClient(Environment.GetEnvironmentVariable("NLTG_VLS_APPROVAL_SOCKET")!,
                                                     Environment.GetEnvironmentVariable("NLTG_VLS_APPROVAL_TOKEN")!);
        var gate = new BoundaryGate(directory, phase);
        var aliceScript = new BitcoinScript(new PubKey((byte[])new VlsSecureKeyManager(connection)
            .GetWalletPublicKey(7, false, AddressType.P2Wpkh)).WitHash.ScriptPubKey.ToBytes());
        var bobScript = new BitcoinScript(new Key(Enumerable.Repeat((byte)0xB7, 32).ToArray()).PubKey.WitHash
                                                                                       .ScriptPubKey.ToBytes());
        await using var harness = await TaprootOpenHarness.CreateAsync(new PersistentHarnessDatabase(directory),
            (node, services) =>
            {
                services.AddLogging(builder => builder.AddProvider(new WorkerLoggerProvider()));
                // The harness's fee mock answers only the async estimate; a restarted closing node also reads the cache
                var fees = new Mock<IFeeService>();
                fees.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(LightningMoney.Satoshis(2_500));
                fees.Setup(f => f.GetCachedFeeRatePerKw()).Returns(LightningMoney.Satoshis(2_500));
                services.AddSingleton(fees.Object);
                services.AddScoped<ShutdownScriptProvider>(_ => new FixedShutdownScriptProvider(
                    node.Name == "Alice" ? aliceScript : bobScript));
                services.AddChannelCloseServices();
                services.AddScoped<IChannelMessageHandler<ShutdownMessage>, ShutdownMessageHandler>();
                services.AddScoped<IChannelMessageHandler<ClosingSignedMessage>, ClosingSignedMessageHandler>();
                services.AddChannelSafetyServices();
                services.AddSingleton<IChannelErrorSender>(new NoErrorSender());
                if (node.Name != "Alice") return;
                node.Options.MaxDustHtlcExposureMsat = 0;
                node.Options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
                node.PublicKeyManager = new VlsSecureKeyManager(connection);
                services.AddSingleton<ISecureKeyManager>(node.PublicKeyManager);
                services.AddSingleton(connection);
                services.AddSingleton<VlsChannelMappingRegistry>();
                services.AddSingleton<ILightningSigner>(sp => new VlsLightningSigner(connection,
                    sp.GetRequiredService<VlsChannelMappingRegistry>(),
                    sp.GetRequiredService<IChannelSigningInfoSource>(),
                    sp.GetRequiredService<IUtxoMemoryRepository>()));
                services.AddSingleton<IVlsChannelSigner>(sp => (IVlsChannelSigner)sp.GetRequiredService<ILightningSigner>());
                services.AddSingleton<IVlsGossipSigner>(sp => (IVlsGossipSigner)sp.GetRequiredService<ILightningSigner>());
                services.AddSingleton<VlsSigningWorkflowCoordinator>();
                services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp =>
                    sp.GetRequiredService<VlsSigningWorkflowCoordinator>());
                services.ConfigureDbContext<NLightningDbContext>(o => o.AddInterceptors(gate));
            }, simpleTaproot: false);
        var alice = harness.Alice;
        var published = new List<SignedTransaction>();
        RecordPublications(alice, gate, published);
        var flow = phase[(phase.IndexOf('-') + 1)..];
        if (phase.StartsWith("recover-", StringComparison.Ordinal))
        {
            await RecoverAsync(harness, flow, published, approval, ct);
            File.WriteAllText(Path.Combine(directory, "result"), "converged");
            return;
        }

        if (phase.StartsWith("refuse-", StringComparison.Ordinal))
        {
            await RefuseAsync(harness, flow, published);
            File.WriteAllText(Path.Combine(directory, "result"), "refused");
            return;
        }

        if (phase.StartsWith("funding-", StringComparison.Ordinal))
            gate.Armed = true;
        var (channelId, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000));
        await harness.ConfirmFundingAsync(channelId, funding.TransactionId);
        await harness.Alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(300_000),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.PumpAsync();
        gate.Armed = true;
        if (phase.StartsWith("close-", StringComparison.Ordinal))
        {
            await alice.Services.GetRequiredService<IChannelCloseService>()
                       .CloseChannelAsync(channelId, new ChannelCloseRequest(), ct);
            await harness.PumpAsync();
        }
        else if (phase.StartsWith("force-", StringComparison.Ordinal))
        {
            await alice.Services.GetRequiredService<IChannelFailureService>()
                       .FailChannelAsync(channelId, new ChannelFailureRequest("crash proof", "crash proof"), ct);
        }

        throw new InvalidOperationException("Requested VLS process killpoint was never reached.");
    }

    private static async Task RecoverAsync(TaprootOpenHarness harness, string flow, List<SignedTransaction> published,
                                           VlsPaymentApprovalClient approval, CancellationToken ct)
    {
        var alice = harness.Alice;
        foreach (var node in harness.Nodes)
            await node.LoadStoredChannelsAsync();
        var channel = Assert.Single(alice.Memory.FindChannels(_ => true));
        var channelId = channel.ChannelId;
        switch (flow)
        {
            case "funding":
            {
                // The funding saved with its broadcast row is exactly the original request's receipt, witnessed by VLS
                var row = Assert.Single(await RowsAsync(alice, channelId), b => b.Purpose == BroadcastPurpose.Funding);
                var tx = Transaction.Load(row.RawTransaction, Network.RegTest);
                var walletKey = new PubKey((byte[])alice.PublicKeyManager.GetWalletPublicKey(0, false, AddressType.P2Wpkh));
                var spent = new TxOut(Money.Satoshis(TaprootOpenHarness.WalletSat), walletKey.WitHash.ScriptPubKey);
                Assert.Null(tx.CreateValidator([spent]).ValidateInput(0).Error);
                await harness.ReconnectAsync();
                await harness.PumpAsync();
                await harness.ConfirmFundingAsync(channelId, row.TransactionId);
                await alice.PayAsync(harness.Bob, channelId, LightningMoney.Satoshis(20_000),
                    invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
                await harness.PumpAsync();
                await harness.Bob.PayAsync(alice, channelId, LightningMoney.Satoshis(10_000));
                await harness.PumpAsync();
                var a = alice.Channel(channelId).Commitments!;
                var b = harness.Bob.Channel(channelId).Commitments!;
                Assert.True(a.Htlcs.IsEmpty && b.Htlcs.IsEmpty);
                Assert.Equal(a.LocalBalanceMsat, b.RemoteBalanceMsat);
                Assert.Equal(a.RemoteBalanceMsat, b.LocalBalanceMsat);
                break;
            }
            case "close":
            {
                var closingAtStart = alice.Channel(channelId).State == ChannelState.Closing;
                await harness.ReconnectAsync();
                await harness.PumpAsync();
                var ours = alice.Channel(channelId);
                var theirs = harness.Bob.Channel(channelId);
                Assert.Equal(ChannelState.Closing, ours.State);
                Assert.Equal(ChannelState.Closing, theirs.State);
                Assert.Equal(theirs.ClosingTransaction!.RawTxBytes, ours.ClosingTransaction!.RawTxBytes);
                var tx = Transaction.Load(ours.ClosingTransaction.RawTxBytes, Network.RegTest);
                Assert.Null(tx.CreateValidator([FundingTxOut(ours)]).ValidateInput(0).Error);
                // Every closing_signed we sent after the restart carries VLS's saved signature, never a new one
                var stored = VlsClosingSignature(tx, ours);
                var agreed = harness.Sent.Where(s => s.From == "Alice").Select(s => s.Message)
                                    .OfType<ClosingSignedMessage>()
                                    .Where(m => m.Payload.FeeAmount.Satoshi == FeeOf(tx, ours)).ToList();
                Assert.All(agreed, m => Assert.Equal(stored, m.Payload.Signature.Value));
                // A node already Closing answers the restarted negotiation with the saved signature
                if (closingAtStart)
                    Assert.NotEmpty(agreed);
                break;
            }
            case "force":
            {
                // A force close saved before the crash is published again from its exact saved bytes
                if (alice.Channel(channelId).State == ChannelState.Failed)
                    await alice.Services.GetRequiredService<IChannelFailureService>()
                               .FailChannelAsync(channelId, new ChannelFailureRequest("crash proof", "crash proof"), ct);
                var row = Assert.Single(await RowsAsync(alice, channelId),
                                        b => b.Purpose == BroadcastPurpose.LocalCommitment);
                Assert.Contains(published, p => p.RawTxBytes.AsSpan().SequenceEqual(row.RawTransaction));
                var ours = alice.Channel(channelId);
                Assert.Equal(ChannelState.Failed, ours.State);
                Assert.Equal(ours.Commitments!.LocalCommit.Number, row.CommitmentNumber);
                var tx = Transaction.Load(row.RawTransaction, Network.RegTest);
                Assert.Null(tx.CreateValidator([FundingTxOut(ours)]).ValidateInput(0).Error);
                break;
            }
        }
    }

    private static async Task RefuseAsync(TaprootOpenHarness harness, string flow, List<SignedTransaction> published)
    {
        await harness.Bob.LoadStoredChannelsAsync();
        var channels = await harness.Alice.InScopeAsync(async uow => (await uow.ChannelDbRepository.GetAllAsync()).ToList());
        var channel = Assert.Single(channels);
        var refused = await Record.ExceptionAsync(() => harness.Alice.ChannelManager.RegisterExistingChannelAsync(channel));
        Assert.NotNull(refused);
        Assert.Empty(published);
        var rows = await RowsAsync(harness.Alice, channel.ChannelId);
        // Nothing new was saved for publication: no funding for an interrupted funding, no commitment ever
        Assert.DoesNotContain(rows, b => b.Purpose == BroadcastPurpose.LocalCommitment
                                      || flow == "funding" && b.Purpose == BroadcastPurpose.Funding);
        var saved = await harness.Alice.InScopeAsync(uow => uow.ChannelDbRepository.GetByIdAsync(channel.ChannelId));
        Assert.Equal(channel.State, saved!.State);
    }

    private static void RecordPublications(TaprootOpenNode alice, BoundaryGate gate, List<SignedTransaction> published)
    {
        alice.ChainMonitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
             .Callback<BroadcastTransactionModel>(b =>
              {
                  lock (alice.Published)
                      alice.Published.Add(b);
                  lock (published)
                      published.Add(b.ToSignedTransaction());
                  gate.Published(b.Purpose == BroadcastPurpose.Funding ? "funding" : "force");
              })
             .ReturnsAsync(true);
        alice.ChainMonitor.Setup(m => m.PublishTransactionAsync(It.IsAny<SignedTransaction>()))
             .Callback<SignedTransaction>(tx =>
              {
                  lock (published)
                      published.Add(tx);
                  gate.Published("close");
              })
             .Returns(Task.CompletedTask);
    }

    private static Task<IReadOnlyList<BroadcastTransactionModel>> RowsAsync(TaprootOpenNode node,
        NLightning.Domain.Channels.ValueObjects.ChannelId channelId) =>
        node.InScopeAsync(async uow => await uow.BroadcastTransactionDbRepository.GetByChannelIdAsync(channelId)
                                    ?? []);

    private static TxOut FundingTxOut(ChannelModel channel)
    {
        var keys = new[] { new PubKey((byte[])channel.LocalFundingPubKey), new PubKey((byte[])channel.RemoteFundingPubKey!) }
                  .OrderBy(k => k.ToHex(), StringComparer.Ordinal).ToArray();
        return new TxOut(Money.Satoshis(channel.FundingOutput!.Amount.Satoshi),
                         PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, keys).WitHash.ScriptPubKey);
    }

    private static long FeeOf(Transaction tx, ChannelModel channel) =>
        channel.FundingOutput!.Amount.Satoshi - tx.Outputs.Sum(o => o.Value.Satoshi);

    private static byte[] VlsClosingSignature(Transaction tx, ChannelModel channel)
    {
        var witness = tx.Inputs[0].WitScript;
        var keys = PayToMultiSigTemplate.Instance.ExtractScriptPubKeyParameters(new Script(witness[3]))!.PubKeys;
        var index = Array.IndexOf(keys, new PubKey((byte[])channel.LocalFundingPubKey));
        return new TransactionSignature(witness[index + 1]).Signature.ToCompact();
    }

    private static uint OperationOf(string flow) => flow switch
    {
        "funding" => VlsOperations.WalletSign,
        "close" => VlsOperations.MutualClose,
        _ => VlsOperations.ForceClose
    };

    private static int KindOf(string flow) => (int)(flow switch
    {
        "funding" => SigningWorkflowKind.Funding,
        "close" => SigningWorkflowKind.MutualClose,
        _ => SigningWorkflowKind.ForceClose
    });

    private static async Task<List<StoredRequest>> KillAtAsync(VlsGatewayFixture signer, string directory, string phase,
                                                               CancellationToken ct)
    {
        await using var child = StartChild(directory, signer, phase);
        await WaitForGateAsync(child, Path.Combine(directory, "gate"));
        var before = await ReadRequestsAsync(Path.Combine(directory, "alice.db"));
        child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync(ct);
        return before;
    }

    private static async Task RunToEndAsync(VlsGatewayFixture signer, string directory, string phase,
                                            CancellationToken ct)
    {
        await using var child = StartChild(directory, signer, phase);
        await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(180), ct);
        Assert.True(child.ExitCode == 0, await child.Output);
    }

    private static async Task DamageAsync(string database, StoredRequest target, string damage)
    {
        await using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.Parameters.AddWithValue("$id", target.RawRequestId);
        switch (damage)
        {
            case "lose-request":
                command.CommandText = "DELETE FROM SigningRequests WHERE RequestId = $id";
                break;
            case "alter-response":
                var response = JsonNode.Parse(target.Response!)!.AsObject();
                var witness = response["witnesses"]![0]![0]!.GetValue<string>();
                response["witnesses"]![0]![0] = (witness[0] == '3' ? '4' : '3') + witness[1..];
                command.CommandText = "UPDATE SigningRequests SET Response = $value WHERE RequestId = $id";
                command.Parameters.AddWithValue("$value", JsonSerializer.SerializeToUtf8Bytes(response));
                break;
            default:
                var envelope = JsonNode.Parse(target.Envelope)!.AsObject();
                var field = envelope["command"]!.AsObject().ContainsKey("holder_sat") ? "holder_sat" : "number";
                envelope["command"]![field] = envelope["command"]![field]!.GetValue<ulong>() + 1;
                command.CommandText = "UPDATE SigningRequests SET Envelope = $value WHERE RequestId = $id";
                command.Parameters.AddWithValue("$value", JsonSerializer.SerializeToUtf8Bytes(envelope));
                break;
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    private static OwnedChild StartChild(string directory, VlsGatewayFixture signer, string phase)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(VlsFundingCloseCrashTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add(typeof(VlsFundingCloseCrashTests).FullName + ".ChildEntrypoint");
        start.ArgumentList.Add("-explicit");
        start.ArgumentList.Add("on");
        start.ArgumentList.Add("-noColor");
        start.Environment[ChildEnvironment] = directory;
        start.Environment["NLTG_VLS_FUNDCLOSE_PHASE"] = phase;
        start.Environment["NLTG_VLS_FUNDCLOSE_SOCKET"] = signer.SocketPath;
        start.Environment["NLTG_VLS_TOKEN"] = signer.TokenFile;
        start.Environment["NLTG_VLS_APPROVAL_SOCKET"] = signer.ApprovalSocketPath;
        start.Environment["NLTG_VLS_APPROVAL_TOKEN"] = signer.ApprovalTokenFile;
        return new OwnedChild(Process.Start(start) ?? throw new InvalidOperationException("Failed to start node worker."));
    }

    private static async Task WaitForGateAsync(OwnedChild process, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!File.Exists(path) && !process.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        if (!File.Exists(path))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.Fail("Node never reached killpoint: " + await process.Output);
        }
    }

    private sealed record StoredRequest(Guid RequestId, string RawRequestId, uint Operation, byte[] Envelope, int State,
                                        byte[]? Response);

    private sealed record StoredWorkflow(int Kind, int State);

    private static async Task<List<StoredRequest>> ReadRequestsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RequestId,Operation,Envelope,State,Response FROM SigningRequests ORDER BY CreatedAtTicks";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var result = new List<StoredRequest>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            result.Add(new StoredRequest(Guid.Parse(reader.GetString(0)), reader.GetString(0), (uint)reader.GetInt64(1),
                (byte[])reader[2], reader.GetInt32(3), reader.IsDBNull(4) ? null : (byte[])reader[4]));
        return result;
    }

    private static async Task<List<StoredWorkflow>> ReadWorkflowsAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind,State FROM SigningWorkflows";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var result = new List<StoredWorkflow>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            result.Add(new StoredWorkflow(reader.GetInt32(0), reader.GetInt32(1)));
        return result;
    }

    private sealed class OwnedChild : IAsyncDisposable
    {
        private readonly Process _process;
        public Task<string> Output { get; }
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;

        public OwnedChild(Process process)
        {
            _process = process;
            // Drain both pipes at once, also while the process is parked at a killpoint
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            Output = JoinOutputAsync(stdout, stderr);
        }

        private static async Task<string> JoinOutputAsync(Task<string> stdout, Task<string> stderr)
        {
            await Task.WhenAll(stdout, stderr);
            return await stdout + await stderr;
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

    private sealed class PersistentHarnessDatabase(string directory) : ITaprootHarnessDatabase
    {
        public string Provider => "sqlite";
        public bool CreatesMigrated => false;

        public Task<string> CreateAsync(string name) =>
            Task.FromResult($"Data Source={Path.Combine(directory, name + ".db")};Pooling=False");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedShutdownScriptProvider(BitcoinScript script)
        : ShutdownScriptProvider(Options.Create(new NodeOptions()), new Mock<IBitcoinWalletService>().Object)
    {
        public override Task<BitcoinScript> GetLocalScriptAsync(ChannelModel channel) => Task.FromResult(script);
    }

    private sealed class NoErrorSender : IChannelErrorSender
    {
        public Task<bool> TrySendAsync(CompactPubKey peer, ErrorMessage error) => Task.FromResult(false);
    }

    /// <summary>
    /// Parks the node at one durable boundary: after the save that makes it durable, or (<c>committed</c>) before the
    /// save that would record a gateway reply, so the gateway committed while the node keeps no receipt.
    /// </summary>
    private sealed class BoundaryGate(string directory, string phase) : SaveChangesInterceptor
    {
        private readonly Dictionary<string, int> _seen = [];
        private bool _matches;
        public bool Armed { get; set; }

        public void Published(string flow)
        {
            if (Armed && phase == flow + "-published")
                Park();
        }

        private bool Before(DbContext? context)
        {
            _matches = false;
            if (!Armed || context is null) return false;
            var requests = context.ChangeTracker.Entries<SigningRequestEntity>()
                                  .Where(e => e.State is EntityState.Added or EntityState.Modified)
                                  .Select(e => e.Entity).ToList();
            var workflows = context.ChangeTracker.Entries<SigningWorkflowEntity>()
                                   .Where(e => e.State is EntityState.Added or EntityState.Modified)
                                   .Select(e => e.Entity).ToList();
            var closingWatch = context.ChangeTracker.Entries()
                                      .Any(e => e.State == EntityState.Added
                                             && e.Entity.GetType().Name == "WatchedTransactionEntity");
            var (operation, kind) = phase[..phase.IndexOf('-')] switch
            {
                "funding" => (VlsOperations.WalletSign, (int)SigningWorkflowKind.Funding),
                "close" => (VlsOperations.MutualClose, (int)SigningWorkflowKind.MutualClose),
                _ => (VlsOperations.ForceClose, (int)SigningWorkflowKind.ForceClose)
            };
            var prepared = requests.Any(r => r.Operation == operation && r.State == 1);
            var completed = requests.Any(r => r.Operation == operation && r.State == 2);
            var consumed = workflows.Any(w => w.Kind == kind && w.State == 2);
            var boundary = phase[(phase.IndexOf('-') + 1)..];
            var occurrence = Count(completed ? "completed" : prepared ? "prepared" : consumed ? "consumed" : "");
            var before = false;
            _matches = boundary switch
            {
                "prepared" => prepared && occurrence == 1,
                "committed" => before = completed && occurrence == 1,
                "completed" => completed && occurrence == 1,
                "final-committed" => before = completed && occurrence == 2,
                "final-completed" => completed && occurrence == 2,
                "consumed" => consumed && (kind != (int)SigningWorkflowKind.MutualClose || closingWatch),
                _ => false
            };
            if (_matches && before)
            {
                _matches = false;
                Park();
            }

            return _matches;
        }

        private int Count(string key)
        {
            if (key.Length == 0) return 0;
            _seen[key] = _seen.GetValueOrDefault(key) + 1;
            return _seen[key];
        }

        private void After()
        {
            if (_matches)
                Park();
        }

        private void Park()
        {
            File.WriteAllText(Path.Combine(directory, "gate"), phase);
            if (!new ManualResetEventSlim(false).Wait(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Owning proof did not kill its parked VLS node worker.");
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData,
                                                              InterceptionResult<int> result)
        {
            Before(eventData.Context);
            return result;
        }

        public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
        {
            After();
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Before(eventData.Context);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
                                                         CancellationToken cancellationToken = default)
        {
            After();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class WorkerLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new WorkerLogger(categoryName);
        public void Dispose() { }

        private sealed class WorkerLogger(string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) =>
                logLevel >= LogLevel.Warning && !category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                    Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel)) Console.Error.WriteLine($"{category}: {formatter(state, exception)}\n{exception}");
            }
        }
    }
}