namespace NLightning.Testing.Cluster.Tests.Chain;

using Cluster.Chain;

public class ChainFollowerTests
{
    private static readonly ChainTip s_tip = new(10, "aa");

    [Theory]
    [InlineData(10, null, true, true, "10")]
    [InlineData(10, "AA", true, true, "10")]
    [InlineData(9, "bb", true, false, "9")]
    [InlineData(10, "bb", true, false, "10 on another branch (bb)")]
    [InlineData(10, "aa", false, false, "10 (not synced)")]
    [InlineData(11, null, true, false, "11")]
    public void Given_AReport_When_Evaluated_Then_OnlySyncedAtTheTipsHeightAndHashIsAtTheTip(
        long height, string? hash, bool synced, bool atTip, string state)
    {
        // Act
        var status = ChainFollower.Evaluate(s_tip, new FollowerReport(height, hash, synced));

        // Assert
        Assert.Equal(atTip, status.AtTip);
        Assert.Equal(state, status.State);
    }

    [Fact]
    public async Task Given_AFollowerThatThrows_When_Probed_Then_ItIsNotAtTheTipAndSaysWhy()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var follower = new ChainFollower("lnd", (_, _) => throw new InvalidOperationException("restarting"));

        // Act
        var status = await follower.ProbeAsync(s_tip, ct);

        // Assert
        Assert.False(status.AtTip);
        Assert.Equal("unreachable: restarting", status.State);
    }

    [Fact]
    public async Task Given_ACancelledWait_When_Probed_Then_TheCancellationIsNotSwallowed()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var follower = ChainFollower.AtHeight("x", ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(10L);
        });

        // Act / Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower.ProbeAsync(s_tip, cts.Token));
    }

    [Fact]
    public async Task Given_ACustomPredicate_When_Probed_Then_ItSeesTheTip()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        ChainTip? seen = null;
        var follower = new ChainFollower("nltg", (tip, _) =>
        {
            seen = tip;
            return Task.FromResult(new FollowerStatus(true, "monitor at tip"));
        });

        // Act
        var status = await follower.ProbeAsync(s_tip, ct);

        // Assert
        Assert.True(status.AtTip);
        Assert.Equal(s_tip, seen);
        Assert.Equal("nltg", follower.ToString());
    }

    [Fact]
    public async Task Given_APeerBitcoind_When_Probed_Then_ItsTipHashDecides()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var peer = new FakeBitcoinCore();
        await peer.GenerateToAddressAsync(2, "a", ct);
        var peerTip = await peer.GetTipAsync(ct);
        var follower = ChainFollower.Bitcoind("follower", peer);

        // Act
        var same = await follower.ProbeAsync(peerTip, ct);
        var other = await follower.ProbeAsync(peerTip with { Hash = "other" }, ct);

        // Assert
        Assert.True(same.AtTip);
        Assert.False(other.AtTip);
        Assert.Contains("another branch", other.State);
    }

    [Fact]
    public void Given_NoName_When_Created_Then_ItIsRefused()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new ChainFollower(" ", (_, _) => Task.FromResult(new FollowerStatus(true, ""))));
    }
}