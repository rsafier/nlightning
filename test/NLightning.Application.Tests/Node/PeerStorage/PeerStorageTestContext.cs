using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Tests.Node.PeerStorage;

using Application.Node.PeerStorage;
using Channels.Close;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Node.PeerStorage;

/// <summary>
/// One node's peer storage: the production <see cref="PeerStorageService"/>, <see cref="PeerStorageCipher"/> and
/// <see cref="ChannelListPeerBackupBlobProvider"/> over an in-memory <c>PeerStorageBlobs</c> table, a channel list the
/// test controls and a manual clock.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class PeerStorageTestContext : IDisposable
{
    private readonly Mock<IChannelMemoryRepository> _channelMemory = new();
    private readonly Mock<IChannelDbRepository> _channelDb = new();
    private readonly ServiceProvider _provider;

    public PeerStorageTestContext(byte nodeSeed = 1, FeatureSupport offerStorage = FeatureSupport.Optional,
                                  PeerStorageOptions? options = null, InMemoryPeerStorageDbRepository? store = null)
    {
        Store = store ?? new InMemoryPeerStorageDbRepository();
        _channelMemory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                      .Returns((Func<ChannelModel, bool> predicate) => Channels.Where(predicate).ToList());
        _channelDb.Setup(r => r.ExistsAsync(It.IsAny<ChannelId>()))
                  .ReturnsAsync((ChannelId id) => KnownChannelIds.Contains(id));

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.PeerStorageDbRepository).Returns(Store);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(_channelDb.Object);
        unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() => Store.SaveAsync());

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        _provider = services.BuildServiceProvider();

        var keyManager = new Mock<ISecureKeyManager>();
        var secret = Enumerable.Repeat(nodeSeed, 32).ToArray();
        var nodePubKey = new byte[33];
        nodePubKey[0] = 0x02;
        keyManager.Setup(k => k.GetNodeKeyPair())
                  .Returns(() => new CryptoKeyPair(new PrivKey(secret.ToArray()), new CompactPubKey(nodePubKey)));
        Cipher = new PeerStorageCipher(keyManager.Object);
        BlobProvider = new ChannelListPeerBackupBlobProvider(_channelMemory.Object, Cipher, Time);

        var nodeOptions = new NodeOptions();
        nodeOptions.Features.OptionProvideStorage = offerStorage;
        Service = new PeerStorageService(_provider.GetRequiredService<IServiceScopeFactory>(), BlobProvider,
                                         _channelMemory.Object, Options.Create(nodeOptions),
                                         NullLogger<PeerStorageService>.Instance,
                                         Options.Create(options ?? new PeerStorageOptions { RetrievalWait = TimeSpan.Zero }),
                                         Time);
    }

    public ManualTimeProvider Time { get; } = new();
    public InMemoryPeerStorageDbRepository Store { get; }
    public PeerStorageCipher Cipher { get; }
    public ChannelListPeerBackupBlobProvider BlobProvider { get; }
    public PeerStorageService Service { get; }

    /// <summary>The channels <see cref="IChannelMemoryRepository"/> holds.</summary>
    public List<ChannelModel> Channels { get; } = [];

    /// <summary>The channels <see cref="IChannelDbRepository.ExistsAsync"/> knows.</summary>
    public HashSet<ChannelId> KnownChannelIds { get; } = [];

    public ChannelModel AddChannel(CompactPubKey peer, ChannelState state = ChannelState.Open, bool known = true)
    {
        var channel = CreateChannel(peer, state);
        Channels.Add(channel);
        if (known)
            KnownChannelIds.Add(channel.ChannelId);
        return channel;
    }

    public static ChannelModel CreateChannel(CompactPubKey peer, ChannelState state)
    {
        var pubKey = new CompactPubKey(new Key().PubKey.ToBytes());
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Satoshis(1_000),
                                     LightningMoney.Satoshis(1), 30, LightningMoney.Satoshis(100_000), 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(253), 3, false,
                                              FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, pubKey, pubKey, pubKey, pubKey, pubKey, pubKey);
        return new ChannelModel(channelParams, new ChannelId(RandomUtils.GetBytes(32)), null, null, true, null,
                                null, LightningMoney.Satoshis(100_000), keySet, 0, 0, LightningMoney.Zero, null, 0,
                                peer, 0, state, ChannelVersion.V1);
    }

    public void Dispose()
    {
        Service.Dispose();
        _provider.Dispose();
    }
}

/// <summary>
/// The <c>PeerStorageBlobs</c> table in memory: upserts are staged and applied by <see cref="SaveAsync"/>.
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class InMemoryPeerStorageDbRepository : IPeerStorageDbRepository
{
    private readonly Dictionary<CompactPubKey, StoredPeerBlob> _saved = new();
    private readonly List<Action> _staged = [];
    private readonly Lock _lock = new();

    public int Saves { get; private set; }

    /// <summary>The next saves fail.</summary>
    public int FailNextSaves { get; set; }

    public StoredPeerBlob? GetSaved(CompactPubKey peer)
    {
        lock (_lock)
            return _saved.GetValueOrDefault(peer);
    }

    public Task<StoredPeerBlob?> GetAsync(CompactPubKey peerNodeId) => Task.FromResult(GetSaved(peerNodeId));

    /// <summary>The next loads of every row fail.</summary>
    public int FailNextLoads { get; set; }

    public Task<IReadOnlyList<StoredPeerBlob>> GetAllAsync()
    {
        lock (_lock)
        {
            if (FailNextLoads > 0)
            {
                FailNextLoads--;
                throw new InvalidOperationException("Simulated load failure");
            }

            return Task.FromResult<IReadOnlyList<StoredPeerBlob>>(_saved.Values.ToList());
        }
    }

    public Task UpsertAsync(StoredPeerBlob blob)
    {
        lock (_lock)
            _staged.Add(() => _saved[blob.PeerNodeId] = blob with { Blob = blob.Blob.ToArray() });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CompactPubKey peerNodeId)
    {
        lock (_lock)
            _staged.Add(() => _saved.Remove(peerNodeId));
        return Task.CompletedTask;
    }

    public Task SaveAsync()
    {
        lock (_lock)
        {
            if (FailNextSaves > 0)
            {
                FailNextSaves--;
                _staged.Clear();
                throw new InvalidOperationException("Simulated save failure");
            }

            foreach (var apply in _staged)
                apply();
            _staged.Clear();
            Saves++;
        }

        return Task.CompletedTask;
    }
}