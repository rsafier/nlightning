using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Application.Channels.Backup.Models;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.ValueObjects;
using Payments;

/// <summary>
/// Channels, peers and a <see cref="ChannelBackupService"/> over mocked repositories for the backup tests.
/// </summary>
internal sealed class BackupTestData
{
    public List<ChannelModel> Channels { get; } = [];
    public List<PeerModel> Peers { get; } = [];
    public TestNodeKeyManager KeyManager { get; }
    public Mock<ILightningSigner> Signer { get; } = new();
    public ChannelBackupOptions Options { get; } = new() { WriteDelay = TimeSpan.Zero };
    public BitcoinNetwork Network { get; set; } = BitcoinNetwork.Regtest;
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_790_000_000);

    public BackupTestData(byte nodeSeed = 7)
    {
        KeyManager = new TestNodeKeyManager(nodeSeed);
        Signer.Setup(s => s.GetChannelBasepoints(It.IsAny<uint>()))
              .Returns((uint index) => new ChannelBasepoints(Key(0x02, (byte)index, 1), Key(0x02, (byte)index, 2),
                                                             Key(0x02, (byte)index, 3), Key(0x02, (byte)index, 4),
                                                             Key(0x02, (byte)index, 5)));
    }

    /// <summary>A compressed-looking public key filled with <paramref name="tag"/> and <paramref name="role"/>.</summary>
    public static CompactPubKey Key(byte prefix, byte tag, byte role)
    {
        var bytes = new byte[33];
        bytes[0] = prefix;
        for (var i = 1; i < 33; i++)
            bytes[i] = (byte)(tag + role + i);
        return new CompactPubKey(bytes);
    }

    /// <summary>A funded channel with key index <paramref name="tag"/>, keys matching <see cref="Signer"/>.</summary>
    public ChannelModel AddChannel(byte tag, bool anchors = false, ChannelState state = ChannelState.Open,
                                   bool withFunding = true, bool initiator = true, ShortChannelId? scid = null,
                                   bool withPeer = true)
    {
        var remoteNode = Key(0x03, tag, 9);
        var local = new ChannelKeySetModel(tag, Key(0x02, tag, 1), Key(0x02, tag, 2), Key(0x02, tag, 3),
                                           Key(0x02, tag, 4), Key(0x02, tag, 5), Key(0x02, tag, 6));
        var remote = ChannelKeySetModel.CreateForRemote(Key(0x03, tag, 1), Key(0x03, tag, 2), Key(0x03, tag, 3),
                                                        Key(0x03, tag, 4), Key(0x03, tag, 5), Key(0x03, tag, 6));
        var localParty = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                          LightningMoney.MilliSatoshis(1_000), 30,
                                          LightningMoney.Satoshis(900_000), 144);
        var remoteParty = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(12_000),
                                           LightningMoney.MilliSatoshis(1), 483,
                                           LightningMoney.Satoshis(990_000), 720);
        var channelParams = new ChannelParams(localParty, remoteParty, LightningMoney.Satoshis(2_500), 3, anchors,
                                              FeatureSupport.No);
        FundingOutputInfo? funding = null;
        if (withFunding)
            funding = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000 + tag), local.FundingCompactPubKey,
                                            remote.FundingCompactPubKey,
                                            Enumerable.Repeat((byte)(0xA0 + tag), 32).ToArray(), (ushort)(tag % 3));

        var channel = new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null,
                                       funding, initiator, null, null, LightningMoney.Satoshis(600_000), local, 0, 0,
                                       LightningMoney.Satoshis(400_000), remote, 0, remoteNode, 0, state,
                                       ChannelVersion.V1)
        {
            FundingCreatedAtBlockHeight = 100u + tag
        };
        if (scid is { } shortChannelId)
            channel.ShortChannelId = shortChannelId;

        Channels.Add(channel);
        if (withPeer && Peers.All(p => p.NodeId != remoteNode))
            Peers.Add(new PeerModel(remoteNode, $"10.0.0.{tag}", 9735u + tag, "IPv4"));

        return channel;
    }

    public IServiceProvider BuildProvider()
    {
        var channelRepository = new Mock<IChannelDbRepository>();
        channelRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(() => Channels.ToList());
        channelRepository.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                         .ReturnsAsync((ChannelId id) => Channels.FirstOrDefault(c => c.ChannelId == id));
        var peerRepository = new Mock<IPeerDbRepository>();
        peerRepository.Setup(r => r.GetByNodeIdAsync(It.IsAny<CompactPubKey>()))
                      .ReturnsAsync((CompactPubKey id) => Peers.FirstOrDefault(p => p.NodeId == id));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelRepository.Object);
        unitOfWork.SetupGet(u => u.PeerDbRepository).Returns(peerRepository.Object);

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        return services.BuildServiceProvider();
    }

    public ChannelBackupService CreateService(IServiceProvider? provider = null) =>
        new((provider ?? BuildProvider()).GetRequiredService<IServiceScopeFactory>(), KeyManager, Signer.Object,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = Network }),
            Microsoft.Extensions.Options.Options.Create(Options), new FixedTimeProvider(this),
            NullLogger<ChannelBackupService>.Instance);

    /// <summary>A backup snapshot with every field set, for codec tests.</summary>
    public static ChannelBackupSnapshot SampleSnapshot()
    {
        var data = new BackupTestData();
        var anchors = data.AddChannel(1, anchors: true, scid: new ShortChannelId(700_123, 42, 1));
        var legacy = data.AddChannel(2, initiator: false);
        return new ChannelBackupSnapshot(BitcoinNetwork.Regtest.ChainHash, data.KeyManager.NodeId,
                                         DateTimeOffset.FromUnixTimeSeconds(1_790_000_000),
                                         [
                                             ChannelBackupService.CreateEntry(anchors, data.Peers[0]),
                                             ChannelBackupService.CreateEntry(legacy, data.Peers[1]) with
                                             {
                                                 Addresses =
                                                 [
                                                     new ChannelBackupAddress("IPv6", "::1", 9736),
                                                     new ChannelBackupAddress(
                                                         "Tor",
                                                         "vww6ybal4bd7szmgncyruucpgfkqahzddi37ktceo3ah7ngmcopnpyyd.onion",
                                                         9735)
                                                 ],
                                                 AnnounceChannel = true,
                                                 HasInferredParams = true,
                                                 UseScidAlias = FeatureSupport.Optional
                                             }
                                         ]);
    }

    /// <summary>Asserts two entries are equal field by field (the channel type is an array).</summary>
    public static void AssertSameEntry(ChannelBackupEntry expected, ChannelBackupEntry actual)
    {
        Assert.Equal(expected.ChannelType, actual.ChannelType);
        Assert.Equal(expected.Addresses, actual.Addresses);
        Assert.True(new EntryComparer().Equals(expected, actual), $"Channel {expected.ChannelId} differs");
    }

    private sealed class EntryComparer : IEqualityComparer<ChannelBackupEntry>
    {
        public bool Equals(ChannelBackupEntry? x, ChannelBackupEntry? y) =>
            x is not null && y is not null
         && x.ChannelId == y.ChannelId && x.RemoteNodeId == y.RemoteNodeId && x.FundingTxId == y.FundingTxId
         && x.FundingOutputIndex == y.FundingOutputIndex && x.CapacitySat == y.CapacitySat
         && x.FundingHeight == y.FundingHeight && Nullable.Equals(x.ShortChannelId, y.ShortChannelId)
         && x.IsInitiator == y.IsInitiator && x.OptionAnchorOutputs == y.OptionAnchorOutputs
         && x.AnnounceChannel == y.AnnounceChannel && x.HasInferredParams == y.HasInferredParams
         && x.Version == y.Version && x.UseScidAlias == y.UseScidAlias && x.MinimumDepth == y.MinimumDepth
         && x.KeyIndex == y.KeyIndex && x.LocalFundingPubKey == y.LocalFundingPubKey
         && x.LocalPaymentBasepoint == y.LocalPaymentBasepoint && x.RemoteFundingPubKey == y.RemoteFundingPubKey
         && x.RemoteRevocationBasepoint == y.RemoteRevocationBasepoint
         && x.RemotePaymentBasepoint == y.RemotePaymentBasepoint
         && x.RemoteDelayedPaymentBasepoint == y.RemoteDelayedPaymentBasepoint
         && x.RemoteHtlcBasepoint == y.RemoteHtlcBasepoint && x.Local == y.Local && x.Remote == y.Remote;

        public int GetHashCode(ChannelBackupEntry obj) => obj.ChannelId.GetHashCode();
    }

    private sealed class FixedTimeProvider(BackupTestData data) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => data.Now;
    }
}