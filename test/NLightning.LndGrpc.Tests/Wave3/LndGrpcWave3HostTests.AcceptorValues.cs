using NBitcoin;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Channels.Acceptance;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Node;
using Domain.Node.Options;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;

/// <summary>
/// NL-1181: a <c>ChannelAcceptResponse</c>'s values reach what we announce, as LND's funding manager applies them: the
/// stream's answer goes through the real gate and <see cref="ChannelOpenDecisionRules.TryApply"/> as the accept builders
/// call it.
/// </summary>
public sealed partial class LndGrpcWave3HostTests
{
    private static readonly ChannelParty s_nodeLocal = new(LightningMoney.Satoshis(354), LightningMoney.Satoshis(5_000),
                                                           LightningMoney.MilliSatoshis(1), 483,
                                                           LightningMoney.Satoshis(450_000), 144);

    [Fact]
    public async Task Given_AZeroConfOpen_When_TheAcceptorAcceptsEveryValue_Then_TheyApplyAtDepthZero()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);
        var open = CreateOpenRequest(5) with { ChannelType = ZeroConfChannelType() };
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest);

        // Act
        var decision = _gate.DecideAsync(open, Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        var request = stream.ResponseStream.Current;
        await stream.RequestStream.WriteAsync(new ChannelAcceptResponse
        {
            PendingChanId = request.PendingChanId,
            Accept = true,
            UpfrontShutdown = address.ToString(),
            CsvDelay = 288,
            ReserveSat = 7_500,
            InFlightMaxMsat = 300_000_000,
            MaxHtlcCount = 30,
            MinHtlcIn = 2_000,
            ZeroConf = true
        }, Ct);
        var result = await decision;
        var error = ChannelOpenDecisionRules.TryApply(result, open, s_nodeLocal, 3, open.FundingAmount,
                                                      out var local, out var depth,
                                                      new FeatureOptions
                                                      {
                                                          UpfrontShutdownScript = Domain.Enums.FeatureSupport.Optional
                                                      });

        // Assert
        Assert.True(request.WantsZeroConf);
        Assert.True(request.WantsScidAlias);
        Assert.Null(error);
        Assert.Equal(0U, depth);
        Assert.Equal((ushort)288, local.ToSelfDelay);
        Assert.Equal(LightningMoney.Satoshis(7_500), local.ChannelReserveAmount);
        Assert.Equal(LightningMoney.MilliSatoshis(300_000_000), local.MaxHtlcValueInFlight);
        Assert.Equal((ushort)30, local.MaxAcceptedHtlcs);
        Assert.Equal(LightningMoney.MilliSatoshis(2_000), local.HtlcMinimumAmount);
        Assert.Equal(address.ScriptPubKey.ToBytes(), (byte[])local.UpfrontShutdownScript!.Value);
    }

    [Fact]
    public async Task Given_AZeroConfOpen_When_TheAcceptorAcceptsWithoutZeroConf_Then_TheOpenIsRefusedAsLndDoes()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);
        var open = CreateOpenRequest(6) with { ChannelType = ZeroConfChannelType() };

        // Act
        var decision = _gate.DecideAsync(open, Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        await stream.RequestStream.WriteAsync(new ChannelAcceptResponse
        {
            PendingChanId = stream.ResponseStream.Current.PendingChanId,
            Accept = true
        }, Ct);
        var result = await decision;
        var error = ChannelOpenDecisionRules.TryApply(result, open, s_nodeLocal, 0, open.FundingAmount, out _,
                                                      out _);

        // Assert
        Assert.True(result.Accept);
        Assert.Equal("channel acceptor blocked zero-conf channel negotiation", error);
    }

    [Fact]
    public async Task Given_AnOpenWithoutTheZeroConfType_When_TheAcceptorAsksForZeroConf_Then_TheOpenIsRefused()
    {
        // Arrange: LND would make it zero-conf through option_scid_alias; NLightning has no zero-conf channels of its
        // own, so the answer cannot apply
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);
        var open = CreateOpenRequest(7);

        // Act
        var decision = _gate.DecideAsync(open, Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        await stream.RequestStream.WriteAsync(new ChannelAcceptResponse
        {
            PendingChanId = stream.ResponseStream.Current.PendingChanId,
            Accept = true,
            ZeroConf = true
        }, Ct);
        var error = ChannelOpenDecisionRules.TryApply(await decision, open, s_nodeLocal, 3, open.FundingAmount,
                                                      out var local, out var depth);

        // Assert
        Assert.NotNull(error);
        Assert.Equal(s_nodeLocal, local);
        Assert.Equal(3U, depth);
    }

    [Fact]
    public async Task Given_AnAcceptorRejectionWithoutText_When_AnOpenArrives_Then_TheOpenerGetsLndsGenericText()
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        using var stream = connection.LightningClient.ChannelAcceptor(cancellationToken: Ct);
        await WaitUntilAsync(() => _gate.HasDeciders);

        // Act
        var decision = _gate.DecideAsync(CreateOpenRequest(8), Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        await stream.RequestStream.WriteAsync(new ChannelAcceptResponse
        {
            PendingChanId = stream.ResponseStream.Current.PendingChanId,
            Accept = false
        }, Ct);

        // Assert
        var result = await decision;
        Assert.False(result.Accept);
        Assert.Equal(ChannelOpenDecision.GenericRejection, result.Error);
    }

    private static FeatureSet ZeroConfChannelType()
    {
        var channelType = FeatureSet.NewBasicChannelType();
        channelType.SetFeature(Domain.Enums.Feature.OptionAnchors, true);
        channelType.SetFeature(Domain.Enums.Feature.OptionScidAlias, true);
        channelType.SetFeature(Domain.Enums.Feature.OptionZeroconf, true);
        return channelType;
    }
}