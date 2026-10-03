namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Wave M6 (OM2-T2, OM-R-01): the onion message token buckets on a manual clock: burst, refill, per-peer isolation,
/// the global cap, and a disconnected peer that cannot refill its buckets by reconnecting.
/// </summary>
public class OnionMessageRateLimiterTests
{
    private const int LargeMessage = 32834;
    private const int SmallMessage = 1366;

    private static readonly CompactPubKey s_peerA = CreateKey(1);
    private static readonly CompactPubKey s_peerB = CreateKey(2);

    private readonly ManualClock _clock = new();

    [Fact]
    public void Given_TheDefaults_When_APeerSendsTwentyOneMessagesInASecond_Then_TheTwentyFirstIsDropped()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);

        // Act
        var results = Enumerable.Range(0, 21).Select(_ => limiter.TryAdmit(s_peerA, SmallMessage)).ToArray();

        // Assert
        Assert.All(results.Take(20), Assert.True);
        Assert.False(results[20]);
        Assert.Equal(20, limiter.AdmittedCount);
        Assert.Equal(1, limiter.DroppedCount);
    }

    [Fact]
    public void Given_AnEmptyMessageBucket_When_TimePasses_Then_ItRefillsAtItsRate()
    {
        // Arrange: 20 messages/s is one every 50 ms
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        for (var i = 0; i < 20; i++)
            Assert.True(limiter.TryAdmit(s_peerA, SmallMessage));

        // Act
        _clock.Advance(TimeSpan.FromMilliseconds(40));
        var tooEarly = limiter.TryAdmit(s_peerA, SmallMessage);
        _clock.Advance(TimeSpan.FromMilliseconds(10));
        var afterOneToken = limiter.TryAdmit(s_peerA, SmallMessage);
        var noSecondToken = limiter.TryAdmit(s_peerA, SmallMessage);
        _clock.Advance(TimeSpan.FromHours(1));
        var afterALongTime = Enumerable.Range(0, 21).Count(_ => limiter.TryAdmit(s_peerA, SmallMessage));

        // Assert: never more than the burst, however long the peer was quiet
        Assert.False(tooEarly);
        Assert.True(afterOneToken);
        Assert.False(noSecondToken);
        Assert.Equal(20, afterALongTime);
    }

    [Fact]
    public void Given_LargeMessages_When_ThePeerByteBurstIsUsed_Then_TheNextOneIsDroppedUntilBytesRefill()
    {
        // Arrange: 256 KiB burst holds seven 32,834-byte messages; 64 KiB/s refills one in about 0.5 s
        var limits = new OnionMessageRateLimits(PeerMessagesPerSecond: 1000, PeerBurstMessages: 1000);
        var limiter = new OnionMessageRateLimiter(limits, _clock);

        // Act
        var burst = Enumerable.Range(0, 8).Select(_ => limiter.TryAdmit(s_peerA, LargeMessage)).ToArray();
        var smallStillFits = limiter.TryAdmit(s_peerA, SmallMessage);
        _clock.Advance(TimeSpan.FromMilliseconds(510));
        var afterRefill = limiter.TryAdmit(s_peerA, LargeMessage);

        // Assert
        Assert.All(burst.Take(7), Assert.True);
        Assert.False(burst[7]);
        Assert.True(smallStillFits);
        Assert.True(afterRefill);
    }

    [Fact]
    public void Given_OnePeerOverItsLimit_When_AnotherPeerSends_Then_TheOtherPeerIsAdmitted()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        while (limiter.TryAdmit(s_peerA, SmallMessage))
        {
        }

        // Act
        var other = Enumerable.Range(0, 20).Count(_ => limiter.TryAdmit(s_peerB, SmallMessage));
        var stillBlocked = limiter.TryAdmit(s_peerA, SmallMessage);

        // Assert
        Assert.Equal(20, other);
        Assert.False(stillBlocked);
    }

    [Fact]
    public void Given_AGlobalMessageCap_When_ManyPeersSend_Then_TheTotalIsCapped()
    {
        // Arrange: 5 messages in total, 100 per peer
        var limits = new OnionMessageRateLimits(PeerMessagesPerSecond: 100, PeerBurstMessages: 100,
                                                GlobalMessagesPerSecond: 5, GlobalBurstMessages: 5);
        var limiter = new OnionMessageRateLimiter(limits, _clock);
        var peers = Enumerable.Range(10, 6).Select(i => CreateKey((byte)i)).ToArray();

        // Act
        var results = peers.Select(p => limiter.TryAdmit(p, SmallMessage)).ToArray();
        _clock.Advance(TimeSpan.FromMilliseconds(200));
        var afterOneGlobalToken = limiter.TryAdmit(peers[5], SmallMessage);

        // Assert
        Assert.Equal([true, true, true, true, true, false], results);
        Assert.True(afterOneGlobalToken);
    }

    [Fact]
    public void Given_AGlobalByteCap_When_ManyPeersSendLargeMessages_Then_TheTotalBytesAreCapped()
    {
        // Arrange: three large messages fit the global byte burst
        var limits = new OnionMessageRateLimits(GlobalBytesPerSecond: 10_000, GlobalBurstBytes: 3 * LargeMessage);
        var limiter = new OnionMessageRateLimiter(limits, _clock);

        // Act
        var results = Enumerable.Range(20, 4).Select(i => limiter.TryAdmit(CreateKey((byte)i), LargeMessage))
                                .ToArray();

        // Assert
        Assert.Equal([true, true, true, false], results);
    }

    [Fact]
    public void Given_AMessageDroppedByTheGlobalCap_When_Dropped_Then_ThePeerBucketsKeepTheirTokens()
    {
        // Arrange: global 1 message; peer A spends it
        var limits = new OnionMessageRateLimits(GlobalMessagesPerSecond: 1, GlobalBurstMessages: 1);
        var limiter = new OnionMessageRateLimiter(limits, _clock);
        Assert.True(limiter.TryAdmit(s_peerA, SmallMessage));

        // Act: peer B is dropped 30 times by the global bucket, then the global bucket refills
        var dropped = Enumerable.Range(0, 30).Count(_ => !limiter.TryAdmit(s_peerB, SmallMessage));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var admittedAfter = limiter.TryAdmit(s_peerB, SmallMessage);

        // Assert: had the drops taken B's tokens, its 20-message bucket would be empty
        Assert.Equal(30, dropped);
        Assert.True(admittedAfter);
    }

    [Fact]
    public void Given_APeerWithFullBuckets_When_RemovePeer_Then_ItIsForgotten()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        Assert.True(limiter.TryAdmit(s_peerA, SmallMessage));
        _clock.Advance(TimeSpan.FromSeconds(10));

        // Act
        limiter.RemovePeer(s_peerA);

        // Assert
        Assert.Equal(0, limiter.TrackedPeerCount);
    }

    [Fact]
    public void Given_APeerOverItsLimit_When_ItDisconnectsAndReconnects_Then_ItsBucketsAreNotRefilled()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        while (limiter.TryAdmit(s_peerA, SmallMessage))
        {
        }

        // Act
        limiter.RemovePeer(s_peerA);
        var afterReconnect = limiter.TryAdmit(s_peerA, SmallMessage);

        // Assert
        Assert.False(afterReconnect);
        Assert.Equal(1, limiter.TrackedPeerCount);
    }

    [Fact]
    public void Given_ADisconnectedPeerInDebt_When_ItsBucketsRefill_Then_ASweepForgetsIt()
    {
        // Arrange: 21 calls (20 admitted, 1 dropped), then A disconnects in debt and its buckets refill
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        for (var i = 0; i < 21; i++)
            limiter.TryAdmit(s_peerA, SmallMessage);

        limiter.RemovePeer(s_peerA);
        _clock.Advance(TimeSpan.FromSeconds(10));

        // Act: the sweep runs on the SweepInterval-th call (here, another peer's traffic)
        for (var i = 21; i < OnionMessageRateLimiter.SweepInterval - 1; i++)
            limiter.TryAdmit(s_peerB, 0);
        var beforeSweep = limiter.TrackedPeerCount;
        limiter.TryAdmit(s_peerB, 0);
        var afterSweep = limiter.TrackedPeerCount;

        // Assert: A and B before, only B after
        Assert.Equal(2, beforeSweep);
        Assert.Equal(1, afterSweep);
    }

    [Fact]
    public void Given_AMessageAdmittedAfterRemovePeer_When_ItsBucketsRefill_Then_ASweepStillForgetsIt()
    {
        // Arrange: the old connection's read loop admits one last message after the disconnect released the peer (the
        // race). Per-peer message bucket off, so the other peer's zero-length traffic leaves its own buckets full.
        var limits = new OnionMessageRateLimits(PeerMessagesPerSecond: 0, PeerBurstMessages: 0);
        var limiter = new OnionMessageRateLimiter(limits, _clock);
        Assert.True(limiter.TryAdmit(s_peerA, SmallMessage));
        limiter.RemovePeer(s_peerA);
        Assert.True(limiter.TryAdmit(s_peerA, SmallMessage));
        _clock.Advance(TimeSpan.FromSeconds(10));

        // Act: the sweep runs on the SweepInterval-th call
        for (var i = 2; i < OnionMessageRateLimiter.SweepInterval - 1; i++)
            limiter.TryAdmit(s_peerB, 0);
        var beforeSweep = limiter.TrackedPeerCount;
        limiter.TryAdmit(s_peerB, 0);
        var afterSweep = limiter.TrackedPeerCount;

        // Assert
        Assert.Equal(2, beforeSweep);
        Assert.Equal(0, afterSweep);
    }

    [Fact]
    public void Given_APeerNeverRemoved_When_ItsBucketsRefill_Then_ASweepForgetsIt()
    {
        // Arrange: a connection that received a message but never became the peer's session (no RemovePeer ever)
        var limits = new OnionMessageRateLimits(PeerMessagesPerSecond: 0, PeerBurstMessages: 0);
        var limiter = new OnionMessageRateLimiter(limits, _clock);
        Assert.True(limiter.TryAdmit(s_peerA, LargeMessage));
        _clock.Advance(TimeSpan.FromSeconds(10));

        // Act
        for (var i = 1; i < OnionMessageRateLimiter.SweepInterval; i++)
            limiter.TryAdmit(s_peerB, 0);

        // Assert
        Assert.Equal(0, limiter.TrackedPeerCount);
    }

    [Fact]
    public void Given_APeerInDebtNeverRemoved_When_ASweepRuns_Then_ItIsKeptAndNotRefilledEarly()
    {
        // Arrange: A spends its whole message burst; no time passes
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        while (limiter.TryAdmit(s_peerA, SmallMessage))
        {
        }

        // Act: enough calls from A for a sweep
        for (var i = 0; i < OnionMessageRateLimiter.SweepInterval; i++)
            limiter.TryAdmit(s_peerA, SmallMessage);
        var afterSweep = limiter.TryAdmit(s_peerA, SmallMessage);

        // Assert
        Assert.False(afterSweep);
        Assert.Equal(1, limiter.TrackedPeerCount);
    }

    [Fact]
    public void Given_ZeroLimits_When_Admitting_Then_ThoseBucketsAreOff()
    {
        // Arrange: every bucket off
        var limits = new OnionMessageRateLimits(0, 0, 0, 0, 0, 0, 0, 0);
        var limiter = new OnionMessageRateLimiter(limits, _clock);

        // Act
        var admitted = Enumerable.Range(0, 1000).Count(_ => limiter.TryAdmit(s_peerA, LargeMessage));

        // Assert
        Assert.Equal(1000, admitted);
    }

    [Fact]
    public void Given_ANegativeLength_When_Admitting_Then_ItIsDropped()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);

        // Act
        var admitted = limiter.TryAdmit(s_peerA, -1);

        // Assert
        Assert.False(admitted);
        Assert.Equal(1, limiter.DroppedCount);
        Assert.Equal(0, limiter.TrackedPeerCount);
    }

    [Fact]
    public void Given_AnUnknownPeer_When_RemovePeer_Then_NothingHappens()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);

        // Act
        limiter.RemovePeer(s_peerA);

        // Assert
        Assert.Equal(0, limiter.TrackedPeerCount);
    }

    [Fact]
    public async Task Given_ConcurrentCallers_When_TheClockStands_Then_ExactlyTheBurstIsAdmitted()
    {
        // Arrange
        var limiter = new OnionMessageRateLimiter(timeProvider: _clock);
        var admitted = 0;

        // Act
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                if (limiter.TryAdmit(s_peerA, SmallMessage))
                    Interlocked.Increment(ref admitted);
        }, TestContext.Current.CancellationToken)));

        // Assert
        Assert.Equal(20, admitted);
        Assert.Equal(780, limiter.DroppedCount);
    }

    [Fact]
    public void Given_NoArguments_When_Constructed_Then_TheDocumentedDefaultsApply()
    {
        // Act
        var limiter = new OnionMessageRateLimiter();

        // Assert
        Assert.Same(OnionMessageRateLimits.Default, limiter.Limits);
        Assert.Equal(64 * 1024, limiter.Limits.PeerBytesPerSecond);
        Assert.Equal(256 * 1024, limiter.Limits.PeerBurstBytes);
        Assert.Equal(20, limiter.Limits.PeerMessagesPerSecond);
        Assert.Equal(20, limiter.Limits.PeerBurstMessages);
        Assert.Equal(640 * 1024, limiter.Limits.GlobalBytesPerSecond);
        Assert.True(limiter.Limits.PeerBurstBytes >= LargeMessage);
    }

    private static CompactPubKey CreateKey(byte id)
    {
        var key = new byte[33];
        key[0] = 0x02;
        key[32] = id;
        return new CompactPubKey(key);
    }

    /// <summary>A clock that moves only when told (monotonic timestamps in ticks).</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}