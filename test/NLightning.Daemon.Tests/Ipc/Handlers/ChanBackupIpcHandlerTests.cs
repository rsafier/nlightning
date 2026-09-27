using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Channels.Backup;
using Daemon.Extensions;
using Daemon.Ipc.Handlers;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// exportchanbackup (ClientCommand 21) and verifychanbackup (22) over IPC: the daemon's registrations, client
/// handlers and MessagePack contract around the real backup service (repositories, key manager and signer mocked).
/// </summary>
public class ChanBackupIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private readonly List<ChannelModel> _channels = [];
    private readonly Key _nodeKey = new(Enumerable.Repeat((byte)3, 32).ToArray());

    public ChanBackupIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
    }

    [Fact]
    public async Task Given_TwoChannels_When_ExportedThenVerifiedOverIpc_Then_TheBackupIsValidWithBothChannels()
    {
        // Arrange
        AddChannel(1, anchors: true);
        AddChannel(2, anchors: false);
        await using var provider = BuildProvider();
        var export = new ExportChanBackupIpcHandler(NullLogger<ExportChanBackupIpcHandler>.Instance, provider);
        var verify = new VerifyChanBackupIpcHandler(NullLogger<VerifyChanBackupIpcHandler>.Instance, provider);

        // Act
        var exportEnvelope = await export.HandleAsync(Envelope(ClientCommand.ExportChanBackup,
                                                               new ExportChanBackupIpcRequest()),
                                                      TestContext.Current.CancellationToken);
        var exported = Read<ExportChanBackupIpcResponse>(exportEnvelope);
        var verifyEnvelope = await verify.HandleAsync(Envelope(ClientCommand.VerifyChanBackup,
                                                               new VerifyChanBackupIpcRequest
                                                               {
                                                                   Backup = exported.Backup
                                                               }), TestContext.Current.CancellationToken);
        var verified = Read<VerifyChanBackupIpcResponse>(verifyEnvelope);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, exportEnvelope.Kind);
        Assert.Equal(_channels.Select(c => c.ChannelId), exported.ChannelIds);
        Assert.Equal("/tmp/nltg-test/channel.backup", exported.FilePath);
        Assert.True(verified.IsValid, verified.Error);
        Assert.NotNull(verified.CreatedAt);
        Assert.Equal(2, verified.Channels.Count);
        var anchors = verified.Channels[0];
        Assert.True(anchors.OptionAnchors);
        Assert.False(verified.Channels[1].OptionAnchors);
        Assert.True(anchors.KeysMatch);
        Assert.Equal(ChannelState.Open, anchors.LocalState);
        Assert.Equal(["10.0.0.1:9736"], anchors.Addresses);
        Assert.Equal(Convert.ToHexStringLower(Enumerable.Repeat((byte)0xA1, 32).ToArray()), anchors.FundingTxId);
        Assert.Equal(1_000_001UL, anchors.CapacitySat);
    }

    [Fact]
    public async Task Given_AnUnknownChannel_When_Exported_Then_InvalidChannel()
    {
        // Arrange
        AddChannel(1, anchors: false);
        await using var provider = BuildProvider();
        var export = new ExportChanBackupIpcHandler(NullLogger<ExportChanBackupIpcHandler>.Instance, provider);

        // Act
        var response = await export.HandleAsync(Envelope(ClientCommand.ExportChanBackup,
                                                         new ExportChanBackupIpcRequest
                                                         {
                                                             ChannelId = new ChannelId(new byte[32])
                                                         }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        Assert.Equal(ErrorCodes.InvalidChannel, Read<IpcError>(response).Code);
    }

    [Fact]
    public async Task Given_GarbageOrNothing_When_Verified_Then_InvalidOrRefused()
    {
        // Arrange
        await using var provider = BuildProvider();
        var verify = new VerifyChanBackupIpcHandler(NullLogger<VerifyChanBackupIpcHandler>.Instance, provider);

        // Act
        var garbage = await verify.HandleAsync(Envelope(ClientCommand.VerifyChanBackup,
                                                        new VerifyChanBackupIpcRequest { Backup = [1, 2, 3] }),
                                               TestContext.Current.CancellationToken);
        var empty = await verify.HandleAsync(Envelope(ClientCommand.VerifyChanBackup,
                                                      new VerifyChanBackupIpcRequest { Backup = [] }),
                                             TestContext.Current.CancellationToken);

        // Assert
        var result = Read<VerifyChanBackupIpcResponse>(garbage);
        Assert.False(result.IsValid);
        Assert.Contains("magic", result.Error);
        Assert.Empty(result.Channels);
        Assert.Equal(IpcEnvelopeKind.Error, empty.Kind);
        Assert.Equal(ErrorCodes.InvalidOperation, Read<IpcError>(empty).Code);
    }

    [Fact]
    public void Given_TheDaemonRegistrations_When_Resolved_Then_TheBackupFileDefaultsToTheConfigDirectory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChannelBackupNodeServices(new ConfigurationBuilder().Build());
        services.AddChannelBackupFile("/tmp/nltg-x");
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<ChannelBackupOptions>>().Value;

        // Assert
        Assert.Equal(Path.Combine("/tmp/nltg-x", "channel.backup"), options.FilePath);
        Assert.Contains(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.ExportChanBackup);
        Assert.Contains(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.VerifyChanBackup);
    }

    [Fact]
    public void Given_AConfiguredFile_When_Resolved_Then_ItIsKept()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Backup:FilePath"] = "/backups/node.scb",
                               ["Node:Backup:RefreshInterval"] = "00:01:00"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddChannelBackupNodeServices(configuration);
        services.AddChannelBackupFile("/tmp/nltg-x");
        using var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<ChannelBackupOptions>>().Value;

        // Assert
        Assert.Equal("/backups/node.scb", options.FilePath);
        Assert.Equal(TimeSpan.FromMinutes(1), options.RefreshInterval);
    }

    private ServiceProvider BuildProvider()
    {
        var channelRepository = new Mock<IChannelDbRepository>();
        channelRepository.Setup(r => r.GetAllAsync()).ReturnsAsync(() => _channels.ToList());
        channelRepository.Setup(r => r.GetByIdAsync(It.IsAny<ChannelId>()))
                         .ReturnsAsync((ChannelId id) => _channels.FirstOrDefault(c => c.ChannelId == id));
        var peerRepository = new Mock<IPeerDbRepository>();
        peerRepository.Setup(r => r.GetByNodeIdAsync(It.IsAny<CompactPubKey>()))
                      .ReturnsAsync((CompactPubKey id) => new PeerModel(id, $"10.0.0.{((byte[])id)[1]}", 9736,
                                                                        "IPv4"));
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelRepository.Object);
        unitOfWork.SetupGet(u => u.PeerDbRepository).Returns(peerRepository.Object);

        var nodeId = new CompactPubKey(_nodeKey.PubKey.ToBytes());
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodeKeyPair()).Returns(() => new CryptoKeyPair(_nodeKey.ToBytes(), nodeId));
        keyManager.Setup(k => k.GetNodePubKey()).Returns(nodeId);
        var signer = new Mock<ILightningSigner>();
        signer.Setup(s => s.GetChannelBasepoints(It.IsAny<uint>()))
              .Returns((uint index) => new ChannelBasepoints(PubKey(0x02, (byte)index, 1), PubKey(0x02, (byte)index, 2),
                                                             PubKey(0x02, (byte)index, 3), PubKey(0x02, (byte)index, 4),
                                                             PubKey(0x02, (byte)index, 5)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => unitOfWork.Object);
        services.AddSingleton(keyManager.Object);
        services.AddSingleton(signer.Object);
        services.AddSingleton(new Mock<IChannelMemoryRepository>().Object);
        services.AddSingleton(Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
        services.AddChannelBackupNodeServices(new ConfigurationBuilder().Build());
        services.AddChannelBackupFile("/tmp/nltg-test");
        return services.BuildServiceProvider();
    }

    private void AddChannel(byte tag, bool anchors)
    {
        var local = new ChannelKeySetModel(tag, PubKey(0x02, tag, 1), PubKey(0x02, tag, 2), PubKey(0x02, tag, 3),
                                           PubKey(0x02, tag, 4), PubKey(0x02, tag, 5), PubKey(0x02, tag, 6));
        var remote = ChannelKeySetModel.CreateForRemote(PubKey(0x03, tag, 1), PubKey(0x03, tag, 2),
                                                        PubKey(0x03, tag, 3), PubKey(0x03, tag, 4),
                                                        PubKey(0x03, tag, 5), PubKey(0x03, tag, 6));
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
                                     LightningMoney.MilliSatoshis(1_000), 30, LightningMoney.Satoshis(900_000), 144);
        var funding = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000UL + tag), local.FundingCompactPubKey,
                                            remote.FundingCompactPubKey,
                                            Enumerable.Repeat((byte)(0xA0 + tag), 32).ToArray(), 0);
        _channels.Add(new ChannelModel(new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, anchors,
                                                         FeatureSupport.No),
                                       new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), null, funding, true, null,
                                       null, LightningMoney.Satoshis(600_000), local, 0, 0,
                                       LightningMoney.Satoshis(400_000), remote, 0, PubKey(0x03, tag, 9), 0,
                                       ChannelState.Open, ChannelVersion.V1));
    }

    private static CompactPubKey PubKey(byte prefix, byte tag, byte role)
    {
        var bytes = new byte[33];
        bytes[0] = prefix;
        bytes[1] = tag;
        for (var i = 2; i < 33; i++)
            bytes[i] = (byte)(tag + role + i);
        return new CompactPubKey(bytes);
    }

    private static IpcEnvelope Envelope<T>(ClientCommand command, T request) =>
        new()
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };

    private static T Read<T>(IpcEnvelope envelope) =>
        MessagePackSerializer.Deserialize<T>(envelope.Payload, s_options, TestContext.Current.CancellationToken);
}