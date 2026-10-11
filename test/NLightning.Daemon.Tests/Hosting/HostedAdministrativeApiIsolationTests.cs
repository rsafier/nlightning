using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using NLightning.Daemon.Contracts.Utilities;
using NLightning.Daemon.Hosting;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing;

namespace NLightning.Daemon.Tests.Hosting;

public sealed class HostedAdministrativeApiIsolationTests
{
    [Theory]
    [InlineData("shared-directory")]
    [InlineData("copied-root")]
    [InlineData("unauthenticated")]
    [InlineData("listener")]
    public async Task LndMisconfigurationFailsBeforeAnyHostedProcessStarts(string change)
    {
        using var a = new ConfiguredNode("a", 19735);
        using var b = new ConfiguredNode("b", 29735);
        var aDirectory = Path.Combine(a.DirectoryPath, "lnd");
        var bDirectory = Path.Combine(b.DirectoryPath, "lnd");
        a.Document["LndGrpc"] = Lnd(aDirectory);
        b.Document["LndGrpc"] = Lnd(bDirectory);
        switch (change)
        {
            case "shared-directory": b.Document["LndGrpc"]!["DataDirectory"] = aDirectory; break;
            case "copied-root":
                Directory.CreateDirectory(aDirectory);
                Directory.CreateDirectory(bDirectory);
                var bytes = RandomNumberGenerator.GetBytes(32);
                File.WriteAllBytes(Path.Combine(aDirectory, "macaroons.key"), bytes);
                File.WriteAllBytes(Path.Combine(bDirectory, "macaroons.key"), bytes);
                break;
            case "unauthenticated": b.Document["LndGrpc"]!["AllowNoMacaroons"] = true; break;
            case "listener": b.Document["LndGrpc"]!["Port"] = 19735; break;
        }
        a.Save();
        b.Save();
        await using var supervisor = new HostedNativeNodeProcessSupervisor(
            new HostedNodeIsolationManifest([a.Launch.Enrollment, b.Launch.Enrollment]));
        await Assert.ThrowsAsync<ArgumentException>(() => supervisor.StartAsync([a.Launch, b.Launch],
            TestContext.Current.CancellationToken));
        Assert.Empty(supervisor.Nodes);
        Assert.False(File.Exists(a.Launch.Enrollment.NodeCredentialPath));
        Assert.False(File.Exists(b.Launch.Enrollment.NodeCredentialPath));
    }

    [Theory]
    [InlineData("Cashu", "shared-directory")]
    [InlineData("Cashu", "copied-ca")]
    [InlineData("Cashu", "unauthenticated")]
    [InlineData("LnBackend", "shared-directory")]
    [InlineData("LnBackend", "copied-ca")]
    [InlineData("LnBackend", "unauthenticated")]
    public void ClientAuthenticatedApiRejectsSharedAuthorityAndAuthenticationBypass(string api, string change)
    {
        using var a = new ConfiguredNode("a", 19735);
        using var b = new ConfiguredNode("b", 29735);
        var firstDirectory = MakeClientCa(a.DirectoryPath);
        var secondDirectory = MakeClientCa(b.DirectoryPath);
        a.SetClientApi(api, firstDirectory);
        b.SetClientApi(api, secondDirectory);
        if (change == "shared-directory") b.SetClientApi(api, firstDirectory);
        else if (change == "copied-ca")
            File.Copy(Path.Combine(firstDirectory, "ca.pem"), Path.Combine(secondDirectory, "ca.pem"), true);
        else b.SetClientApi(api, secondDirectory, true);
        a.Save();
        b.Save();
        Assert.Throws<ArgumentException>(() => HostedAdministrativeApiIsolation.Validate([a.Launch, b.Launch]));
    }

    [Fact]
    public void ClientCaCannotBeReusedAcrossDifferentApiTypesOrOwners()
    {
        using var a = new ConfiguredNode("a", 19735);
        using var b = new ConfiguredNode("b", 29735);
        var firstDirectory = MakeClientCa(a.DirectoryPath);
        var secondDirectory = MakeClientCa(b.DirectoryPath);
        File.Copy(Path.Combine(firstDirectory, "ca.pem"), Path.Combine(secondDirectory, "ca.pem"), true);
        a.SetClientApi("Cashu", firstDirectory);
        b.SetClientApi("LnBackend", secondDirectory);
        a.Save();
        b.Save();
        Assert.Throws<ArgumentException>(() => HostedAdministrativeApiIsolation.Validate([a.Launch, b.Launch]));
    }

    [Fact]
    public void ReissuedClientCaWithSameAuthorityKeyStillAliasesOwners()
    {
        using var a = new ConfiguredNode("a", 19735);
        using var b = new ConfiguredNode("b", 29735);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=reissued-client-ca", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var first = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var second = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        Assert.NotEqual(first.RawData, second.RawData);
        var firstDirectory = MakeClientCa(a.DirectoryPath);
        var secondDirectory = MakeClientCa(b.DirectoryPath);
        File.WriteAllText(Path.Combine(firstDirectory, "ca.pem"), first.ExportCertificatePem());
        File.WriteAllText(Path.Combine(secondDirectory, "ca.pem"), second.ExportCertificatePem());
        a.SetClientApi("Cashu", firstDirectory);
        b.SetClientApi("LnBackend", secondDirectory);
        a.Save();
        b.Save();
        Assert.Throws<ArgumentException>(() => HostedAdministrativeApiIsolation.Validate([a.Launch, b.Launch]));
    }

