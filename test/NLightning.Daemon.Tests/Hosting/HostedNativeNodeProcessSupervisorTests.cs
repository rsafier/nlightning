using System.Text.Json.Nodes;
using NLightning.Daemon.Contracts.Utilities;
using NLightning.Daemon.Hosting;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing;

namespace NLightning.Daemon.Tests.Hosting;

public sealed class HostedNativeNodeProcessSupervisorTests
{
    [Theory]
    [InlineData("mode")]
    [InlineData("node")]
    [InlineData("owner")]
    [InlineData("signer")]
    [InlineData("network")]
    [InlineData("public-key")]
    [InlineData("database")]
    [InlineData("listener")]
    [InlineData("token")]
    [InlineData("socket")]
    public void LaunchRejectsConfigurationOutsideItsEnrollment(string field)
    {
        using var configured = new ConfiguredNode();
        var document = configured.Document;
        switch (field)
        {
            case "mode": document["Signing"]!["Mode"] = "Local"; break;
            case "node": document["Signing"]!["NodeId"] = "other-node"; break;
            case "owner": document["Signing"]!["OwnerId"] = "other-owner"; break;
            case "signer": document["Signing"]!["SignerId"] = "other-signer"; break;
            case "network": document["Node"]!["Network"] = "mainnet"; break;
            case "public-key": document["Signing"]!["ExpectedNodePublicKey"] = "03" + new string('1', 64); break;
            case "database": document["Database"]!["ConnectionString"] = "Data Source=" + Path.Combine(configured.DirectoryPath, "another.db"); break;
            case "listener": document["Node"]!["ListenAddresses"] = new JsonArray("127.0.0.1:29735"); break;
            case "token": document["Signing"]!["AuthTokenFile"] = Path.Combine(configured.DirectoryPath, "another.token"); break;
            case "socket": document["Signing"]!["SocketPath"] = Path.Combine(configured.DirectoryPath, "another.sock"); break;
        }
        configured.Save();
        Assert.Throws<ArgumentException>(() => HostedNativeNodeProcessSupervisor.ValidateConfiguration(configured.Launch));
    }

    [Fact]
    public void LaunchUsesActualDaemonEntrypointInForegroundWithoutSecretArguments()
    {
        using var configured = new ConfiguredNode();
        HostedNativeNodeProcessSupervisor.ValidateConfiguration(configured.Launch);
        var process = new HostedNativeNodeProcess(configured.Launch);
        var start = process.CreateStartInfo();
        Assert.Equal(configured.Launch.ExecutablePath, start.FileName);
        Assert.Equal([configured.Launch.ManagedAssemblyPath!, "--config", configured.Launch.ConfigurationFilePath,
            "--network", "regtest", "--daemon=false"], start.ArgumentList.ToArray());
        Assert.DoesNotContain(start.Environment.Keys, key => key.StartsWith("NLTG_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(start.ArgumentList, argument => argument.Contains("token", StringComparison.Ordinal));
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput);
    }

    private sealed class ConfiguredNode : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "native-launch-" + Guid.NewGuid().ToString("N"));
        public JsonObject Document { get; }
        public HostedDaemonLaunch Launch { get; }

        public ConfiguredNode()
        {
            Directory.CreateDirectory(DirectoryPath);
            var key = new CompactPubKey(Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
            var context = new NodeSigningContext("node-a", "owner-a", "signer-a", "regtest", key);
            var signer = new HostedSignerEnrollment(context, Path.Combine(DirectoryPath, "state"),
                Path.Combine(DirectoryPath, "signer.sock"), Path.Combine(DirectoryPath, "signer.token"));
            var enrollment = new HostedNativeNodeEnrollment(signer, Path.Combine(DirectoryPath, "node.db"),
                NodeUtils.GetCookieFilePath(DirectoryPath), NodeUtils.GetNamedPipeFilePath(DirectoryPath),
                [new HostedPeerListener("127.0.0.1", 19735)]);
            Launch = new HostedDaemonLaunch(enrollment, Path.Combine(DirectoryPath, "appsettings.json"),
                Path.Combine(DirectoryPath, "dotnet"), Path.Combine(DirectoryPath, "NLightning.Daemon.dll"));
            Document = new JsonObject
            {
                ["Node"] = new JsonObject { ["Network"] = "regtest", ["ListenAddresses"] = new JsonArray("127.0.0.1:19735") },
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

        public void Save() => File.WriteAllText(Launch.ConfigurationFilePath, Document.ToJsonString());
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}