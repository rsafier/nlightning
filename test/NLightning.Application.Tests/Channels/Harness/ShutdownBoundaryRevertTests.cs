namespace NLightning.Application.Tests.Channels.Harness;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.ValueObjects;
using Domain.Protocol.Onion.ValueObjects;

/// <summary>
/// NL-279 at a reconnection (BOLT 2 B2-SHUT-S08): the peer's uncommitted adds are dropped by
/// <c>RevertUncommitted</c>, and the peer reuses their ids for adds it sends on the new connection, after our
/// retransmitted <c>shutdown</c>. The boundary recorded with our shutdown is lowered to the peer's next HTLC id and
/// saved with the revert, so those adds count as added after our shutdown too.
/// </summary>
public class ShutdownBoundaryRevertTests
{
    private const uint CltvExpiry = 700;

    private static readonly OnionPacket s_onion = new(TwoNodeHarness.Onion);
    private static readonly BitcoinScript s_bobScript = new([0x00, 0x14, .. Enumerable.Repeat((byte)0xB0, 20)]);

    [Fact]
    public async Task Given_BobSentShutdownWithAnUncommittedAddOfAlice_When_TheLinkDrops_Then_TheBoundaryIsLoweredAndSaved()
    {
        // Arrange: Bob has Alice's update_add (id 0) but not her commitment_signed when he records his shutdown
        using var harness = new TwoNodeHarness();
        await OfferAsync(harness.Alice, 50_000_000, TwoNodeHarness.Preimage(1));
        Assert.True(await harness.Alice.DeliverNextAsync());
        var bobChannel = harness.Bob.Channel;
        Assert.Equal(1UL, bobChannel.Commitments!.RemoteNextHtlcId);
        bobChannel.SetLocalShutdownScript(s_bobScript);
        bobChannel.SetFirstRemoteHtlcIdAfterLocalShutdown(bobChannel.Commitments.RemoteNextHtlcId);
        Assert.False(bobChannel.IsRemoteHtlcAddedAfterLocalShutdown(0));

        // Act
        await harness.DisconnectAsync();

        // Assert: id 0 was dropped, so Alice's next add (id 0 again) comes after Bob's shutdown
        Assert.Equal(0UL, harness.Bob.Channel.Commitments!.RemoteNextHtlcId);
        Assert.Equal(0UL, harness.Bob.Channel.FirstRemoteHtlcIdAfterLocalShutdown);
        Assert.True(harness.Bob.Channel.IsRemoteHtlcAddedAfterLocalShutdown(0));
        Assert.Equal(0UL, harness.Bob.Store.CommittedShutdownBoundary);
    }

    [Fact]
    public async Task Given_NoShutdown_When_TheLinkDropsWithAnUncommittedAdd_Then_NoBoundaryIsRecorded()
    {
        // Arrange
        using var harness = new TwoNodeHarness();
        await OfferAsync(harness.Alice, 50_000_000, TwoNodeHarness.Preimage(2));
        Assert.True(await harness.Alice.DeliverNextAsync());

        // Act
        await harness.DisconnectAsync();

        // Assert
        Assert.Equal(0UL, harness.Bob.Channel.Commitments!.RemoteNextHtlcId);
        Assert.Null(harness.Bob.Channel.FirstRemoteHtlcIdAfterLocalShutdown);
        Assert.False(harness.Bob.Channel.IsRemoteHtlcAddedAfterLocalShutdown(0));
        Assert.Null(harness.Bob.Store.CommittedShutdownBoundary);
    }

    private static Task<ulong> OfferAsync(HarnessNode node, ulong amountMsat, Secret preimage)
    {
        var hash = TwoNodeHarness.Hash(preimage);
        return node.Operations.OfferHtlcAsync(TwoNodeHarness.ChannelId, LightningMoney.MilliSatoshis(amountMsat), hash,
                                              CltvExpiry, s_onion, null, HtlcOrigin.Local(hash),
                                              TestContext.Current.CancellationToken);
    }
}