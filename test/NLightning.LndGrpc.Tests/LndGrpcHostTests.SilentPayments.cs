using Grpc.Core;

namespace NLightning.LndGrpc.Tests;

using Domain.Client.Constants;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;

public sealed partial class LndGrpcHostTests
{
    [Theory]
    [InlineData("Silent payment sending is disabled.")]
    [InlineData("Silent payment address requires HRP 'sprt' for network 'regtest'.")]
    public async Task Given_SilentPaymentSendRefusal_When_SendCoins_Then_InvalidArgumentRetainsReason(string reason)
    {
        // Arrange
        _dispatcher.On<WithdrawClientRequest, WithdrawClientResponse>(
            _ => throw new ClientException(ErrorCodes.InvalidAddress, reason));
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        // Act
        var error = await Assert.ThrowsAsync<RpcException>(() => connection.LightningClient.SendCoinsAsync(
            new SendCoinsRequest { Addr = "sprt1q", Amount = 20_000 }, cancellationToken: Ct).ResponseAsync);
        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Equal(reason, error.Status.Detail);
        Assert.Equal("sprt1q", Assert.IsType<WithdrawClientRequest>(Assert.Single(_dispatcher.Requests)).Address);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_SilentPaymentChannelDestination_When_OpenOrClose_Then_RefusedWithWithdrawExplanation(bool close)
    {
        // Arrange
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        // Act
        var error = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            if (close)
            {
                using var call = connection.LightningClient.CloseChannel(new CloseChannelRequest { DeliveryAddress = "sprt1q" }, cancellationToken: Ct);
                await call.ResponseStream.MoveNext(Ct);
            }
            else
                await connection.LightningClient.OpenChannelSyncAsync(new OpenChannelRequest { CloseAddress = "sprt1q" }, cancellationToken: Ct);
        });
        // Assert
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
        Assert.Contains("pay the wallet, then `withdraw`", error.Status.Detail);
        Assert.Empty(_dispatcher.Requests);
    }

}