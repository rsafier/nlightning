using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>Synthetic reconciliation outcomes against production SQLite persistence; no VLS policy/process-kill claim.</summary>
public sealed class VlsWorkflowReceiptPolicyTests
{
    [Theory]
    [InlineData(RemoteSigningRequestOutcome.Unknown)]
    [InlineData(RemoteSigningRequestOutcome.Invalidated)]
    [InlineData(RemoteSigningRequestOutcome.Unsupported)]
    public async Task UncertainPreparedReceiptBlocksWithoutDispatch(RemoteSigningRequestOutcome outcome)
    {
        await using var identity = await IdentityFixture.CreateAsync();
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (descriptor, command) = await CreateMappingAsync(identity.Connection, harness);
        using var coordinator = new VlsSigningWorkflowCoordinator(identity.Connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var scope = await coordinator.BeginAsync(descriptor);
        scope.Activate();
        var envelope = VlsSignerConnection.EncodeRequest(Guid.NewGuid(), command);
        var executions = 0;
        Assert.Throws<VlsSigningWorkflowException>(() => coordinator.Execute(VlsOperations.SignRemote, envelope,
            Fingerprint(command), _ => new(outcome), _ => { executions++; return "{}"u8.ToArray(); }));
        Assert.Equal(0, executions);
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Equal(SigningWorkflowState.Blocked, (await uow.SigningWorkflowDbRepository.GetAsync(scope.WorkflowId))!.State);
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(scope.WorkflowId));
            Assert.Equal(SigningRequestState.Blocked, request.State);
            Assert.Equal(envelope, request.Envelope);
            return 0;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedReceiptMustStillExistAndMatchAfterCoordinatorRestart(bool changedResponse)
    {
        await using var identity = await IdentityFixture.CreateAsync();
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (descriptor, command) = await CreateMappingAsync(identity.Connection, harness);
        var original = VlsSignerConnection.EncodeRequest(Guid.NewGuid(), command);
        Guid workflowId;
        using (var coordinator = new VlsSigningWorkflowCoordinator(identity.Connection,
                   harness.Alice.Services.GetRequiredService<IServiceScopeFactory>()))
        using (var scope = await coordinator.BeginAsync(descriptor))
        {
            workflowId = scope.WorkflowId;
            scope.Activate();
            var result = coordinator.Execute(VlsOperations.SignRemote, original, Fingerprint(command),
                _ => new(RemoteSigningRequestOutcome.NotFound), _ => "{\"signature\":\"original\"}"u8.ToArray());
            Assert.Equal("{\"signature\":\"original\"}"u8.ToArray(), result);
        }
        using var restarted = new VlsSigningWorkflowCoordinator(identity.Connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var resumed = await restarted.BeginAsync(descriptor);
        resumed.Activate();
        byte[]? reconciledEnvelope = null;
        var dispatched = 0;
        var proposal = VlsSignerConnection.EncodeRequest(Guid.NewGuid(), command);
        Assert.Throws<VlsSigningWorkflowException>(() => restarted.Execute(VlsOperations.SignRemote,
            proposal, Fingerprint(command), envelope =>
            {
                reconciledEnvelope = envelope;
                return changedResponse ? new(RemoteSigningRequestOutcome.Completed, "{}"u8.ToArray())
                                       : new(RemoteSigningRequestOutcome.NotFound);
            }, _ => { dispatched++; return "{}"u8.ToArray(); }));
        Assert.Equal(original, reconciledEnvelope);
        Assert.Equal(0, dispatched);
        Assert.Equal(workflowId, resumed.WorkflowId);
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Equal(SigningWorkflowState.Blocked, (await uow.SigningWorkflowDbRepository.GetAsync(workflowId))!.State);
            return 0;
        });
    }

    private static async Task<(SigningWorkflowDescriptor Descriptor, JsonObject Command)> CreateMappingAsync(
        VlsSignerConnection connection, TaprootOpenHarness harness)
    {
        var channel = new ChannelId(Enumerable.Repeat((byte)5, 32).ToArray());
        var peer = new CompactPubKey(Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
        var vlsId = new byte[41];
        ((byte[])peer).CopyTo(vlsId, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(vlsId.AsSpan(33), 1);
        var allocationId = Guid.NewGuid();
        var allocation = VlsSignerConnection.EncodeRequest(allocationId,
            new JsonObject { ["op"] = "allocate", ["peer"] = peer.ToString(), ["dbid"] = 1 });
        var ticks = DateTime.UtcNow.Ticks;
        await harness.Alice.InScopeAsync(async uow =>
        {
            await uow.VlsChannelMappingDbRepository.AddAsync(new VlsChannelMapping(0, 1, peer, null,
                (byte[])connection.Identity.NodePublicKey, "regtest", allocationId, allocation, null, null, ticks, ticks));
            await uow.SaveChangesAsync();
            await uow.VlsChannelMappingDbRepository.CompleteAllocationAsync(0, "{}"u8.ToArray(), vlsId);
            await uow.VlsChannelMappingDbRepository.BindChannelAsync(0, channel);
            await uow.SaveChangesAsync();
            return 0;
        });
        return (new(channel, SigningWorkflowKind.SendCommit, 0, 0, SHA256.HashData("snapshot"u8)),
            new JsonObject { ["op"] = "sign_remote", ["channel"] = Convert.ToHexString(vlsId).ToLowerInvariant(), ["number"] = 1 });
    }

    private static byte[] Fingerprint(JsonObject command)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command);
        var material = new byte[sizeof(uint) + bytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, VlsOperations.SignRemote);
        bytes.CopyTo(material, sizeof(uint));
        return SHA256.HashData(material);
    }

    private sealed class IdentityFixture(string directory, VlsSignerConnection connection) : IAsyncDisposable
    {
        public VlsSignerConnection Connection { get; } = connection;
        public static async Task<IdentityFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "vls-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "socket");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptAsync(TestContext.Current.CancellationToken);
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                using var reader = new StreamReader(stream, leaveOpen: true);
                Assert.NotNull(await reader.ReadLineAsync(TestContext.Current.CancellationToken));
                await stream.WriteAsync("{\"ok\":true,\"result\":{\"node_id\":\"0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798\",\"network\":\"regtest\",\"derivation\":\"native\"}}\n"u8.ToArray(), TestContext.Current.CancellationToken);
            });
            var connection = new VlsSignerConnection(new() { SocketPath = path, AuthToken = new string('a', 32) });
            await server;
            return new(directory, connection);
        }
        public ValueTask DisposeAsync()
        {
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }
}