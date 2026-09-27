namespace NLightning.Application.Tests.Channels.Quiescence;

using Domain.Channels.Quiescence;
using Domain.Exceptions;
using Domain.Protocol.Messages;
using Harness;

/// <summary>
/// The update gate of <c>ChannelOperationsService</c> (splicing plan Q1-T4): while quiescing or quiescent every update
/// of ours is refused with the retryable <see cref="ChannelQuiescentException"/>, with nothing persisted or sent.
/// </summary>
public class QuiescenceGateTests
{
    [Fact]
    public async Task Given_QuiescentChannel_When_FailOrFeeUpdateOrFulfill_Then_AllRefusedAsQuiescent()
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
        var bobOps = pair.Bob.Operations;
        var saves = pair.Bob.Store.Saves;

        // Act / Assert (Q1-T4): refused with the retryable subtype, nothing persisted or sent
        await Assert.ThrowsAsync<ChannelQuiescentException>(
            () => bobOps.FailHtlcAsync(TwoNodeHarness.ChannelId, 0, new byte[292], ct));
        await Assert.ThrowsAsync<ChannelQuiescentException>(
            () => bobOps.FulfillHtlcAsync(TwoNodeHarness.ChannelId, 0, TwoNodeHarness.Preimage(1), ct));
        await Assert.ThrowsAsync<ChannelQuiescentException>(
            () => pair.Alice.Operations.UpdateFeeAsync(TwoNodeHarness.ChannelId, 3_000, ct));
        Assert.Equal(saves, pair.Bob.Store.Saves);
        await pair.PumpAsync();
        Assert.DoesNotContain(pair.Alice.Received, m => m is UpdateFailHtlcMessage);
    }
}