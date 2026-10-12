using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeChannelStaticPolicyTests
{
    [Theory]
    [InlineData(HtlcDirection.Outgoing)]
    [InlineData(HtlcDirection.Incoming)]
    public void ReceiverMinimumRejectsOneMillisatoshiBelowButAcceptsTheExactBoundary(HtlcDirection direction)
    {
        var enrollment = Enrollment();
        var receiver = Receiver(enrollment, direction) with { HtlcMinimumMsat = 1_000_001 };
        enrollment = WithReceiver(enrollment, direction, receiver);
        Assert.Throws<UnauthorizedAccessException>(() => NativeChannelStaticPolicy.Validate(enrollment, Spec(direction, 1_000_000)));
        NativeChannelStaticPolicy.Validate(enrollment, Spec(direction, 1_000_001));
    }

    [Theory]
    [InlineData(HtlcDirection.Outgoing)]
    [InlineData(HtlcDirection.Incoming)]
    public void ReceiverMaximumHtlcsCountsOnlyThatDirection(HtlcDirection direction)
    {
        var enrollment = Enrollment();
        enrollment = WithReceiver(enrollment, direction, Receiver(enrollment, direction) with { MaximumHtlcs = 1 });
        var current = Spec(direction, 1_000_001);
        var opposite = direction == HtlcDirection.Outgoing ? HtlcDirection.Incoming : HtlcDirection.Outgoing;
        var crossed = new CommitmentSpec(current.Holder, current.FeeratePerKw,
            opposite == HtlcDirection.Outgoing ? current.LocalMsat - 2_000_000 : current.LocalMsat,
            opposite == HtlcDirection.Incoming ? current.RemoteMsat - 2_000_000 : current.RemoteMsat,
            current.Htlcs.Concat([new SpecHtlc(opposite, 0, 1_000_000, new Hash(new byte[32]), 100),
                                 new SpecHtlc(opposite, 1, 1_000_000, new Hash(new byte[32]), 100)]));
        NativeChannelStaticPolicy.Validate(enrollment, crossed);
        Assert.Throws<UnauthorizedAccessException>(() => NativeChannelStaticPolicy.Validate(enrollment,
            Spec(direction, 1_000_001, 1_000_001)));
    }

    [Theory]
    [InlineData(HtlcDirection.Outgoing)]
    [InlineData(HtlcDirection.Incoming)]
    public void ReceiverMaximumInFlightIncludesFractionalMillisatoshis(HtlcDirection direction)
    {
        var enrollment = Enrollment();
        enrollment = WithReceiver(enrollment, direction, Receiver(enrollment, direction) with { MaximumInFlightMsat = 2_000_001 });
        NativeChannelStaticPolicy.Validate(enrollment, Spec(direction, 1_000_000, 1_000_001));
        Assert.Throws<UnauthorizedAccessException>(() => NativeChannelStaticPolicy.Validate(enrollment,
            Spec(direction, 1_000_001, 1_000_001)));
    }

    [Theory]
    [InlineData(true, false, 1_810_000UL)]
    [InlineData(false, false, 1_810_000UL)]
    [InlineData(true, true, 3_470_000UL)]
    [InlineData(false, true, 3_470_000UL)]
    public void FunderMustCoverTheFullFeeAndAnchorsWithoutRoundingAwayOneMissingMillisatoshi(bool localFunder,
        bool anchors, ulong requiredMsat)
    {
        var enrollment = Enrollment() with { IsInitiator = localFunder, HasAnchors = anchors };
        var exact = new CommitmentSpec(CommitmentSide.Local, 2500,
            localFunder ? requiredMsat : 1_000_000_000 - requiredMsat,
            localFunder ? 1_000_000_000 - requiredMsat : requiredMsat, []);
        NativeChannelStaticPolicy.Validate(enrollment, exact);
        var insufficient = new CommitmentSpec(CommitmentSide.Local, 2500,
            localFunder ? requiredMsat - 1 : 1_000_000_001 - requiredMsat,
            localFunder ? 1_000_000_001 - requiredMsat : requiredMsat - 1, []);
        Assert.Throws<UnauthorizedAccessException>(() => NativeChannelStaticPolicy.Validate(enrollment, insufficient));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DustExposureIncludesTheCompleteFractionalAmountButDoesNotDisableValidTrimmedHtlcs(bool anchors)
    {
        var enrollment = Enrollment() with { HasAnchors = anchors };
        var spec = Spec(HtlcDirection.Outgoing, 99_999);
        NativeChannelStaticPolicy.Validate(enrollment, spec);
        NativeChannelStaticPolicy.Validate(enrollment, spec, maximumDustExposureMsat: 99_999);
        Assert.Throws<UnauthorizedAccessException>(() => NativeChannelStaticPolicy.Validate(enrollment, spec, 99_998));
    }

    [Fact]
    public void InitialNonfunderZeroBalanceIsValidDespiteItsNegotiatedReserve()
    {
        NativeChannelStaticPolicy.Validate(Enrollment(),
            new CommitmentSpec(CommitmentSide.Local, 2500, 1_000_000_000, 0, []));
    }

    private static CommitmentSpec Spec(HtlcDirection direction, params ulong[] amounts)
    {
        var total = amounts.Aggregate(0UL, (sum, amount) => checked(sum + amount));
        return new CommitmentSpec(CommitmentSide.Local, 2500,
            direction == HtlcDirection.Outgoing ? 600_000_000 - total : 600_000_000,
            direction == HtlcDirection.Incoming ? 400_000_000 - total : 400_000_000,
            amounts.Select((amount, index) => new SpecHtlc(direction, (ulong)index, amount, new Hash(new byte[32]), 100)));
    }

    private static NativeChannelParty Receiver(NativeChannelEnrollment enrollment, HtlcDirection direction) =>
        direction == HtlcDirection.Outgoing ? enrollment.Remote : enrollment.Local;

    private static NativeChannelEnrollment WithReceiver(NativeChannelEnrollment enrollment, HtlcDirection direction,
                                                        NativeChannelParty receiver) =>
        direction == HtlcDirection.Outgoing ? enrollment with { Remote = receiver } : enrollment with { Local = receiver };

    private static NativeChannelEnrollment Enrollment()
    {
        var key = new CompactPubKey(Convert.FromHexString("0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798"));
        var basepoints = new ChannelBasepoints(key, key, key, key, key);
        var party = new NativeChannelParty(546, 10_000, 1, 30, 1_000_000_000, 144);
        return new NativeChannelEnrollment(new NativeSignerBinding("node", "owner", "signer", "regtest", key.ToString()),
            new ChannelId(new byte[32]), new TxId(new byte[32]), 0, 1_000_000, 0,
            basepoints, basepoints, key, true, party, party, false, 1000, 5000);
    }
}