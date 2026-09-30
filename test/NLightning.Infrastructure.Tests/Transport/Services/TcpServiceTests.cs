using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Tests.Transport.Services;

using Domain.Exceptions;
using Domain.Node.Options;
using Infrastructure.Protocol.Models;
using Infrastructure.Transport.Events;
using Infrastructure.Transport.Services;
using NLightning.Tests.Utils;

// ReSharper disable AccessToDisposedClosure
/// <summary>
/// NL-178: <c>TcpService</c> over real loopback sockets: the listener accepts a connection and raises its event, a
/// connect to a listener works end to end (bytes both ways), a refused connect becomes a <c>ConnectionException</c>,
/// a bad listen address is skipped, and stop closes the listeners.
/// </summary>
public class TcpServiceTests
{
    [Fact]
    public async Task Given_AListener_When_AClientConnects_Then_TheEventCarriesThePeerAndAConnectedClient()
    {
        // Arrange
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var service = CreateService([$"127.0.0.1:{port}"]);
        var connected = new TaskCompletionSource<NewPeerConnectedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnNewPeerConnected += (_, e) => connected.TrySetResult(e);

        try
        {
            await service.StartListeningAsync(TestContext.Current.CancellationToken);
            using var client = new TcpClient();

            // Act
            await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
            var eventArgs = await connected.Task.WaitAsync(TimeSpan.FromSeconds(5),
                                                          TestContext.Current.CancellationToken);

            // Assert: the event names the peer's end (its ephemeral port), not our listener
            Assert.Equal("127.0.0.1", eventArgs.Host);
            Assert.NotEqual((uint)port, eventArgs.Port);
            Assert.True(eventArgs.TcpClient.Connected);
            Assert.Single(service.ListeningTo);
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_APeerListener_When_Connecting_Then_TheConnectedPeerSendsAndReceives()
    {
        // Arrange: a raw listener as the peer
        var port = await PortPoolUtil.GetAvailablePortAsync();
        using var peerListener = new TcpListener(IPAddress.Loopback, port);
        peerListener.Start();
        var peerAccepted = Task.Run(async () =>
        {
            var peer = await peerListener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
            var stream = peer.GetStream();
            var buffer = new byte[5];
            await stream.ReadExactlyAsync(buffer, TestContext.Current.CancellationToken);
            await stream.WriteAsync(new byte[] { 0x0a, 0x0b, 0x0c }, TestContext.Current.CancellationToken);
            return peer;
        }, TestContext.Current.CancellationToken);
        var service = CreateService([]);
        PeerAddress peerAddress = new($"02{new string('0', 64)}@127.0.0.1:{port}");

        try
        {
            // Act
            var connectedPeer = await service.ConnectToPeerAsync(peerAddress);

            // Assert: the service handed back a live client; bytes travel both ways
            Assert.Equal(peerAddress.Host.ToString(), connectedPeer.Host);
            Assert.Equal((uint)port, connectedPeer.Port);
            Assert.Equal(peerAddress.PubKey, connectedPeer.CompactPubKey);
            Assert.True(connectedPeer.TcpClient.Connected);
            await connectedPeer.TcpClient.Client.SendAsync(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 },
                                                           TestContext.Current.CancellationToken);
            var peer = await peerAccepted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var reply = new byte[3];
            await connectedPeer.TcpClient.GetStream()
                         .ReadExactlyAsync(reply, TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 0x0a, 0x0b, 0x0c }, reply);
            connectedPeer.TcpClient.Dispose();
            peer.Dispose();
        }
        finally
        {
            peerListener.Stop();
        }
    }

    [Fact]
    public async Task Given_AClosedPort_When_Connecting_Then_AConnectionExceptionIsThrown()
    {
        // Arrange: take a port and release it, so nothing listens there
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Start();
        holder.Stop();
        var service = CreateService([]);
        PeerAddress peerAddress = new($"02{new string('0', 64)}@127.0.0.1:{port}");

        // Act
        var exception = await Assert.ThrowsAsync<ConnectionException>(
            () => service.ConnectToPeerAsync(peerAddress));

        // Assert
        Assert.Contains($"Failed to connect to peer 127.0.0.1:{port}", exception.Message);
    }

    [Fact]
    public async Task Given_AnInvalidListenAddress_When_TheServiceStarts_Then_ItIsSkippedAndNothingIsListening()
    {
        // Arrange
        var service = CreateService(["127.0.0.1"]);

        try
        {
            // Act
            await service.StartListeningAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Empty(service.ListeningTo);
        }
        finally
        {
            await service.StopListeningAsync();
        }
    }

    [Fact]
    public async Task Given_ARunningListener_When_TheServiceStops_Then_FurtherConnectionsAreRefused()
    {
        // Arrange
        var port = await PortPoolUtil.GetAvailablePortAsync();
        var service = CreateService([$"127.0.0.1:{port}"]);
        await service.StartListeningAsync(TestContext.Current.CancellationToken);
        Assert.Single(service.ListeningTo);

        // Act
        await service.StopListeningAsync();

        // Assert: the port no longer accepts connections
        Assert.Empty(service.ListeningTo);
        using var client = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(
            () => client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Given_AServiceThatNeverStarted_When_Stopped_Then_AnInvalidOperationExceptionIsThrown()
    {
        // Arrange
        var service = CreateService([]);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StopListeningAsync());
    }

    private static TcpService CreateService(IReadOnlyList<string> listenAddresses)
    {
        return new TcpService(NullLogger<TcpService>.Instance,
                              Options.Create(new NodeOptions { ListenAddresses = [.. listenAddresses] }));
    }
}