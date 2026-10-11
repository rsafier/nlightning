using Microsoft.Extensions.Logging;

namespace NLightning.Integration.Tests.Persistence;

using Application.Channels.Handlers;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Repositories;
using Infrastructure.Repositories.Database.Channel;
using Infrastructure.Repositories.Memory;

/// <summary>
/// scid aliases (NL-103) survive a restart: the memory repository starts empty, so the database is the only place that
/// still knows the aliases already handed out.
/// </summary>
public class FundingConfirmedAliasPersistenceTests
{
    private static readonly ShortChannelId s_firstAlias = new(16_000_000, 1, 0);
    private static readonly ShortChannelId s_secondAlias = new(16_000_000, 2, 0);

    private sealed class ScriptedAliasHandler : FundingConfirmedMessageHandler
    {
        private readonly Queue<ShortChannelId> _candidates;

        public ScriptedAliasHandler(IEnumerable<ShortChannelId> candidates, IChannelMemoryRepository memoryRepository,
                                    ILightningSigner signer, IUnitOfWork uow)
            : base(memoryRepository, signer, new Mock<ILogger<FundingConfirmedMessageHandler>>().Object,
                   new Mock<IMessageFactory>().Object, uow)
        {
            _candidates = new Queue<ShortChannelId>(candidates);
        }

        protected override ShortChannelId GenerateRandomScidAlias() => _candidates.Dequeue();
    }

    [Fact]
    public async Task Given_AliasesPersistedBeforeARestart_When_AnotherChannelConfirms_Then_TheyAreNotReused()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var oldChannel = SqliteDbTestContext.CreateChannel(true);
        oldChannel.LocalAliases = [s_firstAlias, s_secondAlias];
        await AddChannelAsync(db, oldChannel);

        var channel = SqliteDbTestContext.CreateChannel(false, state: ChannelState.V1FundingSigned,
                                                        useScidAlias: FeatureSupport.Optional);
        await AddChannelAsync(db, channel);

        var fresh = Enumerable.Range(1, 5).Select(i => new ShortChannelId(16_100_000, (uint)i, 0)).ToList();
        ShortChannelId[] candidates = [s_firstAlias, s_secondAlias, .. fresh];

        // Act
        await RunHandlerAfterRestartAsync(db, channel, candidates);

        // Assert
        Assert.NotNull(channel.LocalAliases);
        var aliases = channel.LocalAliases.ToList();
        Assert.Equal(fresh.Take(aliases.Count), aliases);
        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256)
                          .GetByIdAsync(channel.ChannelId);
        Assert.NotNull(reloaded?.LocalAliases);
        Assert.Equal(aliases.ToHashSet(), reloaded.LocalAliases.ToHashSet());
    }

    [Fact]
    public async Task Given_ChannelWhoseAliasesArePersisted_When_ConfirmedAgainWithoutThemInMemory_Then_TheyAreReused()
    {
        // Arrange
        await using var db = await SqliteDbTestContext.CreateAsync(TestContext.Current.CancellationToken);
        var persisted = SqliteDbTestContext.CreateChannel(false, state: ChannelState.V1FundingSigned,
                                                          useScidAlias: FeatureSupport.Optional);
        persisted.LocalAliases = [s_firstAlias, s_secondAlias];
        await AddChannelAsync(db, persisted);

        // Same channel, but the model in hand lost its aliases
        var channel = SqliteDbTestContext.CreateChannel(false, state: ChannelState.V1FundingSigned,
                                                        useScidAlias: FeatureSupport.Optional);
        var fresh = Enumerable.Range(1, 5).Select(i => new ShortChannelId(16_100_000, (uint)i, 0));

        // Act
        await RunHandlerAfterRestartAsync(db, channel, fresh);

        // Assert
        Assert.NotNull(channel.LocalAliases);
        Assert.Equal(new HashSet<ShortChannelId> { s_firstAlias, s_secondAlias }, channel.LocalAliases.ToHashSet());
        await using var readContext = db.CreateDbContext();
        var reloaded = await new ChannelDbRepository(readContext, db.Sha256)
                          .GetByIdAsync(channel.ChannelId);
        Assert.Equal(ChannelState.ReadyForUs, reloaded?.State);
        Assert.Equal(new HashSet<ShortChannelId> { s_firstAlias, s_secondAlias }, reloaded!.LocalAliases!.ToHashSet());
    }

    private static async Task AddChannelAsync(SqliteDbTestContext db, ChannelModel channel)
    {
        await using var context = db.CreateDbContext();
        await new ChannelDbRepository(context, db.Sha256).AddAsync(channel);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task RunHandlerAfterRestartAsync(SqliteDbTestContext db, ChannelModel channel,
                                                         IEnumerable<ShortChannelId> candidates)
    {
        // A restarted node has no other channel in memory yet
        var memoryRepository = new Mock<IChannelMemoryRepository>();
        memoryRepository.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>())).Returns([]);

        var signer = new Mock<ILightningSigner>();
        signer.Setup(x => x.GetPerCommitmentPoint(It.IsAny<ChannelId>(), It.IsAny<ulong>()))
              .Returns(SqliteDbTestContext.RemoteNodeId);

        using var unitOfWork = new UnitOfWork(db.CreateDbContext(), new Mock<ILogger<UnitOfWork>>().Object,
                                              db.Sha256, new UtxoMemoryRepository());
        var handler = new ScriptedAliasHandler(candidates, memoryRepository.Object, signer.Object, unitOfWork);

        await handler.HandleAsync(channel);
    }
}