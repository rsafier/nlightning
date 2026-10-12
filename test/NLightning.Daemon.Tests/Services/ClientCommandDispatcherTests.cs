using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Services;

using Daemon.Interfaces;
using Daemon.Services;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;

/// <summary>The LND gRPC server's view of the client command handlers (NL-1164).</summary>
public class ClientCommandDispatcherTests
{
    [Fact]
    public async Task Given_ARegisteredHandler_When_Dispatched_Then_ItRunsInAScopeOfItsOwn()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddScoped<IClientCommandHandler<DisconnectPeerClientRequest, DisconnectPeerClientResponse>,
            RecordingHandler>();
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new ClientCommandDispatcher(provider.GetRequiredService<IServiceScopeFactory>());
        var nodeId = new CompactPubKey(Enumerable.Repeat((byte)2, 33).ToArray());

        // Act
        var first = await dispatcher.DispatchAsync<DisconnectPeerClientRequest, DisconnectPeerClientResponse>(
                        new DisconnectPeerClientRequest(nodeId), TestContext.Current.CancellationToken);
        var second = await dispatcher.DispatchAsync<DisconnectPeerClientRequest, DisconnectPeerClientResponse>(
                         new DisconnectPeerClientRequest(nodeId), TestContext.Current.CancellationToken);

        // Assert: each call got a handler of its own scope
        Assert.Equal(nodeId, first.NodeId);
        Assert.NotEqual(first.ChannelCount, second.ChannelCount);
    }

    [Fact]
    public async Task Given_NoHandler_When_Dispatched_Then_InvalidOperation()
    {
        // Arrange
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var dispatcher = new ClientCommandDispatcher(provider.GetRequiredService<IServiceScopeFactory>());

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync<DisconnectPeerClientRequest, DisconnectPeerClientResponse>(
                new DisconnectPeerClientRequest(new CompactPubKey(Enumerable.Repeat((byte)3, 33).ToArray())), TestContext.Current.CancellationToken));
    }

    private sealed class RecordingHandler
        : IClientCommandHandler<DisconnectPeerClientRequest, DisconnectPeerClientResponse>
    {
        private static int s_instances;
        private readonly int _instance = Interlocked.Increment(ref s_instances);

        public ClientCommand Command => ClientCommand.DisconnectPeer;

        public Task<DisconnectPeerClientResponse> HandleAsync(DisconnectPeerClientRequest request,
                                                              CancellationToken ct) =>
            Task.FromResult(new DisconnectPeerClientResponse(request.NodeId, _instance, 0));
    }
}