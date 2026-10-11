using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using NLightning.Daemon.Contracts.Provisioning;
using NLightning.Daemon.Provisioning;
using NLightning.LndGrpc.Macaroons;
using NLightning.LndGrpc.Tls;

namespace NLightning.Daemon.Tests.Provisioning;

/// <summary>
/// The file secrets of a locked start (NL-1352): the version 2 frame, the checks before the key is accepted, and the
/// answers of the locked start.
/// </summary>
public sealed class ProvisionedSecretsTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly LockedNodeFiles _files = new();

    public void Dispose() => _files.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Given_ARequestWithoutVersion2Fields_When_Written_Then_ItIsAVersion1FrameAVersion1NodeReads()
    {
        // Arrange
        var stream = new MemoryStream();
        var request = new KeyProvisioningRequest
        {
            Kind = KeyProvisioningProtocol.UnlockKind,
            Secrets = new KeyProvisioningSecrets { BitcoinRpcPassword = "rpc" }
        };

        // Act
        await KeyProvisioningProtocol.WriteRequestAsync(stream, request, Ct);
        stream.Position = 0;
        var read = await KeyProvisioningProtocol.ReadRequestAsync(stream, Ct);

        // Assert
        Assert.Equal(KeyProvisioningProtocol.Version1, stream.ToArray()[4]);
        Assert.Equal(KeyProvisioningProtocol.Version1, read!.FrameVersion);
        Assert.Equal("rpc", read.Secrets!.BitcoinRpcPassword);
    }

    [Theory]
    [InlineData("tor")]
    [InlineData("root")]
    [InlineData("tls")]
    [InlineData("macaroons")]
    public async Task Given_AVersion2Field_When_Written_Then_TheFrameIsVersion2AndReadsBack(string field)
    {
        // Arrange
        var stream = new MemoryStream();
        var secrets = new KeyProvisioningSecrets();
        var request = new KeyProvisioningRequest { Kind = KeyProvisioningProtocol.UnlockKind, Secrets = secrets };
        switch (field)
        {
            case "tor": secrets.TorOnionServiceKey = "ED25519-V3:x"; break;
            case "root": secrets.LndGrpcMacaroonRootKey = "AA=="; break;
            case "tls": secrets.CashuProcessorTls = new KeyProvisioningTls { Certificate = "c", Key = "k" }; break;
            default: request.ReturnLndGrpcMacaroons = true; break;
        }

        // Act
        await KeyProvisioningProtocol.WriteRequestAsync(stream, request, Ct);
        stream.Position = 0;
        var read = await KeyProvisioningProtocol.ReadRequestAsync(stream, Ct);

        // Assert
        Assert.Equal(KeyProvisioningProtocol.Version, stream.ToArray()[4]);
        Assert.Equal(KeyProvisioningProtocol.Version, read!.FrameVersion);
        Assert.True(read.RequiresVersion2());
    }

    [Fact]
    public async Task Given_Version2FieldsInAVersion1Frame_When_Read_Then_TheRequestIsRefused()
    {
        // Arrange: a client that labels a version 2 body as version 1 (a version 1 node would ignore the fields)
        var body = """{"kind":"unlock","secrets":{"torOnionServiceKey":"ED25519-V3:secret-value"}}"""u8.ToArray();
        var stream = new MemoryStream(Frame(KeyProvisioningProtocol.Version1, body));

        // Act
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => KeyProvisioningProtocol.ReadRequestAsync(stream, Ct));

        // Assert
        Assert.Contains("version 2 fields in a version 1 frame", e.Message);
        Assert.DoesNotContain("secret-value", e.Message);
    }

    [Fact]
    public async Task Given_AnUnknownVersion_When_Read_Then_TheFrameIsRefused()
    {
        // Arrange
        var stream = new MemoryStream(Frame(3, """{"kind":"status"}"""u8.ToArray()));

        // Act / Assert
        var e = await Assert.ThrowsAsync<InvalidDataException>(() => KeyProvisioningProtocol.ReadRequestAsync(stream, Ct));
        Assert.Contains("Unsupported key provisioning version 3", e.Message);
    }

    [Fact]
    public void Given_EverySecretWellFormed_When_Read_Then_TheyAreNormalized()
    {
        // Arrange: PEM for the LND pair, DER (base64, PKCS#8 key) for the Cashu one
        using var ca = Authority();
        using var server = Issue(ca);
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var onionKey = OnionKey();
        var secrets = new KeyProvisioningSecrets
        {
            LndGrpcMacaroonRootKey = Convert.ToBase64String(rootKey),
            LndGrpcTls = PemTls(server, null),
            CashuProcessorTls = new KeyProvisioningTls
            {
                Certificate = Convert.ToBase64String(server.RawData),
                Key = Convert.ToBase64String(server.GetECDsaPrivateKey()!.ExportPkcs8PrivateKey()),
                ClientCa = Convert.ToBase64String(ca.RawData)
            },
            LnBackendTls = PemTls(server, ca),
            TorOnionServiceKey = onionKey
        };

        // Act
        var read = ProvisionedSecretsReader.Read(secrets, returnLndGrpcMacaroons: true, requireProvisioned: false,
                                                 EmptyConfiguration(), _files.ConfigPath);

        // Assert
        Assert.Equal(rootKey, read.LndGrpcMacaroonRootKey);
        Assert.Equal(onionKey, read.TorOnionServiceKey);
        Assert.NotNull(read.LndGrpcTls);
        Assert.Null(read.LndGrpcTls.ClientCaPem);
        using (var fromDer = X509Certificate2.CreateFromPem(read.CashuProcessorTls!.CertificatePem,
                                                           read.CashuProcessorTls.KeyPem))
            Assert.Equal(server.Thumbprint, fromDer.Thumbprint);
        Assert.Contains("BEGIN CERTIFICATE", read.CashuProcessorTls.ClientCaPem);
        Assert.NotNull(read.LnBackendTls!.ClientCaPem);
        Assert.False(read.RefuseGeneration);
    }

    [Theory]
    [InlineData("root-short", "secrets.lndGrpcMacaroonRootKey")]
    [InlineData("root-not-base64", "secrets.lndGrpcMacaroonRootKey")]
    [InlineData("onion-prefix", "secrets.torOnionServiceKey")]
    [InlineData("onion-length", "secrets.torOnionServiceKey")]
    [InlineData("tls-certificate", "secrets.lndGrpcTls.certificate")]
    [InlineData("tls-key-mismatch", "secrets.lndGrpcTls.key")]
    [InlineData("tls-key-garbage", "secrets.cashuProcessorTls.key")]
    [InlineData("tls-no-key", "secrets.lnBackendTls")]
    [InlineData("tls-client-ca", "secrets.lnBackendTls.clientCa")]
    [InlineData("macaroons-without-root", "returnLndGrpcMacaroons")]
    public void Given_AMalformedSecret_When_Read_Then_ItIsRefusedByNameWithoutItsValue(string kind, string named)
    {
        // Arrange
        using var ca = Authority();
        using var server = Issue(ca);
        using var other = Issue(ca);
        const string marker = "c2VjcmV0LXZhbHVl"; // "secret-value"
        var secrets = new KeyProvisioningSecrets();
        var returnMacaroons = false;
        switch (kind)
        {
            case "root-short": secrets.LndGrpcMacaroonRootKey = Convert.ToBase64String(new byte[31]); break;
            case "root-not-base64": secrets.LndGrpcMacaroonRootKey = "!" + marker; break;
            case "onion-prefix": secrets.TorOnionServiceKey = "RSA1024:" + marker; break;
            case "onion-length": secrets.TorOnionServiceKey = "ED25519-V3:" + marker; break;
            case "tls-certificate":
                secrets.LndGrpcTls = new KeyProvisioningTls { Certificate = marker, Key = marker };
                break;
            case "tls-key-mismatch":
                secrets.LndGrpcTls = new KeyProvisioningTls
                {
                    Certificate = server.ExportCertificatePem(),
                    Key = other.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem()
                };
                break;
            case "tls-key-garbage":
                secrets.CashuProcessorTls = new KeyProvisioningTls
                {
                    Certificate = server.ExportCertificatePem(),
                    Key = "-----BEGIN PRIVATE KEY-----\n" + marker + "\n-----END PRIVATE KEY-----"
                };
                break;
            case "tls-no-key":
                secrets.LnBackendTls = new KeyProvisioningTls { Certificate = server.ExportCertificatePem() };
                break;
            case "tls-client-ca":
                secrets.LnBackendTls = PemTls(server, null);
                secrets.LnBackendTls.ClientCa = marker;
                break;
            default:
                returnMacaroons = true;
                break;
        }

        // Act
        var e = Assert.Throws<InvalidDataException>(() => ProvisionedSecretsReader.Read(
                                                         secrets, returnMacaroons, false, EmptyConfiguration(),
                                                         _files.ConfigPath));

        // Assert
        Assert.Contains(named, e.Message);
        Assert.DoesNotContain(marker, e.Message);
    }

    [Fact]
    public void Given_RequiredSecretsAndNeitherFilesNorValues_When_Read_Then_EachMissingOneIsNamed()
    {
        // Arrange: LND gRPC on and an onion service, nothing on disk
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["LndGrpc:Enabled"] = "true",
            ["Node:Tor:Mode"] = "TorOnly",
            ["Node:Tor:OnionServiceKeyFile"] = Path.Combine(_files.ConfigPath, "tor_onion_v3.key")
        });

        // Act
        var e = Assert.Throws<InvalidDataException>(() => ProvisionedSecretsReader.Read(
                                                         null, false, true, configuration, _files.ConfigPath));

        // Assert
        Assert.Contains("secrets.lndGrpcTls", e.Message);
        Assert.Contains("secrets.lndGrpcMacaroonRootKey", e.Message);
        Assert.Contains("secrets.torOnionServiceKey", e.Message);
    }

    [Fact]
    public void Given_RequiredSecretsWithFilesOnDisk_When_Read_Then_TheFilesSatisfyThem()
    {
        // Arrange: an existing node keeps its files (today's behavior); only generation is forbidden
        var keyFile = Path.Combine(_files.ConfigPath, "tor_onion_v3.key");
        File.WriteAllText(keyFile, OnionKey());
        var lndDirectory = Path.Combine(_files.ConfigPath, "lnd-grpc");
        LndTlsFiles.EnsureCreated(lndDirectory, new LndGrpc.LndGrpcOptions(), TimeProvider.System).Dispose();
        LndMacaroonFiles.EnsureCreated(lndDirectory);
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["LndGrpc:Enabled"] = "true",
            ["Node:Tor:Mode"] = "TorOnly",
            ["Node:Tor:OnionServiceKeyFile"] = keyFile
        });

        // Act
        var read = ProvisionedSecretsReader.Read(null, false, true, configuration, _files.ConfigPath);

        // Assert
        Assert.True(read.RefuseGeneration);
        Assert.False(read.HasAny);
    }

    [Fact]
    public async Task Given_AMalformedSecret_When_Delivered_Then_TheNodeStaysLockedUntilAWellFormedOne()
    {
        // Arrange: an enrolled node; then a bad onion key, then a good one
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out var nodeId);
        await _files.UnlockOnceAsync(keyFile, Password);
        var before = _files.SnapshotConfigDirectory();
        var provisioner = new QueueKeyProvisioner();
        var bad = provisioner.Enqueue(keyFile, Password,
                                      new KeyProvisioningSecrets { TorOnionServiceKey = "ED25519-V3:AAAA" });
        var onionKey = OnionKey();
        var good = provisioner.Enqueue(keyFile, Password, new KeyProvisioningSecrets
        {
            TorOnionServiceKey = onionKey,
            TorControlPassword = "tor-control-secret"
        });
        var startup = _files.CreateStartup(provisioner, new ListLogger());

        // Act
        var unlocked = await startup.RunAsync(Ct);

        // Assert: the first was refused by name with nothing written; the second unlocked with its secrets
        var refused = await bad.Response;
        Assert.False(refused.Ok);
        Assert.Equal(KeyProvisioningProtocol.LockedState, refused.State);
        Assert.Contains("secrets.torOnionServiceKey", refused.Error);
        Assert.Equal(before, _files.SnapshotConfigDirectory());
        using var keyManager = unlocked!.KeyManager;
        Assert.Equal(nodeId, (await good.Response).NodeId);
        Assert.Equal(onionKey, unlocked.Secrets.TorOnionServiceKey);
        Assert.Equal("tor-control-secret", unlocked.Configuration["Node:Tor:ControlPassword"]);
        _files.AssertNoSecretInConfigDirectory("tor-control-secret");
        _files.AssertNoSecretInConfigDirectory(onionKey["ED25519-V3:".Length..]);
    }

    [Fact]
    public async Task Given_AProvisionedRootKey_When_MacaroonsAreAsked_Then_TheAnswerCarriesThemAndNoFileIsWritten()
    {
        // Arrange
        var keyFile = _files.CreateKeyFile(Password, bip32: true, out _);
        var rootKey = RandomNumberGenerator.GetBytes(32);
        var provisioner = new QueueKeyProvisioner();
        var attempt = provisioner.EnqueueReturningMacaroons(keyFile, Password, new KeyProvisioningSecrets
        {
            LndGrpcMacaroonRootKey = Convert.ToBase64String(rootKey)
        });
        var startup = _files.CreateStartup(provisioner, new ListLogger());

        // Act
        var unlocked = await startup.RunAsync(Ct);

        // Assert: three macaroons, each signed by the delivered root key with LND's permission sets
        using var keyManager = unlocked!.KeyManager;
        var response = await attempt.Response;
        Assert.True(response.Ok);
        Assert.Equal(["admin", "invoice", "readonly"], response.LndGrpcMacaroons!.Keys.Order(StringComparer.Ordinal));
        var verifier = new MacaroonVerifier(rootKey, TimeProvider.System);
        foreach (var (name, required) in new[]
                 {
                     ("admin", LndPermissions.Admin), ("readonly", LndPermissions.Read),
                     ("invoice", LndPermissions.Invoice)
                 })
            Assert.Equal(MacaroonCheck.Allowed,
                         verifier.Check(Convert.FromBase64String(response.LndGrpcMacaroons[name]), required,
                                        "/lnrpc.Lightning/GetInfo", IPAddress.Loopback)
                                 .Outcome);
        Assert.Equal(rootKey, unlocked.Secrets.LndGrpcMacaroonRootKey);
        Assert.False(Directory.Exists(Path.Combine(_files.ConfigPath, "lnd-grpc")));
    }

    private static byte[] Frame(byte version, byte[] body)
    {
        var frame = new byte[KeyProvisioningProtocol.HeaderLength + body.Length];
        "NLKP"u8.CopyTo(frame);
        frame[4] = version;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), (uint)body.Length);
        body.CopyTo(frame, KeyProvisioningProtocol.HeaderLength);
        return frame;
    }

    private static IConfiguration EmptyConfiguration() => Configuration([]);

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    internal static string OnionKey() => "ED25519-V3:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    internal static KeyProvisioningTls PemTls(X509Certificate2 server, X509Certificate2? ca) => new()
    {
        Certificate = server.ExportCertificatePem(),
        Key = server.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem(),
        ClientCa = ca?.ExportCertificatePem()
    };

    internal static X509Certificate2 Authority()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=nltg-test-ca", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true));
        return Persisted(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30)));
    }

    internal static X509Certificate2 Issue(X509Certificate2 ca, string eku = "1.3.6.1.5.5.7.3.1")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(eku)], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var issued = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(10),
                                          RandomNumberGenerator.GetBytes(8));
        using var withKey = issued.CopyWithPrivateKey(key);
        return Persisted(withKey);
    }

    /// <summary>Through PKCS#12, so the private key exports and works in TLS on every platform.</summary>
    private static X509Certificate2 Persisted(X509Certificate2 certificate)
    {
        using (certificate)
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
                                                   X509KeyStorageFlags.Exportable);
    }
}