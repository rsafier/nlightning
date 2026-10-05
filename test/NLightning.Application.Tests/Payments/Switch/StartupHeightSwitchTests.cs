using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NLightning.Application.Tests.Payments.Switch;

using Application.Payments.Routing;
using Channels.Harness;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-267: the daemon connects the peers before it starts the chain monitor, so a forward locked in (or replayed)
/// before the monitor's first processed block used to be answered <c>temporary_node_failure</c>. Now the forward
/// checks read the monitor's stored tip while it has no height of its own; without a stored state (a fresh node
/// before its first block) the failure still goes out. On <see cref="ThreeNodeHarness"/> (production managers,
/// <c>HtlcSwitch</c>, real onions, SQLite).
/// </summary>
public class StartupHeightSwitchTests
{
    private static readonly LightningMoney s_amount = LightningMoney.MilliSatoshis(50_000_123);

    [Fact]
    public async Task Given_ForwardBeforeTheMonitorHasAHeight_When_TheStoredTipExists_Then_BobForwardsWithIt()
    {
        // Arrange: Bob's monitor has not reported a height yet, but his database holds the tip of the previous run
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices =
                                                                         services => WithoutHeight(services));
        await harness.Bob.InScopeAsync(async unitOfWork =>
        {
            unitOfWork.BlockchainStateDbRepository.Add(new BlockchainState(ThreeNodeHarness.BlockHeight, Hash.Empty,
                                                                           DateTime.UtcNow));
            await unitOfWork.SaveChangesAsync();
            return true;
        });
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "before first block", null,
                                                                      TestContext.Current.CancellationToken);

        // Act
        await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash, invoice.PaymentSecret));
        await harness.PumpAsync();

        // Assert: the checks ran with the stored tip, so the payment went through
        Assert.Empty(harness.Alice.PaymentHandler.Failed);
        Assert.Equal(invoice.Preimage!.Value, Assert.Single(harness.Alice.PaymentHandler.Fulfilled).PaymentPreimage);
        Assert.Equal(ForwardCircuitStatus.Fulfilled, (await GetCircuitAsync(harness))!.Status);
        AssertNoHtlcs(harness);
    }

    [Fact]
    public async Task Given_ForwardBeforeTheMonitorHasAHeight_When_NoTipIsStored_Then_TemporaryNodeFailure()
    {
        // Arrange - a fresh node: without a stored state there is no height to check with
        await using var harness = await ThreeNodeHarness.CreateAsync(h => h.Bob.ConfigureServices =
                                                                         services => WithoutHeight(services));
        var invoice = await harness.Carol.Invoices.CreateInvoiceAsync(s_amount, "before first block", null,
                                                                      TestContext.Current.CancellationToken);

        // Act
        var (_, onion) = await harness.AlicePaysAsync(harness.RouteToCarol(s_amount, invoice.PaymentHash,
                                                                          invoice.PaymentSecret));
        await harness.PumpAsync();

        // Assert: Bob (hop 0) failed it back without a circuit; the payer retries
        var decrypted = Decrypt(harness, onion, Assert.Single(harness.Alice.PaymentHandler.Failed));
        Assert.Equal(0, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
        Assert.DoesNotContain(harness.Sent, s => s is { From: "Bob", To: "Carol" }
                                              && s.Message is UpdateAddHtlcMessage);
        Assert.Null(await GetCircuitAsync(harness));
        AssertNoHtlcs(harness);
    }

    private static void WithoutHeight(IServiceCollection services)
    {
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(0);
        services.Replace(ServiceDescriptor.Singleton(monitor.Object));
    }

    private static Task<ForwardCircuitModel?> GetCircuitAsync(ThreeNodeHarness harness) =>
        harness.Bob.InScopeAsync(u => u.ForwardCircuitDbRepository.GetByIncomingAsync(
                                     ThreeNodeHarness.AliceBobChannelId, 0));

    private static void AssertNoHtlcs(ThreeNodeHarness harness)
    {
        foreach (var node in harness.Nodes)
            foreach (var channel in node.Channels)
                Assert.Empty(channel.Commitments!.Htlcs);
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