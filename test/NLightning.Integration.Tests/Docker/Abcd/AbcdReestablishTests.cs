namespace NLightning.Integration.Tests.Docker.Abcd;

using Domain.Client.Responses;
using Domain.Protocol.Messages;
using Fixtures;
using TestCollections;
using Utils;

/// <summary>
/// ABCD roadmap §3 "Reestablish step": every ABCD channel survives a reconnect initiated by LND, one initiated by us
/// and a restart of Carol, with <c>channel_reestablish</c> exchanged and the commitment numbers unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Sent <c>channel_reestablish</c> messages are counted on the node that stays up (<see cref="ChannelMessageRecorder"/>
/// is re-attached only after a restart, so the restarted node's first messages are not seen); every end of ours is
/// counted at least once (bob A-B and B-C, carol B-C and C-D). A channel whose <c>IsReestablished</c> flag is set proves
/// its end also received the peer's, and LND listing its channel <c>Active</c> again proves LND's end.
/// </para>
/// <para>
/// Deviation from roadmap §3 ("count via OnResponseMessageReady plus inbound hook"): there is no inbound hook (the peer
/// services are created per connection), so receipt rests on <c>IsReestablished</c>, which W2-A resets on every
/// disconnect. The count relies on W2-A sending <c>channel_reestablish</c> through
/// <c>IChannelManager.OnResponseMessageReady</c>, the node's single ordered send path; a different send path makes
/// the waits below time out although reestablish works.
/// </para>
/// </remarks>
[Collection(LightningRegtestNetworkFixtureCollection.Name)]
public class AbcdReestablishTests(LightningRegtestNetworkFixture fixture, ITestOutputHelper output)
    : AbcdTestBase(fixture, output)
{
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AbcdNetwork_When_PeersReconnectAndCarolRestarts_Then_EveryChannelReestablishedUnchanged()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var n = Network;
        var before = await n.SnapshotAsync(ct);

        // Act 1: alice drops bob; bob reconnects (alice is his channel peer) and reestablishes A-B
        var bobMark = n.BobSent.Mark;
        await LndTestHelpers.DisconnectPeerAsync(n.Alice, n.Bob.NodeIdHex, ct);
        await AbcdNetwork.WaitForAsync(() => Task.FromResult(
                                           (n.BobSent.CountSent<ChannelReestablishMessage>(n.AliceBob.ChannelId,
                                                                                           bobMark) >= 1,
                                            "bob sent no channel_reestablish for A-B yet")),
                                       AbcdNetwork.ActiveTimeout, "bob reestablished A-B after alice dropped him",
                                       ct);
        await n.WaitUntilUsableAsync(ct);

        // Act 2: carol drops bob, then connects again herself (bob's own reconnect may win the race)
        bobMark = n.BobSent.Mark;
        var carolMark = n.CarolSent.Mark;
        n.Carol.PeerManager.DisconnectPeer(n.Bob.NodeId);
        await Poll.UntilAsync(() => !n.Carol.IsConnectedTo(n.Bob.NodeId)
                                 || n.CarolSent.CountSent<ChannelReestablishMessage>(n.BobCarol.ChannelId,
                                                                                     carolMark) > 0,
                              PeerDropTimeout, "carol dropped bob", ct);
        if (!n.Carol.IsConnectedTo(n.Bob.NodeId))
        {
            try
            {
                await n.Carol.ConnectToAsync(n.Bob, ct);
            }
            catch (Exception e) when (e is InvalidOperationException or TimeoutException)
            {
                // Bob's own reconnect raced ours: he was already connected (InvalidOperationException), or one of the
                // two simultaneous connections replaced the other within the stable window (TimeoutException). Which
                // one survives is untested (roadmap §5); the waits below are the real assertion.
                Console.WriteLine($"[abcd] carol -> bob raced bob's reconnect: {e.Message}");
            }
        }

        await AbcdNetwork.WaitForAsync(() =>
        {
            var bobSent = n.BobSent.CountSent<ChannelReestablishMessage>(n.BobCarol.ChannelId, bobMark);
            var carolSent = n.CarolSent.CountSent<ChannelReestablishMessage>(n.BobCarol.ChannelId, carolMark);
            return Task.FromResult((bobSent >= 1 && carolSent >= 1,
                                    $"B-C channel_reestablish sent: bob {bobSent}, carol {carolSent}"));
        }, AbcdNetwork.ActiveTimeout, "bob and carol both sent channel_reestablish for B-C", ct);
        await n.WaitUntilUsableAsync(ct);

        // Act 2b: david drops carol; carol reconnects (david is her channel peer) and reestablishes C-D
        carolMark = n.CarolSent.Mark;
        await LndTestHelpers.DisconnectPeerAsync(n.David, n.Carol.NodeIdHex, ct);
        await AbcdNetwork.WaitForAsync(() => Task.FromResult(
                                           (n.CarolSent.CountSent<ChannelReestablishMessage>(n.CarolDavid.ChannelId,
                                                                                             carolMark) >= 1,
                                            "carol sent no channel_reestablish for C-D yet")),
                                       AbcdNetwork.ActiveTimeout, "carol reestablished C-D after david dropped her",
                                       ct);
        await n.WaitUntilUsableAsync(ct);

        // Act 3: restart carol on the same key and database; she reconnects to bob and david by herself
        bobMark = n.BobSent.Mark;
        await n.StopNodeAsync(n.Carol, crash: false);
        await n.StartNodeAsync(n.Carol, ct);
        await AbcdNetwork.WaitForAsync(() =>
        {
            var bobSent = n.BobSent.CountSent<ChannelReestablishMessage>(n.BobCarol.ChannelId, bobMark);
            return Task.FromResult((bobSent >= 1, $"bob sent {bobSent} channel_reestablish for B-C"));
        }, AbcdNetwork.ActiveTimeout, "bob reestablished B-C with the restarted carol", ct);
        await n.WaitUntilUsableAsync(ct);
        await ChainSync.WaitAllAtTipAsync(Fixture, n.Nodes, ct);

        // Assert: every end usable again (IsReestablished: each also received the peer's channel_reestablish), and
        // no commitment moved (a reestablish with nothing to retransmit signs nothing)
        var after = await n.SnapshotAsync(ct);
        Console.WriteLine($"Before: {before}");
        Console.WriteLine($"After:  {after}");
        Assert.True(after.BobAliceBob.IsReestablished && after.BobBobCarol.IsReestablished
                 && after.CarolBobCarol.IsReestablished && after.CarolCarolDavid.IsReestablished,
                    "a channel end is not reestablished");
        Assert.True(after.AliceAliceBob.Active, "alice no longer lists A-B active");
        Assert.True(after.DavidCarolDavid.Active, "david did not list C-D active again after carol's restart");
        AssertSameCommitments(before.BobAliceBob, after.BobAliceBob, "bob A-B");
        AssertSameCommitments(before.BobBobCarol, after.BobBobCarol, "bob B-C");
        AssertSameCommitments(before.CarolBobCarol, after.CarolBobCarol, "carol B-C");
        AssertSameCommitments(before.CarolCarolDavid, after.CarolCarolDavid, "carol C-D");
        Assert.Equal(before.AliceAliceBob.LocalBalance, after.AliceAliceBob.LocalBalance);
        Assert.Equal(before.DavidCarolDavid.LocalBalance, after.DavidCarolDavid.LocalBalance);
        Assert.Equal(before.AliceAliceBob.NumUpdates, after.AliceAliceBob.NumUpdates);
        Assert.Equal(before.DavidCarolDavid.NumUpdates, after.DavidCarolDavid.NumUpdates);
        await AssertNetworkHealthyAsync(after, ct);
    }

    private static void AssertSameCommitments(ChannelInfoClientResponse before, ChannelInfoClientResponse after,
                                              string what)
    {
        Assert.True(before.LocalCommitmentNumber == after.LocalCommitmentNumber
                 && before.RemoteCommitmentNumber == after.RemoteCommitmentNumber,
                    $"{what}: commitments {before.LocalCommitmentNumber}/{before.RemoteCommitmentNumber} became "
                  + $"{after.LocalCommitmentNumber}/{after.RemoteCommitmentNumber}");
        Assert.Equal(before.LocalBalance, after.LocalBalance);
    }
}