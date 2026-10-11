using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Application.Payments.Routing;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Harness;

/// <summary>
/// Splicing plan D12 (SP2-B-T2): right after a splice lock moved the Bob-Carol channel to a new short channel id, an
/// onion that still names the old one is forwarded by Bob's production <c>HtlcSwitch</c> through the registered
/// <see cref="RetiredScidMap"/> for <see cref="RetiredShortChannelId.RetentionBlocks"/> blocks, then fails
/// <c>unknown_next_peer</c> (<see cref="ThreeNodeHarness"/>: real onions, signatures and SQLite).
/// </summary>
/// <remarks>
/// The lock is reproduced at the switch's boundary (both ends' channel models take the splice's short channel id and
/// Bob's map retires the old one, as <c>SpliceService</c> does after the lock's save): the three-node harness has no
/// splice services; the lock itself is proven in <see cref="SpliceLockRulesTests"/> and
/// <c>Gossip.Announcements.SpliceAnnouncementHarnessTests</c> (real splice, real engine).
/// </remarks>
public class RetiredScidForwardTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(40_000_321);
    private static readonly ShortChannelId s_spliceScid = new(ThreeNodeHarness.BlockHeight - 2, 9, 1);

    [Fact]
    public async Task Given_TheOldScidRetiredByTheLock_When_AnOnionNamesIt_Then_BobForwardsAndCarolIsPaid()
    {
        // Arrange
        await using var harness = await CreateAsync();
        var map = LockBobCarol(harness);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "splice", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret,
                                         bobCarolScid: ThreeNodeHarness.BobCarolScid);

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: forwarded over the spliced channel, Carol paid, the circuit names that channel
        Assert.True(map.TryResolve(ThreeNodeHarness.BobCarolScid, out _));
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage!.Value, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var circuit = await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                                         ThreeNodeHarness.AliceBobChannelId, 0));
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit!.Status);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, circuit.OutgoingChannelId);
    }

    [Fact]
    public async Task Given_BobRestartsAfterTheLock_When_TheMapIsLoadedAndAnOnionNamesTheOldScid_Then_CarolIsPaid()
    {
        // Arrange: Bob's map rebuilds from the ChannelFundings rows a lock leaves (the replaced funding with the old
        // short channel id, the splice current since the harness tip), as the host's startup LoadAsync does
        var fundings = CreateLockedFundingRows();
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices = services =>
            services.AddSingleton<IRetiredScidMap>(sp => new RetiredScidMap(
                                                       CreateFundingRowsProvider(sp, fundings),
                                                       NullLogger<RetiredScidMap>.Instance)));
        LockBobCarol(harness);

        // Act 1: Bob restarts (the in-memory map is gone) and loads the map before his links come back
        await harness.RestartAsync(harness.Bob);
        MoveBobCarolScid(harness);
        var map = harness.Bob.Services.GetRequiredService<IRetiredScidMap>();
        Assert.False(map.TryResolve(ThreeNodeHarness.BobCarolScid, out _));
        await map.LoadAsync(ThreeNodeHarness.BlockHeight, TestContext.Current.CancellationToken);
        await harness.ReconnectAsync(harness.Bob);
        await harness.PumpAsync();

        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "splice", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret,
                                         bobCarolScid: ThreeNodeHarness.BobCarolScid);

        // Act 2
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: the D12 guarantee survives the restart
        var entry = Assert.Single(map.GetByChannel(ThreeNodeHarness.BobCarolChannelId));
        Assert.Equal(ThreeNodeHarness.BlockHeight + RetiredShortChannelId.RetentionBlocks, entry.ExpiresAtHeight);
        var fulfilled = Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Equal(invoice.Preimage!.Value, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_ALockedSplice_When_AnOnionNamesTheNewScid_Then_BobForwards()
    {
        // Arrange
        await using var harness = await CreateAsync();
        LockBobCarol(harness);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "splice", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret,
                                         bobCarolScid: s_spliceScid);

        // Act
        await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert
        Assert.Single(harness.Alice.PaymentHandler.Fulfilled);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
    }

    [Fact]
    public async Task Given_TheRetentionOver_When_AnOnionNamesTheOldScid_Then_BobFailsWithUnknownNextPeer()
    {
        // Arrange: 72 blocks after the lock the old short channel id is forgotten
        await using var harness = await CreateAsync();
        var map = LockBobCarol(harness);
        Assert.Equal(1, map.PruneExpired(ThreeNodeHarness.BlockHeight + RetiredShortChannelId.RetentionBlocks));
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "splice", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret,
                                         bobCarolScid: ThreeNodeHarness.BobCarolScid);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert: Bob (hop 0) refused it and nothing reached Carol
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        var decrypted = Decrypt(harness, onion, failed);
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.UnknownNextPeer, decrypted.Code);
        Assert.Empty(harness.Alice.PaymentHandler.Fulfilled);
        Assert.DoesNotContain(harness.Sent, m => m is { From: "Bob", To: "Carol" }
                                               && m.Message is UpdateAddHtlcMessage);
    }

    [Fact]
    public async Task Given_NoRetiredEntry_When_AnOnionNamesTheReplacedScid_Then_BobFailsWithUnknownNextPeer()
    {
        // Arrange: the control, the scid moved without the map knowing the old one
        await using var harness = await CreateAsync();
        MoveBobCarolScid(harness);
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "splice", null,
                                                                      TestContext.Current.CancellationToken);
        var route = harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret,
                                         bobCarolScid: ThreeNodeHarness.BobCarolScid);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(route);
        await harness.PumpAsync();

        // Assert
        var failed = Assert.Single(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(FailureCode.UnknownNextPeer, Decrypt(harness, onion, failed).Code);
    }

    private static Task<ThreeNodeHarness> CreateAsync() =>
        ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices =
                                              services => services.AddSingleton<IRetiredScidMap, RetiredScidMap>());

    /// <summary>What the lock leaves at the switch's boundary: the new short channel id, the old one retired at the
    /// splice's height (the harness tip).</summary>
    private static IRetiredScidMap LockBobCarol(ThreeNodeHarness harness)
    {
        MoveBobCarolScid(harness);
        var map = harness.Bob.Services.GetRequiredService<IRetiredScidMap>();
        map.Retire(RetiredScidMap.Create(ThreeNodeHarness.BobCarolScid, ThreeNodeHarness.BobCarolChannelId,
                                         ThreeNodeHarness.BlockHeight));
        return map;
    }

    /// <summary>The Bob-Carol rows after a lock at the harness tip: the initial funding replaced with its short
    /// channel id, the splice current at <see cref="s_spliceScid"/>.</summary>
    private static IReadOnlyList<ChannelFunding> CreateLockedFundingRows()
    {
        CompactPubKey key = new(Enumerable.Repeat((byte)0x02, 33).ToArray());
        return
        [
            new ChannelFunding(new TxId(Enumerable.Repeat((byte)0x01, 32).ToArray()), 1, 1_000_000, key, key, 0, 0, 0,
                               ChannelFundingKind.Initial, ChannelFundingStatus.Replaced,
                               ShortChannelId: ThreeNodeHarness.BobCarolScid),
            new ChannelFunding(new TxId(Enumerable.Repeat((byte)0x02, 32).ToArray()), 1, 1_100_000, key, key, 1, 0, 0,
                               ChannelFundingKind.Splice, ChannelFundingStatus.Current,
                               ConfirmedHeight: ThreeNodeHarness.BlockHeight, ShortChannelId: s_spliceScid)
        ];
    }

    /// <summary>A provider whose unit of work lists Bob's stored channels and <paramref name="fundings"/> as the
    /// Bob-Carol channel's rows.</summary>
    private static IServiceProvider CreateFundingRowsProvider(IServiceProvider bob,
                                                              IReadOnlyList<ChannelFunding> fundings)
    {
        var channelDb = new Mock<IChannelDbRepository>();
        channelDb.Setup(r => r.GetByIdAsync(ThreeNodeHarness.BobCarolChannelId))
                 .ReturnsAsync(() => bob.GetRequiredService<IChannelMemoryRepository>()
                                        .FindChannels(c => c.ChannelId == ThreeNodeHarness.BobCarolChannelId)
                                        .SingleOrDefault());
        var fundingDb = new Mock<IChannelFundingDbRepository>();
        fundingDb.Setup(r => r.GetChannelIdsWithRetiredFundingsAsync())
                 .ReturnsAsync([ThreeNodeHarness.BobCarolChannelId]);
        fundingDb.Setup(r => r.GetByChannelIdAsync(ThreeNodeHarness.BobCarolChannelId)).ReturnsAsync(fundings);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);
        unitOfWork.SetupGet(u => u.ChannelFundingDbRepository).Returns(fundingDb.Object);
        var services = new ServiceCollection();
        services.AddScoped(_ => unitOfWork.Object);
        return services.BuildServiceProvider();
    }

    private static void MoveBobCarolScid(ThreeNodeHarness harness)
    {
        harness.Bob.Channel(ThreeNodeHarness.BobCarolChannelId).ShortChannelId = s_spliceScid;
        harness.Carol.Channel(ThreeNodeHarness.BobCarolChannelId).ShortChannelId = s_spliceScid;
    }

    private static DecryptedFailure Decrypt(ThreeNodeHarness harness, PaymentOnion onion, OutgoingHtlcFailed failed)
    {
        Assert.Equal(HtlcRemovalKind.Fail, failed.Removal.Kind);
        var decrypted = harness.Alice.Services.GetRequiredService<IFailureOnionService>()
                               .DecryptErrorPacket(onion.SharedSecrets, failed.Removal.Reason.Span);
        Assert.NotNull(decrypted);
        return decrypted;
    }
}