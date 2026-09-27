using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Enums;
using Domain.Node.Options;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>NL-045: the upfront shutdown script is a reserved P2WPKH wallet address, reserved in its own scope.</summary>
public class UpfrontShutdownScriptSourceTests
{
    [Fact]
    public async Task Given_AWallet_When_Reserving_Then_TheReservedAddressesScriptIsReturnedAndWatched()
    {
        // Arrange
        var key = new Key(Enumerable.Repeat((byte)0x31, 32).ToArray());
        var address = key.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);
        var walletAddress = new WalletAddressModel(AddressType.P2Wpkh, 4, false, address.ToString())
        {
            IsReserved = true
        };
        var wallet = new Mock<IBitcoinWalletService>(MockBehavior.Strict);
        wallet.Setup(w => w.ReserveUnusedAddressAsync(AddressType.P2Wpkh, false)).ReturnsAsync(walletAddress);
        var monitor = new Mock<IBlockchainMonitor>();
        var provider = new ServiceCollection().AddScoped(_ => wallet.Object).BuildServiceProvider();
        var source = new UpfrontShutdownScriptSource(
            Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
            provider.GetRequiredService<IServiceScopeFactory>(), monitor.Object);

        // Act
        var script = await source.ReserveAsync();

        // Assert
        Assert.Equal(address.ScriptPubKey.ToBytes(), (byte[])script);
        wallet.Verify(w => w.ReserveUnusedAddressAsync(AddressType.P2Wpkh, false), Times.Once);
        wallet.Verify(w => w.GetUnusedAddressAsync(It.IsAny<AddressType>(), It.IsAny<bool>()), Times.Never);
        monitor.Verify(m => m.WatchBitcoinAddress(walletAddress), Times.Once);
    }

    [Theory]
    [InlineData(FeatureSupport.No, false)]
    [InlineData(FeatureSupport.Optional, true)]
    [InlineData(FeatureSupport.Compulsory, true)]
    public void Given_NegotiatedFeatures_When_Checked_Then_OnlyBothSidesAdvertisingCounts(FeatureSupport negotiated,
                                                                                            bool expected)
    {
        // Act / Assert (negotiated features: Optional when both support it, Compulsory when either requires it)
        Assert.Equal(expected,
                     UpfrontShutdownScriptSource.IsNegotiated(new FeatureOptions
                     {
                         UpfrontShutdownScript = negotiated
                     }));
    }
}