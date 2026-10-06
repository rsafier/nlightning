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

namespace NLightning.LndGrpc;

using Macaroons;
using Services;
using Tls;

/// <summary>What the host prepared at the start for the services of its own web host.</summary>
/// <param name="Verifier">The macaroon verifier, or null with <c>LndGrpc:AllowNoMacaroons</c>.</param>
internal sealed record LndGrpcRuntime(MacaroonVerifier? Verifier);

/// <summary>
/// Runs LND's <c>lnrpc.Lightning</c> service (<see cref="LightningService"/>) on its own Kestrel instance inside the
/// node's host (<c>LND_GRPC_PLAN.md</c>, NL-1161). Nothing happens unless <c>LndGrpc:Enabled</c>. At the start it makes
/// (or loads) <c>tls.cert</c>/<c>tls.key</c>, the macaroon root key and the three default macaroons in the data
/// directory, then listens with TLS on exactly the configured address; every call passes the
/// <see cref="MacaroonAuthInterceptor"/>.
/// </summary>
public sealed class LndGrpcHost : IHostedService, IAsyncDisposable
{
    /// <summary>The TLS web client authentication extended key usage.</summary>
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    private readonly ILogger<LndGrpcHost> _logger;
    private readonly LndGrpcOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;

    private WebApplication? _app;
    private X509Certificate2? _certificate;

    public LndGrpcHost(IServiceProvider serviceProvider, IOptions<LndGrpcOptions> options, string dataDirectory,
                       ILogger<LndGrpcHost> logger, TimeProvider? timeProvider = null)
    {
        _serviceProvider = serviceProvider;
        _options = options.Value;
        DataDirectory = dataDirectory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Where the certificate, the root key and the macaroons are.</summary>
    public string DataDirectory { get; }

    /// <summary>The port the server listens on once started (the configured one, or the one picked for port 0).</summary>
    public int? BoundPort { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return Task.CompletedTask;

        foreach (var error in _options.GetValidationErrors())
            throw new InvalidOperationException(error);

        _certificate = LndTlsFiles.EnsureCreated(DataDirectory, _options, _timeProvider, _logger);
        MacaroonVerifier? verifier = null;
        if (!_options.AllowNoMacaroons)
        {
            LndMacaroonFiles.EnsureCreated(DataDirectory, _logger);
            var rootKeys = _serviceProvider.GetService<LndRootKeyStore>() is { } registered
                        && registered.DataDirectory == DataDirectory
                               ? registered
                               : new LndRootKeyStore(DataDirectory);
            verifier = new MacaroonVerifier(rootKeys, _timeProvider);
        }

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
        builder.Services.AddSingleton(new LndGrpcRuntime(verifier));
        builder.Services.AddSingleton<MacaroonAuthInterceptor>();
        builder.Services.AddGrpc(grpc =>
        {
            grpc.Interceptors.Add<MacaroonAuthInterceptor>();
            // DescribeGraph answers can be large (LND clients raise their receive limit too)
            grpc.MaxSendMessageSize = null;
        });
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<LightningService>());
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<RouterService>());
        builder.Services.AddSingleton(_serviceProvider.GetRequiredService<InvoicesService>());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxConcurrentConnections = _options.MaxConnections;
            kestrel.Limits.Http2.MaxStreamsPerConnection = 64;
            kestrel.Listen(IPAddress.Parse(_options.ListenAddress), _options.Port, ConfigureListener);
        });

        var app = builder.Build();
        app.MapGrpcService<LightningService>();
        app.MapGrpcService<RouterService>();
        app.MapGrpcService<InvoicesService>();
        app.StartAsync(cancellationToken).GetAwaiter().GetResult();
        _app = app;

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                     ?? [];
        if (addresses.Count != 1)
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("The LND gRPC server must listen on exactly its configured address, "
                                              + $"not on {string.Join(", ", addresses)}.");
        }

        BoundPort = new Uri(addresses.First()).Port;
        _logger.LogInformation("LND gRPC (lnrpc.Lightning, routerrpc.Router, invoicesrpc.Invoices) listening on {Address}:{Port} (TLS{ClientCertificates}, "
                             + "{Macaroons}; files in {Directory})", _options.ListenAddress, BoundPort,
                               string.IsNullOrWhiteSpace(_options.ClientCaPath) ? "" : " with client certificates",
                               verifier is null ? "NO macaroons" : "macaroons", DataDirectory);
        if (verifier is null)
            _logger.LogWarning("INSECURE: the LND gRPC server checks no macaroon (LndGrpc:AllowNoMacaroons): every "
                             + "local process and user can spend from this node through it");
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

        _certificate?.Dispose();
        _certificate = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _certificate?.Dispose();
    }

    private void ConfigureListener(ListenOptions listen)
    {
        listen.Protocols = HttpProtocols.Http2;
        var ca = string.IsNullOrWhiteSpace(_options.ClientCaPath)
                     ? null
                     : X509CertificateLoader.LoadCertificateFromFile(_options.ClientCaPath);
        listen.UseHttps(https =>
        {
            https.ServerCertificate = _certificate;
            if (ca is null)
                return;

            https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (clientCertificate, _, _) => IsSignedBy(clientCertificate, ca);
        });
    }

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