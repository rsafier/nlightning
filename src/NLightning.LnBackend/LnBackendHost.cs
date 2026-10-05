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

namespace NLightning.LnBackend;

/// <summary>
/// Runs the <c>hold.Hold</c> and <c>cln.Node</c> gRPC services on their own Kestrel instance inside the node's host
/// (the Bark/ASP backend). It does nothing unless <c>LnBackend:Enabled</c>. The TLS story is the Cashu payment
/// processor's (<c>LnBackend:TlsDirectory</c>: <c>server.pem</c>/<c>server.key</c>, client certificates signed by its
/// <c>ca.pem</c> required; without it, h2c on loopback only with <c>AllowInsecureLoopback</c>).
/// </summary>
public sealed class LnBackendHost : IHostedService, IAsyncDisposable
{
    /// <summary>The TLS web client authentication extended key usage.</summary>
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private readonly ILogger<LnBackendHost> _logger;
    private readonly LnBackendOptions _options;
    private readonly IServiceProvider _serviceProvider;

    private WebApplication? _app;

    public LnBackendHost(IServiceProvider serviceProvider, IOptions<LnBackendOptions> options,
                         ILogger<LnBackendHost> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>The port the server listens on once started (the configured one, or the one picked for port 0).</summary>
    public int? BoundPort { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return Task.CompletedTask;

        foreach (var error in _options.GetValidationErrors())
            throw new InvalidOperationException(error);

        // No ambient configuration (appsettings.json in the working directory, environment variables): a Kestrel
        // section there would add endpoints beside the checked one (the Cashu host's NL-998 rule)
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
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<HoldBackendService>());
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<ClnNodeBackendService>());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxConcurrentConnections = _options.MaxConnections;
            kestrel.Limits.Http2.MaxStreamsPerConnection = 16;
            kestrel.Listen(IPAddress.Parse(_options.ListenAddress), _options.Port, ConfigureListener);
        });

        var app = builder.Build();
        app.MapGrpcService<HoldBackendService>();
        app.MapGrpcService<ClnNodeBackendService>();
        app.StartAsync(cancellationToken).GetAwaiter().GetResult();
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                     ?? [];
        if (addresses.Count != 1)
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("The LN backend must listen on exactly its configured address, "
                                              + $"not on {string.Join(", ", addresses)}.");
        }

        BoundPort = new Uri(addresses.First()).Port;
        var mutualTls = !string.IsNullOrWhiteSpace(_options.TlsDirectory) && HasCa(_options.TlsDirectory);
        _logger.LogInformation("LN backend (hold.Hold, cln.Node) listening on {Address}:{Port} ({Transport})",
                               _options.ListenAddress, BoundPort,
                               mutualTls ? "mTLS"
                                         : string.IsNullOrWhiteSpace(_options.TlsDirectory)
                                             ? "insecure loopback: h2c, no client authentication"
                                             : "insecure loopback: TLS without client authentication");
        if (!mutualTls)
            _logger.LogWarning("INSECURE: the LN backend authenticates no client "
                             + "(LnBackend:AllowInsecureLoopback): every local process and user can settle or cancel "
                             + "its hold invoices and create invoices of this node");
        return Task.CompletedTask;
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
        var ca = HasCa(directory)
                     ? X509CertificateLoader.LoadCertificateFromFile(Path.Combine(directory, "ca.pem"))
                     : null;
        listen.UseHttps(https =>
        {
            https.ServerCertificate = certificate;
            if (ca is null)
                return;

            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (clientCertificate, _, _) => IsSignedBy(clientCertificate, ca);
        });
    }

    private static bool HasCa(string directory) => File.Exists(Path.Combine(directory, "ca.pem"));

    /// <summary>Whether <paramref name="certificate"/> chains to <paramref name="ca"/> alone (custom trust).</summary>
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
}