using System.Security.Cryptography;
using NLightning.Domain.Protocol.Constants;
using NLightning.Infrastructure.Bitcoin.Managers;
using NLightning.Signer;

namespace NLightning.RemoteSigning.Tests;

public sealed class AllocationCheckpointTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rs-allocation-" + Guid.NewGuid().ToString("N"));
    private string JournalPath => Path.Combine(_directory, "allocation");
    private static byte[] NodePublicKey => [2, .. Enumerable.Repeat((byte)1, 32)];

    public AllocationCheckpointTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Given_InjectedJournal_When_MarkerAndAllocationsCommit_Then_ExactHistoryDigestChangesAndSurvivesRestart()
    {
        if (OperatingSystem.IsWindows()) return;
        byte[] digest;
        using (var journal = new InjectedKeyIndexJournal(JournalPath, "regtest", NodePublicKey))
        {
            var header = journal.GetCheckpointDigest();
            journal.MarkStateInitialized();
            var marker = journal.GetCheckpointDigest();
            Assert.NotEqual(header, marker);
            journal.Persist(1);
            var allocation = journal.GetCheckpointDigest();
            Assert.NotEqual(marker, allocation);
            // A digest read must restore the append position before the next allocation.
            journal.Persist(2);
            digest = journal.GetCheckpointDigest();
            Assert.NotEqual(allocation, digest);
            journal.MarkStateInitialized();
            journal.Persist(1);
            Assert.Equal(digest, journal.GetCheckpointDigest());
        }
        Assert.Equal(SHA256.HashData(File.ReadAllBytes(JournalPath)), digest);
        using var restarted = new InjectedKeyIndexJournal(JournalPath, "regtest", NodePublicKey);
        Assert.Equal(2u, restarted.LastIndex);
        Assert.True(restarted.StateInitialized);
        Assert.Equal(digest, restarted.GetCheckpointDigest());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Given_InjectedJournal_When_NetworkOrIdentityChanges_Then_ReopenFails(bool changeNetwork)
    {
        if (OperatingSystem.IsWindows()) return;
        using (var journal = new InjectedKeyIndexJournal(JournalPath, "regtest", NodePublicKey))
            journal.MarkStateInitialized();
        var key = NodePublicKey;
        if (!changeNetwork) key[1]++;
        Assert.Throws<IOException>(() =>
        {
            if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return new InjectedKeyIndexJournal(JournalPath, changeNetwork ? "mainnet" : "regtest", key);
        });
    }

    [Fact]
    public void Given_AllocationCheckpointEnrolled_When_AllocationHistoryRollsBack_Then_AuthorityBlocksExecution()
    {
        if (OperatingSystem.IsWindows()) return;
        var journal = new InjectedKeyIndexJournal(JournalPath, "regtest", NodePublicKey);
        try
        {
            journal.MarkStateInitialized();
            journal.Dispose();
            var initial = File.ReadAllBytes(JournalPath);
            journal = new InjectedKeyIndexJournal(JournalPath, "regtest", NodePublicKey);
            using var fixture = new NativeAuthorizedSignerExecutorTests.Fixture(() =>
            {
                if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                return journal.GetCheckpointDigest();
            });
            var executor = fixture.Executor(new NativeAuthorizedSignerExecutorTests.Validator());
            var request = fixture.ApprovedRequest();
            executor.Execute(request, "writer-a", 1, () =>
            {
                if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                journal.Persist(1);
                return fixture.MutateJournal(request);
            });
            var main = fixture.Journal.GetCheckpointDigest();
            journal.Dispose();
            File.WriteAllBytes(JournalPath, initial);
            journal = new InjectedKeyIndexJournal(JournalPath, "regtest", NodePublicKey);
            Assert.Throws<InvalidOperationException>(() => executor.Execute(fixture.ApprovedRequest(), "writer-a", 1,
                () => throw new Exception("Rolled-back allocation must not execute.")));
            Assert.Throws<InvalidDataException>(() => fixture.Executor(new NativeAuthorizedSignerExecutorTests.Validator()));
            Assert.Equal(main, fixture.Journal.GetCheckpointDigest());
        }
        finally { journal.Dispose(); }
    }

    [Fact]
    public void Given_FileBackedKeys_When_AllocationCommits_Then_CheckpointHashesExactFileAndSurvivesRestart()
    {
        var path = Path.Combine(_directory, "keys.json");
        const string password = "allocation checkpoint test";
        byte[] digest;
        using (var keys = SecureKeyManager.CreateNew(NetworkConstants.Regtest, path, 0))
        {
            Assert.Throws<FileNotFoundException>(() => keys.GetAllocationCheckpointDigest());
            keys.SaveToFile(password);
            var initial = keys.GetAllocationCheckpointDigest();
            Assert.Equal(1u, keys.ReserveChannelKeyIndex());
            digest = keys.GetAllocationCheckpointDigest();
            Assert.NotEqual(initial, digest);
            Assert.Equal(SHA256.HashData(File.ReadAllBytes(path)), digest);
        }
        using var restarted = SecureKeyManager.FromFilePath(path, NetworkConstants.Regtest, password);
        Assert.Equal(digest, restarted.GetAllocationCheckpointDigest());
        Assert.Equal(2u, restarted.ReserveChannelKeyIndex());
        Assert.NotEqual(digest, restarted.GetAllocationCheckpointDigest());
    }

    [Fact]
    public void Given_InjectedKeys_When_FileCheckpointIsRequested_Then_AllocationJournalIsRequired()
    {
        using var keys = SecureKeyManager.FromSeed(Enumerable.Repeat((byte)1, 32).ToArray(), NetworkConstants.Regtest, _ => { });
        Assert.Throws<InvalidOperationException>(() => keys.GetAllocationCheckpointDigest());
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}