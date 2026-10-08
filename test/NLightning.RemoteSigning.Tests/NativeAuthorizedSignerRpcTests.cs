using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeAuthorizedSignerRpcTests
{
    [Fact]
    public async Task Given_InstalledWriterCredential_When_AdapterOptionsAreMutated_Then_OriginalContextExecutesAndReconciles()
    {
        await using var host = await Host.CreateAsync();
        var options = host.ConnectionOptions();
        using var connection = new RemoteSignerConnection(options, host.ConnectAsync);
        options.AuthToken = "changed-node-authentication-token-00000000000000000";
        options.WriterId = "writer-b";
        options.WriterEpoch = 2;
        options.WriterCredential = Host.NewWriterToken;
        options.OwnerId = "other-owner";
        var request = connection.PrepareForContext(SignerOperations.MarkDataLoss, new ChannelId(new byte[32]));
        host.State.Authority.Approve(host.State.Binding, NativeSignerIntent.FromRequest(request),
            DateTimeOffset.UtcNow.AddMinutes(5));

        Assert.Empty(connection.Execute(request));
        var after = host.State.Journal.GetCheckpointDigest();
        var reconciled = connection.Reconcile(request);
        Assert.Equal(RequestOutcome.Completed, reconciled.Outcome);
        Assert.Equal(SignerWire.Encode([]), reconciled.Response.Payload.ToByteArray());
        Assert.Empty(connection.Execute(request));
        Assert.Equal(after, host.State.Journal.GetCheckpointDigest());
        Assert.Equal(host.State.Binding.OwnerId, request.OwnerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_NewWriter_When_AdapterHasMissingOrStaleCredential_Then_ExecuteAndReconcileRejectIt(bool staleCredential)
    {
        await using var host = await Host.CreateAsync(transferWriter: true);
        var options = host.ConnectionOptions();
        if (staleCredential)
        {
            options.WriterId = "writer-b";
            options.WriterEpoch = 2;
            options.WriterCredential = NativeAuthorizedSignerExecutorTests.Fixture.WriterToken;
        }
        else
        {
            options.WriterId = null;
            options.WriterEpoch = null;
            options.WriterCredential = null;
        }
        using var connection = new RemoteSignerConnection(options, host.ConnectAsync);
        var request = host.State.ApprovedRequest();
        var before = host.State.Journal.GetCheckpointDigest();
        var execute = Assert.Throws<RemoteSignerTransportException>(() => connection.Execute(request));
        var reconcile = Assert.Throws<RemoteSignerTransportException>(() => connection.Reconcile(request));
        Assert.Equal(StatusCode.PermissionDenied, Assert.IsType<RpcException>(execute.InnerException).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, Assert.IsType<RpcException>(reconcile.InnerException).StatusCode);
        Assert.Equal(before, host.State.Journal.GetCheckpointDigest());
    }

    [Fact]
    public async Task Given_ValidNodeTokenWithoutOwnerApproval_When_Executing_Then_SignerRejectsBeforeMutation()
    {
        await using var host = await Host.CreateAsync();
        var before = host.State.Journal.GetCheckpointDigest();
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(host.State.Request(), Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
        Assert.Equal(before, host.State.Journal.GetCheckpointDigest());
    }

    [Fact]
    public async Task Given_OwnerApprovalAndNodeToken_When_IndependentValidationFails_Then_NoMutationOccurs()
    {
        await using var host = await Host.CreateAsync(reject: true);
        var before = host.State.Journal.GetCheckpointDigest();
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(host.State.ApprovedRequest(), Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
        Assert.Equal(before, host.State.Journal.GetCheckpointDigest());
    }

    [Fact]
    public async Task Given_CompletedCachedRequest_When_WriterTransfers_Then_ExecuteAndReconcileBothRejectStaleWriter()
    {
        await using var host = await Host.CreateAsync();
        var request = host.State.ApprovedRequest();
        await host.Client.ExecuteAsync(request, Headers(), cancellationToken: TestContext.Current.CancellationToken);
        host.State.Authority.AcquireWriter(host.State.Binding, 1, "writer-b");
        var execute = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(request, Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        var reconcile = await Assert.ThrowsAsync<RpcException>(() => host.Client.ReconcileAsync(request, Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, execute.StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition, reconcile.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("changed-epoch")]
    [InlineData("changed-writer")]
    [InlineData("missing-writer-token")]
    public async Task Given_NodeCredential_When_ExecutionMetadataIsMissingOrAltered_Then_ItCannotGainWriterAuthority(string attack)
    {
        await using var host = await Host.CreateAsync();
        var headers = attack switch
        {
            "missing" => new Metadata { { "x-signer-token", NativeAuthorizedSignerExecutorTests.Fixture.Token } },
            "changed-epoch" => Headers(epoch: "2"),
            "missing-writer-token" => new Metadata
            {
                { "x-signer-token", NativeAuthorizedSignerExecutorTests.Fixture.Token },
                { "x-nltg-writer-id", "writer-a" }, { "x-nltg-writer-epoch", "1" }
            },
            _ => Headers(writer: "writer-b")
        };
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(host.State.ApprovedRequest(), headers, cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public async Task Given_NewWriter_When_StaleCredentialRelabelsHeaders_Then_ExecuteAndReconcileRejectIt()
    {
        await using var host = await Host.CreateAsync(transferWriter: true);
        var request = host.State.ApprovedRequest();
        var before = host.State.Journal.GetCheckpointDigest();
        var staleHeaders = Headers(writer: "writer-b", epoch: "2");
        var execute = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(request, staleHeaders,
            cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        var reconcile = await Assert.ThrowsAsync<RpcException>(() => host.Client.ReconcileAsync(request, staleHeaders,
            cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, execute.StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, reconcile.StatusCode);
        Assert.Equal(before, host.State.Journal.GetCheckpointDigest());
        await host.Client.ExecuteAsync(request, Headers("writer-b", "2", Host.NewWriterToken),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Given_AuthorityWithoutInstalledWriterVerifier_When_NodeExecutes_Then_ItFailsClosed()
    {
        await using var host = await Host.CreateAsync(installWriterVerifier: false);
        var before = host.State.Journal.GetCheckpointDigest();
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(host.State.ApprovedRequest(),
            Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
        Assert.Equal(before, host.State.Journal.GetCheckpointDigest());
    }

    [Fact]
    public async Task Given_OwnerApproval_When_NodeChangesThePayload_Then_ApprovalCannotBeReused()
    {
        await using var host = await Host.CreateAsync();
        var request = host.State.ApprovedRequest();
        request.Payload = ByteString.CopyFrom(SignerWire.Encode([new ChannelId(Enumerable.Repeat((byte)1, 32).ToArray())]));
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(request, Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public async Task Given_ValidCredential_When_NodeRelabelsOwner_Then_ContextBoundaryRejectsIt()
    {
        await using var host = await Host.CreateAsync();
        var request = host.State.ApprovedRequest();
        request.OwnerId = "other-owner";
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(request, Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, exception.StatusCode);
    }

    [Fact]
    public async Task Given_RetiredChannel_When_CachedAuthorityResultIsRequested_Then_InvalidatedReceiptIsRejected()
    {
        await using var host = await Host.CreateAsync();
        var original = host.State.ApprovedRequest();
        await host.Client.ExecuteAsync(original, Headers(), cancellationToken: TestContext.Current.CancellationToken);
        var retired = host.State.ApprovedRequest(SignerOperations.UnregisterChannel);
        await host.Client.ExecuteAsync(retired, Headers(), cancellationToken: TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<RpcException>(() => host.Client.ExecuteAsync(original, Headers(), cancellationToken: TestContext.Current.CancellationToken).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, exception.StatusCode);
    }

    private static Metadata Headers(string writer = "writer-a", string epoch = "1",
                                    string writerToken = NativeAuthorizedSignerExecutorTests.Fixture.WriterToken) => new()
    {
        { "x-signer-token", NativeAuthorizedSignerExecutorTests.Fixture.Token },
        { "x-nltg-writer-id", writer }, { "x-nltg-writer-epoch", epoch }, { "x-nltg-writer-token", writerToken }
    };

    private sealed class Host : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly GrpcChannel _channel;
        private readonly Uri _endpoint;
        public NativeAuthorizedSignerExecutorTests.Fixture State { get; }
        public SignerRpc.SignerRpcClient Client { get; }

        private Host(WebApplication application, GrpcChannel channel, NativeAuthorizedSignerExecutorTests.Fixture state,
                     string endpoint)
        {
            _application = application;
            _channel = channel;
            _endpoint = new Uri(endpoint);
            State = state;
            Client = new SignerRpc.SignerRpcClient(channel);
        }

        public RemoteSignerOptions ConnectionOptions() => new()
        {
            SocketPath = Path.Combine(Path.GetTempPath(), "unused-native-test.sock"),
            AuthToken = NativeAuthorizedSignerExecutorTests.Fixture.Token,
            Network = State.Binding.Network,
            NodeId = State.Binding.NodeId,
            OwnerId = State.Binding.OwnerId,
            SignerId = State.Binding.SignerId,
            ExpectedNodePublicKey = State.Binding.PublicKey,
            WriterId = "writer-a",
            WriterEpoch = 1,
            WriterCredential = NativeAuthorizedSignerExecutorTests.Fixture.WriterToken
        };

        public async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, _endpoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }

        public const string NewWriterToken = "writer-b-native-execution-credential-000000000000000";
        public static async Task<Host> CreateAsync(bool reject = false, bool transferWriter = false,
                                                 bool installWriterVerifier = true)
        {
            var state = new NativeAuthorizedSignerExecutorTests.Fixture();
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0,
                listen => listen.Protocols = HttpProtocols.Http2));
            builder.Services.AddGrpc();
            var execution = transferWriter ? state.Authority.AcquireWriter(state.Binding, 1, "writer-b") : null;
            builder.Services.AddSingleton(state.Service(reject, execution,
                transferWriter ? NewWriterToken : NativeAuthorizedSignerExecutorTests.Fixture.WriterToken,
                installWriterVerifier));
            var application = builder.Build();
            try
            {
                application.MapGrpcService<SignerRpcService>();
                await application.StartAsync();
                var endpoint = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                return new Host(application, GrpcChannel.ForAddress(endpoint), state, endpoint);
            }
            catch
            {
                await application.DisposeAsync();
                state.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Dispose();
            await _application.StopAsync();
            await _application.DisposeAsync();
            State.Dispose();
        }
    }
}