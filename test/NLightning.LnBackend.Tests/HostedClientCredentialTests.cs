using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Node.Options;
using NLightning.Domain.Payments.Interfaces;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.ValueObjects;
using NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

namespace NLightning.LnBackend.Tests;

public sealed class HostedClientCredentialTests
{
    [Fact]
    public async Task IndependentClientAuthoritiesRejectOtherHostedNodeCertificateAtActualTlsListeners()
    {
        using var firstAuthority = Authority("first-client-authority");
        using var secondAuthority = Authority("second-client-authority");
        using var firstClient = Issue(firstAuthority, "first-client", "1.3.6.1.5.5.7.3.2");
        using var secondClient = Issue(secondAuthority, "second-client", "1.3.6.1.5.5.7.3.2");
        await using var a = await RunningHost.StartAsync(firstAuthority);
        await using var b = await RunningHost.StartAsync(secondAuthority);
        await a.GetInfoAsync(firstClient);
        await b.GetInfoAsync(secondClient);
        var denied = await Assert.ThrowsAsync<RpcException>(() => a.GetInfoAsync(secondClient));
        Assert.Equal(StatusCode.Unavailable, denied.StatusCode);
        denied = await Assert.ThrowsAsync<RpcException>(() => b.GetInfoAsync(firstClient));
        Assert.Equal(StatusCode.Unavailable, denied.StatusCode);
    }

    private static X509Certificate2 Authority(string name)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return Copy(certificate);
    }

    private static X509Certificate2 Issue(X509Certificate2 authority, string name, string usage, bool server = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var certificate = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(10), RandomNumberGenerator.GetBytes(16));
        using var withKey = certificate.CopyWithPrivateKey(key);
        return Copy(withKey);
    }

    private static X509Certificate2 Copy(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.Exportable);

    private sealed class RunningHost(ServiceProvider services, LnBackendHost host, string directory,
                                     X509Certificate2 serverCertificate) : IAsyncDisposable
    {
        public static async Task<RunningHost> StartAsync(X509Certificate2 authority)
        {
            var directory = Directory.CreateTempSubdirectory("isolated-hold-tls-").FullName;
            var server = Issue(authority, "127.0.0.1", "1.3.6.1.5.5.7.3.1", true);
            File.WriteAllText(Path.Combine(directory, "ca.pem"), authority.ExportCertificatePem());
            File.WriteAllText(Path.Combine(directory, "server.pem"), server.ExportCertificatePem());
            using (var key = server.GetECDsaPrivateKey())
                File.WriteAllText(Path.Combine(directory, "server.key"), key!.ExportPkcs8PrivateKeyPem());
            var collection = new ServiceCollection();
            collection.AddLogging();
            collection.AddSingleton(Options.Create(new LnBackendOptions { Enabled = true, Port = 0, TlsDirectory = directory }));
            collection.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
            collection.AddSingleton(Mock.Of<IInvoiceService>());
            collection.AddSingleton(Mock.Of<IHoldInvoiceService>());
            collection.AddSingleton(Mock.Of<IChannelMemoryRepository>());
            collection.AddSingleton(Mock.Of<ISecureKeyManager>());
            collection.AddSingleton(Mock.Of<IBlockchainMonitor>());
            collection.AddSingleton(Mock.Of<IPaymentService>());
            collection.AddSingleton<HoldBackendService>();
            collection.AddSingleton<ClnNodeBackendService>();
            collection.AddSingleton<LnBackendHost>();
            var services = collection.BuildServiceProvider();
            var host = services.GetRequiredService<LnBackendHost>();
            try
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
                return new RunningHost(services, host, directory, server);
            }
            catch
            {
                await services.DisposeAsync();
                server.Dispose();
                Directory.Delete(directory, true);
                throw;
            }
        }

        public async Task GetInfoAsync(X509Certificate2 clientCertificate)
        {
            using var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.RawData.AsSpan().SequenceEqual(serverCertificate.RawData)
            };
            handler.ClientCertificates.Add(clientCertificate);
            using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{host.BoundPort}",
                new GrpcChannelOptions { HttpHandler = handler });
            var client = new Hold.Hold.HoldClient(channel);
            await client.GetInfoAsync(new Hold.GetInfoRequest(), deadline: DateTime.UtcNow.AddSeconds(15),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
            serverCertificate.Dispose();
            Directory.Delete(directory, true);
        }
    }
}