using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Channels.Splicing;

using Application.Channels.Splicing;
using Application.Payments.Routing;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Splicing.Interfaces;
using Domain.Channels.Splicing.Models;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
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
        Assert.Equal(invoice.Preimage, fulfilled.PaymentPreimage);
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        var circuit = await harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                                         ThreeNodeHarness.AliceBobChannelId, 0));
        Assert.Equal(ForwardCircuitStatus.Fulfilled, circuit!.Status);
        Assert.Equal(ThreeNodeHarness.BobCarolChannelId, circuit.OutgoingChannelId);
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