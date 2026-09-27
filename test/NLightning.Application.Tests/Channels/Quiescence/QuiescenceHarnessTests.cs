namespace NLightning.Application.Tests.Channels.Quiescence;

using Application.Channels.Quiescence;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Quiescence;
using Domain.Exceptions;
using Domain.Protocol.Messages;
using Harness;

/// <summary>
/// Proof of splicing plan Q1-T6 (wave Q, lane Q-B): two in-process nodes (<see cref="TwoNodeHarness"/>, production
/// channel managers, handlers, signers, channel operations and commit schedulers) with the production
/// <see cref="QuiescenceService"/> and <see cref="QuiescenceTimeoutMonitor"/>: simultaneous <c>stfu</c>, a request with
/// HTLCs in flight both ways (each <c>stfu</c> waits until its sender's changes are committed), a fulfill refused while
/// quiescing and sent after the end, a disconnection ending the quiescence, and the timeouts on a manual clock. After
/// every scenario the two commitment chains still agree (I7) and the snapshots mirror each other.
/// </summary>
public class QuiescenceHarnessTests
{
    [Fact]
    public async Task Given_BothNodesRequest_When_TheirStfuCross_Then_TheFunderIsTheInitiatorOnBothSides()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;

        // Act: both stfu(1) go out before either is delivered
        var aliceRequest = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        var bobRequest = pair.Quiescence(pair.Bob).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();

