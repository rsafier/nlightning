using NLightning.Infrastructure.Bitcoin.KeyRing;
using NLightning.Infrastructure.Bitcoin.Signers;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeJournalDurabilityTests
{
    [Fact]
    public async Task Given_AFreshSigner_When_Ready_Then_SwapHistoryAlreadyExistsBeforeAnySwapRequest()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        Assert.True(File.Exists(Path.Combine(daemon.DirectoryPath, "state.swap-sessions")));
    }

    [Theory]
    [InlineData(".nonces")]
    [InlineData(".swap-sessions")]
    public async Task Given_ExistingSafetyState_When_RequiredJournalIsDeleted_Then_RestartFailsBeforeReadiness(string suffix)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        await daemon.StopAsync();
        var journal = Path.Combine(daemon.DirectoryPath, "state" + suffix);
        Assert.True(File.Exists(journal));
        File.Delete(journal);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.RestartAsync());
        Assert.Contains("before readiness", error.Message);
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(daemon.SocketPath));
    }

    [Fact]
    public async Task Given_ExistingSafetyState_When_SwapJournalIsTruncated_Then_RestartFailsBeforeReadiness()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        await daemon.StopAsync();
        var journal = Path.Combine(daemon.DirectoryPath, "state.swap-sessions");
        await File.WriteAllBytesAsync(journal, [1, 2, 3], TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => daemon.RestartAsync());
        Assert.Contains("before readiness", error.Message);
        Assert.False(File.Exists(daemon.SocketPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Given_ADanglingJournalSymlink_When_StoreOpens_Then_ItRejectsWithoutCreatingTheTarget(bool swap)
    {
        var directory = Path.Combine(Path.GetTempPath(), "native-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "missing-target");
        var journal = Path.Combine(directory, "journal");
        try
        {
            File.CreateSymbolicLink(journal, target);
            Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using IDisposable store = swap
                    ? new SwapSessionStateStore(journal, new byte[32])
                    : new NativeNonceStateStore(journal, new byte[32]);
            });
            Assert.False(File.Exists(target));
            Assert.NotNull(new FileInfo(journal).LinkTarget);
        }
        finally
        {
            File.Delete(journal);
            Directory.Delete(directory, recursive: true);
        }
    }
}