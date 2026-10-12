using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signer;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeSignerExecutionLoadingTests
{
    [Fact]
    public void Given_CommittedSigningHistory_When_LoadingExistingWriter_Then_CurrentCheckpointsAreReturnedWithoutTransfer()
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var original = fixture.Authority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
            fixture.Checkpoints.GetCheckpoint);
        var request = fixture.ApprovedRequest();
        var committed = fixture.Executor(new NativeAuthorizedSignerExecutorTests.Validator())
            .Execute(request, "writer-a", 1, () => fixture.MutateJournal(request));

        var restartedAuthority = new NativeSignerAuthority(fixture.Connection);
        var current = restartedAuthority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
            fixture.Checkpoints.GetCheckpoint);

        Assert.Equal(original.WriterId, current.WriterId);
        Assert.Equal(original.Epoch, current.Epoch);
        Assert.NotEqual(original.Checkpoint, current.Checkpoint);
        Assert.NotEqual(original.SignerCheckpoint, current.SignerCheckpoint);
        Assert.Equal(committed.Checkpoint, current.Checkpoint);
        Assert.Equal(committed.SignerCheckpoint, current.SignerCheckpoint);
        Assert.Equal(current, restartedAuthority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
            fixture.Checkpoints.GetCheckpoint));
    }

    [Fact]
    public void Given_StaleInstalledCheckpoints_When_RestartingBootstrap_Then_CurrentAuthorityAllowsTheNextApprovedOperation()
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var installed = new SignerAuthorityConfiguration(fixture.Binding,
            fixture.Authority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
                fixture.Checkpoints.GetCheckpoint));
        var request = fixture.ApprovedRequest();
        var committed = fixture.Executor(new NativeAuthorizedSignerExecutorTests.Validator())
            .Execute(request, "writer-a", 1, () => fixture.MutateJournal(request));

        var resumed = SignerAuthorityBootstrap.Create(installed, fixture.Binding,
            new NativeSignerAuthority(fixture.Connection), fixture.Journal, fixture.Checkpoints,
            new Evidence(), _ => new NativeAuthorizedSignerExecutorTests.Validator());
        var next = fixture.ApprovedRequest();
        var calls = 0;
        var result = resumed.Execute(next, "writer-a", 1, () => { calls++; return [42]; });

        Assert.Equal(1, calls);
        Assert.False(result.Replayed);
        Assert.NotEqual(committed.Checkpoint, result.Checkpoint);
        Assert.Equal(committed.SignerCheckpoint, result.SignerCheckpoint);
    }

    [Theory]
    [InlineData("wrong-writer")]
    [InlineData("wrong-epoch")]
    [InlineData("wrong-owner")]
    [InlineData("rolled-back-history")]
    [InlineData("unavailable-history")]
    public void Given_InvalidExecutionOrHistory_When_Loading_Then_NoAuthorityHistoryIsReset(string failure)
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var oldCheckpoint = fixture.Checkpoints.GetCheckpoint();
        var request = fixture.ApprovedRequest();
        fixture.Executor(new NativeAuthorizedSignerExecutorTests.Validator())
            .Execute(request, "writer-a", 1, () => fixture.MutateJournal(request));
        var current = fixture.Authority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
            fixture.Checkpoints.GetCheckpoint);
        var binding = failure == "wrong-owner" ? fixture.Binding with { OwnerId = "other-owner" } : fixture.Binding;

        Assert.ThrowsAny<Exception>(() => fixture.Authority.LoadCurrentExecution(binding,
            failure == "wrong-writer" ? "writer-b" : "writer-a", failure == "wrong-epoch" ? 2 : 1,
            () => failure switch
            {
                "rolled-back-history" => oldCheckpoint,
                "unavailable-history" => throw new IOException("Safety history is unavailable."),
                _ => fixture.Checkpoints.GetCheckpoint()
            }));

        Assert.Equal(current, fixture.Authority.LoadCurrentExecution(fixture.Binding, "writer-a", 1,
            fixture.Checkpoints.GetCheckpoint));
    }

    [Fact]
    public void Given_TransferredWriter_When_LoadingOldExecution_Then_HistoryIsNotReadAndNewWriterKeepsItsCheckpoints()
    {
        using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture();
        var nextWriter = fixture.Authority.AcquireWriter(fixture.Binding, 1, "writer-b");
        var read = false;

        Assert.Throws<InvalidOperationException>(() => fixture.Authority.LoadCurrentExecution(fixture.Binding,
            "writer-a", 1, () => { read = true; return fixture.Checkpoints.GetCheckpoint(); }));

        Assert.False(read);
        Assert.Equal(nextWriter, fixture.Authority.LoadCurrentExecution(fixture.Binding, "writer-b", 2,
            fixture.Checkpoints.GetCheckpoint));
    }

    private sealed class Evidence : IAuthenticatedNativeChainEvidence
    {
        public void RequireFresh(NativeSignerBinding binding) { }
        public NativeWalletInputEvidence GetOutput(NativeSignerBinding binding, string transactionId, uint outputIndex) =>
            throw new InvalidOperationException("Bootstrap does not request transaction evidence.");
    }
}