        // Assert: Q-R-05, Alice sent open_channel
        Assert.Equal(QuiescenceInitiator.Local, await aliceRequest.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(QuiescenceInitiator.Remote, await bobRequest.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.All(pair.StfuSent, s => Assert.True(s.Initiator));
        Assert.Equal(2, pair.StfuSent.Count);
        Assert.Empty(pair.StfuWarnings);
        foreach (var node in new[] { pair.Alice, pair.Bob })
        {
            var state = pair.State(node);
            Assert.True(state.IsQuiescent);
            Assert.True(state.SentStfuInitiator);
            Assert.True(state.ReceivedStfuInitiator);
        }

        Assert.Equal(QuiescenceInitiator.Local, pair.State(pair.Alice).Initiator);
        Assert.Equal(QuiescenceInitiator.Remote, pair.State(pair.Bob).Initiator);
        AssertAgreement(pair);
    }

    [Fact]
    public async Task Given_HtlcsInFlightBothWays_When_AliceRequests_Then_EachStfuWaitsUntilItsSendersChangesAreCommitted()
    {
        // Arrange: an HTLC each way, neither signed yet
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        pair.HoldFulfills["Alice"] = true;
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        await pair.OfferAsync(pair.Bob, 20_000_000, 2);

        // Act: Alice asks while her add is pending: no stfu yet, and she proposes nothing new
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.WhenIdleAsync();
        Assert.Empty(pair.StfuSent);
        Assert.True(pair.State(pair.Alice).IsQuiescing);
        var refusal = await Assert.ThrowsAsync<ChannelQuiescentException>(() => pair.OfferAsync(pair.Alice, 10_000_000, 3));
        Assert.Equal("Q-R-02", refusal.RequirementId);

        await pair.PumpAsync();

        // Assert: quiescent, Alice initiator, and each stfu went out only with its sender's changes committed
        Assert.Equal(QuiescenceInitiator.Local, await request.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.True(pair.State(pair.Alice).IsQuiescent);
        Assert.True(pair.State(pair.Bob).IsQuiescent);
        Assert.Equal([("Alice", true, false), ("Bob", false, false)], pair.StfuSent.ToArray());
        Assert.All(pair.Alice.State.Htlcs.Values,
                   h => Assert.True(h.State is HtlcState.SentAddAckRevocation or HtlcState.RcvdAddAckRevocation));
        Assert.All(pair.Bob.State.Htlcs.Values,
                   h => Assert.True(h.State is HtlcState.SentAddAckRevocation or HtlcState.RcvdAddAckRevocation));

        // Each stfu follows every commitment message of its sender on the wire
        AssertStfuLastFrom(pair.Bob.Received);
        AssertStfuLastFrom(pair.Alice.Received);
        AssertAgreement(pair);

        // Both sides refuse updates while quiescent (Q-S-04: both sent stfu)
        var quiescentRefusal =
            await Assert.ThrowsAsync<ChannelQuiescentException>(() => pair.OfferAsync(pair.Bob, 10_000_000, 4));
        Assert.Equal("Q-S-04", quiescentRefusal.RequirementId);

        // The dependent protocol ends it: updates flow again
        await pair.TerminateAsync(QuiescenceEndReason.TxAbort);
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
        await pair.OfferAsync(pair.Alice, 10_000_000, 5);
        await pair.PumpAsync();
        Assert.Equal(3, pair.Alice.State.Htlcs.Count);
        AssertAgreement(pair);
    }

    [Fact]
    public async Task Given_AFulfillWhileQuiescing_When_TheQuiescenceEnds_Then_TheFulfillIsSentAfterwards()
    {
        // Arrange: Alice's HTLC locked in at Bob, whose switch holds it for now
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 40_000_000, 7);
        await pair.PumpAsync();
        var lockedIn = Assert.Single(pair.Bob.Events.OfType<IncomingHtlcLockedIn>());

        // Alice asks for quiescence; before it is delivered Bob's switch wants to fulfill (Bob is quiescing: he owes
        // his reply), then again once quiescent
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.WhenIdleAsync();
        Assert.True(await pair.DeliverNextAsync(pair.Alice)); // Alice's stfu reaches Bob, who replies at once
        Assert.True(pair.State(pair.Bob).StfuSent);
        pair.HoldFulfills["Bob"] = false;
        var bobSwitch = pair.Bob.Services.GetService(typeof(Domain.Channels.Interfaces.IHtlcSwitch))
                     as Domain.Channels.Interfaces.IHtlcSwitch;
        await bobSwitch!.HandleAsync(lockedIn, ct);
        await pair.PumpAsync();
        Assert.Equal(QuiescenceInitiator.Local, await request.WaitAsync(TimeSpan.FromSeconds(10), ct));
        await bobSwitch.HandleAsync(lockedIn, ct);

        // Assert: refused, nothing sent, the HTLC still locked in
        Assert.Equal(2, pair.FulfillRefusals.Count);
        Assert.All(pair.FulfillRefusals, r => Assert.Equal("Q-S-04", r.Refusal.RequirementId));
        Assert.DoesNotContain(pair.Alice.Received, m => m is UpdateFulfillHtlcMessage);
        Assert.Equal(HtlcState.RcvdAddAckRevocation, Assert.Single(pair.Bob.State.Htlcs.Values).State);

        // Act: the quiescence ends; the replay hands the lock-in to the switch again
        await pair.TerminateAsync(QuiescenceEndReason.TxAbort);
        await pair.PumpAsync();

        // Assert: fulfilled once, settled on both sides
        Assert.Single(pair.Alice.Received.OfType<UpdateFulfillHtlcMessage>());
        Assert.Single(pair.Alice.Events.OfType<OutgoingHtlcFulfilled>());
        Assert.Empty(pair.Alice.State.Htlcs);
        Assert.Empty(pair.Bob.State.Htlcs);
        AssertAgreement(pair);
    }

    [Fact]
    public async Task Given_AQuiescentChannel_When_TheLinkDrops_Then_TheQuiescenceEndsAndUpdatesFlowAfterTheReestablish()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();
        await request.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(pair.State(pair.Bob).IsQuiescent);

        // Act
        await pair.DisconnectAsync();
        await pair.Harness.ReconnectAsync();
        await pair.PumpAsync();

        // Assert: Q-R-04 on both sides, and an HTLC goes through after the reestablish
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Bob));
        await pair.OfferAsync(pair.Alice, 25_000_000, 9);
        await pair.PumpAsync();
        Assert.Empty(pair.Alice.State.Htlcs);
        Assert.Single(pair.Alice.Events.OfType<OutgoingHtlcFulfilled>());
        AssertAgreement(pair);
    }

    [Fact]
    public async Task Given_ARequestWaitingForItsDrain_When_TheLinkDrops_Then_TheRequestFails()
    {
        // Arrange: Alice's add is pending, so her stfu waits; the link is down, so nothing drains
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        pair.Alice.PeerAlive = false;
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.WhenIdleAsync();
        Assert.False(request.IsCompleted);

        // Act
        await pair.DisconnectAsync();

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
        Assert.Empty(pair.StfuSent);
    }

    [Fact]
    public async Task Given_QuiescentWithAnHtlcPending_When_SixtySecondsPass_Then_BothNodesDisconnect()
    {
        // Arrange: an HTLC Bob holds, then quiescence
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        pair.HoldFulfills["Bob"] = true;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        await pair.PumpAsync();
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();
        await request.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act 1: 59 s
        pair.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Empty(await pair.Monitor(pair.Alice).CheckAsync(ct));
        Assert.Empty(await pair.Monitor(pair.Bob).CheckAsync(ct));

        // Act 2: 60 s
        pair.Clock.Advance(TimeSpan.FromSeconds(1));
        var aliceExpired = await pair.Monitor(pair.Alice).CheckAsync(ct);
        var bobExpired = await pair.Monitor(pair.Bob).CheckAsync(ct);

        // Assert: Q-R-03 on both sides: the quiescence ended and the connection is closed
        Assert.Single(aliceExpired);
        Assert.Single(bobExpired);
        Assert.Equal(2, pair.Disconnects.Count);
        Assert.Contains(pair.Disconnects, d => d.Node == "Alice" && d.Peer == pair.Bob.NodeId);
        Assert.Contains(pair.Disconnects, d => d.Node == "Bob" && d.Peer == pair.Alice.NodeId);
        Assert.All(pair.Disconnects, d => Assert.Contains("Q-R-03", d.Reason));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Bob));
    }

    [Fact]
    public async Task Given_AStfuThatCanNotDrain_When_SixtySecondsPassWithTheHtlcPending_Then_TheRequestEndsWithADisconnect()
    {
        // Arrange: Alice's add is pending and the peer never answers, so her stfu never goes out
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        await pair.OfferAsync(pair.Alice, 30_000_000, 1);
        pair.Alice.PeerAlive = false;
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.WhenIdleAsync();

        // Act
        pair.Clock.Advance(TimeSpan.FromSeconds(60));
        var expired = await pair.Monitor(pair.Alice).CheckAsync(ct);

        // Assert
        Assert.Single(expired);
        Assert.Single(pair.Disconnects);
        await Assert.ThrowsAsync<InvalidOperationException>(() => request.WaitAsync(TimeSpan.FromSeconds(10), ct));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
    }

    [Fact]
    public async Task Given_QuiescentWithoutHtlcs_When_TheTimeoutPasses_Then_OnlyTheIdleTimeoutDisconnects()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();
        await request.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act 1: past the 60 s rule, which needs HTLCs
        pair.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Empty(await pair.Monitor(pair.Alice).CheckAsync(ct));

        // Act 2: just below the idle timeout (5 min), then at it
        pair.Clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(62));
        Assert.Empty(await pair.Monitor(pair.Alice).CheckAsync(ct));
        pair.Clock.Advance(TimeSpan.FromSeconds(1));
        var expired = await pair.Monitor(pair.Alice).CheckAsync(ct);

        // Assert
        Assert.Single(expired);
        var disconnect = Assert.Single(pair.Disconnects);
        Assert.Equal(("Alice", pair.Bob.NodeId), (disconnect.Node, disconnect.Peer));
        Assert.Equal(QuiescenceState.None, pair.State(pair.Alice));
        Assert.True(pair.State(pair.Bob).IsQuiescent);
    }

    [Fact]
    public async Task Given_TheReplyIsOwed_When_ThePeerSendsASecondStfu_Then_AWarningClosesTheConnection()
    {
        // Arrange
        using var pair = new QuiescenceTestPair();
        var ct = TestContext.Current.CancellationToken;
        var request = pair.Quiescence(pair.Alice).RequestAsync(TwoNodeHarness.ChannelId, QuiescencePurpose.Probe, ct);
        await pair.PumpAsync();
        await request.WaitAsync(TimeSpan.FromSeconds(10), ct);

        // Act: Alice's stfu again (Q-S-03)
        pair.Alice.ChannelManager.Publish(pair.Bob.NodeId, [new StfuMessage(new(TwoNodeHarness.ChannelId, true))]);
        await pair.PumpAsync();

        // Assert
        var (node, warning) = Assert.Single(pair.StfuWarnings);
        Assert.Equal("Bob", node);
        Assert.True(warning.CloseConnection);
        Assert.Contains("Q-S-03", warning.Message);
    }

    private static void AssertStfuLastFrom(List<Domain.Protocol.Interfaces.IChannelMessage> received)
    {
        var stfuIndex = received.FindIndex(m => m is StfuMessage);
        Assert.True(stfuIndex >= 0);
        var lastCommitment = received.FindLastIndex(m => m is CommitmentSignedMessage or RevokeAndAckMessage);
        Assert.True(stfuIndex > lastCommitment,
                    $"stfu at {stfuIndex} before a commitment message at {lastCommitment}");
    }

    /// <summary>I7 and mirrored snapshots (as <c>TwoNodeHarnessTests</c> checks).</summary>
    private static void AssertAgreement(QuiescenceTestPair pair)
    {
        var alice = pair.Alice;
        var bob = pair.Bob;
        Assert.Equal(alice.Signed, bob.Verified);
        Assert.Equal(bob.Signed, alice.Verified);
        Assert.Null(alice.State.RemoteNextCommit);
        Assert.Null(bob.State.RemoteNextCommit);
        Assert.Equal(alice.State.LocalCommit.Number, bob.State.RemoteCommit.Number);
        Assert.Equal(bob.State.LocalCommit.Number, alice.State.RemoteCommit.Number);
        Assert.Equal(alice.State.LocalBalanceMsat, bob.State.RemoteBalanceMsat);
        Assert.Equal(alice.State.RemoteBalanceMsat, bob.State.LocalBalanceMsat);
        Assert.Equal(alice.State.Htlcs.Count, bob.State.Htlcs.Count);
    }
}