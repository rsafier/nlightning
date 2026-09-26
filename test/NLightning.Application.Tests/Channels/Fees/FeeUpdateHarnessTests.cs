using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Channels.Fees;

using Application.Channels.Fees;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Messages;
using Domain.Protocol.Onion.ValueObjects;
using Harness;

/// <summary>
/// BOLT2 plan N9-T1 with the real stack (<see cref="TwoNodeHarness"/>: production handlers, engine, signers, channel
/// operations and commit schedulers): the funder's scheduler sends one <c>update_fee</c>, the commit scheduler signs
/// it, both commitments end at the new feerate with the same txids, also when the peer's add crosses it.
/// </summary>
public class FeeUpdateHarnessTests
{
    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_EstimateUp_When_FunderSchedulerRuns_Then_BothSidesCommitTheNewFeerate(bool anchors)
    {
        // Arrange - the harness opens at 2,500 sat/kw; the anchors maximum is 2,500, so estimate a decrease there
        using var harness = new TwoNodeHarness(anchors);
        var alice = harness.Alice;
        var bob = harness.Bob;
        var estimate = anchors ? 1_000U : 4_000U;
        var aliceScheduler = CreateScheduler(alice, estimate);
        var bobScheduler = CreateScheduler(bob, estimate);

        // Act
        var aliceOutcome = Assert.Single(await aliceScheduler.RunOnceAsync(TestContext.Current.CancellationToken));
        var bobOutcomes = await bobScheduler.RunOnceAsync(TestContext.Current.CancellationToken);
        await harness.PumpAsync();

        // Assert - one update_fee, signed and revoked both ways; the non-funder sent nothing
        Assert.True(aliceOutcome.Sent);
        Assert.Empty(bobOutcomes);
        Assert.Single(bob.Received, m => m is UpdateFeeMessage);
        Assert.DoesNotContain(alice.Received, m => m is UpdateFeeMessage);
        Assert.Equal(estimate, alice.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(estimate, alice.State.RemoteCommit.Spec.FeeratePerKw);
        Assert.Equal(estimate, bob.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(estimate, bob.State.RemoteCommit.Spec.FeeratePerKw);
        AssertAgreement(harness);

        // A second round with the same estimate changes nothing
        var again = Assert.Single(await aliceScheduler.RunOnceAsync(TestContext.Current.CancellationToken));
        Assert.False(again.Sent);
    }

    [Fact]
    public async Task Given_PeerAddCrossesOurFeeUpdate_When_Pumped_Then_BothConvergeWithTheSameTxIds()
    {
        // Arrange - Bob offers before he sees our update_fee, and our update_fee leaves before we see his add
        using var harness = new TwoNodeHarness();
        var alice = harness.Alice;
        var bob = harness.Bob;
        var hash = TwoNodeHarness.Hash(TwoNodeHarness.Preimage(1));
        var aliceScheduler = CreateScheduler(alice, 5_000);

        // Act
        await bob.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.Satoshis(40_000), hash, 700,
                                            s_onion, null, HtlcOrigin.Local(hash),
                                            TestContext.Current.CancellationToken);
        var outcome = Assert.Single(await aliceScheduler.RunOnceAsync(TestContext.Current.CancellationToken));
        await harness.PumpAsync();

        // Assert
        Assert.True(outcome.Sent);
        Assert.Single(alice.Events.OfType<IncomingHtlcLockedIn>());
        Assert.Equal(5_000U, alice.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Equal(5_000U, bob.State.LocalCommit.Spec.FeeratePerKw);
        Assert.Single(alice.State.LocalCommit.Spec.Htlcs);
        AssertAgreement(harness);
    }

    [Fact]
    public async Task Given_PeerAway_When_SchedulerRuns_Then_RefusedAndSentOnTheNextRound()
    {
        // Arrange
        using var harness = new TwoNodeHarness();
        var alice = harness.Alice;
        var aliceScheduler = CreateScheduler(alice, 5_000);
        alice.PeerAlive = false;

        // Act
        var refused = Assert.Single(await aliceScheduler.RunOnceAsync(TestContext.Current.CancellationToken));
        alice.PeerAlive = true;
        var sent = Assert.Single(await aliceScheduler.RunOnceAsync(TestContext.Current.CancellationToken));
        await harness.PumpAsync();

        // Assert - nothing was persisted by the refused round
        Assert.False(refused.Sent);
        Assert.Contains("B2-NO-02", refused.Reason);
        Assert.True(sent.Sent);
        Assert.Empty(alice.Dropped);
        Assert.Equal(5_000U, harness.Bob.State.LocalCommit.Spec.FeeratePerKw);
        AssertAgreement(harness);
    }

    private static FeeUpdateScheduler CreateScheduler(HarnessNode node, uint estimatePerKw)
    {
        var channels = new Mock<IChannelMemoryRepository>();
        channels.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
                .Returns((Func<ChannelModel, bool> predicate) =>
                             new[] { node.Channel }.Where(predicate).ToList());
        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.GetFeeRatePerKwAsync(It.IsAny<CancellationToken>()))
                  .ReturnsAsync(LightningMoney.Satoshis(estimatePerKw));
        return new FeeUpdateScheduler(channels.Object, node.Operations, feeService.Object,
                                      NullLogger<FeeUpdateScheduler>.Instance,
                                      Options.Create(new NodeOptions
                                      {
                                          EnableHtlcs = true,
                                          FeeUpdates = new FeeUpdateOptions { NonAnchorFeerateMarginPercent = 100 }
                                      }));
    }

    private static void AssertAgreement(TwoNodeHarness harness)
    {
        var alice = harness.Alice;
        var bob = harness.Bob;
        Assert.Equal(alice.Signed, bob.Verified);
        Assert.Equal(bob.Signed, alice.Verified);
        Assert.False(alice.State.HasPendingChangesForRemote || bob.State.HasPendingChangesForRemote);
        Assert.Null(alice.State.RemoteNextCommit);
        Assert.Null(bob.State.RemoteNextCommit);
        Assert.Equal(alice.State.LocalCommit.Number, bob.State.RemoteCommit.Number);
        Assert.Equal(bob.State.LocalCommit.Number, alice.State.RemoteCommit.Number);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);
    }
}