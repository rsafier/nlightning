using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using NBitcoin;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signer;

namespace NLightning.RemoteSigning.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class SignerWalletAuthorityCompositionTests
{
    [Fact]
    public void Given_GenericApproval_When_RestrictedCompositionExecutesAnotherPurpose_Then_JournalIsUntouched()
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var execution = fixture.Authority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
            fixture.Checkpoints.GetCheckpoint);
        var configuration = Configuration(fixture.Binding, execution, "/unused");
        var evidence = new Evidence();
        var executor = SignerWalletAuthorityProfile.CreateExecutor(configuration, fixture.Authority,
            fixture.Journal, fixture.Checkpoints, new Registry(), evidence);
        var request = fixture.ApprovedRequest();
        var before = fixture.Journal.GetCheckpointDigest();

        Assert.Throws<NotSupportedException>(() => executor.Execute(request, "writer-a", 1,
            () => throw new Exception("Unsupported purpose reached private dispatch.")));

        Assert.Equal(before, fixture.Journal.GetCheckpointDigest());
        Assert.Equal(1, evidence.FreshnessChecks);
    }

    [Theory]
    [InlineData("Disable")]
    [InlineData("Require")]
    [InlineData("VerifyCA")]
    public void Given_UnverifiedAuthorityTransport_When_ProfileIsValidated_Then_ItFails(string sslMode)
    {
        Assert.Throws<InvalidDataException>(() => SignerWalletAuthorityProfile.ValidatePostgreSql(
            $"Host=authority.example;Database=signer;Username=signer;Password=private-password;SSL Mode={sslMode}"));
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-mode")]
    [InlineData("worker-secret")]
    [InlineData("relative-secret")]
    [InlineData("unknown-field")]
    [InlineData("public-secret")]
    public void Given_InvalidInstalledProfile_When_Loaded_Then_ItCannotBecomeAuthority(string failure)
    {
        using var fixture = new ProfileFixture();
        var configuration = fixture.Configuration;
        if (failure == "wrong-owner") configuration = configuration with
        { Binding = configuration.Binding with { OwnerId = "other-owner" } };
        if (failure == "wrong-mode") configuration = configuration with { Mode = "NativeFullNode" };
        if (failure == "worker-secret") configuration = configuration with
        { WriterCredentialFile = fixture.Options.AuthTokenFilePath };
        if (failure == "relative-secret") configuration = configuration with { WriterCredentialFile = "relative" };
        fixture.WriteConfiguration(configuration, failure == "unknown-field");
        if (failure == "public-secret") File.SetUnixFileMode(configuration.WriterCredentialFile,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);

        Assert.ThrowsAny<Exception>(() => fixture.Load());
    }

    [Fact]
    public void Given_InstalledProfile_When_WriterTransfersAndSecretsRotate_Then_IdentityMarkerStillMatches()
    {
        using var fixture = new ProfileFixture();
        var original = fixture.Load();
        original.InstallMarker(fixture.Options.StateFilePath);
        var replacement = fixture.Configuration with
        { Execution = fixture.Configuration.Execution with { WriterId = "writer-b", Epoch = 2 } };
        fixture.WriteConfiguration(replacement);
        fixture.WriteSecret(replacement.WriterCredentialFile, "rotated-writer-credential-0000000000000000000");
        fixture.WriteSecret(replacement.PostgreSqlConnectionFile,
            "Host=authority.example;Database=signer;Username=signer;Password=rotated-password;SSL Mode=VerifyFull");
        fixture.WriteSecret(replacement.CoreCredentialsFile,
            JsonSerializer.Serialize(new SignerCoreCredentials("core-user", "rotated-core-password")));
        var rotated = fixture.Load();

        Assert.Equal(original.Fingerprint, rotated.Fingerprint);
        SignerWalletAuthorityProfile.RequireInstalled(fixture.Options.StateFilePath, rotated, manifest: false);
        Assert.Throws<InvalidOperationException>(() =>
            SignerWalletAuthorityProfile.RequireInstalled(fixture.Options.StateFilePath, null, manifest: false));

        fixture.WriteSecret(replacement.PostgreSqlConnectionFile,
            "Host=other-authority.example;Database=signer;Username=signer;Password=rotated-password;SSL Mode=VerifyFull");
        Assert.Throws<InvalidOperationException>(() =>
            SignerWalletAuthorityProfile.RequireInstalled(fixture.Options.StateFilePath, fixture.Load(), manifest: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ExistingSignerHistory_When_PublicManifestIsPrepared_Then_NoListenerOrHistoryMutationOccurs(bool injected)
    {
        var fixture = new SignerDaemonFixture(injected);
        await fixture.InitializeAsync();
        try
        {
            await fixture.StopAsync();
            var directory = fixture.DirectoryPath;
            var state = Path.Combine(directory, injected ? "state" : "node.key.signer-state");
            var histories = Directory.GetFiles(directory).ToDictionary(path => path,
                path => SHA256.HashData(File.ReadAllBytes(path)));
            var start = new ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = injected };
            foreach (var arg in new[] { typeof(SignerAssemblyMarker).Assembly.Location, "--socket", fixture.SocketPath,
                "--auth-token-file", Path.Combine(directory, "token"), "--network", NetworkConstants.Regtest,
                "--authority-manifest" }) start.ArgumentList.Add(arg);
            foreach (var arg in injected
                ? new[] { "--seed-stdin", "--state-file", state }
                : new[] { "--key-file", Path.Combine(directory, "node.key"), "--password-file", Path.Combine(directory, "password") })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Manifest process did not start.");
            if (injected)
            {
                await process.StandardInput.WriteLineAsync(new string('0', 63) + "1");
                process.StandardInput.Close();
            }
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            var error = await stderr;
            var output = await stdout;
            Assert.True(process.ExitCode == 0, $"Manifest exited {process.ExitCode}: {error}; stdout: {output}");
            Assert.Empty(error);
            using var manifest = JsonDocument.Parse(output);
            Assert.Equal(fixture.LocalKeys.GetNodePubKey().ToString(),
                manifest.RootElement.GetProperty("Binding").GetProperty("PublicKey").GetString());
            Assert.Equal(64, manifest.RootElement.GetProperty("SignerCheckpoint").GetString()!.Length);
            Assert.DoesNotContain("SIGNER_READY", output);
            Assert.False(File.Exists(fixture.SocketPath));
            Assert.Equal(histories.Keys.Order(), Directory.GetFiles(directory).Order());
            foreach (var (path, digest) in histories)
                Assert.Equal(digest, SHA256.HashData(File.ReadAllBytes(path)));
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static SignerWalletAuthorityConfiguration Configuration(NativeSignerBinding binding,
        NativeSignerExecution execution, string directory) => new("NativeWalletAuthorityV1", binding, execution,
        Path.Combine(directory, "postgres"), Path.Combine(directory, "writer"), Path.Combine(directory, "core"),
        new NativeCoreChainEvidenceOptions(new Uri("https://core.example/"), Network.RegTest.GetGenesis().GetHash().ToString(),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)),
        [new NativeWalletKeyLocator(0, false, NLightning.Domain.Bitcoin.Enums.AddressType.P2Wpkh)]);

    private sealed class ProfileFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "signer-profile-" + Guid.NewGuid().ToString("N"));
        public SignerWalletAuthorityConfiguration Configuration { get; }
        public SignerDaemonOptions Options { get; }
        private string ProfilePath => Path.Combine(_directory, "profile.json");

        public ProfileFixture()
        {
            Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var binding = new NativeSignerBinding("node-a", "owner-a", "signer-a", "regtest",
                "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");
            var execution = new NativeSignerExecution("writer-a", 1, NativeSignerAuthority.InitialCheckpoint, new string('A', 64));
            Configuration = SignerWalletAuthorityCompositionTests.Configuration(binding, execution, _directory);
            Options = SignerDaemonOptions.Parse(["--socket", Path.Combine(_directory, "signer.sock"),
                "--seed-stdin", "--state-file", Path.Combine(_directory, "state"),
                "--auth-token-file", Path.Combine(_directory, "worker-token")]);
            WriteSecret(Configuration.PostgreSqlConnectionFile,
                "Host=authority.example;Database=signer;Username=signer;Password=private-password;SSL Mode=VerifyFull");
            WriteSecret(Configuration.WriterCredentialFile, "installed-writer-credential-0000000000000000000");
            WriteSecret(Configuration.CoreCredentialsFile,
                JsonSerializer.Serialize(new SignerCoreCredentials("core-user", "core-password")));
            WriteConfiguration(Configuration);
        }

        public void WriteConfiguration(SignerWalletAuthorityConfiguration configuration, bool unknown = false)
        {
            var json = JsonSerializer.Serialize(configuration);
            if (unknown) json = json[..^1] + ",\"Unexpected\":true}";
            WriteSecret(ProfilePath, json);
        }
        public void WriteSecret(string path, string content)
        {
            File.WriteAllText(path, content);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        public SignerWalletAuthorityProfile Load() => SignerWalletAuthorityProfile.Load(ProfilePath, Options, Configuration.Binding);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class Evidence : IAuthenticatedNativeChainEvidence
    {
        public int FreshnessChecks { get; private set; }
        public void RequireFresh(NativeSignerBinding binding) => FreshnessChecks++;
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex) =>
            throw new Exception("Unsupported purpose must not reach output evidence.");
    }

    private sealed class Registry : INativeSignerWalletDerivationRegistry
    {
        public bool IsOwned(NativeSignerBinding binding, byte[] scriptPubKey) => false;
        public byte[] GetScript(NativeSignerBinding binding, NativeWalletKeyLocator derivation) =>
            throw new Exception("Unsupported purpose must not derive wallet keys.");
    }
}