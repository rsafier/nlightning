using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Tests.Utils.Channels;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Application.Tests.Channels.Close;

using Application.Channels.Close;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Models;
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

    [Fact]
    public async Task Given_AFundeeOpenAbandonedBeforeFundingCreated_When_AnotherPeerOpens_Then_ItsScriptIsReused()
    {
        // Arrange (a peer that opens and walks away must not grow the wallet)
        var context = new FundeeContext();
        var first = FundeeChannel(0x01);
        context.AddTemporary(s_peerA, first);
        var firstScript = await context.Source.AssignIfNegotiatedAsync(first, s_negotiated, s_peerA);
        context.RemoveTemporary(s_peerA, first);
        var second = FundeeChannel(0x02);
        context.AddTemporary(s_peerB, second);

        // Act
        var secondScript = await context.Source.AssignIfNegotiatedAsync(second, s_negotiated, s_peerB);

        // Assert
        Assert.Equal(firstScript, secondScript);
        Assert.Equal(secondScript, second.LocalUpfrontShutdownScript);
        Assert.Equal(1, context.Source.Reservations);
        Assert.Equal(1, context.Source.ReservationCount);
    }

    [Fact]
    public async Task Given_AFundeeOpenStillRunning_When_AnotherOpens_Then_AFreshAddressIsReserved()
    {
        // Arrange
        var context = new FundeeContext();
        var first = FundeeChannel(0x01);
        context.AddTemporary(s_peerA, first);
        var firstScript = await context.Source.AssignIfNegotiatedAsync(first, s_negotiated, s_peerA);
        var second = FundeeChannel(0x02);
        context.AddTemporary(s_peerA, second);

        // Act
        var secondScript = await context.Source.AssignIfNegotiatedAsync(second, s_negotiated, s_peerA);

        // Assert
        Assert.NotEqual(firstScript, secondScript);
        Assert.Equal(2, context.Source.Reservations);
    }

    [Fact]
    public async Task Given_AFundeeOpenPastFundingCreated_When_AnotherOpens_Then_ItsScriptIsNeverReused()
    {
        // Arrange: funding_created moved the channel on (persisted with its script) and dropped the temporary channel
        var context = new FundeeContext();
        var first = FundeeChannel(0x01);
        context.AddTemporary(s_peerA, first);
        var firstScript = await context.Source.AssignIfNegotiatedAsync(first, s_negotiated, s_peerA);
        first.UpdateState(ChannelState.V1FundingSigned);
        context.RemoveTemporary(s_peerA, first);
        var second = FundeeChannel(0x02);
        context.AddTemporary(s_peerB, second);

        // Act
        var secondScript = await context.Source.AssignIfNegotiatedAsync(second, s_negotiated, s_peerB);

        // Assert
        Assert.NotEqual(firstScript, secondScript);
        Assert.Equal(2, context.Source.Reservations);
        Assert.Equal(1, context.Source.ReservationCount);
    }

    [Fact]
    public async Task Given_OurOwnAbandonedOpen_When_WeOpenAgain_Then_ItsScriptIsReused()
    {
        // Arrange (NL-463): the funder path passes the peer too, so a refused open costs one address, not one per try
        var context = new FundeeContext();
        var abandoned = FundeeChannel(0x01);
        context.AddTemporary(s_peerA, abandoned);
        var firstScript = await context.Source.AssignIfNegotiatedAsync(abandoned, s_negotiated, s_peerA);
        context.RemoveTemporary(s_peerA, abandoned);
        var second = FundeeChannel(0x02);
        context.AddTemporary(s_peerA, second);

        // Act
        var secondScript = await context.Source.AssignIfNegotiatedAsync(second, s_negotiated, s_peerA);

        // Assert
        Assert.Equal(firstScript, secondScript);
        Assert.Equal(1, context.Source.Reservations);
        Assert.Equal(1, context.Source.ReservationCount);
    }

    [Fact]
    public async Task Given_OurOwnOpenWithoutAPeer_When_Assigned_Then_AFreshAddressIsReservedAndNotRemembered()
    {
        // Arrange: a caller without the peer (no memory repository) can never reuse
        var context = new FundeeContext();

        // Act
        await context.Source.AssignIfNegotiatedAsync(FundeeChannel(0x02), s_negotiated);

        // Assert
        Assert.Equal(1, context.Source.Reservations);
        Assert.Equal(0, context.Source.ReservationCount);
    }

    [Fact]
    public async Task Given_AbandonedOpensOfBothRoles_When_OtherOpensFollow_Then_TheOldestScriptIsReusedFirst()
    {
        // Arrange: a fundee open (peer A) and one of ours (peer B) are both running, so each reserves fresh; then
        // both are abandoned and later opens take the remembered scripts in order, either role's first
        var context = new FundeeContext();
        var fundee = FundeeChannel(0x01);
        context.AddTemporary(s_peerA, fundee);
        var fundeeScript = await context.Source.AssignIfNegotiatedAsync(fundee, s_negotiated, s_peerA);
        var own = FundeeChannel(0x02);
        context.AddTemporary(s_peerB, own);
        var ownScript = await context.Source.AssignIfNegotiatedAsync(own, s_negotiated, s_peerB);
        context.RemoveTemporary(s_peerA, fundee);
        var next = FundeeChannel(0x03);
        context.AddTemporary(s_peerA, next);
        var nextScript = await context.Source.AssignIfNegotiatedAsync(next, s_negotiated, s_peerA);
        context.RemoveTemporary(s_peerB, own);
        var last = FundeeChannel(0x04);
        context.AddTemporary(s_peerB, last);

        // Act
        var lastScript = await context.Source.AssignIfNegotiatedAsync(last, s_negotiated, s_peerB);

        // Assert: the running opens reserved fresh; the abandoned fundee's script went to the third open (and stays
        // remembered with it) and our abandoned open's script to the last one
        Assert.NotEqual(fundeeScript, ownScript);
        Assert.Equal(fundeeScript, nextScript);
        Assert.Equal(ownScript, lastScript);
        Assert.Equal(2, context.Source.Reservations);
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

    private static readonly FeatureOptions s_negotiated = new() { UpfrontShutdownScript = FeatureSupport.Optional };
    private static readonly CompactPubKey s_peerA = new Key(Enumerable.Repeat((byte)0x0A, 32).ToArray()).PubKey.ToBytes();
    private static readonly CompactPubKey s_peerB = new Key(Enumerable.Repeat((byte)0x0B, 32).ToArray()).PubKey.ToBytes();

    private static ChannelModel FundeeChannel(byte tag)
    {
        var pubKey = new Key(Enumerable.Repeat((byte)0x21, 32).ToArray()).PubKey.ToBytes();
        CompactPubKey key = pubKey;
        var amount = LightningMoney.Satoshis(10_000);
        var channelParams = TestChannelParams.Create(LightningMoney.Satoshis(1_000), LightningMoney.Zero,
                                                     LightningMoney.Satoshis(1), LightningMoney.Satoshis(354), 10,
                                                     amount, 3, false, LightningMoney.Satoshis(354), 144,
                                                     FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, key, key, key, key, key, key);
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()),
                                new CommitmentNumber(key, key, new FakeSha256()),
                                new FundingOutputInfo(amount, key, key), false, null, null, LightningMoney.Zero, keySet,
                                0, 0, amount, keySet, 0, s_peerA, 0, ChannelState.V1Opening, ChannelVersion.V1);
    }

    /// <summary>The source over a channel memory whose temporary channels the test adds and removes.</summary>
    private sealed class FundeeContext
    {
        private readonly HashSet<(CompactPubKey, ChannelId)> _temporary = [];

        public FundeeContext()
        {
            var memory = new Mock<IChannelMemoryRepository>();
            memory.Setup(m => m.TryGetTemporaryChannel(It.IsAny<CompactPubKey>(), It.IsAny<ChannelId>(),
                                                       out It.Ref<ChannelModel?>.IsAny))
                  .Returns(new TryGetTemporaryCallback((CompactPubKey peer, ChannelId id, out ChannelModel? channel) =>
                   {
                       channel = null;
                       return _temporary.Contains((peer, id));
                   }));
            Source = new CountingSource(memory.Object);
        }

        public CountingSource Source { get; }

        public void AddTemporary(CompactPubKey peer, ChannelModel channel) =>
            _temporary.Add((peer, channel.ChannelId));

        public void RemoveTemporary(CompactPubKey peer, ChannelModel channel) =>
            _temporary.Remove((peer, channel.ChannelId));
    }

    private delegate bool TryGetTemporaryCallback(CompactPubKey peer, ChannelId channelId, out ChannelModel? channel);

    /// <summary>Hands out a new script per reservation and counts them.</summary>
    private sealed class CountingSource(IChannelMemoryRepository memory)
        : UpfrontShutdownScriptSource(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }),
                                      new Mock<IServiceScopeFactory>().Object, channelMemoryRepository: memory)
    {
        public int Reservations { get; private set; }

        public override Task<BitcoinScript> ReserveAsync()
        {
            Reservations++;
            BitcoinScript script = new byte[] { 0x00, 0x14 }.Concat(Enumerable.Repeat((byte)Reservations, 20)).ToArray();
            return Task.FromResult(script);
        }
    }
}