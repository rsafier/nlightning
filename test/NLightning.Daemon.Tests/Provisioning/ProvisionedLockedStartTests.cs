using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NLightning.Cashu.PaymentProcessor;
using NLightning.Cashu.PaymentProcessor.Grpc;
using NLightning.Daemon.Contracts.Provisioning;
using NLightning.Daemon.Extensions;
using NLightning.Daemon.Provisioning;
using NLightning.Infrastructure.Transport.Tor;
using NLightning.LnBackend;
using NLightning.LndGrpc;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Daemon.Tests.Provisioning;

/// <summary>
/// A locked start with every file secret delivered with the key (NL-1352) and a configuration directory holding only
/// <c>appsettings.json</c>: the LND gRPC server, the Cashu processor, the LN backend and the Tor onion service run from
/// memory and the directory still holds only that file afterwards.
/// </summary>
[Collection(Cashu.CashuHostCollection.Name)]
public sealed class ProvisionedLockedStartTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string TorControlPassword = "tor-control-secret";
    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    private readonly LockedNodeFiles _files = new();
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "nltg-data-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _files.Dispose();
        if (Directory.Exists(_dataPath))
            Directory.Delete(_dataPath, true);
    }

    [Fact]
    public async Task Given_EverySecretProvisioned_When_TheNodeUnlocksAndItsServersStart_Then_NoSecretFileExists()
    {
        // Arrange: the configuration directory holds appsettings.json only; the database lives elsewhere (data, not a
        // secret) and its connection string comes with the key
        await using var tor = new FakeTorControlPort("HASHEDPASSWORD", TorControlPassword);
        var appsettings = Path.Combine(_files.ConfigPath, "appsettings.json");
        await File.WriteAllTextAsync(appsettings, $$"""
            {
              "Node": {
                "Network": "regtest",
                "ListenAddresses": [ "127.0.0.1:19738" ],
                "Tor": {
                  "Mode": "TorOnly",
                  "Control": "{{tor.EndPoint}}",
                  "OnionServiceKeyFile": "{{Path.Combine(_files.ConfigPath, "tor_onion_v3.key")}}"
                },
                "Startup": { "Locked": true, "RequireProvisionedSecrets": true }
              },
              "Bitcoin": { "RpcEndpoint": "127.0.0.1:18443", "Notifications": "Poll" },
              "Database": { "Provider": "Sqlite", "RunMigrations": true },
              "LndGrpc": { "Enabled": true, "Port": 0 },
              "Cashu": { "PaymentProcessor": { "Enabled": true, "Port": 0 } },
              "LnBackend": { "Enabled": true, "Port": 0 }
            }
            """, Ct);
        var configuration = new ConfigurationBuilder().AddJsonFile(appsettings).Build();
        Directory.CreateDirectory(_dataPath);
        var connectionString = $"Data Source={Path.Combine(_dataPath, "nltg.db")};Pooling=False";

        using var ca = ProvisionedSecretsTests.Authority();
        using var server = ProvisionedSecretsTests.Issue(ca);
        using var client = ProvisionedSecretsTests.Issue(ca, ClientAuthentication);
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var onionKey = ProvisionedSecretsTests.OnionKey();
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var nodeId);
        var provisioner = new QueueKeyProvisioner();
        var attempt = provisioner.EnqueueReturningMacaroons(keyFile, Password, new KeyProvisioningSecrets
        {
            DatabaseConnectionString = connectionString,
            BitcoinRpcUser = "rpc-user",
            BitcoinRpcPassword = "rpc-secret",
            TorControlPassword = TorControlPassword,
            TorOnionServiceKey = onionKey,
            LndGrpcMacaroonRootKey = Convert.ToBase64String(rootKey),
            LndGrpcTls = ProvisionedSecretsTests.PemTls(server, null),
            CashuProcessorTls = ProvisionedSecretsTests.PemTls(server, ca),
            LnBackendTls = ProvisionedSecretsTests.PemTls(server, ca)
        });
        var startup = new LockedStartup("regtest", _files.ConfigPath, configuration, provisioner, new ListLogger(),
                                        TimeSpan.Zero, requireProvisionedSecrets: true);

        // Act: unlock, then the node's servers as the daemon composes them
        var unlocked = await startup.RunAsync(Ct);
        using var keyManager = unlocked!.KeyManager;
        var response = await attempt.Response;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNltgNodeServices(unlocked.Configuration, keyManager);
        services.AddProvisionedSecrets(unlocked.Secrets);
        services.AddCashuPaymentProcessorHost();
        services.AddLnBackendHost();
        services.AddLndGrpcHost(_files.ConfigPath);
        await using var provider = services.BuildServiceProvider();
        var hosts = provider.GetServices<IHostedService>().ToList();
        var lnd = hosts.OfType<LndGrpcHost>().Single();
        var cashu = hosts.OfType<CashuPaymentProcessorHost>().Single();
        var backend = hosts.OfType<LnBackendHost>().Single();
        var onion = provider.GetRequiredService<ITorOnionService>();
        await lnd.StartAsync(Ct);
        await cashu.StartAsync(Ct);
        await backend.StartAsync(Ct);
        await onion.StartAsync(Ct);
        try
        {
            // Assert: unlocked to the key's node id with the three macaroons
            Assert.True(response.Ok, response.Error);
            Assert.Equal(nodeId, response.NodeId);

            // LND gRPC: the delivered certificate, and the admin macaroon of the delivered root key
            var admin = Convert.FromBase64String(response.LndGrpcMacaroons!["admin"]);
            Assert.Equal("0", await RawGrpcStatusAsync(lnd.BoundPort!.Value, "/verrpc.Versioner/GetVersion", server,
                                                       null, admin));
            Assert.Equal("16", await RawGrpcStatusAsync(lnd.BoundPort.Value, "/verrpc.Versioner/GetVersion", server,
                                                        null, null));

            // Cashu processor and LN backend: mutual TLS with the delivered CA
            Assert.Equal("sat", (await CashuSettingsAsync(cashu.BoundPort!.Value, client)).Unit);
            await Assert.ThrowsAnyAsync<Exception>(() => CashuSettingsAsync(cashu.BoundPort.Value, null));
            Assert.NotNull(await RawGrpcStatusAsync(backend.BoundPort!.Value, "/cln.Node/Getinfo", server, client,
                                                    null));
            await Assert.ThrowsAsync<HttpRequestException>(
                () => RawGrpcStatusAsync(backend.BoundPort.Value, "/cln.Node/Getinfo", server, null, null));

            // Tor: authenticated with the delivered control password, the service added with the delivered key
            await WaitUntilAsync(() => onion.OnionHost is not null);
            Assert.Contains(tor.Commands, c => c.StartsWith($"ADD_ONION {onionKey} ", StringComparison.Ordinal));
            Assert.DoesNotContain(tor.Commands, c => c.StartsWith("ADD_ONION NEW", StringComparison.Ordinal));

            // Nothing but appsettings.json in the configuration directory, and no secret anywhere in it
            var files = Directory.EnumerateFileSystemEntries(_files.ConfigPath, "*", SearchOption.AllDirectories)
                                 .Select(p => Path.GetRelativePath(_files.ConfigPath, p)).ToList();
            Assert.Equal(["appsettings.json"], files);
            _files.AssertNoSecretInConfigDirectory(TorControlPassword);
            _files.AssertNoSecretInConfigDirectory("rpc-secret");
            _files.AssertNoSecretInConfigDirectory(onionKey["ED25519-V3:".Length..]);
            _files.AssertNoSecretInConfigDirectory(Convert.ToBase64String(rootKey));
        }
        finally
        {
            await onion.StopAsync();
            await backend.StopAsync(CancellationToken.None);
            await cashu.StopAsync(CancellationToken.None);
            await lnd.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Given_RequiredSecretsMissing_When_TheKeyIsDelivered_Then_TheNodeStaysLockedAndWritesNothing()
    {
        // Arrange: LND gRPC on, RequireProvisionedSecrets, neither the files nor the secrets
        var configuration = new ConfigurationBuilder().AddConfiguration(_files.Configuration())
                                                      .AddInMemoryCollection(new Dictionary<string, string?>
                                                      {
                                                          ["LndGrpc:Enabled"] = "true"
                                                      }).Build();
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out _);
        var before = _files.SnapshotConfigDirectory();
        var provisioner = new QueueKeyProvisioner();
        var attempt = provisioner.Enqueue(keyFile, Password);
        provisioner.Complete();
        var startup = new LockedStartup("regtest", _files.ConfigPath, configuration, provisioner, new ListLogger(),
                                        TimeSpan.Zero, requireProvisionedSecrets: true);

        // Act
        var unlocked = await startup.RunAsync(Ct);

        // Assert
        Assert.Null(unlocked);
        var refused = await attempt.Response;
        Assert.False(refused.Ok);
        Assert.Contains("secrets.lndGrpcTls", refused.Error);
        // The macaroon root key needs no file since NL-1389: the database holds it, and the start fails closed
        Assert.DoesNotContain("secrets.lndGrpcMacaroonRootKey", refused.Error);
        Assert.Equal(before, _files.SnapshotConfigDirectory());
    }

    /// <summary>A gRPC call by hand (no generated client): the <c>grpc-status</c> of the answer.</summary>
    /// <exception cref="HttpRequestException">The TLS handshake failed.</exception>
    private static async Task<string?> RawGrpcStatusAsync(int port, string path, X509Certificate2 pinned,
                                                          X509Certificate2? clientCertificate, byte[]? macaroon)
    {
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, certificate, _, _) => certificate is not null && certificate.GetRawCertData().SequenceEqual(pinned.RawData);
        if (clientCertificate is not null)
            handler.SslOptions.ClientCertificates = [clientCertificate];
        using var http = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}{path}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(new byte[5])
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("te", "trailers");
        if (macaroon is not null)
            request.Headers.TryAddWithoutValidation("macaroon", Convert.ToHexString(macaroon));
        using var response = await http.SendAsync(request, Ct);
        await response.Content.ReadAsByteArrayAsync(Ct);
        return response.TrailingHeaders.TryGetValues("grpc-status", out var trailer) ? trailer.First()
             : response.Headers.TryGetValues("grpc-status", out var header) ? header.First()
             : null;
    }

    private static async Task<SettingsResponse> CashuSettingsAsync(int port, X509Certificate2? clientCertificate)
    {
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        if (clientCertificate is not null)
            handler.ClientCertificates.Add(clientCertificate);
        using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{port}",
                                                   new GrpcChannelOptions { HttpHandler = handler });
        return await new CdkPaymentProcessor.CdkPaymentProcessorClient(channel)
                     .GetSettingsAsync(new EmptyRequest(), deadline: DateTime.UtcNow.AddSeconds(20),
                                       cancellationToken: Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out");
            await Task.Delay(20, Ct);
        }
    }
}