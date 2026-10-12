using System.Text.Json;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signer;

namespace NLightning.RemoteSigning.Tests;

public sealed class SignerAuthorityBootstrapTests
{
    [Fact]
    public void Given_CurrentPreinstalledWriter_When_Bootstrapping_Then_NoSpendingAuthorityIsCreated()
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var evidence = new Evidence();
        var validator = new NativeAuthorizedSignerExecutorTests.Validator();
        var executor = SignerAuthorityBootstrap.Create(Configuration(fixture), fixture.Binding,
            fixture.Authority, fixture.Journal, fixture.Checkpoints, evidence, installedEvidence =>
            {
                Assert.Same(evidence, installedEvidence);
                return validator;
            });
        Assert.True(evidence.Checked);
        var before = fixture.Journal.GetCheckpointDigest();
        Assert.Throws<UnauthorizedAccessException>(() => executor.Execute(fixture.Request(), "writer-a", 1,
            () => throw new Exception("Unapproved operation must never execute.")));
        Assert.Equal(before, fixture.Journal.GetCheckpointDigest());
    }

    [Theory]
    [InlineData("stale-writer")]
    [InlineData("missing-evidence")]
    [InlineData("changed-history")]
    [InlineData("wrong-owner")]
    public void Given_InvalidTrustedInputs_When_Bootstrapping_Then_ValidatorIsNeverConstructed(string failure)
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var configuration = Configuration(fixture);
        if (failure == "stale-writer") fixture.Authority.AcquireWriter(fixture.Binding, 1, "writer-b");
        if (failure == "changed-history") fixture.MutateJournal(fixture.Request());
        if (failure == "wrong-owner") configuration = configuration with
        {
            Binding = fixture.Binding with { OwnerId = "other-owner" }
        };
        var constructed = false;
        Assert.ThrowsAny<Exception>(() => SignerAuthorityBootstrap.Create(configuration, fixture.Binding,
            fixture.Authority, fixture.Journal, fixture.Checkpoints,
            new Evidence { Available = failure != "missing-evidence" }, _ =>
            {
                constructed = true;
                return new NativeAuthorizedSignerExecutorTests.Validator();
            }));
        Assert.False(constructed);
    }

    [Fact]
    public void Given_CurrentWriter_When_AuthorityBecomesUnavailable_Then_BootstrapDoesNotFallBack()
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var constructed = false;
        var unavailable = new NativeSignerAuthority(() => throw new IOException("Independent authority unavailable."));
        Assert.Throws<IOException>(() => SignerAuthorityBootstrap.Create(Configuration(fixture), fixture.Binding,
            unavailable, fixture.Journal, fixture.Checkpoints, new Evidence(), _ =>
            {
                constructed = true;
                return new NativeAuthorizedSignerExecutorTests.Validator();
            }));
        Assert.False(constructed);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("unknown-property")]
    [InlineData("missing-execution")]
    [InlineData("invalid-checkpoint")]
    [InlineData("invalid-signer-checkpoint")]
    [InlineData("wrong-owner")]
    [InlineData("public-file")]
    public void Given_AdministratorConfiguration_When_Loading_Then_OnlyExactPrivateCompleteConfigurationIsAccepted(string scenario)
    {
        if (OperatingSystem.IsWindows())
            return;
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var configuration = Configuration(fixture);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".authority-config");
        var serialized = scenario switch
        {
            "unknown-property" => JsonSerializer.Serialize(new { configuration.Binding, configuration.Execution, ApproveAll = true }),
            "missing-execution" => JsonSerializer.Serialize(new { configuration.Binding }),
            "invalid-checkpoint" => JsonSerializer.Serialize(configuration with
            {
                Execution = configuration.Execution with { Checkpoint = "invalid" }
            }),
            "invalid-signer-checkpoint" => JsonSerializer.Serialize(configuration with
            {
                Execution = configuration.Execution with { SignerCheckpoint = NativeSignerAuthority.InitialCheckpoint }
            }),
            "wrong-owner" => JsonSerializer.Serialize(configuration with { Binding = fixture.Binding with { OwnerId = "other-owner" } }),
            _ => JsonSerializer.Serialize(configuration)
        };
        try
        {
            File.WriteAllText(path, serialized);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
                                     | (scenario == "public-file" ? UnixFileMode.OtherRead : 0));
            if (scenario == "valid")
                Assert.Equal(configuration, SignerAuthorityBootstrap.Load(path, fixture.Binding));
            else
                Assert.ThrowsAny<Exception>(() =>
                {
                    if (OperatingSystem.IsWindows()) return;
                    _ = SignerAuthorityBootstrap.Load(path, fixture.Binding);
                });
        }
        finally { File.Delete(path); }
    }

    private static SignerAuthorityConfiguration Configuration(NativeAuthorizedSignerExecutorTests.Fixture fixture) =>
        new(fixture.Binding, new NativeSignerExecution("writer-a", 1, NativeSignerAuthority.InitialCheckpoint,
            fixture.Checkpoints.GetCheckpoint()));

    private sealed class Evidence : IAuthenticatedNativeChainEvidence
    {
        public bool Available { get; init; } = true;
        public bool Checked { get; private set; }
        public void RequireFresh(NativeSignerBinding binding)
        {
            if (!Available) throw new InvalidOperationException("Independent evidence unavailable.");
            Checked = true;
        }
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex) =>
            throw new InvalidOperationException("Bootstrap does not establish transaction spending authority.");
    }
}