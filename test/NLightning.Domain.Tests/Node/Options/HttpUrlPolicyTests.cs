namespace NLightning.Domain.Tests.Node.Options;

using Domain.Node.Options;

/// <summary>Which URLs the node's own HTTP clients may use (NL-678).</summary>
public class HttpUrlPolicyTests
{
    [Theory]
    [InlineData("https://mempool.space/api/v1/fees/recommended")]
    [InlineData("http://127.0.0.1:8999/api")]
    [InlineData("http://127.8.9.10/api")]
    [InlineData("http://[::1]:3002/")]
    [InlineData("http://[::ffff:127.0.0.1]:3002/")]
    [InlineData("http://localhost:3002/")]
    [InlineData("http://LOCALHOST/")]
    [InlineData("http://mempool.localhost/")]
    [InlineData("http://mempoolhqx4isw62xs7abwphsq7ldayuidyx2v2oethdhhj6mlo2r6ad.onion/api")]
    [InlineData("http://192.168.1.10:3006/api/v1/fees/recommended")] // a LAN mempool from before NL-678 (NL-735)
    [InlineData("http://10.0.0.2/")]
    [InlineData("http://172.16.5.4/")]
    [InlineData("http://[fd00::4]:3006/")]
    public void Given_AnAuthenticatedOrLocalUrl_When_Checked_Then_ItIsAccepted(string url)
    {
        // Act & Assert
        Assert.Null(HttpUrlPolicy.GetError(url, false, "X:Url", "X:AllowPlainHttp"));
    }

    [Theory]
    [InlineData("http://mempool.space/api")]
    [InlineData("http://203.0.113.5/")]
    [InlineData("http://100.64.0.7/")]
    [InlineData("http://mempool.lan/")]
    [InlineData("http://localhost.example.com/")]
    [InlineData("http://onion.example.com/")]
    public void Given_PlainHttpToAnotherHost_When_Checked_Then_ItIsRefusedUnlessAllowed(string url)
    {
        // Act
        var error = HttpUrlPolicy.GetError(url, false, "X:Url", "X:AllowPlainHttp");

        // Assert
        Assert.NotNull(error);
        Assert.Contains("X:AllowPlainHttp", error);
        Assert.Null(HttpUrlPolicy.GetError(url, true, "X:Url", "X:AllowPlainHttp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://mempool.space/")]
    [InlineData("/api/v1/fees")]
    public void Given_NoHttpUrl_When_Checked_Then_ItIsRefusedEvenWhenPlainHttpIsAllowed(string? url)
    {
        // Act
        var error = HttpUrlPolicy.GetError(url, true, "X:Url", "X:AllowPlainHttp");

        // Assert
        Assert.NotNull(error);
        Assert.Contains("absolute http(s) URL", error);
    }
}