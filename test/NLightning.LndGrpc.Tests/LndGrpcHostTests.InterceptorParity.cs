namespace NLightning.LndGrpc.Tests;

using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;

/// <summary>NL-1182: <c>GetInfo</c> reports LND's <c>require_htlc_interceptor</c> from the options.</summary>
public partial class LndGrpcHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_RequireInterceptor_When_GetInfo_Then_ItIsReported(bool require)
    {
        // Arrange
        _serviceOptions.RequireInterceptor = require;
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var info = await connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct);

        // Assert
        Assert.Equal(require, info.RequireHtlcInterceptor);
    }
}