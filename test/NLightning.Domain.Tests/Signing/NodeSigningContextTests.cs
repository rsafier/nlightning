using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Signing;

namespace NLightning.Domain.Tests.Signing;

public sealed class NodeSigningContextTests
{
    private static byte[] PublicKey() => Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798");

    [Fact]
    public void PublicIdentityCannotBeMutatedThroughInputOrReturnedKeyBuffers()
    {
        var bytes = PublicKey();
        var context = new NodeSigningContext("node", "owner", "signer", "regtest", new CompactPubKey(bytes));
        var expected = context.NodePublicKey.ToString();
        bytes[1] ^= 0xff;
        Assert.Equal(expected, context.NodePublicKey.ToString());
        var returned = (byte[])context.NodePublicKey;
        returned[0] = 0;
        Assert.Equal(expected, context.NodePublicKey.ToString());
        context.Validate();
    }

    [Fact]
    public void IndependentlyConstructedEnrollmentsUseValueEquality()
    {
        var a = new NodeSigningContext("node", "owner", "signer", "regtest", new CompactPubKey(PublicKey()));
        var b = new NodeSigningContext("node", "owner", "signer", "regtest", new CompactPubKey(PublicKey()));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { OwnerId = "another-owner" });
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner with spaces")]
    [InlineData("owner\nnode")]
    public void InvalidIdentifiersCannotBecomeAnEnrollment(string owner)
    {
        var context = new NodeSigningContext("node", owner, "signer", "regtest", new CompactPubKey(PublicKey()));
        Assert.Throws<ArgumentException>(context.Validate);
    }
}