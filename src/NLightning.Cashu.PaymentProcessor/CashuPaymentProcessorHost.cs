using System.Net;
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
/// provider, so it uses the node's invoice, payment and event services. Without <c>TlsDirectory</c> it serves
/// HTTP/2 without TLS (h2c, what <c>cdk-mintd</c> uses without <c>tls_dir</c>), which the options allow on loopback
/// only. With it, it serves TLS with <c>server.pem</c>/<c>server.key</c>, and when <c>ca.pem</c> is there too every
/// client must present a certificate that CA signed.
/// </remarks>
public sealed class CashuPaymentProcessorHost : IHostedService, IAsyncDisposable
{
    private readonly ILogger<CashuPaymentProcessorHost> _logger;
    private readonly CashuPaymentProcessorOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private WebApplication? _app;

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

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<ILoggerFactory>());
        builder.Services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(_ => _serviceProvider.GetRequiredService<CdkPaymentProcessorService>());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Parse(_options.ListenAddress), _options.Port, ConfigureListener);
        });

        var app = builder.Build();
        app.MapGrpcService<CdkPaymentProcessorService>();
        await app.StartAsync(cancellationToken);
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        BoundPort = addresses?.Select(a => new Uri(a).Port).FirstOrDefault(_options.Port) ?? _options.Port;
        _logger.LogInformation("Cashu payment processor listening on {Address}:{Port} ({Transport}, unit {Unit})",
                               _options.ListenAddress, BoundPort,
                               string.IsNullOrWhiteSpace(_options.TlsDirectory) ? "h2c" : "TLS", _options.Unit);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is null)
            return;

        await _app.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
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
        var ca = File.Exists(caPath) ? X509CertificateLoader.LoadCertificateFromFile(caPath) : null;
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
    internal static bool IsSignedBy(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate);
    }
}