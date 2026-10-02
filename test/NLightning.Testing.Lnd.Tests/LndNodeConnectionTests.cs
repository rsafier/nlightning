using System.Security.Authentication;
using Grpc.Core;

namespace NLightning.Testing.Lnd.Tests;

using Lnrpc;
using TestUtils;

public class LndNodeConnectionTests
{
    private const string NodePubKey = "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619";

    private static readonly byte[] s_macaroon = [0x02, 0x01, 0x03, 0x6c, 0x6e, 0x64, 0xAB, 0xCD];

    [Fact]
    public async Task Given_PinnedCertificateAndMacaroon_When_Calling_Then_TheServerGetsTheHexMacaroonAndTheCallSucceeds()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        server.Reply("/lnrpc.State/GetState", () => new GetStateResponse { State = WalletState.ServerActive });
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(server.GrpcEndpoint, TestCertificates.ToPem(certificate), s_macaroon));

        // Act
        var state = await connection.StateClient.GetStateAsync(new GetStateRequest(), cancellationToken: ct);

        // Assert
        Assert.Equal(WalletState.ServerActive, state.State);
        var call = Assert.Single(server.Calls);
        Assert.Equal("/lnrpc.State/GetState", call.Path);
        Assert.Equal("0201036c6e64abcd", call.Macaroon);
    }

    [Fact]
    public async Task Given_AnotherCertificatePinned_When_Calling_Then_TheTlsHandshakeFailsAndNothingReachesTheServer()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        using var other = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        server.Reply("/lnrpc.State/GetState", () => new GetStateResponse { State = WalletState.ServerActive });
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(server.GrpcEndpoint, TestCertificates.ToPem(other), s_macaroon));

        // Act
        var exception = await Assert.ThrowsAsync<RpcException>(
            async () => await connection.StateClient.GetStateAsync(new GetStateRequest(), cancellationToken: ct));
        var safeState = await connection.GetStateSafeAsync(TimeSpan.FromSeconds(5), ct);

        // Assert
        // Grpc.Net reports a failed TLS handshake as Internal, with the callback's rejection inside.
        Assert.Equal(StatusCode.Internal, exception.StatusCode);
        Assert.IsType<AuthenticationException>(exception.Status.DebugException?.InnerException);
        Assert.Equal(WalletState.NonExisting, safeState);
        Assert.Empty(server.Calls);
    }

    [Fact]
    public async Task Given_ACustomValidationCallback_When_Calling_Then_ItDecidesInsteadOfPinning()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        server.Reply("/lnrpc.State/GetState", () => new GetStateResponse { State = WalletState.RpcActive });
        string? seenThumbprint = null;
        var settings = new LndSettings
        {
            GrpcEndpoint = server.GrpcEndpoint,
            Macaroon = s_macaroon,
            ServerCertificateValidation = (presented, _, _) =>
            {
                seenThumbprint = presented.Thumbprint;
                return true;
            }
        };
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(settings);

        // Act
        var state = await connection.GetStateSafeAsync(TimeSpan.FromSeconds(5), ct);

        // Assert
        Assert.Equal(WalletState.RpcActive, state);
        Assert.Equal(certificate.Thumbprint, seenThumbprint);
    }

    [Fact]
    public async Task Given_NoMacaroon_When_Calling_Then_NoMacaroonHeaderIsSent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        server.Reply("/lnrpc.State/GetState", () => new GetStateResponse { State = WalletState.ServerActive });
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(server.GrpcEndpoint, TestCertificates.ToDer(certificate), null));

        // Act
        var state = await connection.GetStateSafeAsync(TimeSpan.FromSeconds(5), ct);

        // Assert
        Assert.Equal(WalletState.ServerActive, state);
        Assert.Null(Assert.Single(server.Calls).Macaroon);
    }

    [Fact]
    public async Task Given_ANode_When_ConnectAsync_Then_ItsIdentityAliasAndUrisAreLoaded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        server.Reply("/lnrpc.Lightning/GetInfo", () => new GetInfoResponse
        {
            IdentityPubkey = NodePubKey,
            Alias = "alice",
            Uris =
            {
                $"{NodePubKey}@abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcd.onion:9735",
                $"{NodePubKey}@172.17.0.3:9735"
            }
        });
        var settings = LndSettings.FromBytes(server.GrpcEndpoint, TestCertificates.ToPem(certificate), s_macaroon);

        // Act
        using var connection = await LndNodeConnection.ConnectAsync(settings, cancellationToken: ct);

        // Assert
        Assert.Equal(NodePubKey, connection.LocalNodePubKey);
        Assert.Equal(Convert.FromHexString(NodePubKey), connection.LocalNodePubKeyBytes);
        Assert.Equal("alice", connection.LocalAlias);
        Assert.Equal($"{NodePubKey}@172.17.0.3:9735", connection.ClearnetConnectString);
        Assert.EndsWith(".onion:9735", connection.OnionConnectString);
        Assert.Equal(server.GrpcEndpoint, connection.Host);
    }

    [Fact]
    public async Task Given_ANode_When_TheBlockingConstructorAndCloneRun_Then_EachLoadsTheIdentity()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        server.Reply("/lnrpc.Lightning/GetInfo",
                     () => new GetInfoResponse { IdentityPubkey = NodePubKey, Alias = "bob" });
        var settings = LndSettings.FromBytes(server.GrpcEndpoint, TestCertificates.ToPem(certificate), s_macaroon);

        // Act
        using var connection = new LndNodeConnection(settings);
        using var clone = connection.Clone();

        // Assert
        Assert.Equal("bob", connection.LocalAlias);
        Assert.Equal("bob", clone.LocalAlias);
        Assert.NotSame(connection.Channel, clone.Channel);
        Assert.Equal(2, server.Calls.Count(x => x.Path == "/lnrpc.Lightning/GetInfo"));
    }

    [Fact]
    public async Task Given_AnUnimplementedMethod_When_Called_Then_TheStatusComesBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        using var certificate = TestCertificates.CreateServerCertificate();
        await using var server = await FakeLndServer.StartAsync(certificate, ct);
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(server.GrpcEndpoint, TestCertificates.ToPem(certificate), s_macaroon));

        // Act
        var exception = await Assert.ThrowsAsync<RpcException>(
            async () => await connection.VersionerClient.GetVersionAsync(new Verrpc.VersionRequest(),
                                                                         cancellationToken: ct));

        // Assert
        Assert.Equal(StatusCode.Unimplemented, exception.StatusCode);
        Assert.Equal("/verrpc.Versioner/GetVersion", Assert.Single(server.Calls).Path);
    }

    [Fact]
    public void Given_AConnection_When_Disposed_Then_DisposingAgainIsHarmless()
    {
        // Arrange
        using var certificate = TestCertificates.CreateServerCertificate();
        var connection = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes("https://127.0.0.1:1", TestCertificates.ToPem(certificate), s_macaroon));

        // Act
        connection.Dispose();
        var exception = Record.Exception(connection.Dispose);

        // Assert
        Assert.Null(exception);
        Assert.Equal(WalletState.NonExisting, connection.GetStateSafe(1));
    }
}