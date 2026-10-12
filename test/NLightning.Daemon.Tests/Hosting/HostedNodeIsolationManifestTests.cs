using NLightning.Daemon.Hosting;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing;

namespace NLightning.Daemon.Tests.Hosting;

public sealed class HostedNodeIsolationManifestTests
{
    private static HostedNativeNodeEnrollment Node(string name, int port, byte prefix = 2)
    {
        var context = new NodeSigningContext("node-" + name, "owner-" + name, "signer-" + name, "regtest",
            new CompactPubKey([prefix, .. Convert.FromHexString("79be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798")]));
        var directory = Path.Combine(Path.GetTempPath(), "native-manifest-" + name);
        return new HostedNativeNodeEnrollment(new HostedSignerEnrollment(context,
            Path.Combine(directory, "state"), Path.Combine(directory, "signer.sock"), Path.Combine(directory, "signer.token")),
            Path.Combine(directory, "node.db"), Path.Combine(directory, "node.cookie"), Path.Combine(directory, "node.sock"),
            [new HostedPeerListener("127.0.0.1", port)]);
    }

    [Fact]
    public void SeparateNodeResourcesPassAndManifestSnapshotsListenerLists()
    {
        var listeners = new List<HostedPeerListener> { new("127.0.0.1", 19735) };
        var a = Node("a", 19735) with { PeerListeners = listeners };
        var b = Node("b", 29735, 3);
        var manifest = new HostedNodeIsolationManifest([a, b]);
        listeners.Clear();
        manifest.Validate();
        Assert.Single(manifest.Nodes[0].PeerListeners);
    }

    [Fact]
    public void CanonicalEnrollmentPreservesEveryAuthorityAndResourceBinding()
    {
        var enrollment = Node("canonical", 19735);
        var manifest = new HostedNodeIsolationManifest([enrollment]);
        Assert.Same(manifest.Nodes[0], manifest.Resolve(enrollment));
        Assert.Same(manifest.Nodes[0], manifest.Resolve(enrollment with
        { PeerListeners = enrollment.PeerListeners.ToArray() }));
        Assert.Throws<ArgumentException>(() => manifest.Resolve(enrollment with
        { Signer = enrollment.Signer with { Context = enrollment.Signer.Context with { OwnerId = "other-owner" } } }));
        Assert.Throws<ArgumentException>(() => manifest.Resolve(enrollment with
        { PrivateDatabasePath = enrollment.PrivateDatabasePath + ".other" }));
        Assert.Throws<ArgumentException>(() => manifest.Resolve(enrollment with
        { NodeCredentialPath = enrollment.NodeCredentialPath + ".other" }));
        Assert.Throws<ArgumentException>(() => manifest.Resolve(enrollment with
        { NodeIpcPath = enrollment.NodeIpcPath + ".other" }));
        Assert.Throws<ArgumentException>(() => manifest.Resolve(enrollment with
        { PeerListeners = [new HostedPeerListener("127.0.0.1", 29735)] }));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("owner")]
    [InlineData("signer")]
    [InlineData("identity")]
    public void IdentityCollisionsAreRefused(string kind)
    {
        var a = Node("a", 19735);
        var b = Node("b", 29735, 3);
        var context = b.Signer.Context;
        context = kind switch
        {
            "node" => context with { NodeId = a.Signer.Context.NodeId },
            "owner" => context with { OwnerId = a.Signer.Context.OwnerId },
            "signer" => context with { SignerId = a.Signer.Context.SignerId },
            _ => new NodeSigningContext(context.NodeId, context.OwnerId, context.SignerId,
                context.Network, a.Signer.Context.NodePublicKey)
        };
        b = b with { Signer = b.Signer with { Context = context } };
        Assert.Throws<ArgumentException>(() => new HostedNodeIsolationManifest([a, b]));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("socket")]
    [InlineData("derived-state")]
    [InlineData("credential")]
    [InlineData("ipc")]
    public void CrossNodeAndCrossPurposePathCollisionsAreRefused(string kind)
    {
        var a = Node("a", 19735);
        var b = Node("b", 29735, 3);
        b = kind switch
        {
            "database" => b with { PrivateDatabasePath = a.PrivateDatabasePath },
            "socket" => b with { Signer = b.Signer with { SocketPath = a.Signer.SocketPath } },
            "derived-state" => b with { PrivateDatabasePath = a.Signer.StatePath + ".nonces" },
            "credential" => b with { NodeCredentialPath = a.Signer.CredentialPath },
            _ => b with { NodeIpcPath = a.NodeIpcPath }
        };
        Assert.Throws<ArgumentException>(() => new HostedNodeIsolationManifest([a, b]));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")]
    public void OverlappingListenersAreRefused(string address)
    {
        var a = Node("a", 19735);
        var b = Node("b", 29735, 3) with { PeerListeners = [new HostedPeerListener(address, 19735)] };
        Assert.Throws<ArgumentException>(() => new HostedNodeIsolationManifest([a, b]));
    }

    [Fact]
    public void CredentialContentCannotBeSharedThroughDifferentFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "native-credential-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var a = Path.Combine(directory, "a");
            var b = Path.Combine(directory, "b");
            File.WriteAllText(a, new string('a', 64));
            File.WriteAllText(b, new string('a', 64));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(a, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(b, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            Assert.Throws<ArgumentException>(() => HostedNodeIsolationManifest.ValidateCredentialFiles([a, b]));
            File.WriteAllText(b, new string('b', 64));
            HostedNodeIsolationManifest.ValidateCredentialFiles([a, b]);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SymbolicLinkAliasOfPrivateDatabaseIsRefused()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "native-alias-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var real = Path.Combine(directory, "real.db");
            var alias = Path.Combine(directory, "alias.db");
            File.WriteAllText(real, "");
            File.CreateSymbolicLink(alias, real);
            var a = Node("a", 19735) with { PrivateDatabasePath = real };
            var b = Node("b", 29735, 3) with { PrivateDatabasePath = alias };
            Assert.Throws<UnauthorizedAccessException>(() => new HostedNodeIsolationManifest([a, b]));
        }
        finally { Directory.Delete(directory, true); }
    }
}