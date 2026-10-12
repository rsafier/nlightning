using System.Net;
using System.Security.Cryptography;
using NBitcoin;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing;
using NLightning.LndGrpc.Macaroons;

namespace NLightning.LndGrpc.Tests.Macaroons;

public sealed class LndCredentialEnrollmentTests
{
    [Theory]
    [InlineData("node")]
    [InlineData("owner")]
    [InlineData("signer")]
    [InlineData("network")]
    [InlineData("identity")]
    public void EveryEnrollmentFieldSeparatesCredentialsEvenWhenStoredRootsCollide(string field)
    {
        using var directories = new Directories();
        var a = Context();
        var b = field switch
        {
            "node" => a with { NodeId = "node-b" },
            "owner" => a with { OwnerId = "owner-b" },
            "signer" => a with { SignerId = "signer-b" },
            "network" => a with { Network = "testnet" },
            _ => new NodeSigningContext(a.NodeId, a.OwnerId, a.SignerId, a.Network,
                new CompactPubKey(new Key().PubKey.ToBytes()))
        };
        LndCredentialEnrollment.Bind(directories.A, a);
        LndCredentialEnrollment.Bind(directories.B, b);
        var raw = RandomNumberGenerator.GetBytes(32);
        LndMacaroonFiles.WriteExclusive(Path.Combine(directories.A, LndMacaroonFiles.RootKeyFileName), raw);
        LndMacaroonFiles.WriteExclusive(Path.Combine(directories.B, LndMacaroonFiles.RootKeyFileName), raw);
        LndMacaroonFiles.EnsureCreated(directories.A, context: a);
        LndMacaroonFiles.EnsureCreated(directories.B, context: b);
        var adminA = File.ReadAllBytes(Path.Combine(directories.A, LndMacaroonFiles.AdminFileName));
        var adminB = File.ReadAllBytes(Path.Combine(directories.B, LndMacaroonFiles.AdminFileName));
        var first = new MacaroonVerifier(new LndRootKeyStore(directories.A, a), TimeProvider.System);
        var second = new MacaroonVerifier(new LndRootKeyStore(directories.B, b), TimeProvider.System);
        Assert.Equal(MacaroonCheck.Allowed, Check(first, adminA));
        Assert.Equal(MacaroonCheck.Allowed, Check(second, adminB));
        Assert.Equal(MacaroonCheck.Unauthenticated, Check(second, adminA));
        Assert.Equal(MacaroonCheck.Unauthenticated, Check(first, adminB));
        var retained = File.ReadAllBytes(Path.Combine(directories.A, LndCredentialEnrollment.FileName));
        LndCredentialEnrollment.Bind(directories.A, a);
        Assert.Equal(retained, File.ReadAllBytes(Path.Combine(directories.A, LndCredentialEnrollment.FileName)));
        Assert.Equal(MacaroonCheck.Allowed, Check(new MacaroonVerifier(new LndRootKeyStore(directories.A, a), TimeProvider.System), adminA));
        Assert.Throws<InvalidOperationException>(() => LndCredentialEnrollment.Bind(directories.A, b));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingUnboundOrCopiedCredentialHistoryCannotBeAdopted(bool copiedEnrollment)
    {
        using var directories = new Directories();
        var context = Context();
        if (copiedEnrollment) LndMacaroonFiles.EnsureCreated(directories.A, context: context);
        else LndMacaroonFiles.EnsureCreated(directories.A);
        if (copiedEnrollment)
            foreach (var file in Directory.EnumerateFiles(directories.A))
                File.Copy(file, Path.Combine(directories.B, Path.GetFileName(file)));
        var target = copiedEnrollment ? directories.B : directories.A;
        var root = File.ReadAllBytes(Path.Combine(target, LndMacaroonFiles.RootKeyFileName));
        Assert.Throws<InvalidOperationException>(() => LndCredentialEnrollment.Bind(target,
            context with { OwnerId = "owner-b" }));
        Assert.Equal(root, File.ReadAllBytes(Path.Combine(target, LndMacaroonFiles.RootKeyFileName)));
    }

    [Fact]
    public void SecondaryRootsUseContextAndDeletionStillRevokesTheirMacaroons()
    {
        using var directories = new Directories();
        var a = Context();
        var b = a with { OwnerId = "owner-b" };
        LndMacaroonFiles.EnsureCreated(directories.A, context: a);
        LndMacaroonFiles.EnsureCreated(directories.B, context: b);
        var first = new LndRootKeyStore(directories.A, a);
        var second = new LndRootKeyStore(directories.B, b);
        var key = first.GetOrCreate(7);
        second.GetOrCreate(7);
        File.Copy(Path.Combine(directories.A, "macaroon-root-keys", "7.key"),
            Path.Combine(directories.B, "macaroon-root-keys", "7.key"), true);
        second = new LndRootKeyStore(directories.B, b);
        var id = new MacaroonId(RandomNumberGenerator.GetBytes(MacaroonId.NonceLength),
            LndRootKeyStore.StorageIdOf(7), LndPermissions.Admin);
        var macaroon = Macaroon.Create(key, id.Encode(), LndPermissions.Location).Serialize();
        Assert.Equal(MacaroonCheck.Allowed, Check(new MacaroonVerifier(first, TimeProvider.System), macaroon));
        Assert.Equal(MacaroonCheck.Unauthenticated, Check(new MacaroonVerifier(second, TimeProvider.System), macaroon));
        Assert.True(first.Delete(7));
        Assert.Equal(MacaroonCheck.Unauthenticated, Check(new MacaroonVerifier(first, TimeProvider.System), macaroon));
    }

    private static MacaroonCheck Check(MacaroonVerifier verifier, byte[] macaroon) =>
        verifier.Check(macaroon, LndPermissions.ForMethod("/lnrpc.Lightning/GetInfo")!,
            "/lnrpc.Lightning/GetInfo", IPAddress.Loopback).Outcome;

    internal static NodeSigningContext Context() => new("node-a", "owner-a", "signer-a", "regtest",
        new CompactPubKey(Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798")));

    private sealed class Directories : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "enrolled-macaroons-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(_root, "a");
        public string B => Path.Combine(_root, "b");
        public Directories() { Directory.CreateDirectory(A); Directory.CreateDirectory(B); }
        public void Dispose() => Directory.Delete(_root, true);
    }
}