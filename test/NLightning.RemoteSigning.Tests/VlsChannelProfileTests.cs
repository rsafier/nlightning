using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Commitments.Interfaces;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Enums;
using NLightning.Domain.Money;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class VlsChannelProfileTests
{
    [Fact]
    public void StartupRejectsAnExistingTrimmedPendingAddEvenWithZeroBudgetAndOneThousandSatMinimum()
    {
        var channel = Channel(2_500);
        var state = channel.Commitments!;
        state = state.ReceiveAdd(0, 1_000_000, Hash(), 500, new byte[1366]).Next;
        SetZeroBudget(channel, state);
        var before = channel.Commitments;

        var failure = Assert.Throws<InvalidOperationException>(() => VlsChannelMappingRegistry.ValidateChannelProfile(channel));

        Assert.Contains("contains trimmed HTLCs", failure.Message);
        Assert.Same(before, channel.Commitments);
    }

    [Fact]
    public void StartupRejectsAStoredPendingFeeThatWouldTrimAnOtherwiseUntrimmedAdd()
    {
        var channel = Channel(253);
        var state = channel.Commitments!;
        state = state.ReceiveAdd(0, 1_000_000, Hash(), 500, new byte[1366]).Next;
        state = state.SendFee(2_500).Next;
        SetZeroBudget(channel, state);

        var failure = Assert.Throws<InvalidOperationException>(() => VlsChannelMappingRegistry.ValidateChannelProfile(channel));

        Assert.Contains("contains trimmed HTLCs", failure.Message);
    }

    [Fact]
    public void StartupRejectsAnAlreadyCommittedTrimmedOutput()
    {
        var channel = Channel(2_500);
        var state = channel.Commitments!;
        state = state.ReceiveAdd(0, 1_000_000, Hash(), 500, new byte[1366]).Next;
        state = state.ReceiveCommit(new CommitmentSignatures(new CompactSignature(new byte[64]), []),
            new AcceptingVerifier()).Next;
        SetZeroBudget(channel, state);

        var failure = Assert.Throws<InvalidOperationException>(() => VlsChannelMappingRegistry.ValidateChannelProfile(channel));

        Assert.Contains("contains trimmed HTLCs", failure.Message);
    }

    [Fact]
    public void StartupAcceptsAnUntrimmedPendingAddWithoutChangingItsState()
    {
        var channel = Channel(2_500);
        var state = channel.Commitments!;
        state = state.ReceiveAdd(0, 100_000_000, Hash(), 500, new byte[1366]).Next;
        SetZeroBudget(channel, state);
        var before = channel.Commitments;

        VlsChannelMappingRegistry.ValidateChannelProfile(channel);

        Assert.Same(before, channel.Commitments);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupRejectsFractionalPendingHtlcsWithoutChangingState(bool outgoing)
    {
        var channel = Channel(2_500);
        var state = channel.Commitments!;
        state = outgoing ? state.SendAdd(10_000_001, Hash(), 500, new byte[1366]).Next
                         : state.ReceiveAdd(0, 10_000_001, Hash(), 500, new byte[1366]).Next;
        SetZeroBudget(channel, state);
        var before = channel.Commitments;

        var failure = Assert.Throws<InvalidOperationException>(() => VlsChannelMappingRegistry.ValidateChannelProfile(channel));

        Assert.Contains("whole-satoshi", failure.Message);
        Assert.Same(before, channel.Commitments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StartupRejectsFractionalBalancesEvenWhenTheirTotalIsWholeSatoshis(bool localFraction)
    {
        var channel = Channel(2_500);
        var local = localFraction ? 800_000_001UL : 799_999_999UL;
        var remote = 1_000_000_000UL - local;
        var state = ChannelCommitments.Create(channel.ChannelId, CommitmentParams.FromChannel(channel),
            local, remote, 2_500, Point(20), Point(21));
        SetZeroBudget(channel, state);
        var before = channel.Commitments;

        var failure = Assert.Throws<InvalidOperationException>(() => VlsChannelMappingRegistry.ValidateChannelProfile(channel));

        Assert.Contains("whole-satoshi", failure.Message);
        Assert.Same(before, channel.Commitments);
        Assert.Equal(local, channel.LocalBalance.MilliSatoshi);
        Assert.Equal(remote, channel.RemoteBalance.MilliSatoshi);
    }

    private static ChannelModel Channel(uint feerate)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(546), LightningMoney.Satoshis(10_000),
            LightningMoney.Satoshis(1_000), 30, LightningMoney.Satoshis(1_000_000), 144);
        var parameters = new ChannelParams(party, party, LightningMoney.Satoshis((ulong)feerate), 3, false, FeatureSupport.No);
        var funding = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), Point(1), Point(2))
        {
            TransactionId = new TxId(Enumerable.Repeat((byte)0x77, 32).ToArray()),
            Index = 0
        };
        var local = new ChannelKeySetModel(0, Point(1), Point(3), Point(4), Point(5), Point(6), Point(7));
        var remote = ChannelKeySetModel.CreateForRemote(Point(2), Point(13), Point(14), Point(15), Point(16), Point(20));
        var channel = new ChannelModel(parameters, new ChannelId(Enumerable.Repeat((byte)0x3C, 32).ToArray()), null, funding, true,
            null, null, LightningMoney.Satoshis(800_000), local, 0, 0,
            LightningMoney.Satoshis(200_000), remote, 0, Point(10), 0, ChannelState.Open, ChannelVersion.V1);
        channel.UpdateCommitments(ChannelCommitments.Create(channel.ChannelId, CommitmentParams.FromChannel(channel),
            800_000_000, 200_000_000, feerate, Point(20), Point(21)));
        return channel;
    }

    private static void SetZeroBudget(ChannelModel channel, ChannelCommitments state) =>
        channel.UpdateCommitments(ChannelCommitments.Restore(channel.ChannelId,
            state.Params with { MaxDustHtlcExposureMsat = 0 }, state.LocalBalanceMsat, state.RemoteBalanceMsat,
            state.Htlcs.Values, state.FeeUpdates, state.LocalNextHtlcId, state.RemoteNextHtlcId,
            state.LocalCommit, state.RemoteCommit, state.RemoteNextCommit, state.RemoteNextPerCommitmentPoint));

    private static CompactPubKey Point(byte tag)
    {
        var bytes = new byte[33]; bytes[0] = 2; bytes[32] = tag; return new CompactPubKey(bytes);
    }
    private static Hash Hash() => new(Enumerable.Repeat((byte)7, 32).ToArray());
    private sealed class AcceptingVerifier : ICommitmentVerifier
    {
        public bool VerifyLocalCommitment(ChannelId channelId, NLightning.Domain.Channels.Splicing.ChannelFunding? funding,
            ulong number, CommitmentSpec spec, CommitmentSignatures signatures) => true;
    }
}