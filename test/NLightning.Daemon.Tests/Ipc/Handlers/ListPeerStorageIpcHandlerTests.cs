using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.PeerStorage;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>listpeerstorage</c> (ClientCommand 32, NL-432) over IPC: the retrievals and kept blobs of
/// <see cref="IPeerStorageService"/> cross the envelope with every field, a node id filters both lists, the blob bytes
/// come back only when asked for, and a node without peer storage answers <c>invalid_operation</c>.
/// </summary>
public class ListPeerStorageIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly CompactPubKey s_peerA = Key(0x02, 0x11);
    private static readonly CompactPubKey s_peerB = Key(0x03, 0x22);
    private static readonly ChannelId s_known = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_lost = new(Enumerable.Repeat((byte)0xA2, 32).ToArray());
    private static readonly DateTimeOffset s_receivedAt = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private readonly Mock<IPeerStorageService> _service = new();

    public ListPeerStorageIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        var contents = new PeerBackupContents(s_receivedAt.AddHours(-1),
                                              [new PeerBackupChannel(s_known, s_peerA), new PeerBackupChannel(s_lost, s_peerB)]);
        _service.Setup(s => s.ListRetrievalsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([
                    new PeerStorageRetrievalReport(s_peerA, s_receivedAt, [1, 2, 3], contents, null,
                                                   [
                                                       new PeerBackupChannelStatus(s_known, s_peerA, false, true),
                                                       new PeerBackupChannelStatus(s_lost, s_peerB, true, false)
                                                   ], true),
                    new PeerStorageRetrievalReport(s_peerB, s_receivedAt.AddMinutes(1), [9], null, false, [], false)
                ]);
        _service.Setup(s => s.ListStoredBlobsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync([new StoredPeerBlob(s_peerB, [7, 7], s_receivedAt.AddMinutes(2))]);
        _service.Setup(s => s.GetRefusals())
                .Returns([new PeerStorageRefusalReport(s_peerA, 2, 1024, PeerStorageConstants.MaxBlobLength,
                                                        s_receivedAt.AddMinutes(3))]);
        _service.SetupGet(s => s.BackupsHeldForDataLoss).Returns(true);
    }

    [Fact]
    public async Task Given_RetrievalsAndBlobs_When_Listed_Then_EveryFieldCrossesTheEnvelopeWithoutTheBlobs()
    {
        // Arrange
        var handler = GetHandler(_service.Object);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new ListPeerStorageIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = Deserialize(response);
        Assert.True(payload.BackupsHeldForDataLoss);
        Assert.Equal(2, payload.Retrievals.Count);
        var ours = payload.Retrievals[0];
        Assert.Equal(s_peerA, ours.PeerNodeId);
        Assert.Equal(s_receivedAt, ours.ReceivedAt);
        Assert.Equal(3, ours.BlobLength);
        Assert.True(ours.IsOurs);
        Assert.Equal(s_receivedAt.AddHours(-1), ours.BackupCreatedAt);
        Assert.Null(ours.MatchesLastSent);
        Assert.True(ours.Persisted);
        Assert.Null(ours.Blob);
        Assert.Equal(2, ours.Channels.Count);
        Assert.Equal(s_lost, ours.Channels[1].ChannelId);
        Assert.Equal(s_peerB, ours.Channels[1].PeerNodeId);
        Assert.True(ours.Channels[1].UnknownWhenReceived);
        Assert.False(ours.Channels[1].KnownNow);
        Assert.True(ours.Channels[0].KnownNow);
        var foreign = payload.Retrievals[1];
        Assert.False(foreign.IsOurs);
        Assert.Null(foreign.BackupCreatedAt);
        Assert.False(foreign.MatchesLastSent);
        Assert.False(foreign.Persisted);
        Assert.Empty(foreign.Channels);
        var stored = Assert.Single(payload.StoredBlobs);
        Assert.Equal(s_peerB, stored.PeerNodeId);
        Assert.Equal(2, stored.BlobLength);
        Assert.Equal(s_receivedAt.AddMinutes(2), stored.UpdatedAt);

        // ...and so does the size refusal the peer sent (NL-559)
        var refusal = Assert.Single(payload.Refusals);
        Assert.Equal(s_peerA, refusal.PeerNodeId);
        Assert.Equal(2, refusal.Count);
        Assert.Equal(1024, refusal.AcceptedLimitBytes);
        Assert.Equal(PeerStorageConstants.MaxBlobLength, refusal.LastRefusedBlobLength);
        Assert.Equal(s_receivedAt.AddMinutes(3), refusal.LastRefusalAt);
    }

    [Fact]
    public async Task Given_ANodeIdAndIncludeBlob_When_Listed_Then_OnlyThatPeerWithItsBlob()
    {
        // Arrange
        var handler = GetHandler(_service.Object);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new ListPeerStorageIpcRequest
        {
            PeerNodeId = s_peerB,
            IncludeBlob = true
        }), TestContext.Current.CancellationToken);

        // Assert
        var payload = Deserialize(response);
        var retrieval = Assert.Single(payload.Retrievals);
        Assert.Equal(s_peerB, retrieval.PeerNodeId);
        Assert.Equal(new byte[] { 9 }, retrieval.Blob);
        Assert.Equal(s_peerB, Assert.Single(payload.StoredBlobs).PeerNodeId);
        Assert.Empty(payload.Refusals);
    }

    [Fact]
    public async Task Given_NoPeerStorageService_When_Listed_Then_InvalidOperation()
    {
        // Arrange
        var handler = GetHandler(null);

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new ListPeerStorageIpcRequest()),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        var error = MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                                TestContext.Current.CancellationToken);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneListPeerStorageCommand()
    {
        // Arrange
        var services = BuildServices(_service.Object);
        services.AddPeerStorageIpcServices();

        // Act
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.ListPeerStorage);
    }

    private static ListPeerStorageIpcResponse Deserialize(IpcEnvelope response) =>
        MessagePackSerializer.Deserialize<ListPeerStorageIpcResponse>(response.Payload, s_options,
                                                                      TestContext.Current.CancellationToken);

    private static IIpcCommandHandler GetHandler(IPeerStorageService? service)
    {
        var provider = BuildServices(service).BuildServiceProvider();
        return provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == ClientCommand.ListPeerStorage);
    }

    private static ServiceCollection BuildServices(IPeerStorageService? service)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (service is not null)
            services.AddSingleton(service);
        services.AddPeerStorageIpcServices();
        return services;
    }

    private static IpcEnvelope CreateEnvelope(ListPeerStorageIpcRequest request) => new()
    {
        Version = 1,
        Command = ClientCommand.ListPeerStorage,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static CompactPubKey Key(byte prefix, byte fill)
    {
        var key = Enumerable.Repeat(fill, 33).ToArray();
        key[0] = prefix;
        return new CompactPubKey(key);
    }
}