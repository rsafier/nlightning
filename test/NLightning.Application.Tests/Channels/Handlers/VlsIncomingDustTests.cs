using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Application.Tests.Channels.Handlers;

using Application.Channels.Handlers;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Signing.Vls;
using static NormalOperationTestContext;

public class VlsIncomingDustTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IncomingFractionalSatoshiAmountsAreAcceptedWithAndWithoutVls(bool vls)
    {
        var context = CreateContext(253);
        var before = context.State;
        var message = new UpdateAddHtlcMessage(new UpdateAddHtlcPayload(
            LightningMoney.MilliSatoshis(10_000_001UL), TestChannelId, 600, 0, HashOf(SecretOf(1)), Onion));
        var receive = AddHandler(context, vls).HandleAsync(message, ChannelState.Open,
                                                        new FeatureOptions(), PeerNodeId);
        // VLS's default policy (enforce_balance off) takes millisatoshi HTLC amounts
        Assert.Empty(await receive);
        Assert.NotSame(before, context.State);
        Assert.Equal(["apply", "save"], context.Calls);
        Assert.Equal(10_000_001UL, context.State.GetHtlc(HtlcDirection.Incoming, 0)!.AmountMsat);
        Assert.Empty(context.LightningSigner.Invocations);
    }

    [Theory]
    [InlineData(546UL, 354UL, 1_100UL, "Local")]
    [InlineData(354UL, 546UL, 1_100UL, "Remote")]
    [InlineData(546UL, 546UL, 1_000UL, "Local")]
    public async Task VlsIncomingAddMustBeNonDustOnEachCommitmentBeforePersistence(
        ulong localDust, ulong remoteDust, ulong amountSat, string holder)
    {
        var context = CreateContext(1_000, localDust, remoteDust);
        var handler = AddHandler(context, vls: true);
        var before = context.State;

        var error = await Assert.ThrowsAsync<ChannelWarningException>(() => handler.HandleAsync(
            Add(amountSat), ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.True(error.CloseConnection);
        Assert.Contains(holder, error.Message);
        Assert.Same(before, context.State);
        Assert.Empty(context.Calls);
        Assert.Empty(context.Applied);
        Assert.Empty(context.LightningSigner.Invocations);
    }

    [Fact]
    public async Task UncommittedFeeDecreaseDoesNotHideDustAtTheCurrentCommitmentFeerate()
    {
        var context = CreateContext(2_500);
        context.SetState(context.State.ReceiveFee(500, 253, 50_000).Next);
        var before = context.State;
        Assert.Equal(500U, before.LatestFeeratePerKw);

        var error = await Assert.ThrowsAsync<ChannelWarningException>(() => AddHandler(context, true).HandleAsync(
            Add(2_000), ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.True(error.CloseConnection);
        Assert.Contains("trim HTLCs", error.Message);
        Assert.Same(before, context.State);
        Assert.Empty(context.Calls);
        Assert.Empty(context.Applied);
    }

    [Fact]
    public async Task VlsIncomingNonDustAddStillPersistsNormally()
    {
        var context = CreateContext(1_000);

        Assert.Empty(await AddHandler(context, true).HandleAsync(
            Add(2_000), ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.Equal(["apply", "save"], context.Calls);
        Assert.Equal(1UL, context.State.RemoteNextHtlcId);
    }

    [Fact]
    public async Task NativeIncomingDustAddRetainsItsExistingBehavior()
    {
        var context = CreateContext(1_000);

        Assert.Empty(await AddHandler(context, false).HandleAsync(
            Add(1_000), ChannelState.Open, new FeatureOptions(), PeerNodeId));

        Assert.Equal(["apply", "save"], context.Calls);
        Assert.Equal(1UL, context.State.RemoteNextHtlcId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FeeIncreaseThatTrimsAnExistingIncomingHtlcIsRejectedOnlyInVlsMode(bool vls)
    {
        var context = CreateContext(253);
        context.SetState(context.State.ReceiveAdd(0, 2_000_000, HashOf(SecretOf(1)), 600, Onion).Next);
        var handler = FeeHandler(context, vls);
        var before = context.State;
        var message = new UpdateFeeMessage(new UpdateFeePayload(TestChannelId, 5_000));

        if (vls)
        {
            var error = await Assert.ThrowsAsync<ChannelWarningException>(() => handler.HandleAsync(
                message, ChannelState.Open, new FeatureOptions(), PeerNodeId));
            Assert.True(error.CloseConnection);
            Assert.Contains("trim HTLCs", error.Message);
            Assert.Same(before, context.State);
            Assert.Empty(context.Calls);
            Assert.Empty(context.Applied);
        }
        else
        {
            Assert.Empty(await handler.HandleAsync(message, ChannelState.Open, new FeatureOptions(), PeerNodeId));
            Assert.Equal(["apply", "save"], context.Calls);
            Assert.Equal(5_000U, context.State.LatestFeeratePerKw);
        }
        Assert.Empty(context.LightningSigner.Invocations);
    }

    [Fact]
    public async Task VlsFeeIncreaseKeepingHtlcsNonDustStillPersistsNormally()
    {
        var context = CreateContext(253);
        context.SetState(context.State.ReceiveAdd(0, 2_000_000, HashOf(SecretOf(1)), 600, Onion).Next);

        Assert.Empty(await FeeHandler(context, true).HandleAsync(
            new UpdateFeeMessage(new UpdateFeePayload(TestChannelId, 500)), ChannelState.Open,
            new FeatureOptions(), PeerNodeId));

        Assert.Equal(["apply", "save"], context.Calls);
        Assert.Equal(500U, context.State.LatestFeeratePerKw);
    }

    private static NormalOperationTestContext CreateContext(uint feerate, ulong localDust = 546, ulong remoteDust = 546)
    {
        var context = new NormalOperationTestContext(localIsFunder: false);
        var old = context.State;
        var parameters = old.Params with
        {
            MaxDustHtlcExposureMsat = 0,
            Local = old.Params.Local with { DustLimitSatoshis = localDust },
            Remote = old.Params.Remote with { DustLimitSatoshis = remoteDust }
        };
        context.SetState(ChannelCommitments.Create(TestChannelId, parameters, old.LocalBalanceMsat,
            old.RemoteBalanceMsat, feerate, Point(0x20), Point(0x21), new(Signature(1), [])));
        return context;
    }

    private static UpdateAddHtlcMessage Add(ulong amountSat) => new(new UpdateAddHtlcPayload(
        LightningMoney.Satoshis(amountSat), TestChannelId, 600, 0, HashOf(SecretOf(1)), Onion));

    private static UpdateAddHtlcMessageHandler AddHandler(NormalOperationTestContext context, bool vls) =>
        new(NullLogger<UpdateAddHtlcMessageHandler>.Instance,
            context.CreateTransitions(vlsSigner: vls ? new Mock<IVlsChannelSigner>(MockBehavior.Strict).Object : null));

    private static UpdateFeeMessageHandler FeeHandler(NormalOperationTestContext context, bool vls) =>
        new(NullLogger<UpdateFeeMessageHandler>.Instance,
            context.CreateTransitions(vlsSigner: vls ? new Mock<IVlsChannelSigner>(MockBehavior.Strict).Object : null));
}