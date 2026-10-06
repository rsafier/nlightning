using Google.Protobuf;
using Grpc.Core;
using NBitcoin;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Channels.Acceptance;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Payments.Interception;
using Domain.Protocol.Constants;
using Domain.Protocol.Onion.Enums;
using LndGrpc.Lnrpc;
using LndGrpc.Macaroons;
using LndGrpc.Routerrpc;
using LndGrpc.Services;
using LnrpcFailureCode = LndGrpc.Lnrpc.Failure.Types.FailureCode;

/// <summary>The wave 3 mappings (NL-1180, NL-1183, NL-1184): LND's rules for acceptor and interceptor answers.</summary>
public sealed class Wave3MappingTests
{
    private static readonly ChannelOpenRequest s_request =
        new(new CompactPubKey(Convert.FromHexString("02" + new string('a', 64))), ChainConstants.Regtest,
            new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray()), LightningMoney.Satoshis(1_000_000),
            LightningMoney.MilliSatoshis(5_000), LightningMoney.Satoshis(354), LightningMoney.MilliSatoshis(9_000_000),
            LightningMoney.Satoshis(10_000), LightningMoney.MilliSatoshis(1), 253, 144, 483, 1, null, false);

    [Fact]
    public void Given_AnOpen_When_SentToTheAcceptor_Then_TheFieldsUseLndsUnits()
    {
        // Act
        var rpc = RpcChannelAcceptor.ToRpc(s_request);

        // Assert
        Assert.Equal(1_000_000UL, rpc.FundingAmt);
        Assert.Equal(5_000UL, rpc.PushAmt); // msat
        Assert.Equal(354UL, rpc.DustLimit); // sat
        Assert.Equal(9_000_000UL, rpc.MaxValueInFlight); // msat
        Assert.Equal(10_000UL, rpc.ChannelReserve); // sat
        Assert.Equal(1UL, rpc.MinHtlc);
        Assert.Equal(253UL, rpc.FeePerKw);
        Assert.Equal(144U, rpc.CsvDelay);
        Assert.Equal(483U, rpc.MaxAcceptedHtlcs);
        Assert.Equal(1U, rpc.ChannelFlags);
        Assert.Equal(32, rpc.PendingChanId.Length);
        Assert.Equal(CommitmentType.UnknownCommitmentType, rpc.CommitmentType);
    }

    [Theory]
    [InlineData(new[] { 12, 22 }, CommitmentType.Anchors, false, false)]
    [InlineData(new[] { 12, 22, 46, 50 }, CommitmentType.Anchors, true, true)]
    [InlineData(new[] { 12 }, CommitmentType.StaticRemoteKey, false, false)]
    [InlineData(new[] { 80 }, CommitmentType.SimpleTaprootFinal, false, false)]
    [InlineData(new int[0], CommitmentType.Legacy, false, false)]
    public void Given_AChannelType_When_Described_Then_ItIsLndsCommitmentType(int[] bits, CommitmentType expected,
                                                                             bool zeroConf, bool scidAlias)
    {
        // Arrange
        var type = FeatureSet.DeserializeFromBytes([]);
        foreach (var bit in bits)
            type.SetFeature(bit, true);

        // Act
        var (commitmentType, wantsZeroConf, wantsScidAlias) = RpcChannelAcceptor.DescribeChannelType(type);

        // Assert
        Assert.Equal(expected, commitmentType);
        Assert.Equal(zeroConf, wantsZeroConf);
        Assert.Equal(scidAlias, wantsScidAlias);
    }

    [Fact]
    public void Given_AnAcceptanceWithValues_When_Validated_Then_TheNonZeroOnesAreKept()
    {
        // Act
        var decision = RpcChannelAcceptor.Validate(s_request, new ChannelAcceptResponse
        {
            Accept = true,
            CsvDelay = 288,
            ReserveSat = 20_000,
            InFlightMaxMsat = 100_000_000,
            MaxHtlcCount = 30,
            MinHtlcIn = 1_000,
            MinAcceptDepth = 6,
            UpfrontShutdown = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080"
        }, Network.RegTest, out var invalid);

        // Assert
        Assert.Null(invalid);
        Assert.True(decision.Accept);
        Assert.Equal((ushort)288, decision.ToSelfDelay);
        Assert.Equal(LightningMoney.Satoshis(20_000), decision.ChannelReserve);
        Assert.Equal(LightningMoney.MilliSatoshis(100_000_000), decision.MaxHtlcValueInFlight);
        Assert.Equal((ushort)30, decision.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.MilliSatoshis(1_000), decision.HtlcMinimum);
        Assert.Equal(6U, decision.MinimumDepth);
        Assert.NotNull(decision.UpfrontShutdownScript);
    }

    [Fact]
    public void Given_ARejectionWithAnError_When_Validated_Then_TheErrorGoesToTheOpener()
    {
        // Act
        var decision = RpcChannelAcceptor.Validate(s_request, new ChannelAcceptResponse { Error = "no thanks" },
                                                   Network.RegTest, out var invalid);

        // Assert
        Assert.Null(invalid);
        Assert.False(decision.Accept);
        Assert.Equal("no thanks", decision.Error);
    }

    [Theory]
    [InlineData(true, "oops", 0U, 0UL, "")] // accept with an error is ambiguous
    [InlineData(true, "", 484U, 0UL, "")] // above the BOLT 2 limit
    [InlineData(true, "", 0U, 100UL, "")] // reserve below the opener's dust limit
    [InlineData(true, "", 0U, 0UL, "not-an-address")]
    public void Given_AnInvalidAnswer_When_Validated_Then_ItIsAGenericRejection(bool accept, string error,
                                                                              uint maxHtlcs, ulong reserve,
                                                                              string upfront)
    {
        // Act
        var decision = RpcChannelAcceptor.Validate(s_request, new ChannelAcceptResponse
        {
            Accept = accept,
            Error = error,
            MaxHtlcCount = maxHtlcs,
            ReserveSat = reserve,
            UpfrontShutdown = upfront
        }, Network.RegTest, out var invalid);

        // Assert
        Assert.NotNull(invalid);
        Assert.False(decision.Accept);
        Assert.Equal(ChannelOpenDecision.GenericRejection, decision.Error);
    }

    [Fact]
    public void Given_AnErrorLongerThan500_When_Validated_Then_ItIsAGenericRejection()
    {
        // Act
        var decision = RpcChannelAcceptor.Validate(s_request, new ChannelAcceptResponse { Error = new string('x', 501) },
                                                   Network.RegTest, out var invalid);

        // Assert
        Assert.NotNull(invalid);
        Assert.Equal(ChannelOpenDecision.GenericRejection, decision.Error);
    }

    [Fact]
    public void Given_AHeldForward_When_SentToTheInterceptor_Then_TheCircuitKeyIsTheIncomingScid()
    {
        // Arrange
        var forward = new InterceptedForward(new ChannelId(new byte[32]), 5, new ShortChannelId(150, 1, 0),
                                             new ShortChannelId(160, 2, 1), null,
                                             new Hash(Enumerable.Repeat((byte)9, 32).ToArray()),
                                             LightningMoney.MilliSatoshis(10_100), LightningMoney.MilliSatoshis(10_000),
                                             500, 460, 481, new byte[1366],
                                             [new Domain.Payments.Keysend.CustomRecord(65_537, [1, 2])]);

        // Act
        var rpc = RouterService.ToRpc(forward);

        // Assert
        Assert.Equal(LightningService.ToChanId(new ShortChannelId(150, 1, 0)), rpc.IncomingCircuitKey.ChanId);
        Assert.Equal(5UL, rpc.IncomingCircuitKey.HtlcId);
        Assert.Equal(LightningService.ToChanId(new ShortChannelId(160, 2, 1)), rpc.OutgoingRequestedChanId);
        Assert.Equal(10_100UL, rpc.IncomingAmountMsat);
        Assert.Equal(10_000UL, rpc.OutgoingAmountMsat);
        Assert.Equal(500U, rpc.IncomingExpiry);
        Assert.Equal(460U, rpc.OutgoingExpiry);
        Assert.Equal(481, rpc.AutoFailHeight);
        Assert.Equal(1366, rpc.OnionBlob.Length);
        Assert.Equal([1, 2], rpc.CustomRecords[65_537].ToByteArray());
    }

    [Fact]
    public void Given_AForwardToANodeId_When_SentToTheInterceptor_Then_TheChanIdIsLndsSentinel()
    {
        // Arrange
        var nodeId = new CompactPubKey(Convert.FromHexString("02" + new string('b', 64)));
        var forward = new InterceptedForward(new ChannelId(new byte[32]), 0, new ShortChannelId(150, 1, 0), default,
                                             nodeId, new Hash(new byte[32]), LightningMoney.MilliSatoshis(1),
                                             LightningMoney.MilliSatoshis(1), 500, 460, 481, new byte[1366], []);

        // Act
        var rpc = RouterService.ToRpc(forward);

        // Assert
        Assert.Equal(ulong.MaxValue, rpc.OutgoingRequestedChanId);
        Assert.Equal(33, rpc.OutgoingRequestedNodeId.Length);
    }

    [Fact]
    public void Given_AFailWithoutCode_When_Mapped_Then_ItIsTemporaryChannelFailure()
    {
        // Act
        var (scid, htlcId, resolution) = RouterService.ToResolution(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey { ChanId = LightningService.ToChanId(new ShortChannelId(150, 1, 0)), HtlcId = 3 },
            Action = ResolveHoldForwardAction.Fail
        });

        // Assert
        Assert.Equal(new ShortChannelId(150, 1, 0), scid);
        Assert.Equal(3UL, htlcId);
        Assert.Equal(ForwardInterceptAction.Fail, resolution.Action);
        Assert.Equal(FailureCode.TemporaryChannelFailure, resolution.FailureCode);
        Assert.Null(resolution.ErrorPacket);
    }

    [Theory]
    [InlineData(LnrpcFailureCode.InvalidOnionHmac, FailureCode.InvalidOnionHmac)]
    [InlineData(LnrpcFailureCode.InvalidOnionKey, FailureCode.InvalidOnionKey)]
    [InlineData(LnrpcFailureCode.InvalidOnionVersion, FailureCode.InvalidOnionVersion)]
    public void Given_ABadOnionCode_When_Mapped_Then_ItIsKept(LnrpcFailureCode code, FailureCode expected)
    {
        // Act
        var (_, _, resolution) = RouterService.ToResolution(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey(),
            Action = ResolveHoldForwardAction.Fail,
            FailureCode = code
        });

        // Assert
        Assert.Equal(expected, resolution.FailureCode);
    }

    [Fact]
    public void Given_AnErrorPacket_When_Mapped_Then_ItIsPassedOn()
    {
        // Act
        var (_, _, resolution) = RouterService.ToResolution(new ForwardHtlcInterceptResponse
        {
            IncomingCircuitKey = new CircuitKey(),
            Action = ResolveHoldForwardAction.Fail,
            FailureMessage = ByteString.CopyFrom(new byte[RouterService.ErrorPacketLength])
        });

        // Assert
        Assert.Equal(RouterService.ErrorPacketLength, resolution.ErrorPacket!.Length);
    }

    public static TheoryData<ForwardHtlcInterceptResponse, StatusCode> InvalidAnswers => new()
    {
        { new ForwardHtlcInterceptResponse { Action = ResolveHoldForwardAction.Resume }, StatusCode.InvalidArgument },
        {
            new ForwardHtlcInterceptResponse
            {
                IncomingCircuitKey = new CircuitKey(), Action = ResolveHoldForwardAction.Fail,
                FailureCode = LnrpcFailureCode.TemporaryChannelFailure,
                FailureMessage = ByteString.CopyFrom(new byte[RouterService.ErrorPacketLength])
            },
            StatusCode.InvalidArgument
        },
        {
            new ForwardHtlcInterceptResponse
            {
                IncomingCircuitKey = new CircuitKey(), Action = ResolveHoldForwardAction.Fail,
                FailureMessage = ByteString.CopyFrom(new byte[10])
            },
            StatusCode.InvalidArgument
        },
        {
            new ForwardHtlcInterceptResponse
            {
                IncomingCircuitKey = new CircuitKey(), Action = ResolveHoldForwardAction.Fail,
                FailureCode = LnrpcFailureCode.FeeInsufficient
            },
            StatusCode.InvalidArgument
        },
        {
            new ForwardHtlcInterceptResponse
                { IncomingCircuitKey = new CircuitKey(), Action = ResolveHoldForwardAction.Settle },
            StatusCode.InvalidArgument
        },
        {
            new ForwardHtlcInterceptResponse
                { IncomingCircuitKey = new CircuitKey(), Action = ResolveHoldForwardAction.ResumeModified },
            StatusCode.Unimplemented
        }
    };

    [Theory]
    [MemberData(nameof(InvalidAnswers))]
    public void Given_AnInvalidInterceptorAnswer_When_Mapped_Then_TheStreamFailsLikeLnd(
        ForwardHtlcInterceptResponse response, StatusCode expected)
    {
        // Act
        var e = Assert.Throws<RpcException>(() => RouterService.ToResolution(response));

        // Assert
        Assert.Equal(expected, e.StatusCode);
    }

    [Theory]
    [InlineData(0, 0, false, 0U, (uint)int.MaxValue)]
    [InlineData(1, 6, false, 1U, 6U)]
    [InlineData(0, 0, true, 0U, 0U)]
    public void Given_ConfLimits_When_Parsed_Then_TheyFollowLnd(int min, int max, bool unconfirmedOnly,
                                                               uint expectedMin, uint expectedMax)
    {
        // Act
        var (parsedMin, parsedMax) = WalletKitService.ParseConfs(min, max, unconfirmedOnly);

        // Assert
        Assert.Equal(expectedMin, parsedMin);
        Assert.Equal(expectedMax, parsedMax);
    }

    [Theory]
    [InlineData(5, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(-1, 0, false)]
    public void Given_InvalidConfLimits_When_Parsed_Then_Refused(int min, int max, bool unconfirmedOnly)
    {
        // Act / Assert
        Assert.Throws<RpcException>(() => WalletKitService.ParseConfs(min, max, unconfirmedOnly));
    }

    [Fact]
    public void Given_TheSubServerTable_When_Read_Then_ItHasLndsWalletKitAndInterceptorPermissions()
    {
        // Act / Assert
        Assert.Equal([new MacaroonOp("offchain", "write")],
                     LndPermissions.ForMethod("/routerrpc.Router/HtlcInterceptor"));
        Assert.Equal([new MacaroonOp("address", "read")], LndPermissions.ForMethod("/walletrpc.WalletKit/NextAddr"));
        Assert.Equal([new MacaroonOp("onchain", "write")], LndPermissions.ForMethod("/walletrpc.WalletKit/FundPsbt"));
        Assert.Equal([new MacaroonOp("onchain", "read")], LndPermissions.ForMethod("/walletrpc.WalletKit/ListUnspent"));
        Assert.Equal([new MacaroonOp("onchain", "write"), new MacaroonOp("offchain", "write")],
                     LndPermissions.ForMethod("/lnrpc.Lightning/ChannelAcceptor"));
        Assert.Equal(30, LndSubServerPermissions.WalletKit.Count);
    }
}