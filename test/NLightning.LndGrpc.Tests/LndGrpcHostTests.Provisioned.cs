using System.Security.Cryptography;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Tests;

using Domain.Node.Provisioning;
using LndGrpc.Macaroons;
using LndGrpc.Tls;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;

/// <summary>
/// The server with its TLS pair and macaroon root key delivered with the key of a locked start (NL-1352): used from
/// memory, nothing written to its data directory; and <c>Node:Startup:RequireProvisionedSecrets</c> without them.
/// </summary>
public sealed partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_AProvisionedRootKeyAndTls_When_Started_Then_TheyServeFromMemoryAndNoFileIsWritten()
    {
        // Arrange: a TLS pair made elsewhere, a root key, and a data directory that does not exist
        var (certificatePem, keyPem) = MakeTlsPair();
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var directory = _directory + "-provisioned";
        var secrets = new ProvisionedSecrets
        {
            LndGrpcMacaroonRootKey = rootKey,
            LndGrpcTls = new ProvisionedTls(certificatePem, keyPem, null)
        };
        var services = new WithSecrets(_services!, secrets);
        await _host!.StopAsync(Ct);
        _host = new LndGrpcHost(services, Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }), directory,
                                NullLogger<LndGrpcHost>.Instance);

        // Act
        await _host.StartAsync(Ct);
        var admin = LndMacaroonFiles.BakeDefaults(rootKey, services)["admin"];
        using var connection = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(Endpoint, System.Text.Encoding.ASCII.GetBytes(certificatePem), admin));
        var info = await connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct);
        var foreign = LndMacaroonFiles.NewMacaroon(new byte[32], LndPermissions.Admin).Serialize();
        using var forged = LndNodeConnection.CreateWithoutNodeInfo(
            LndSettings.FromBytes(Endpoint, System.Text.Encoding.ASCII.GetBytes(certificatePem), foreign));
        var forgedError = await Assert.ThrowsAsync<RpcException>(
            () => forged.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct).ResponseAsync);

        // Assert: served with the delivered certificate and root key; the data directory was never created
        Assert.Equal(NodeIdHex, info.IdentityPubkey);
        Assert.Equal(StatusCode.Unauthenticated, forgedError.StatusCode);
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_RefusedGenerationAndNoFiles_When_Started_Then_TheStartFailsAndNothingIsWritten(
        bool provisionedTls)
    {
        // Arrange: Node:Startup:RequireProvisionedSecrets; either nothing delivered (the TLS pair is missing) or the TLS
        // pair only (the root key is missing)
        var (certificatePem, keyPem) = MakeTlsPair();
        var directory = _directory + "-refused";
        var secrets = new ProvisionedSecrets
        {
            LndGrpcTls = provisionedTls ? new ProvisionedTls(certificatePem, keyPem, null) : null,
            RefuseGeneration = true
        };
        await _host!.StopAsync(Ct);
        _host = new LndGrpcHost(new WithSecrets(_services!, secrets),
                                Options.Create(new LndGrpcOptions { Enabled = true, Port = 0 }), directory,
                                NullLogger<LndGrpcHost>.Instance);

        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _host.StartAsync(Ct));

        // Assert
        Assert.Contains("RequireProvisionedSecrets", error.Message);
        Assert.Contains(provisionedTls ? "lndGrpcMacaroonRootKey" : "lndGrpcTls", error.Message);
        Assert.False(Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any());
        _host = null;
    }

    [Fact]
    public void Given_AProvisionedRootKey_When_TheStoreIsAskedForOtherIds_Then_OnlyIdZeroExistsAndNoFileIsWritten()
    {
        // Arrange
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var directory = _directory + "-store";
        var store = new LndRootKeyStore(directory, null, new ProvisionedSecrets { LndGrpcMacaroonRootKey = rootKey });

        // Act
        var zero = store.TryGet(0);
        var created = Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(7));

        // Assert
        Assert.Equal(rootKey, zero);
        Assert.Contains("provisioned", created.Message);
        Assert.Equal([0UL], store.ListIds());
        Assert.False(store.Delete(7));
        Assert.True(store.IsProvisioned);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Given_RefusedGeneration_When_ANewRootKeyIdIsAsked_Then_ItIsRefused()
    {
        // Arrange: the file root key exists (the started host wrote it), new ids may not be written
        var store = new LndRootKeyStore(_directory, null, new ProvisionedSecrets { RefuseGeneration = true });

        // Act
        var error = Assert.Throws<InvalidOperationException>(() => store.GetOrCreate(3));

        // Assert
        Assert.NotNull(store.TryGet(0));
        Assert.Contains("RequireProvisionedSecrets", error.Message);
        Assert.False(File.Exists(Path.Combine(_directory, "macaroon-root-keys", "3.key")));
    }

    /// <summary>A server certificate and key as the host would generate them, made in a scratch directory.</summary>
    private static (string CertificatePem, string KeyPem) MakeTlsPair()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "nltg-lndtls-" + Guid.NewGuid().ToString("N"));
        try
        {
            LndTlsFiles.EnsureCreated(scratch, new LndGrpcOptions(), TimeProvider.System).Dispose();
            return (File.ReadAllText(Path.Combine(scratch, LndTlsFiles.CertificateFileName)),
                    File.ReadAllText(Path.Combine(scratch, LndTlsFiles.KeyFileName)));
        }
        finally
        {
            Directory.Delete(scratch, true);
        }
    }

    /// <summary>The node's services with the locked start's secrets registered.</summary>
    private sealed class WithSecrets(IServiceProvider inner, ProvisionedSecrets secrets) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ProvisionedSecrets) ? secrets : inner.GetService(serviceType);
    }
}