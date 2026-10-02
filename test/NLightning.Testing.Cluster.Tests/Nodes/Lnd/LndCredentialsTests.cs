using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NLightning.Testing.Cluster.Tests.Nodes.Lnd;

using Cluster.Kube;
using Cluster.Nodes;
using Cluster.Nodes.Lnd;

public class LndCredentialsTests
{
    private static readonly byte[] s_macaroon = [0x02, 0x01, 0x03, 0x6c, 0x6e, 0x64, 0xff];

    /// <summary>A self-signed ECDSA certificate like LND's, as PEM.</summary>
    private static string NewCertificatePem(string subject = "CN=alice")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                                         DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }

    [Fact]
    public void Given_LndsFiles_When_Loaded_Then_TheMacaroonIsHexAndTheCertificateIsPinned()
    {
        // Arrange
        var pem = NewCertificatePem();

        // Act
        var credentials = new LndCredentials(pem, s_macaroon);

        // Assert
        Assert.Equal("0201036c6e64ff", credentials.AdminMacaroonHex);
        using var same = X509Certificate2.CreateFromPem(pem);
        using var other = X509Certificate2.CreateFromPem(NewCertificatePem());
        Assert.True(credentials.Matches(same));
        Assert.False(credentials.Matches(other));
        Assert.False(credentials.Matches(null));
        Assert.Equal(same.RawData, credentials.TlsCertDer);
    }

    [Fact]
    public void Given_BrokenFiles_When_Loaded_Then_TheyThrow()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => new LndCredentials(NewCertificatePem(), []));
        Assert.ThrowsAny<ArgumentException>(() => new LndCredentials(" ", s_macaroon));
        Assert.ThrowsAny<CryptographicException>(() => new LndCredentials(
                                                     "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----",
                                                     s_macaroon));
    }

    [Fact]
    public async Task Given_ANodeThatWroteItsFiles_When_TheCredentialsAreRead_Then_TheyComeFromLndsPaths()
    {
        // Arrange
        var pem = NewCertificatePem();
        var node = new FileNode(new Dictionary<string, byte[]>
        {
            [LndWorkload.TlsCertPath] = Encoding.ASCII.GetBytes(pem),
            [LndWorkload.AdminMacaroonPath] = s_macaroon
        });

        // Act
        var credentials = await LndCredentials.ReadAsync(node, TimeSpan.FromSeconds(5),
                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(pem, credentials.TlsCertPem);
        Assert.Equal(s_macaroon, credentials.AdminMacaroon);
        Assert.Equal([LndWorkload.TlsCertPath, LndWorkload.AdminMacaroonPath], node.Waited);
    }

    /// <summary>A node handle whose files are a dictionary.</summary>
    private sealed class FileNode(IReadOnlyDictionary<string, byte[]> files) : INodeHandle
    {
        public List<string> Waited { get; } = [];

        public string Name => "alice";
        public NodeKind Kind => NodeKind.Lnd;
        public string Namespace => "nltg-spike-r1";
        public string PodName => "alice-0";
        public string ContainerName => "alice";
        public string ServiceDnsName => "alice.nltg-spike-r1.svc.cluster.local";
        public string PodDnsName => "alice-0.alice.nltg-spike-r1.svc.cluster.local";
        public string? PodIp => "10.0.0.1";

        public Task<byte[]> WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Waited.Add(path);
            return Task.FromResult(files[path]);
        }

        public Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(files[path]);

        public Task WaitReadyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> ReadLogAsync(int? tailLines, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RestartAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task KillAsync(TimeSpan readyTimeout, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}