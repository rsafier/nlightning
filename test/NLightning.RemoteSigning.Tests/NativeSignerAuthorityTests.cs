using Microsoft.Data.Sqlite;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeSignerAuthorityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "nltg-authority-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly NativeSignerBinding _binding = new("node-a", "owner-a", "signer-a", "regtest", "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");
    private readonly NativeSignerAuthority _authority;

    public NativeSignerAuthorityTests()
    {
        _authority = NewAuthority();
        _authority.Initialize();
        _authority.Enroll(_binding);
    }

    [Fact]
    public void Given_NodeCredentialsWithoutOwnerApproval_When_Executing_Then_CallbackIsNeverCalled()
    {
        var writer = _authority.AcquireWriter(_binding, 0, "writer-a");
        var called = false;
        Assert.Throws<UnauthorizedAccessException>(() => _authority.Execute(_binding, writer, Intent(), () =>
        { called = true; return [1]; }));
        Assert.False(called);
    }

    [Theory]
    [InlineData("destination")]
    [InlineData("amount")]
    [InlineData("fee")]
    [InlineData("operation")]
    public void Given_ExactOwnerApproval_When_NodeAltersTheIntent_Then_ItIsRejected(string changed)
    {
        var writer = _authority.AcquireWriter(_binding, 0, "writer-a");
        var intent = Intent();
        _authority.Approve(_binding, intent, DateTimeOffset.UtcNow.AddMinutes(5));
        var altered = changed == "operation" ? intent with { Operation = 28 }
            : intent with { Payload = System.Text.Encoding.UTF8.GetBytes(changed) };
        Assert.Throws<UnauthorizedAccessException>(() => _authority.Execute(_binding, writer, altered, () => [1]));
    }

    [Fact]
    public void Given_CompletedIntent_When_WriterTransfers_Then_OriginalReceiptReplaysButStaleWriterFails()
    {
        var first = _authority.AcquireWriter(_binding, 0, "writer-a");
        var intent = Intent();
        _authority.Approve(_binding, intent, DateTimeOffset.UtcNow.AddMinutes(5));
        var saved = _authority.Execute(_binding, first, intent, () => [42]);
        var second = NewAuthority().AcquireWriter(_binding, first.Epoch, "writer-b");
        Assert.Throws<InvalidOperationException>(() => _authority.Execute(_binding, first, intent, () => [99]));
        var replay = NewAuthority().Execute(_binding, second, intent, () => throw new Exception("Must not execute"));
        Assert.True(replay.Replayed);
        Assert.Equal(saved.Response, replay.Response);
        Assert.Equal(saved.Checkpoint, replay.Checkpoint);
    }

    [Fact]
    public void Given_TwoSupervisors_When_AcquiringSameEpoch_Then_OnlyOneWins()
    {
        _authority.AcquireWriter(_binding, 0, "writer-a");
        Assert.Throws<InvalidOperationException>(() => NewAuthority().AcquireWriter(_binding, 0, "writer-b"));
    }

    [Fact]
    public void Given_SignerWithStaleCheckpoint_When_NewIntentArrives_Then_NoSigningOccurs()
    {
        var writer = _authority.AcquireWriter(_binding, 0, "writer-a");
        var first = Intent();
        _authority.Approve(_binding, first, DateTimeOffset.UtcNow.AddMinutes(5));
        _authority.Execute(_binding, writer, first, () => [42]);
        var next = Intent();
        _authority.Approve(_binding, next, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Throws<InvalidOperationException>(() => _authority.Execute(_binding, writer, next,
            () => throw new Exception("Must not execute")));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("signer")]
    [InlineData("network")]
    [InlineData("key")]
    public void Given_EnrolledNode_When_BindingChanges_Then_ExecutionFails(string changed)
    {
        var writer = _authority.AcquireWriter(_binding, 0, "writer-a");
        var altered = changed switch
        {
            "owner" => _binding with { OwnerId = "owner-b" },
            "signer" => _binding with { SignerId = "signer-b" },
            "network" => _binding with { Network = "mainnet" },
            _ => _binding with { PublicKey = "0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798" }
        };
        Assert.Throws<UnauthorizedAccessException>(() => _authority.Execute(altered, writer, Intent(), () => [1]));
    }

    [Fact]
    public void Given_TwoNodesWithCollidingRequestIds_When_Executing_Then_ReceiptsDoNotAlias()
    {
        var other = _binding with { NodeId = "node-b", OwnerId = "owner-b", SignerId = "signer-b", PublicKey = "0379be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798" };
        _authority.Enroll(other);
        var a = _authority.AcquireWriter(_binding, 0, "writer-a");
        var b = _authority.AcquireWriter(other, 0, "writer-b");
        var intent = Intent();
        _authority.Approve(_binding, intent, DateTimeOffset.UtcNow.AddMinutes(5));
        _authority.Approve(other, intent, DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(new byte[] { 1 }, _authority.Execute(_binding, a, intent, () => [1]).Response);
        Assert.Equal(new byte[] { 2 }, _authority.Execute(other, b, intent, () => [2]).Response);
    }

    [Fact]
    public void Given_RolledBackSignerHistory_When_AuthorityHasNewDigest_Then_NewRequestsAreRejected()
    {
        var writer = _authority.AcquireWriter(_binding, 0, "writer-a");
        var intent = Intent();
        var digest = NativeSignerAuthority.InitialCheckpoint;
        _authority.Approve(_binding, intent, DateTimeOffset.UtcNow.AddMinutes(5));
        var saved = _authority.Execute(_binding, writer, intent, () => { digest = "committed-journal"; return [1]; }, () => digest);
        var next = Intent();
        _authority.Approve(_binding, next, DateTimeOffset.UtcNow.AddMinutes(5));
        var current = writer with { Checkpoint = saved.Checkpoint, SignerCheckpoint = saved.SignerCheckpoint };
        digest = NativeSignerAuthority.InitialCheckpoint;
        Assert.Throws<InvalidOperationException>(() => _authority.Execute(_binding, current, next,
            () => throw new Exception("Must not execute"), () => digest));
    }

    [Fact]
    public void Given_ConflictingRequestId_When_ReceiptExists_Then_ReplayIsRejected()
    {
        var writer = _authority.AcquireWriter(_binding, 0, "writer-a");
        var intent = Intent();
        _authority.Approve(_binding, intent, DateTimeOffset.UtcNow.AddMinutes(5));
        _authority.Execute(_binding, writer, intent, () => [42]);
        Assert.Throws<InvalidOperationException>(() => _authority.Execute(_binding, writer,
            intent with { Payload = [99] }, () => [99]));
    }

    private static NativeSignerIntent Intent() => new(Guid.NewGuid().ToString("N"), 27,
        "destination=approved;amount=1000;fee=10"u8.ToArray());
    private NativeSignerAuthority NewAuthority() => new(() => new SqliteConnection("Data Source=" + _path));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(path)) File.Delete(path);
    }
}