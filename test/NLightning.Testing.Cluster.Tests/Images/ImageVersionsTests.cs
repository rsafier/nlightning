namespace NLightning.Testing.Cluster.Tests.Images;

using Cluster.Images;
using Cluster.Nodes;

public class ImageVersionsTests
{
    [Fact]
    public void Given_ALocallyBuiltImage_When_Referenced_Then_ItIsNeverPulled()
    {
        // Assert
        Assert.Equal("custom_lnd:latest", ImageVersions.Lnd.Reference);
        Assert.Equal(ImagePullPolicy.Never, ImageVersions.Lnd.PullPolicy);
        Assert.Equal(ImagePullPolicy.Never, ImageVersions.Eclair.PullPolicy);
        Assert.Equal(ImagePullPolicy.Never, ImageVersions.Ldk.PullPolicy);
    }

    [Fact]
    public void Given_APinnedImage_When_Referenced_Then_TagAndDigestAreBothThere()
    {
        // Assert
        Assert.Equal("elementsproject/lightningd:v26.06.8@sha256:"
                   + "56f1cebe829fbb3c7d5674be8cd1212c7e02527ab32403b695033a29bcd2abce",
                     ImageVersions.Cln.Reference);
        Assert.Equal("IfNotPresent", ImageVersions.Cln.PullPolicyValue);
    }

    [Fact]
    public void Given_TheTable_When_Read_Then_EveryKindImageIsInAllAndNoImageIsListedTwice()
    {
        // Assert
        Assert.All(ImageVersions.ByKind.Values, image => Assert.Contains(image, ImageVersions.All));
        Assert.Equal(ImageVersions.All.Count, ImageVersions.All.Select(i => i.Reference).Distinct().Count());
        Assert.Same(ImageVersions.BitcoinCore, ImageVersions.ByKind[NodeKind.BitcoinCore]);
        Assert.False(ImageVersions.ByKind.ContainsKey(NodeKind.NLightning));
    }
}