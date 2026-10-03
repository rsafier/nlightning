using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Cashu;

using Application.Payments.Events;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Protocol.ValueObjects;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Cashu.PaymentProcessor.Grpc;

/// <summary>
/// The CDK payment processor's listener (NL-998): mutual TLS with the mint's CA, no ambient Kestrel endpoints.
/// </summary>
[Collection(CashuHostCollection.Name)]
public sealed class CashuPaymentProcessorHostTests
{
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthentication = "1.3.6.1.5.5.7.3.1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Given_ClientCertificates_When_CheckedAgainstTheMintCa_Then_OnlyClientCertificatesOfThatCaPass()
    {
        // Arrange
        using var ca = Authority("nltg-test-ca");
        using var otherCa = Authority("nltg-other-ca");
        using var client = Issue(ca, "client", ClientAuthentication);
        using var server = Issue(ca, "server", ServerAuthentication);
        using var stranger = Issue(otherCa, "stranger", ClientAuthentication);
        using var selfSigned = SelfSignedClient("self");

        // Act & Assert
        Assert.True(CashuPaymentProcessorHost.IsSignedBy(client, ca));
        Assert.False(CashuPaymentProcessorHost.IsSignedBy(server, ca));
        Assert.False(CashuPaymentProcessorHost.IsSignedBy(stranger, ca));
        Assert.False(CashuPaymentProcessorHost.IsSignedBy(selfSigned, ca));
    }

    [Fact]
    public async Task Given_MutualTls_When_ClientsConnect_Then_OnlyAClientCertificateOfTheMintCaIsServed()
    {
        // Arrange: the CDK tls_dir layout (ca.pem, server.pem, server.key) and a client certificate of that CA
        using var ca = Authority("nltg-mint-ca");
        using var otherCa = Authority("nltg-other-ca");
        using var server = Issue(ca, "127.0.0.1", ServerAuthentication, IPAddress.Loopback);
        using var client = Issue(ca, "cdk-mintd", ClientAuthentication);
        using var stranger = Issue(otherCa, "stranger", ClientAuthentication);
        var directory = Directory.CreateTempSubdirectory("nltg-cashu-tls-").FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "ca.pem"), ca.ExportCertificatePem(), Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "server.pem"), server.ExportCertificatePem(), Ct);
        await File.WriteAllTextAsync(Path.Combine(directory, "server.key"),
                                     server.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem(), Ct);
        await using var provider = Provider(new CashuPaymentProcessorOptions
        {
            Enabled = true,
            Port = 0,
            TlsDirectory = directory
        });
        var host = provider.GetRequiredService<CashuPaymentProcessorHost>();
        await host.StartAsync(Ct);
        try
        {
            // Act
            var good = await GetSettingsAsync(host.BoundPort!.Value, client);
            var none = await Assert.ThrowsAsync<RpcException>(() => GetSettingsAsync(host.BoundPort.Value, null));
            var other = await Assert.ThrowsAsync<RpcException>(
                            () => GetSettingsAsync(host.BoundPort.Value, stranger));

            // Assert
            Assert.Equal("sat", good.Unit);
            Assert.Equal(StatusCode.Unavailable, none.StatusCode);
            Assert.Equal(StatusCode.Unavailable, other.StatusCode);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Given_AnAmbientKestrelEndpoint_When_TheHostStarts_Then_ItListensOnTheConfiguredAddressOnly()
    {
        // Arrange: an endpoint from the environment must not open a second, unchecked listener
        const string variable = "Kestrel__Endpoints__Ambient__Url";
        Environment.SetEnvironmentVariable(variable, "http://127.0.0.1:0");
        try
        {
            await using var provider = Provider(new CashuPaymentProcessorOptions
            {
                Enabled = true,
                Port = 0,
                AllowInsecureLoopback = true
            });
            var host = provider.GetRequiredService<CashuPaymentProcessorHost>();

            // Act
            await host.StartAsync(Ct);
            var addresses = host.ListeningAddresses;
            await host.StopAsync(CancellationToken.None);

            // Assert
            Assert.StartsWith("http://127.0.0.1:", Assert.Single(addresses));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static async Task<SettingsResponse> GetSettingsAsync(int port, X509Certificate2? clientCertificate)
    {
        var handler = new HttpClientHandler
        {
            // The test's own server certificate (its CA is not a system root)
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        if (clientCertificate is not null)
            handler.ClientCertificates.Add(clientCertificate);
        using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{port}",
                                                   new GrpcChannelOptions { HttpHandler = handler });
        return await new CdkPaymentProcessor.CdkPaymentProcessorClient(channel)
                     .GetSettingsAsync(new EmptyRequest(), deadline: DateTime.UtcNow.AddSeconds(20),
                                       cancellationToken: Ct);
    }

    private static ServiceProvider Provider(CashuPaymentProcessorOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IInvoiceService>().Object);
        services.AddSingleton(new Mock<IPaymentService>().Object);
        services.AddSingleton<IPaymentEventSource>(new PaymentEventHub());
        services.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<CdkPaymentProcessorService>();
        services.AddSingleton<CashuPaymentProcessorHost>();
        return services.BuildServiceProvider();
    }

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        return Persisted(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)));
    }

    private static X509Certificate2 Issue(X509Certificate2 ca, string name, string eku, IPAddress? address = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        if (address is not null)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(address);
            request.CertificateExtensions.Add(names.Build());
        }

        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10),
                                          RandomNumberGenerator.GetBytes(8));
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persisted(withKey);
    }

    private static X509Certificate2 SelfSignedClient(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid(ClientAuthentication)], false));
        return Persisted(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10)));
    }

    /// <summary>Through PKCS#12, so the private key works in TLS on every platform (macOS refuses ephemeral keys).</summary>
    private static X509Certificate2 Persisted(X509Certificate2 certificate)
    {
        using (certificate)
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
                                                   X509KeyStorageFlags.Exportable);
    }
}

/// <summary>The listener tests change process-wide environment variables: never in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CashuHostCollection
{
    public const string Name = "cashu-host";
}