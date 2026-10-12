using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Node.Interfaces;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Payments.Interfaces;
using NLightning.Domain.Signing;
using NLightning.LndGrpc.Macaroons;
using NLightning.LndGrpc.Services;
using NLightning.LndGrpc.Tests.Macaroons;
using NLightning.LndGrpc.Tls;
using NLightning.Testing.Lnd;

namespace NLightning.LndGrpc.Tests;

public sealed class EnrolledLndGrpcHostTests
{
    [Fact]
    public async Task ActualTlsListenersRejectOtherOwnerAdminAndRetainEnrollmentAfterRestartAndRotation()
    {
        using var directories = new Directories();
        var a = LndCredentialEnrollmentTests.Context();
        var b = a with { NodeId = "node-b", OwnerId = "owner-b", SignerId = "signer-b" };
        byte[] originalAdmin;
        byte[] originalEnrollment;
        await using (var first = await RunningHost.StartAsync(directories.A, a))
        await using (var second = await RunningHost.StartAsync(directories.B, b))
        {
            originalAdmin = first.Admin;
            originalEnrollment = File.ReadAllBytes(Path.Combine(directories.A, LndCredentialEnrollment.FileName));
            await first.GetInfoAsync(originalAdmin);
            await second.GetInfoAsync(second.Admin);
            var failure = await Assert.ThrowsAsync<RpcException>(() => second.GetInfoAsync(originalAdmin));
            Assert.Equal(StatusCode.Unauthenticated, failure.StatusCode);
            failure = await Assert.ThrowsAsync<RpcException>(() => first.GetInfoAsync(second.Admin));
            Assert.Equal(StatusCode.Unauthenticated, failure.StatusCode);
        }
        await using (var restarted = await RunningHost.StartAsync(directories.A, a))
        {
            Assert.Equal(originalAdmin, restarted.Admin);
            Assert.Equal(originalEnrollment, File.ReadAllBytes(Path.Combine(directories.A, LndCredentialEnrollment.FileName)));
            await restarted.GetInfoAsync(originalAdmin);
        }
        File.Delete(Path.Combine(directories.A, LndMacaroonFiles.RootKeyFileName));
        await using (var rotated = await RunningHost.StartAsync(directories.A, a))
        {
            Assert.NotEqual(originalAdmin, rotated.Admin);
            Assert.Equal(originalEnrollment, File.ReadAllBytes(Path.Combine(directories.A, LndCredentialEnrollment.FileName)));
            var failure = await Assert.ThrowsAsync<RpcException>(() => rotated.GetInfoAsync(originalAdmin));
            Assert.Equal(StatusCode.Unauthenticated, failure.StatusCode);
            await rotated.GetInfoAsync(rotated.Admin);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingUnboundOrOtherOwnerDirectoryFailsBeforeOpeningListener(bool enrolled)
    {
        using var directories = new Directories();
        var a = LndCredentialEnrollmentTests.Context();
        LndMacaroonFiles.EnsureCreated(directories.A, context: enrolled ? a : null);
        var root = File.ReadAllBytes(Path.Combine(directories.A, LndMacaroonFiles.RootKeyFileName));
        using var services = RunningHost.Services(a with { OwnerId = "owner-b" });
        await using var host = new LndGrpcHost(services, Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }),
            directories.A, NullLogger<LndGrpcHost>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Null(host.BoundPort);
        Assert.False(File.Exists(Path.Combine(directories.A, LndTlsFiles.CertificateFileName)));
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(directories.A, LndMacaroonFiles.RootKeyFileName)));
    }

    [Fact]
    public async Task Given_AStandardNodesExistingCredentials_When_TheEnrollingBuildStarts_Then_ItsMacaroonsKeepWorkingUnbound()
    {
        // Arrange: credentials baked before signing enrollment existed (no context), as on an upgraded standard node
        using var directories = new Directories();
        LndMacaroonFiles.EnsureCreated(directories.A);
        var legacyAdmin = File.ReadAllBytes(Path.Combine(directories.A, LndMacaroonFiles.AdminFileName));
        var standard = LndCredentialEnrollmentTests.Context() with
        {
            NodeId = NodeSigningContext.DefaultNodeId,
            OwnerId = NodeSigningContext.DefaultOwnerId,
            SignerId = NodeSigningContext.DefaultSignerId
        };

        // Act: local signing (no Signing:Mode) with the default context
        await using var host = await RunningHost.StartAsync(directories.A, standard);

        // Assert: the old admin macaroon verifies and nothing was enrolled (NL-1340)
        await host.GetInfoAsync(legacyAdmin);
        Assert.False(File.Exists(Path.Combine(directories.A, LndCredentialEnrollment.FileName)));
    }

    [Theory]
    [InlineData(null, true, false)]
    [InlineData("Local", true, false)]
    [InlineData("RemoteNative", true, true)]
    [InlineData("Vls", true, true)]
    [InlineData("Local", false, true)]
    public void Given_SigningModeAndContext_When_Resolved_Then_OnlyAStandardNodeIsUnbound(string? mode,
                                                                                        bool defaultContext, bool bound)
    {
        // Arrange
        var context = LndCredentialEnrollmentTests.Context();
        if (defaultContext)
            context = context with
            {
                NodeId = NodeSigningContext.DefaultNodeId,
                OwnerId = NodeSigningContext.DefaultOwnerId,
                SignerId = NodeSigningContext.DefaultSignerId
            };
        var services = new ServiceCollection();
        services.AddSingleton(context);
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Signing:Mode"] = mode }).Build());
        using var provider = services.BuildServiceProvider();

        // Act
        var resolved = LndCredentialContext.Resolve(provider);

        // Assert
        Assert.Equal(bound, resolved is not null);
    }

    private sealed class RunningHost(ServiceProvider provider, LndGrpcHost host, string directory) : IAsyncDisposable
    {
        public byte[] Admin => File.ReadAllBytes(Path.Combine(directory, LndMacaroonFiles.AdminFileName));

        public static async Task<RunningHost> StartAsync(string directory, NodeSigningContext context)
        {
            var provider = Services(context);
            var host = new LndGrpcHost(provider, Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }),
                directory, NullLogger<LndGrpcHost>.Instance);
            try
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
                return new RunningHost(provider, host, directory);
            }
            catch
            {
                await host.DisposeAsync();
                await provider.DisposeAsync();
                throw;
            }
        }

        internal static ServiceProvider Services(NodeSigningContext context)
        {
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton(context);
            var signer = new Mock<ILightningSigner>();
            signer.Setup(value => value.GetNodePublicKey()).Returns(context.NodePublicKey);
            var channels = new Mock<IChannelMemoryRepository>();
            channels.Setup(value => value.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);
            var peers = new Mock<IPeerManager>();
            peers.Setup(value => value.ListPeers()).Returns([]);
            collection.AddSingleton(provider => new LightningService(signer.Object,
                Options.Create(new NodeOptions { BitcoinNetwork = NLightning.Domain.Protocol.ValueObjects.BitcoinNetwork.Resolve(context.Network) }), provider.GetRequiredService<IServiceScopeFactory>(), channels.Object,
                peers.Object, Mock.Of<IInvoiceService>(), NullLogger<LightningService>.Instance));
            return collection.BuildServiceProvider();
        }

        public async Task GetInfoAsync(byte[] macaroon)
        {
            var settings = LndSettings.FromBytes($"https://127.0.0.1:{host.BoundPort}",
                File.ReadAllBytes(Path.Combine(directory, LndTlsFiles.CertificateFileName)), macaroon);
            using var connection = await LndNodeConnection.ConnectAsync(settings,
                cancellationToken: TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed class Directories : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "enrolled-lnd-host-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(_root, "a");
        public string B => Path.Combine(_root, "b");
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}