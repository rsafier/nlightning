using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Cashu.PaymentProcessor;

/// <summary>
/// Runs the gRPC server of <see cref="CdkPaymentProcessorService"/> on its own Kestrel instance inside the node's host
/// (Cashu plan C1, NL-992). It does nothing unless <c>Cashu:PaymentProcessor:Enabled</c>.
/// </summary>
/// <remarks>
/// The server has its own small service provider (gRPC and Kestrel only); the service instance comes from the node's
/// provider, so it uses the node's invoice, payment, offer, wallet and event services, and its background loops
/// (<see cref="CdkPaymentProcessorService.StartBackgroundAsync"/>) run while the server does. It reads no ambient
/// configuration (only the one checked endpoint). Without <c>TlsDirectory</c> it serves HTTP/2 without TLS (h2c, what
/// <c>cdk-mintd</c> uses with <c>allow_insecure</c>), which the options allow on loopback only with
/// <c>AllowInsecureLoopback</c>. With it, it serves TLS with <c>server.pem</c>/<c>server.key</c>, and with
/// <c>ca.pem</c> (required unless <c>AllowInsecureLoopback</c>) every client must present a certificate that CA signed
/// for client authentication.
/// </remarks>
public sealed class CashuPaymentProcessorHost : IHostedService, IAsyncDisposable
{
    private readonly ILogger<CashuPaymentProcessorHost> _logger;
    private readonly CashuPaymentProcessorOptions _options;
    private readonly IServiceProvider _serviceProvider;
    /// <summary>The TLS web client authentication extended key usage.</summary>
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private WebApplication? _app;
    private CdkPaymentProcessorService? _service;

    public CashuPaymentProcessorHost(IServiceProvider serviceProvider,
                                     IOptions<CashuPaymentProcessorOptions> options,
                                     ILogger<CashuPaymentProcessorHost> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The port the server listens on once started (the configured one, or the one picked for port 0).</summary>
    public int? BoundPort { get; private set; }

    /// <summary>The addresses the server listens on once started (exactly one).</summary>
    internal IReadOnlyList<string> ListeningAddresses { get; private set; } = [];

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        WarnIfKeyReadable();

        // No ambient configuration (appsettings.json in the working directory, environment variables): a Kestrel
        // section there would add endpoints beside the checked one (NL-998)
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<ILoggerFactory>());
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        builder.Services.AddGrpc();
        var service = _serviceProvider.GetRequiredService<CdkPaymentProcessorService>();
        builder.Services.AddSingleton(service);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Parse(_options.ListenAddress), _options.Port, ConfigureListener);
        });

        // The event and chain loops first, so the mint's first stream sees everything after the start
        await service.StartBackgroundAsync(cancellationToken);
        _service = service;

        var app = builder.Build();
        app.MapGrpcService<CdkPaymentProcessorService>();
        await app.StartAsync(cancellationToken);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                     ?? [];
        if (addresses.Count != 1)
        {
            await StopAsync(CancellationToken.None);
            throw new InvalidOperationException("The Cashu payment processor must listen on exactly its configured "
                                              + $"address, not on {string.Join(", ", addresses)}.");
        }

        ListeningAddresses = [.. addresses];
        BoundPort = new Uri(addresses.First()).Port;
        var mutualTls = !string.IsNullOrWhiteSpace(_options.TlsDirectory) && HasCa(_options.TlsDirectory);
        var transport = mutualTls
                            ? "mTLS"
                            : string.IsNullOrWhiteSpace(_options.TlsDirectory)
                                ? "insecure loopback: h2c, no client authentication"
                                : "insecure loopback: TLS without client authentication";
        _logger.LogInformation("Cashu payment processor listening on {Address}:{Port} ({Transport}, unit {Unit})",
                               _options.ListenAddress, BoundPort, transport, _options.Unit);
        if (!mutualTls)
            _logger.LogWarning("INSECURE: the Cashu payment processor authenticates no client "
                             + "(Cashu:PaymentProcessor:AllowInsecureLoopback): every local process and user can pay "
                             + "from this node's channels through it");
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken);
            await _app.DisposeAsync();
            _app = null;
        }

        if (_service is not null)
        {
            await _service.StopBackgroundAsync();
            _service = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private void ConfigureListener(ListenOptions listen)
    {
        listen.Protocols = HttpProtocols.Http2;
        if (string.IsNullOrWhiteSpace(_options.TlsDirectory))
            return;

        var directory = _options.TlsDirectory;
        var certificate = X509Certificate2.CreateFromPemFile(Path.Combine(directory, "server.pem"),
                                                             Path.Combine(directory, "server.key"));
        var caPath = Path.Combine(directory, "ca.pem");
        var ca = HasCa(directory) ? X509CertificateLoader.LoadCertificateFromFile(caPath) : null;
        listen.UseHttps(https =>
        {
            https.ServerCertificate = certificate;
            if (ca is null)
                return;

            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (clientCertificate, _, _) => IsSignedBy(clientCertificate, ca);
        });
    }

    /// <summary>
    /// Whether <paramref name="certificate"/> chains to <paramref name="ca"/> alone (custom trust, no system roots).
    /// </summary>
    /// <remarks>
    /// The certificate must allow client authentication (the TLS client EKU, when it lists EKUs). No revocation check:
    /// the CA is the operator's own for this one mint.
    /// </remarks>
    internal static bool IsSignedBy(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
        try
        {
            return chain.Build(certificate);
        }
        finally
        {
            foreach (var element in chain.ChainElements)
                element.Certificate.Dispose();
        }
    }

    /// <summary>Warns when <c>server.key</c> can be read by the group or others (NL-1000).</summary>
    private void WarnIfKeyReadable()
    {
        if (string.IsNullOrWhiteSpace(_options.TlsDirectory) || OperatingSystem.IsWindows())
            return;

        var key = Path.Combine(_options.TlsDirectory, "server.key");
        if (File.Exists(key) && (File.GetUnixFileMode(key) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
            _logger.LogWarning("{Key} can be read by its group or by others; restrict it to its owner (chmod 600)",
                               key);
    }

    private static bool HasCa(string directory) => File.Exists(Path.Combine(directory, "ca.pem"));
}