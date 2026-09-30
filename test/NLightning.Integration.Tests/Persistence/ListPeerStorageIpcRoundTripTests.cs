using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Application.Node.PeerStorage;
using Client.Printers;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Node.PeerStorage;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Memory;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// NL-432 end to end on a real SQLite schema: a peer hands back our backup naming a channel the database lost, the
/// production <see cref="PeerStorageService"/> writes it through the production unit of work, and after a restart
/// <c>listpeerstorage</c> (ClientCommand 32) returns it over the IPC envelope with the channel to restore, which the CLI
/// printer shows.
/// </summary>
public class ListPeerStorageIpcRoundTripTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;

    [Fact]
    public async Task Given_ADataLossRetrieval_When_ListedOverIpcAfterARestart_Then_TheChannelToRestoreIsShown()
    {
        // Arrange: one SQLite database for both processes
        MessagePackSerializer.DefaultOptions = s_options;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<NLightningDbContext>()
                       .UseSqlite(connection, x => x.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                       .Options;
        await using (var context = CreateContext(dbOptions))
            await context.Database.MigrateAsync(cancellationToken);

        var peerId = Key();
        var lostPeerId = Key();
        var peerChannel = CreateChannel(peerId);
        var lostChannel = CreateChannel(lostPeerId);
        var channels = new List<ChannelModel> { peerChannel, lostChannel };
        var peer = new Mock<IPeerService>();
        peer.SetupGet(p => p.PeerPubKey).Returns(peerId);
        peer.SetupGet(p => p.Features).Returns(new FeatureOptions());

        // Act: the first process builds our backup, the peer hands it back after the channels' records were lost
        byte[] keptByPeer;
        await using (var first = BuildNode(dbOptions, channels))
        {
            var service = first.GetRequiredService<IPeerStorageService>();
            keptByPeer = (await first.GetRequiredService<IPeerBackupBlobProvider>().CreateBlobAsync(cancellationToken))!
               .Blob;
            channels.Remove(lostChannel);
            service.HandleMessage(peer.Object,
                                  new PeerStorageRetrievalMessage(new PeerStorageRetrievalPayload(keptByPeer)));
            await WaitUntilPersistedAsync(dbOptions, peerId, cancellationToken);
        }

        // A second process over the same database lists it over IPC; the peer also refused our backup for its size
        // there (NL-559: the refusal is counted in memory of the process that received it)
        await using var second = BuildNode(dbOptions, channels);
        second.GetRequiredService<IPeerStorageService>()
              .HandleWarning(peer.Object, "Supports only data up to 1024 bytes in peer storage.");
        var handler = second.GetServices<IIpcCommandHandler>().Single(h => h.Command == ClientCommand.ListPeerStorage);
        var response = await handler.HandleAsync(CreateEnvelope(new ListPeerStorageIpcRequest { IncludeBlob = true }),
                                                 cancellationToken);
        var filtered = await handler.HandleAsync(CreateEnvelope(new ListPeerStorageIpcRequest
        {
            PeerNodeId = lostPeerId
        }), cancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<ListPeerStorageIpcResponse>(response.Payload, s_options,
                                                                                    cancellationToken);
        var retrieval = Assert.Single(payload.Retrievals);
        Assert.Equal(peerId, retrieval.PeerNodeId);
        Assert.True(retrieval.IsOurs);
        Assert.True(retrieval.Persisted);
        Assert.NotNull(retrieval.BackupCreatedAt);
        Assert.Equal(keptByPeer, retrieval.Blob);
        Assert.Equal(PeerStorageConstants.MaxBlobLength, retrieval.BlobLength);
        Assert.Equal(2, retrieval.Channels.Count);
        Assert.All(retrieval.Channels, c => Assert.False(c.KnownNow));
        var lost = Assert.Single(retrieval.Channels, c => c.ChannelId == lostChannel.ChannelId);
        Assert.True(lost.UnknownWhenReceived);
        Assert.Equal(lostPeerId, lost.PeerNodeId);
        Assert.Empty(payload.StoredBlobs);

        var refusal = Assert.Single(payload.Refusals);
        Assert.Equal(peerId, refusal.PeerNodeId);
        Assert.Equal(1, refusal.Count);
        Assert.Equal(1024, refusal.AcceptedLimitBytes);

        var filteredPayload = MessagePackSerializer.Deserialize<ListPeerStorageIpcResponse>(filtered.Payload,
            s_options, cancellationToken);
        Assert.Empty(filteredPayload.Retrievals);
        Assert.Empty(filteredPayload.Refusals);

        using var output = new StringWriter();
        new ListPeerStoragePrinter(output).Print(payload);
        var printed = output.ToString();
        Assert.Contains(peerId.ToString(), printed);
        Assert.Contains($"Channel {lostChannel.ChannelId} with {lostPeerId}: UNKNOWN (restore it)", printed);
        Assert.Contains("restorechanbackup", printed);
        Assert.Contains(Convert.ToHexStringLower(keptByPeer), printed);
        Assert.Contains("Peers that refused our backup for its size (1)", printed);
        Assert.Contains($"{peerId}: 1 refusal(s)", printed);
        Assert.Contains("takes at most 1024 bytes", printed);
    }

    private static async Task WaitUntilPersistedAsync(DbContextOptions<NLightningDbContext> dbOptions,
                                                      CompactPubKey peerId, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var context = CreateContext(dbOptions);
            if (await context.PeerStorageRetrievals.AnyAsync(r => r.NodeId.Equals(peerId), cancellationToken))
                return;

            await Task.Delay(20, cancellationToken);
        }

        Assert.Fail("The retrieval was not written");
    }

    private static ServiceProvider BuildNode(DbContextOptions<NLightningDbContext> dbOptions,
                                             List<ChannelModel> channels)
    {
        var channelMemory = new Mock<IChannelMemoryRepository>();
        channelMemory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                     .Returns((Func<ChannelModel, bool> predicate) => channels.Where(predicate).ToList());
        var secret = Enumerable.Repeat((byte)7, 32).ToArray();
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNodeKeyPair())
                  .Returns(() => new CryptoKeyPair(new PrivKey(secret.ToArray()), Key()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IUtxoMemoryRepository, UtxoMemoryRepository>();
        services.AddScoped<IUnitOfWork>(sp => new UnitOfWork(CreateContext(dbOptions),
                                                             NullLogger<UnitOfWork>.Instance, new Sha256(),
                                                             sp.GetRequiredService<IUtxoMemoryRepository>()));
        services.AddSingleton<IPeerBackupBlobProvider>(
            new ChannelListPeerBackupBlobProvider(channelMemory.Object, new PeerStorageCipher(keyManager.Object)));
        services.AddSingleton<IPeerStorageService>(sp => new PeerStorageService(
                                                       sp.GetRequiredService<IServiceScopeFactory>(),
                                                       sp.GetRequiredService<IPeerBackupBlobProvider>(),
                                                       channelMemory.Object, Options.Create(new NodeOptions()),
                                                       NullLogger<PeerStorageService>.Instance,
                                                       Options.Create(new PeerStorageOptions())));
        services.AddPeerStorageIpcServices();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static NLightningDbContext CreateContext(DbContextOptions<NLightningDbContext> dbOptions) =>
        new(dbOptions, new DatabaseTypeProvider(DatabaseType.Sqlite));

    private static IpcEnvelope CreateEnvelope(ListPeerStorageIpcRequest request) => new()
    {
        Version = 1,
        Command = ClientCommand.ListPeerStorage,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
    };

    private static CompactPubKey Key() => new(new Key().PubKey.ToBytes());

    private static ChannelModel CreateChannel(CompactPubKey peer)
    {
        var pubKey = Key();
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(100_000), 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, false, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, pubKey, pubKey, pubKey, pubKey, pubKey, pubKey);
        return new ChannelModel(channelParams, new ChannelId(RandomUtils.GetBytes(32)), null, null, true, null, null,
                                LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0, peer, 0,
                                ChannelState.Open, ChannelVersion.V1);
    }
}