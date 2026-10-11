using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Infrastructure.Tests.Protocol.Onion;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Protocol.Onion;

/// <summary>
/// <see cref="PersistentOnionReplayStore"/> over a fake table (NL-078): owner semantics, one save per new entry,
/// pruning by height (explicit and from the chain tip, once per height). The SQLite/Postgres/SQL Server behaviour is
/// proven in <c>NLightning.Integration.Tests.Persistence.OnionReplayPersistenceTests</c>.
/// </summary>
public class PersistentOnionReplayStoreTests
{
    private static readonly ChannelId s_channelA = new(Enumerable.Repeat((byte)0xA1, 32).ToArray());
    private static readonly ChannelId s_channelB = new(Enumerable.Repeat((byte)0xB2, 32).ToArray());

    private readonly FakeReplayTable _table = new();
    private readonly Mock<IBlockchainStateDbRepository> _chainState = new();
    private int _saves;

    [Fact]
    public async Task Given_ANewHmac_When_Added_Then_ItIsSavedAtOnce()
    {
        // Arrange
        using var store = CreateStore();

        // Act
        var added = await store.TryAddAsync(Hmac(1), s_channelA, 0, 500, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(added);
        Assert.Equal(1, _saves);
        var entry = Assert.Single(_table.Entries.Values);
        Assert.Equal(s_channelA, entry.ChannelId);
        Assert.Equal(0UL, entry.HtlcId);
        Assert.Equal(500U, entry.ExpiryHeight);
    }

    [Fact]
    public async Task Given_AnHmacOfAnotherHtlc_When_Added_Then_ItIsAReplayAndNothingIsSaved()
    {
        // Arrange
        using var store = CreateStore();
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 500, TestContext.Current.CancellationToken);

        // Act
        var sameChannel = await store.TryAddAsync(Hmac(1), s_channelA, 1, 500, TestContext.Current.CancellationToken);
        var otherChannel = await store.TryAddAsync(Hmac(1), s_channelB, 0, 500, TestContext.Current.CancellationToken);
        var owner = await store.TryAddAsync(Hmac(1), s_channelA, 0, 500, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(sameChannel);
        Assert.False(otherChannel);
        Assert.True(owner);
        Assert.Equal(1, _saves);
        Assert.Single(_table.Entries);
    }

    [Fact]
    public async Task Given_Entries_When_Pruned_Then_OnlyThoseBelowTheHeightGo()
    {
        // Arrange
        using var store = CreateStore();
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 100, TestContext.Current.CancellationToken);
        await store.TryAddAsync(Hmac(2), s_channelA, 1, 200, TestContext.Current.CancellationToken);

        // Act
        var removed = await store.PruneAsync(200, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, removed);
        Assert.True(await store.TryAddAsync(Hmac(1), s_channelB, 0, 300, TestContext.Current.CancellationToken));
        Assert.False(await store.TryAddAsync(Hmac(2), s_channelB, 0, 300, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_TheChainTipMoved_When_Adding_Then_ExpiredEntriesArePrunedOncePerHeight()
    {
        // Arrange
        using var store = CreateStore();
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 100, TestContext.Current.CancellationToken);
        _chainState.Setup(x => x.GetStateAsync())
                   .ReturnsAsync(new BlockchainState(150, new Hash(new byte[32]), DateTime.UtcNow));

        // Act
        await store.TryAddAsync(Hmac(2), s_channelA, 1, 300, TestContext.Current.CancellationToken);
        await store.TryAddAsync(Hmac(3), s_channelA, 2, 300, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(_table.Entries.ContainsKey(Convert.ToHexString(Hmac(1))));
        Assert.Equal(2, _table.Entries.Count);
        Assert.Equal([150U], _table.DeleteCalls);
    }

    [Fact]
    public async Task Given_NoChainState_When_Adding_Then_NothingIsPruned()
    {
        // Arrange
        using var store = CreateStore();

        // Act
        await store.TryAddAsync(Hmac(1), s_channelA, 0, 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(_table.DeleteCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public async Task Given_AnHmacOfTheWrongLength_When_Added_Then_Throws(int length)
    {
        // Arrange
        using var store = CreateStore();

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryAddAsync(new byte[length], s_channelA, 0, 1,
                                                                            TestContext.Current.CancellationToken));
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void Given_AnInMemoryStore_When_AddingThePersistentStore_Then_ItReplacesIt()
    {
        // Arrange
        var services = new ServiceCollection().AddLogging().AddOnionReplayStore();

        // Act
        using var provider = services.AddPersistentOnionReplayStore().BuildServiceProvider();

        // Assert
        var store = provider.GetRequiredService<IOnionReplayStore>();
        Assert.IsType<PersistentOnionReplayStore>(store);
        Assert.Same(store, provider.GetRequiredService<IOnionReplayStore>());
        Assert.Single(services, d => d.ServiceType == typeof(IOnionReplayStore));
    }

    private PersistentOnionReplayStore CreateStore()
    {
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(x => x.OnionReplayDbRepository).Returns(_table);
        unitOfWork.SetupGet(x => x.BlockchainStateDbRepository).Returns(_chainState.Object);
        unitOfWork.Setup(x => x.SaveChangesAsync()).Returns(() =>
        {
            _saves++;
            _table.Commit();
            return Task.CompletedTask;
        });

        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        var provider = services.BuildServiceProvider();
        return new PersistentOnionReplayStore(provider.GetRequiredService<IServiceScopeFactory>(),
                                              NullLogger<PersistentOnionReplayStore>.Instance);
    }

    private static byte[] Hmac(byte seed) => Enumerable.Repeat(seed, 32).ToArray();

    /// <summary>A replay table with staged adds, as the EF repository behaves.</summary>
    private sealed class FakeReplayTable : IOnionReplayDbRepository
    {
        private readonly List<OnionReplayEntry> _staged = [];

        public Dictionary<string, OnionReplayEntry> Entries { get; } = new();
        public List<uint> DeleteCalls { get; } = [];

        public Task<OnionReplayEntry?> GetByHmacAsync(ReadOnlyMemory<byte> hmac) =>
            Task.FromResult(Entries.GetValueOrDefault(Convert.ToHexString(hmac.Span)));

        public void Add(OnionReplayEntry entry) => _staged.Add(entry);

        public Task<int> DeleteExpiredAsync(uint blockHeight)
        {
            DeleteCalls.Add(blockHeight);
            var expired = Entries.Where(e => e.Value.ExpiryHeight < blockHeight).Select(e => e.Key).ToList();
            expired.ForEach(k => Entries.Remove(k));
            return Task.FromResult(expired.Count);
        }

        public Task<int> CountAsync() => Task.FromResult(Entries.Count);

        public void Commit()
        {
            foreach (var entry in _staged)
                Entries.Add(Convert.ToHexString(entry.Hmac.Span), entry);
            _staged.Clear();
        }
    }
}