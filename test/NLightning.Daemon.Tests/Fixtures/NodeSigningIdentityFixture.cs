namespace NLightning.Daemon.Tests.Fixtures;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;

internal static class NodeSigningIdentityFixture
{
    internal static ISecureKeyManager CreateSecureKeyManager()
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodePubKey()).Returns(new CompactPubKey(Convert.FromHexString(
            "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798")));
        return keyManager.Object;
    }
}