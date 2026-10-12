using Microsoft.Data.Sqlite;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeSignerAuthorityConcurrencyTests
{
    [Fact]
    public async Task Given_InFlightSignerCommit_When_OwnerTransfersWriter_Then_TransferIsOrderedAfterCommit()
    {
        var path = Path.Combine(Path.GetTempPath(), "nltg-authority-concurrent-" + Guid.NewGuid().ToString("N") + ".db");
        using var signingEntered = new ManualResetEventSlim();
        using var releaseSigning = new ManualResetEventSlim();
        using var transferStarted = new ManualResetEventSlim();
        var binding = new NativeSignerBinding("node-a", "owner-a", "signer-a", "regtest",
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");
        NativeSignerAuthority Authority() => new(() => new SqliteConnection("Data Source=" + path));
        Task<NativeSignerAuthorityResult>? signing = null;
        Task<NativeSignerExecution>? transfer = null;
        try
        {
            var authority = Authority();
            authority.Initialize();
            authority.Enroll(binding);
            var writer = authority.AcquireWriter(binding, 0, "writer-a");
            var intent = new NativeSignerIntent(Guid.NewGuid().ToString("N"), 27, [1]);
            authority.Approve(binding, intent, DateTimeOffset.UtcNow.AddMinutes(5));
            signing = Task.Run(() => Authority().Execute(binding, writer, intent, () =>
            {
                signingEntered.Set();
                if (!releaseSigning.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException("Test did not release signing.");
                return [42];
            }), TestContext.Current.CancellationToken);
            Assert.True(signingEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            transfer = Task.Run(() =>
            {
                transferStarted.Set();
                return Authority().AcquireWriter(binding, 1, "writer-b");
            }, TestContext.Current.CancellationToken);
            Assert.True(transferStarted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            var early = await Task.WhenAny(transfer, Task.Delay(150, TestContext.Current.CancellationToken));
            Assert.NotSame(transfer, early);
            releaseSigning.Set();
            var committed = await signing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var acquired = await transfer.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(committed.Checkpoint, acquired.Checkpoint);
            Assert.Equal(2, acquired.Epoch);
            Assert.Throws<InvalidOperationException>(() => Authority().Execute(binding, writer, intent, () => [99]));
        }
        finally
        {
            releaseSigning.Set();
            if (signing is not null) try { await signing; } catch { }
            if (transfer is not null) try { await transfer; } catch { }
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(file)) File.Delete(file);
        }
    }
}