using Grpc.Core;

namespace NLightning.LndGrpc.Tests;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Google.Protobuf;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;

/// <summary>
/// <c>BatchOpenChannel</c> (NL-1241), the call Lightning Terminal opens every channel with: a batch of one channel is
/// the node's open plus its routing policy; a larger batch is refused before anything is funded.
/// </summary>
public sealed partial class LndGrpcHostTests
{
    private (ChannelId ChannelId, TxId FundingTxId) ArrangeBatchOpen()
    {
        var channelId = new ChannelId(Enumerable.Repeat((byte)0x71, 32).ToArray());
        var fundingTxId = new TxId(Enumerable.Repeat((byte)0x72, 32).ToArray());
        _dispatcher.On<OpenChannelClientRequest, OpenChannelClientResponse>(_ => new OpenChannelClientResponse(channelId));
        _dispatcher.On<OpenChannelClientSubscriptionRequest, OpenChannelClientSubscriptionResponse>(
            _ => new OpenChannelClientSubscriptionResponse(channelId)
            {
                ChannelState = ChannelState.V1FundingSigned,
                TxId = fundingTxId,
                Index = 2
            });
        return (channelId, fundingTxId);
    }

    // Exactly what Terminal sends (its openChannel and openChannelsTool flows).
    private static BatchOpenChannel TerminalChannel(byte peer) => new()
    {
        NodePubkey = ByteString.CopyFrom((byte[])CreatePubKey(peer)),
        LocalFundingAmount = 750_000,
        Private = true,
        UseBaseFee = true,
        UseFeeRate = true,
        FeeRate = 350,
        BaseFee = 1_200,
        PushSat = 0
    };

    [Fact]
    public async Task Given_TerminalsOneChannelBatch_When_BatchOpenChannel_Then_OpenedWithItsPolicyAtThePublishedFunding()
    {
        // Arrange
        var (channelId, fundingTxId) = ArrangeBatchOpen();
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.LightningClient.BatchOpenChannelAsync(new BatchOpenChannelRequest
        {
            Channels = { TerminalChannel(9) },
            SatPerVbyte = 3,
            Label = "terminal"
        }, cancellationToken: Ct);

        // Assert
        var pending = Assert.Single(response.PendingChannels);
        Assert.Equal((byte[])fundingTxId, pending.Txid.ToByteArray());
        Assert.Equal(2u, pending.OutputIndex);
        var open = Assert.IsType<OpenChannelClientRequest>(_dispatcher.Requests[0]);
        Assert.Equal(CreatePubKey(9).ToString(), open.NodeInfo);
        Assert.Equal(LightningMoney.Satoshis(750_000), open.FundingAmount);
        Assert.Null(open.PushAmount);
        Assert.False(open.IsPublic);
        Assert.Equal(LightningMoney.Satoshis(750), open.FeeRatePerKw);
        Assert.Equal("terminal", open.Label);
        var policy = Assert.IsType<SetChannelPolicyClientRequest>(_dispatcher.Requests.Last());
        Assert.Equal(channelId, policy.Channel.ChannelId);
        Assert.Equal(1_200u, policy.FeeBaseMsat);
        Assert.Equal(350u, policy.FeeProportionalMillionths);
        Assert.Null(policy.CltvExpiryDelta);
    }

    [Fact]
    public async Task Given_ARefusedPolicy_When_BatchOpenChannel_Then_TheOpenStillAnswers()
    {
        // Arrange
        ArrangeBatchOpen();
        _dispatcher.On<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>(
            _ => throw new ClientException(ErrorCodes.InvalidOperation, "refused"));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var response = await connection.LightningClient.BatchOpenChannelAsync(new BatchOpenChannelRequest
        {
            Channels = { TerminalChannel(9) }
        }, cancellationToken: Ct);

        // Assert
        Assert.Single(response.PendingChannels);
    }

    [Fact]
    public async Task Given_BatchesTheNodeCannotFund_When_BatchOpenChannel_Then_RefusedBeforeAnyOpen()
    {
        // Arrange
        ArrangeBatchOpen();
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);
        var withPsbt = TerminalChannel(9);
        withPsbt.PendingChanId = ByteString.CopyFrom(new byte[32]);
        var tooHigh = TerminalChannel(9);
        tooHigh.FeeRate = (ulong)uint.MaxValue + 1;
        var staticRemoteKey = TerminalChannel(9);
        staticRemoteKey.CommitmentType = CommitmentType.StaticRemoteKey;
        var cases = new (BatchOpenChannelRequest Request, StatusCode Code)[]
        {
            (new BatchOpenChannelRequest(), StatusCode.InvalidArgument),
            (new BatchOpenChannelRequest { Channels = { TerminalChannel(9), TerminalChannel(10) } }, StatusCode.Unimplemented),
            (new BatchOpenChannelRequest { Channels = { withPsbt } }, StatusCode.Unimplemented),
            (new BatchOpenChannelRequest { Channels = { tooHigh } }, StatusCode.InvalidArgument),
            (new BatchOpenChannelRequest { Channels = { staticRemoteKey } }, StatusCode.Unimplemented),
            (new BatchOpenChannelRequest { Channels = { TerminalChannel(9) }, SpendUnconfirmed = true }, StatusCode.Unimplemented),
            (new BatchOpenChannelRequest { Channels = { TerminalChannel(9) }, SatPerVbyte = -1 }, StatusCode.InvalidArgument)
        };

        foreach (var (request, code) in cases)
        {
            // Act
            var refused = await Assert.ThrowsAsync<RpcException>(
                () => connection.LightningClient.BatchOpenChannelAsync(request, cancellationToken: Ct).ResponseAsync);

            // Assert
            Assert.Equal(code, refused.StatusCode);
        }

        Assert.Empty(_dispatcher.Requests);
    }
}