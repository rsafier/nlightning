using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NLightning.Integration.Tests.Docker.Bark;

/// <summary>
/// The mTLS material of the Bark/ASP proof: captaind's Lightning clients configure TLS for their URIs whatever the
/// scheme — an <c>http://</c> URI dies at channel build, parsing the certificate paths it still requires ("Error
/// parsing TLS private key") — so the node's LN backend serves <c>https</c> with <c>LnBackend:TlsDirectory</c>: a CA
/// this class makes, the server certificate captaind dials (the host name the pods reach us at) and the client
/// certificate captaind presents, which the backend's <c>ca.pem</c> trust requires.
/// </summary>
public sealed class BarkLnBackendTls : IDisposable
{
    /// <summary>The directory the node's <c>LnBackend:TlsDirectory</c> reads.</summary>
    public string Directory { get; }

    /// <summary>The CA's certificate (<c>ca.pem</c>: the backend's client-certificate trust, captaind's server root).</summary>
    public string CaCertificatePem { get; }

    /// <summary>The server certificate (<c>server.pem</c>): the LN backend's own, valid for the host the pods dial.</summary>
    public string ServerCertificatePem { get; }

    /// <summary>The server's PKCS 8 key (<c>server.key</c>).</summary>
    public string ServerKeyPem { get; }

    /// <summary>The client certificate (<c>client.pem</c>): captaind's identity, carrying the client-auth EKU the backend's
    /// validation requires.</summary>
    public string ClientCertificatePem { get; }

    /// <summary>The client's PKCS 8 key (<c>client.key</c>).</summary>
    public string ClientKeyPem { get; }

    private BarkLnBackendTls(string directory, string caPem, string serverPem, string serverKeyPem, string clientPem,
                             string clientKeyPem)
    {
        Directory = directory;
        CaCertificatePem = caPem;
        ServerCertificatePem = serverPem;
        ServerKeyPem = serverKeyPem;
        ClientCertificatePem = clientPem;
        ClientKeyPem = clientKeyPem;
    }

    /// <summary>
    /// Makes the CA, the server certificate for <paramref name="serverDnsName"/> and the client certificate, and writes
    /// the backend's <c>TlsDirectory</c> layout (<c>ca.pem</c>, <c>server.pem</c>, <c>server.key</c>).
    /// </summary>
    public static BarkLnBackendTls Create(string serverDnsName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverDnsName);
        using var caKey = RSA.Create(2048);
        var notBefore = DateTimeOffset.UtcNow.AddHours(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(2);
        var caRequest = new CertificateRequest("CN=nltg-bark-asp-test CA", caKey, HashAlgorithmName.SHA256,
                                               RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var ca = caRequest.CreateSelfSigned(notBefore, notAfter);

        var (serverCertificate, serverKey) = MakeEndEntityCertificate(ca, "CN=nltg-bark-asp-ln-backend",
                                                                       [ServerNameExtension(serverDnsName)],
                                                                       X509KeyUsageFlags.DigitalSignature,
                                                                       "1.3.6.1.5.5.7.3.1" /* serverAuth */);
        var (clientCertificate, clientKey) = MakeEndEntityCertificate(ca, "CN=nltg-bark-asp-captaind", [],
                                                                       X509KeyUsageFlags.DigitalSignature,
                                                                       "1.3.6.1.5.5.7.3.2" /* clientAuth */);

        var directory = Path.Combine(Path.GetTempPath(), $"nltg-bark-asp-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(directory);
        var tls = new BarkLnBackendTls(directory,
                                       ca.ExportCertificatePem(), serverCertificate.ExportCertificatePem(),
                                       serverKey, clientCertificate.ExportCertificatePem(), clientKey);
        File.WriteAllText(Path.Combine(directory, "ca.pem"), tls.CaCertificatePem);
        File.WriteAllText(Path.Combine(directory, "server.pem"), tls.ServerCertificatePem);
        File.WriteAllText(Path.Combine(directory, "server.key"), tls.ServerKeyPem);
        return tls;
    }

    /// <summary>
    /// An end-entity certificate signed by the test CA (no CA constraint, the key usage and the EKU given) and its
    /// PKCS 8 key: <see cref="X509Certificate2"/> instances built from a <see cref="CertificateRequest"/> carry no
    /// private key, so the key travels beside the certificate.
    /// </summary>
    private static (X509Certificate2 Certificate, string KeyPem) MakeEndEntityCertificate(X509Certificate2 ca,
        string subject, IList<X509Extension> extensions, X509KeyUsageFlags keyUsage, string enhancedKeyUsage)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(enhancedKeyUsage)], true));
        foreach (var extension in extensions)
            request.CertificateExtensions.Add(extension);
        var serial = RandomNumberGenerator.GetBytes(16);
        // Strictly inside the issuer's own validity (a second past it is rejected)
        return (request.Create(ca, ca.NotBefore.ToUniversalTime().AddMinutes(1), ca.NotAfter.ToUniversalTime(),
                               serial),
                key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>The server certificate's SAN: the DNS names and loopback IP captaind may dial us at.</summary>
    private static X509Extension ServerNameExtension(string serverDnsName)
    {
        var builder = new SubjectAlternativeNameBuilder();
        builder.AddDnsName(serverDnsName);
        builder.AddDnsName("localhost");
        builder.AddIpAddress(IPAddress.Loopback);
        return builder.Build();
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception e)
        {
            Console.WriteLine($"failed to delete {Directory}: {e.Message}");
        }
    }
}