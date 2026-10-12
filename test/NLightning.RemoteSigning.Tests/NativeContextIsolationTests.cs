using Grpc.Core;
using Grpc.Net.Client;
using NLightning.Domain.Signing;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeContextIsolationTests(SignerDaemonFixture daemon) : IClassFixture<SignerDaemonFixture>
{
    [Theory]
    [InlineData("node")]
    [InlineData("owner")]
    [InlineData("signer")]
    [InlineData("network")]
    public async Task AuthenticatedRelabeledRequestsCannotExecuteOrReadReceipts(string field)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var original = connection.PrepareForContext(SignerOperations.ReserveChannelKeyIndex);
        connection.Execute(original);
        var changed = original.Clone();
        switch (field)
        {
            case "node": changed.NodeId = "another-node"; break;
            case "owner": changed.OwnerId = "another-owner"; break;
            case "signer": changed.SignerId = "another-signer"; break;
            case "network": changed.Network = "mainnet"; break;
        }
        Assert.Throws<ArgumentException>(() => connection.Execute(changed));
        Assert.Throws<ArgumentException>(() => connection.Reconcile(changed));

        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellation) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                    System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(daemon.SocketPath), cancellation);
                    return new System.Net.Sockets.NetworkStream(socket, true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });
        var client = new SignerRpc.SignerRpcClient(channel);
        var headers = new Metadata { { "x-signer-token", SignerDaemonFixture.Token } };
        var execute = await Assert.ThrowsAsync<RpcException>(async () => await client.ExecuteAsync(changed, headers, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.PermissionDenied, execute.StatusCode);
        var reconcile = await Assert.ThrowsAsync<RpcException>(async () => await client.ReconcileAsync(changed, headers, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(StatusCode.PermissionDenied, reconcile.StatusCode);
        Assert.Equal(RequestOutcome.Completed, connection.Reconcile(original).Outcome);
    }

    [Fact]
    public void MutatingConnectionOptionsDoesNotSwitchEnrollment()
    {
        var options = daemon.Options();
        using var connection = new RemoteSignerConnection(options);
        options.NodeId = "reassigned-node";
        options.OwnerId = "reassigned-owner";
        options.SignerId = "reassigned-signer";
        Assert.Equal(NodeSigningContext.DefaultNodeId, connection.Context.NodeId);
        Assert.Equal(NodeSigningContext.DefaultOwnerId, connection.PrepareForContext(0).OwnerId);
        Assert.Equal(NodeSigningContext.DefaultSignerId, connection.PrepareForContext(0).SignerId);
        Assert.Equal(daemon.LocalKeys.GetNodePubKey(), connection.Identity.NodePublicKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartCannotRelabelOrDiscardAnExistingEnrollment(bool remove)
    {
        await using var owned = new SignerDaemonFixture();
        await owned.InitializeAsync();
        using (var connection = new RemoteSignerConnection(owned.Options()))
            connection.Invoke(SignerOperations.ReserveChannelKeyIndex);
        await owned.StopAsync();
        var path = Path.Combine(owned.DirectoryPath, "node.key.signer-state.enrollment");
        if (remove) File.Delete(path);
        else File.WriteAllText(path, File.ReadAllText(path).Replace("default-owner", "another-owner", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => owned.RestartAsync());
    }

}