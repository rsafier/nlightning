// Ported from LNUnit.LND (https://github.com/nbd-wtf/LNUnit, LNDSetttings.cs), Copyright (c) 2024-2025 nbd,
// MIT License; the full text is in LICENSE-LNUnit.txt next to this file. Changed: the TLS certificate and the macaroon
// are held as bytes (base64 kept as views), "from files"/"from bytes" factories, certificate pinning or a custom
// validation callback instead of accepting any certificate, optional macaroon, configurable message sizes.

using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NLightning.Testing.Lnd;

/// <summary>
/// Where an LND node's gRPC endpoint is and how to authenticate to it: the node's TLS certificate (pinned) and a
/// macaroon. Both the Docker fixtures (files copied out of containers) and the Kubernetes harness (bytes read with
/// exec) feed it, through <see cref="FromFiles"/> or <see cref="FromBytes"/>.
/// </summary>
public class LndSettings
{
    /// <summary>LND's default gRPC port (<c>rpclisten</c>).</summary>
    public const int DefaultGrpcPort = 10009;

    /// <summary>The message size limit in both directions, as LNUnit.LND set it (describegraph replies are large).</summary>
    public const int DefaultMaxMessageSize = 128_000_000;

    private string? _grpcEndpoint;

    /// <summary>The gRPC endpoint, e.g. <c>https://localhost:10009</c>. A value without a scheme gets <c>https://</c>.</summary>
    public string? GrpcEndpoint
    {
        get => _grpcEndpoint;
        set => _grpcEndpoint = value is null ? null : NormalizeEndpoint(value);
    }

    /// <summary>The node's <c>tls.cert</c>, PEM or DER. It is pinned: the server must present exactly this certificate.</summary>
    public byte[]? TlsCert { get; set; }

    /// <summary>The macaroon file's bytes (binary, e.g. <c>admin.macaroon</c>); null for a node run with <c>--no-macaroons</c>.</summary>
    public byte[]? Macaroon { get; set; }

    /// <summary>
    /// Replaces certificate pinning: called with the server's certificate, its chain and the platform's policy errors.
    /// Takes precedence over <see cref="TlsCert"/>. Return true to accept.
    /// </summary>
    public Func<X509Certificate2, X509Chain?, SslPolicyErrors, bool>? ServerCertificateValidation { get; set; }

    /// <summary>The largest message the client accepts, in bytes.</summary>
    public int MaxReceiveMessageSize { get; set; } = DefaultMaxMessageSize;

    /// <summary>The largest message the client sends, in bytes.</summary>
    public int MaxSendMessageSize { get; set; } = DefaultMaxMessageSize;

    /// <summary>LNUnit.LND's name: <see cref="TlsCert"/> as base64.</summary>
    public string? TlsCertBase64
    {
        get => TlsCert is null ? null : Convert.ToBase64String(TlsCert);
        set => TlsCert = value is null ? null : Convert.FromBase64String(value);
    }

    /// <summary>LNUnit.LND's name: <see cref="Macaroon"/> as base64.</summary>
    public string? MacaroonBase64
    {
        get => Macaroon is null ? null : Convert.ToBase64String(Macaroon);
        set => Macaroon = value is null ? null : Convert.FromBase64String(value);
    }

    /// <summary>The macaroon as LND's <c>macaroon</c> metadata value (lower-case hex), or null without one.</summary>
    public string? MacaroonHex => Macaroon is null ? null : Convert.ToHexStringLower(Macaroon);

