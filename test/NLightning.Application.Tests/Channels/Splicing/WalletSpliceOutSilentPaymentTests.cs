using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Domain.Node.Options;

public class WalletSpliceOutSilentPaymentTests
{
    [Theory]
    [InlineData("sp1q")]
    [InlineData("tsp1q")]
    [InlineData("sprt1q")]
    [InlineData("SPRT1Q")]
    public async Task Given_SilentPaymentAddress_When_SpliceOut_Then_RefusalExplainsWalletWithdrawPath(string address)
    {
        // Arrange
        var destination = new WalletSpliceOutDestination(Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
        // Act
        var error = await Assert.ThrowsAsync<ArgumentException>(() => destination.ResolveAsync(address,
            TestContext.Current.CancellationToken));
        // Assert
        Assert.Contains("pay the wallet, then `withdraw`", error.Message);
    }
}