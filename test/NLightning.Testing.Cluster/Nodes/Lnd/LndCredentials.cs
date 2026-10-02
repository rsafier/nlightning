using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NLightning.Testing.Cluster.Nodes.Lnd;

/// <summary>
/// What a gRPC client needs to talk to an LND node: its self-signed TLS certificate and the admin macaroon, read out of
/// the pod with exec (LNUnit PR #10's <c>WaitForFileAndRead</c>, here <see cref="INodeHandle.WaitForFileAsync"/>). Both
/// live on the node's PVC, so they stay the same across restarts.
/// </summary>
public sealed class LndCredentials
{
    public LndCredentials(string tlsCertPem, byte[] adminMacaroon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tlsCertPem);
        ArgumentNullException.ThrowIfNull(adminMacaroon);
        if (adminMacaroon.Length == 0)
            throw new ArgumentException("The macaroon is empty", nameof(adminMacaroon));

        TlsCertPem = tlsCertPem;
        AdminMacaroon = adminMacaroon;
        // Throws when the PEM is not a certificate (a half-written file)
        using var certificate = X509Certificate2.CreateFromPem(tlsCertPem);
        TlsCertDer = certificate.RawData;
    }

    /// <summary>LND's <c>tls.cert</c> as written by LND (PEM).</summary>
    public string TlsCertPem { get; }

    /// <summary>The certificate's DER bytes: the pin a host client compares the server certificate with.</summary>
    public byte[] TlsCertDer { get; }

    /// <summary>The raw <c>admin.macaroon</c>.</summary>
    public byte[] AdminMacaroon { get; }

    /// <summary>The macaroon as LND's gRPC <c>macaroon</c> metadata value (lower-case hex).</summary>
    public string AdminMacaroonHex => Convert.ToHexStringLower(AdminMacaroon);

    /// <summary>Whether <paramref name="presented"/> is exactly the pinned certificate.</summary>
    public bool Matches(X509Certificate? presented) =>
        presented is not null && presented.GetRawCertData().AsSpan().SequenceEqual(TlsCertDer);

    /// <summary>
    /// Waits until LND has written its certificate and admin macaroon (the wallet exists), then reads both.
    /// </summary>
    public static async Task<LndCredentials> ReadAsync(INodeHandle node, TimeSpan timeout,
                                                       CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(node);

        var deadline = DateTime.UtcNow + timeout;
        var cert = await node.WaitForFileAsync(LndWorkload.TlsCertPath, timeout, cancellationToken)
                             .ConfigureAwait(false);
        var remaining = deadline - DateTime.UtcNow;
        var macaroon = await node.WaitForFileAsync(LndWorkload.AdminMacaroonPath,
                                                   remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1),
                                                   cancellationToken)
                                 .ConfigureAwait(false);
        return new LndCredentials(Encoding.ASCII.GetString(cert), macaroon);
    }
}