using Moq;

namespace NLightning.LndGrpc.Tests;

using Domain.Channels.Enums;
using Domain.Channels.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node.Events;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using LndGrpc.Macaroons;
using LndGrpc.Services;
using Testing.Lnd.Lnrpc;

/// <summary>The ListPeers and ListChannels fields that were always 0 (NL-1249).</summary>
public partial class LndGrpcHostTests
{
    private sealed class SteppedClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Given_AConnectedPeer_When_ListPeers_Then_ItsTrafficPingErrorsFlapsAndChannelTotalsAreFilled()
    {
        // Arrange: the peer of channel 7 with traffic, a 2.5 ms ping and two errors; it came online once since start
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var service = new Mock<IPeerService>();
        service.SetupGet(s => s.Features).Returns(new FeatureOptions());
        service.SetupGet(s => s.BytesSent).Returns(1_234);
        service.SetupGet(s => s.BytesReceived).Returns(5_678);
        service.SetupGet(s => s.PingRoundTrip).Returns(TimeSpan.FromMicroseconds(2_500));
        service.SetupGet(s => s.LastPeerPingPayload).Returns(new byte[] { 1, 2, 3 });
        var at = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        service.SetupGet(s => s.RecentErrors).Returns([(at, "first"), (at.AddSeconds(5), "second")]);
        var peer = new PeerModel(channel.RemoteNodeId, "127.0.0.1", 9735, "IPv4");
        peer.SetPeerService(service.Object);
        _peers.Setup(x => x.ListPeers()).Returns([peer]);
        _peers.Raise(x => x.OnPeerStateChanged += null, _peers.Object,
                     new PeerStateChangedEventArgs(channel.RemoteNodeId, true));
        var payment = new PaymentModel(new Hash(Enumerable.Repeat((byte)9, 32).ToArray()), "lnbcrt1",
                                       CreatePubKey(9), LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(1),
                                       DateTimeOffset.UtcNow);
        payment.AddOutgoingHtlc(channel.ChannelId, 0);
        payment.Succeed(new Secret(new byte[32]), DateTimeOffset.UtcNow);
        _payments.Add(payment);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var all = await connection.LightningClient.ListPeersAsync(new ListPeersRequest(), cancellationToken: Ct);
        var latest = await connection.LightningClient.ListPeersAsync(new ListPeersRequest { LatestError = true },
                                                                      cancellationToken: Ct);

        // Assert
        var listed = Assert.Single(all.Peers);
        Assert.Equal((1_234ul, 5_678ul, 2_500L), (listed.BytesSent, listed.BytesRecv, listed.PingTime));
        Assert.Equal(new byte[] { 1, 2, 3 }, listed.LastPingPayload.ToByteArray());
        Assert.Equal(["first", "second"], listed.Errors.Select(e => e.Error));
        Assert.Equal((ulong)at.ToUnixTimeSeconds(), listed.Errors[0].Timestamp);
        Assert.Equal("second", Assert.Single(Assert.Single(latest.Peers).Errors).Error);
        Assert.Equal(1, listed.FlapCount);
        Assert.True(listed.LastFlapNs > 0);
        Assert.Equal(10_001, listed.SatSent);
    }

    [Fact]
    public async Task Given_APaymentAForwardAndAnInvoice_When_ListChannels_Then_TheChannelsTotalsAreFilled()
    {
        // Arrange: on channel 7, a 10,000 sat payment out (1 sat fee), a forward in (2,001 sat) and a settled invoice
        // HTLC in (5,000 sat)
        var channel = CreateChannel(7, ChannelState.Open);
        _channels.Add(channel);
        var created = DateTimeOffset.UtcNow;
        var payment = new PaymentModel(new Hash(Enumerable.Repeat((byte)9, 32).ToArray()), "lnbcrt1",
                                       CreatePubKey(9), LightningMoney.Satoshis(10_000), LightningMoney.Satoshis(1),
                                       created);
        payment.AddOutgoingHtlc(channel.ChannelId, 0);
        payment.Succeed(new Secret(new byte[32]), created);
        _payments.Add(payment);
        _forwards.Add(ForwardCircuitModel.Restore(channel.ChannelId, 4, LightningMoney.Satoshis(2_001), 300,
                                                  new Hash(new byte[32]), new Secret(new byte[32]),
                                                  new ShortChannelId(170, 2, 0), LightningMoney.Satoshis(2_000), 250,
                                                  created, ForwardCircuitStatus.Fulfilled,
                                                  new ChannelId(Enumerable.Repeat((byte)0x70, 32).ToArray()), 9,
                                                  created));
        var invoice = CreateInvoice(3, created, InvoiceStatus.Settled);
        invoice.Htlcs = [
            new InvoiceHtlc(new ShortChannelId(150, 7, 0), 0, 5_000_000, 100, created, created, 200,
                            InvoiceHtlcState.Settled, 5_000_000)
        ];
        _invoices.Add(invoice);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.LightningClient.ListChannelsAsync(new ListChannelsRequest(),
                                                                           cancellationToken: Ct);

        // Assert
        var listed = Assert.Single(response.Channels);
        Assert.Equal(10_001, listed.TotalSatoshisSent);
        Assert.Equal(7_001, listed.TotalSatoshisReceived);
        Assert.True(listed.Lifetime >= 0);
        Assert.Equal(0ul + (ulong)channel.LocalCommitmentNumber, listed.NumUpdates);
    }

    [Fact]
    public void Given_APeerThatFlaps_When_Tracked_Then_UptimeLifetimeAndFlapsFollowItsIntervals()
    {
        // Arrange: monitoring starts at 0 with the peer offline; online 0:10-0:40, offline, online again at 1:00
        var start = new DateTimeOffset(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        var clock = new SteppedClock(start);
        var peers = new Mock<IPeerManager>();
        peers.Setup(p => p.ListPeers()).Returns([]);
        var channels = new Mock<IChannelMemoryRepository>();
        var tracker = new PeerLivenessTracker(peers.Object, channels.Object, clock);
        var peer = CreatePubKey(5);
        void Change(int minutes, bool online)
        {
            clock.Now = start.AddMinutes(minutes);
            peers.Raise(p => p.OnPeerStateChanged += null, peers.Object, new PeerStateChangedEventArgs(peer, online));
        }

        // Act
        Change(10, true);
        Change(40, false);
        Change(60, true);
        clock.Now = start.AddMinutes(70);
        var newChannel = CreateChannel(9, ChannelState.Open);
        channels.Raise(c => c.OnChannelAdded += null, channels.Object, new ChannelUpdatedEventArgs(newChannel));
        clock.Now = start.AddMinutes(90);

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(60), tracker.Uptime(peer, tracker.StartedAt));
        Assert.Equal(start, tracker.MonitoredSince(new ChannelId(new byte[32])));
        Assert.Equal(start.AddMinutes(70), tracker.MonitoredSince(newChannel.ChannelId));
        Assert.Equal(TimeSpan.FromMinutes(20), tracker.Uptime(peer, tracker.MonitoredSince(newChannel.ChannelId)));
        Assert.Equal((3, (DateTimeOffset?)start.AddMinutes(60)), tracker.Flaps(peer));
        Assert.Equal((0, (DateTimeOffset?)null), tracker.Flaps(CreatePubKey(6)));
    }
}