    [Fact]
    public void DistinctOwnerCredentialsMayUseAdditionalSharedTlsCaWhenMacaroonsAuthorizeEachNode()
    {
        using var a = new ConfiguredNode("a", 19735);
        using var b = new ConfiguredNode("b", 29735);
        var ca = Path.Combine(MakeClientCa(a.DirectoryPath), "ca.pem");
        a.Document["LndGrpc"] = Lnd(Path.Combine(a.DirectoryPath, "lnd"));
        b.Document["LndGrpc"] = Lnd(Path.Combine(b.DirectoryPath, "lnd"));
        a.Document["LndGrpc"]!["ClientCaPath"] = ca;
        b.Document["LndGrpc"]!["ClientCaPath"] = ca;
        a.SetClientApi("Cashu", MakeClientCa(Path.Combine(a.DirectoryPath, "cashu")));
        b.SetClientApi("LnBackend", MakeClientCa(b.DirectoryPath));
        a.Save();
        b.Save();
        HostedAdministrativeApiIsolation.Validate([a.Launch, b.Launch]);
    }

    [Fact]
    public void StandaloneExplicitInsecureConfigurationKeepsItsExistingBehavior()
    {
        using var single = new ConfiguredNode("a", 19735);
        single.Document["LndGrpc"] = new JsonObject { ["Enabled"] = true, ["AllowNoMacaroons"] = true };
        single.SetClientApi("Cashu", null, true);
        single.Save();
        HostedAdministrativeApiIsolation.Validate([single.Launch]);
    }

    private static JsonObject Lnd(string directory) => new()
    { ["Enabled"] = true, ["Port"] = 0, ["DataDirectory"] = directory };

    private static string MakeClientCa(string root)
    {
        var directory = Path.Combine(root, "tls");
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=isolated-client-ca", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(Path.Combine(directory, "ca.pem"), certificate.ExportCertificatePem());
        return directory;
    }

    private sealed class ConfiguredNode : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "hosted-api-" + Guid.NewGuid().ToString("N"));
        public JsonObject Document { get; }
        public HostedDaemonLaunch Launch { get; }

        public ConfiguredNode(string name, int port)
        {
            Directory.CreateDirectory(DirectoryPath);
            var key = new CompactPubKey(Convert.FromHexString((name == "a" ? "02" : "03")
                + "79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
            var context = new NodeSigningContext("node-" + name, "owner-" + name, "signer-" + name, "regtest", key);
            var signer = new HostedSignerEnrollment(context, Path.Combine(DirectoryPath, "state"),
                Path.Combine(DirectoryPath, "signer.sock"), Path.Combine(DirectoryPath, "signer.token"));
            var enrollment = new HostedNativeNodeEnrollment(signer, Path.Combine(DirectoryPath, "node.db"),
                NodeUtils.GetCookieFilePath(DirectoryPath), NodeUtils.GetNamedPipeFilePath(DirectoryPath),
                [new HostedPeerListener("127.0.0.1", port)]);
            Launch = new HostedDaemonLaunch(enrollment, Path.Combine(DirectoryPath, "appsettings.json"),
                Path.Combine(DirectoryPath, "nonexistent-daemon"));
            Document = new JsonObject
            {
                ["Node"] = new JsonObject { ["Network"] = "regtest", ["ListenAddresses"] = new JsonArray($"127.0.0.1:{port}") },
                ["Signing"] = new JsonObject
                {
                    ["Mode"] = "RemoteNative",
                    ["NodeId"] = context.NodeId,
                    ["OwnerId"] = context.OwnerId,
                    ["SignerId"] = context.SignerId,
                    ["ExpectedNodePublicKey"] = key.ToString(),
                    ["SocketPath"] = signer.SocketPath,
                    ["AuthTokenFile"] = signer.CredentialPath
                },
                ["Database"] = new JsonObject { ["Provider"] = "Sqlite", ["ConnectionString"] = "Data Source=" + enrollment.PrivateDatabasePath }
            };
            Save();
        }

        public void SetClientApi(string api, string? directory, bool allowInsecure = false)
        {
            var options = new JsonObject
            { ["Enabled"] = true, ["Port"] = 0, ["TlsDirectory"] = directory, ["AllowInsecureLoopback"] = allowInsecure };
            if (api == "Cashu") Document["Cashu"] = new JsonObject { ["PaymentProcessor"] = options };
            else Document["LnBackend"] = options;
        }

        public void Save() => File.WriteAllText(Launch.ConfigurationFilePath, Document.ToJsonString());
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}