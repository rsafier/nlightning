using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Handlers.Interfaces;
using Application.Channels.Splicing;
using Application.Channels.Splicing.Handlers;
using Application.Channels.Splicing.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// Splicing plan SP1-D-T2 wiring: <c>AddSpliceServices</c> (called by <c>AddApplicationServices</c>) and the default
/// <see cref="EngineSpliceStatePort"/> over lanes SP1-B/SP1-C.
/// </summary>
public class SpliceRegistrationTests
{
    [Fact]
    public void Given_AddApplicationServices_When_Registered_Then_SplicingAndItsThreeHandlersAreThere()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddApplicationServices();

        // Assert
        Assert.Single(services, d => d.ServiceType == typeof(ISpliceService));
        Assert.Single(services, d => d.ServiceType == typeof(ISpliceCommitmentReceiver));
        Assert.Single(services, d => d.ServiceType == typeof(SpliceDepthWatcher));
        Assert.Equal(typeof(EngineSpliceStatePort),
                     Assert.Single(services, d => d.ServiceType == typeof(ISpliceStatePort)).ImplementationType);
        Assert.Equal(typeof(SpliceInitMessageHandler),
                     Assert.Single(services, d => d.ServiceType == typeof(IChannelMessageHandler<SpliceInitMessage>))
                           .ImplementationType);
        Assert.Equal(typeof(SpliceAckMessageHandler),
                     Assert.Single(services, d => d.ServiceType == typeof(IChannelMessageHandler<SpliceAckMessage>))
                           .ImplementationType);
        Assert.Equal(typeof(SpliceLockedMessageHandler),
                     Assert.Single(services, d => d.ServiceType == typeof(IChannelMessageHandler<SpliceLockedMessage>))
                           .ImplementationType);
    }

    /// <summary>
    /// Proof SP1 found that nothing resolved the depth watcher, so no <c>splice_locked</c> was ever sent: the splice
    /// service now starts it.
    /// </summary>
    [Fact]
    public void Given_TheSpliceService_When_Resolved_Then_TheDepthWatcherFollowsConfirmations()
    {
        // Arrange
        var monitor = new Mock<Infrastructure.Bitcoin.Wallet.Interfaces.IBlockchainMonitor>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(monitor.Object);
        services.AddSingleton(new Mock<Domain.Channels.Interfaces.IChannelLockProvider>().Object);
        services.AddSingleton(new Mock<Domain.Channels.Interfaces.IChannelMemoryRepository>().Object);
        services.AddSingleton(new Mock<Domain.Protocol.Interfaces.IMessageFactory>().Object);
        services.AddSingleton(new Mock<Domain.Bitcoin.Interfaces.ILightningSigner>().Object);
        services.AddSingleton(new Mock<ISpliceStatePort>().Object);
        services.AddSpliceServices();
        using var provider = services.BuildServiceProvider();

        // Act
        _ = provider.GetRequiredService<ISpliceService>();

        // Assert
        monitor.VerifyAdd(m => m.OnTransactionConfirmed += It.IsAny<EventHandler<Domain.Bitcoin.Events.TransactionConfirmedEventArgs>>(),
                          Times.Once);
    }

    [Fact]
    public void Given_AStatePortRegisteredFirst_When_SpliceServicesAdded_Then_ItIsKeptAndTheCallIsIdempotent()
    {
        // Arrange
        var services = new ServiceCollection();
        var port = new Mock<ISpliceStatePort>().Object;
        services.AddSingleton(port);

        // Act
        services.AddSpliceServices();
        services.AddSpliceServices();

        // Assert
        Assert.Same(port, Assert.Single(services, d => d.ServiceType == typeof(ISpliceStatePort)).ImplementationInstance);
        Assert.Single(services, d => d.ServiceType == typeof(SpliceService));
    }

    [Fact]
    public void Given_AChannelWithoutCommitmentState_When_TheEnginePortIsAsked_Then_TheChannelWasNeverSpliced()
    {
        // Arrange
        var port = new EngineSpliceStatePort(new Mock<IChannelMemoryRepository>().Object,
                                             new Mock<ICommitmentSigner>().Object,
                                             new Mock<ICommitmentVerifier>().Object,
                                             NullLogger<EngineSpliceStatePort>.Instance,
                                             new Mock<IMessageFactory>().Object, new Mock<ILightningSigner>().Object);
        var key = new CompactPubKey([0x02, .. Enumerable.Repeat((byte)0x01, 32)]);
        var other = new CompactPubKey([0x03, .. Enumerable.Repeat((byte)0x02, 32)]);
        var txId = new TxId(Enumerable.Repeat((byte)0x07, 32).ToArray());
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var channel = new ChannelModel(new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, false,
                                                         FeatureSupport.No),
                                       new ChannelId(Enumerable.Repeat((byte)0x01, 32).ToArray()), null,
                                       new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), key, other, txId, 1),
                                       true, null, null, LightningMoney.Satoshis(1_000_000),
                                       new ChannelKeySetModel(0, key, key, key, key, key, key), 0, 0,
                                       LightningMoney.Zero, null, 0, other, 0,
                                       Domain.Channels.Enums.ChannelState.Open,
                                       Domain.Channels.Enums.ChannelVersion.V1);

        // Act
        var fundings = port.GetFundings(channel);

        // Assert
        Assert.False(fundings.HasPending);
        Assert.Equal(txId, fundings.Current.FundingTxId);
        Assert.Equal((ushort)1, fundings.Current.OutputIndex);
        Assert.Equal(ChannelFundingStatus.Current, fundings.Current.Status);
        Assert.Throws<InvalidOperationException>(() => port.OnSpliceCommitmentSaved(channel, fundings.Current));
    }
}