    /// <summary>Settings from the files LND writes (<c>tls.cert</c> and a macaroon, e.g. <c>admin.macaroon</c>).</summary>
    public static LndSettings FromFiles(string grpcEndpoint, string tlsCertPath, string? macaroonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tlsCertPath);
        return FromBytes(grpcEndpoint, File.ReadAllBytes(tlsCertPath),
                         macaroonPath is null ? null : File.ReadAllBytes(macaroonPath));
    }

    /// <summary>Settings from the files LND writes, at <c>https://host:port</c>.</summary>
    public static LndSettings FromFiles(string host, int port, string tlsCertPath, string? macaroonPath) =>
        FromFiles(ToGrpcEndpoint(host, port), tlsCertPath, macaroonPath);

    /// <summary>Settings from the certificate's and the macaroon's bytes (copied, so later changes to the arrays do not leak in).</summary>
    public static LndSettings FromBytes(string grpcEndpoint, byte[] tlsCert, byte[]? macaroon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grpcEndpoint);
        ArgumentNullException.ThrowIfNull(tlsCert);
        if (tlsCert.Length == 0)
            throw new ArgumentException("The TLS certificate is empty.", nameof(tlsCert));

        return new LndSettings
        {
            GrpcEndpoint = grpcEndpoint,
            TlsCert = (byte[])tlsCert.Clone(),
            Macaroon = (byte[]?)macaroon?.Clone()
        };
    }

    /// <summary>Settings from the certificate's and the macaroon's bytes, at <c>https://host:port</c>.</summary>
    public static LndSettings FromBytes(string host, int port, byte[] tlsCert, byte[]? macaroon) =>
        FromBytes(ToGrpcEndpoint(host, port), tlsCert, macaroon);

    /// <summary>Settings from base64 strings, as LNUnit.LND's <c>LNDSettings</c> carried them.</summary>
    public static LndSettings FromBase64(string grpcEndpoint, string tlsCertBase64, string? macaroonBase64) =>
        FromBytes(grpcEndpoint, Convert.FromBase64String(tlsCertBase64),
                  macaroonBase64 is null ? null : Convert.FromBase64String(macaroonBase64));

    /// <summary><c>https://host:port</c>, with an IPv6 literal in brackets.</summary>
    public static string ToGrpcEndpoint(string host, int port = DefaultGrpcPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        var name = host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
        return $"https://{name}:{port}";
    }

    /// <summary>A copy that shares nothing mutable with this one.</summary>
    public LndSettings Clone() => new()
    {
        GrpcEndpoint = GrpcEndpoint,
        TlsCert = (byte[]?)TlsCert?.Clone(),
        Macaroon = (byte[]?)Macaroon?.Clone(),
        ServerCertificateValidation = ServerCertificateValidation,
        MaxReceiveMessageSize = MaxReceiveMessageSize,
        MaxSendMessageSize = MaxSendMessageSize
    };

    /// <summary>Throws <see cref="InvalidOperationException"/> when the settings cannot open a connection.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(GrpcEndpoint))
            throw new InvalidOperationException("LndSettings.GrpcEndpoint is not set.");
        if (ServerCertificateValidation is null && (TlsCert is null || TlsCert.Length == 0))
            throw new InvalidOperationException(
                "LndSettings needs the node's TLS certificate (TlsCert) or a ServerCertificateValidation callback.");
        if (Macaroon is { Length: 0 })
            throw new InvalidOperationException("LndSettings.Macaroon is empty; leave it null for --no-macaroons.");
        if (MaxReceiveMessageSize <= 0 || MaxSendMessageSize <= 0)
            throw new InvalidOperationException("LndSettings message size limits must be positive.");
    }

    /// <summary>The pinned certificate from <see cref="TlsCert"/> (PEM or DER).</summary>
    public X509Certificate2 LoadTlsCertificate()
    {
        if (TlsCert is null || TlsCert.Length == 0)
            throw new InvalidOperationException("LndSettings.TlsCert is not set.");

        return IsPem(TlsCert)
            ? X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(TlsCert))
            : X509CertificateLoader.LoadCertificate(TlsCert);
    }

    internal static string NormalizeEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        var trimmed = endpoint.Trim();
        var withScheme = trimmed.Contains("://", StringComparison.Ordinal) ? trimmed : "https://" + trimmed;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException($"'{endpoint}' is not a gRPC endpoint (expected https://host:port).",
                                        nameof(endpoint));
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"'{endpoint}' is not https: LND's gRPC endpoint always uses TLS.",
                                        nameof(endpoint));

        return $"https://{uri.Authority}";
    }

    private static bool IsPem(ReadOnlySpan<byte> bytes)
    {
        var start = bytes.TrimStart("\r\n\t "u8);
        return start.StartsWith("-----BEGIN"u8);
    }
}