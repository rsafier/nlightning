using Grpc.Core;

namespace NLightning.LndGrpc.Tests;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using LndGrpc.Macaroons;
using Moq;
using Testing.Lnd;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Verrpc;

public sealed partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_ALoopWalletQuote_When_Estimated_Then_TheConfirmedWalletPreviewSetsTheFee()
    {
        // Arrange
        _fees.Setup(f => f.GetFeeRatePerKwAsync(6, It.IsAny<CancellationToken>()))
            .ReturnsAsync(LightningMoney.Satoshis(2500));
        _walletSpend.Setup(w => w.EstimateOutputFeeAsync(
            It.Is<BitcoinScript>(s => s.Length == 34), LightningMoney.Satoshis(500000),
            LightningMoney.Satoshis(2500), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WalletWithdrawEstimate(LightningMoney.Satoshis(1400), LightningMoney.Satoshis(2500), 560, 1));
        using var reader = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        var request = new EstimateFeeRequest { TargetConf = 6 };
        request.AddrToAmount.Add("bcrt1pqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqm3usuw", 500000);
        // Act
        var result = await reader.LightningClient.EstimateFeeAsync(request, cancellationToken: Ct);
        // Assert
        Assert.Equal(1400, result.FeeSat);
        Assert.Equal(10ul, result.SatPerVbyte);
        _walletSpend.VerifyAll();
    }

    [Fact]
    public async Task Given_StartupClients_When_CallingStateAndVersion_Then_LndclientRequirementsAreMet()
    {
        // Arrange: State has no macaroon requirement in LND.
        var certificate = await File.ReadAllBytesAsync(Path.Combine(_directory, "tls.cert"), Ct);
        using var none = LndNodeConnection.CreateWithoutNodeInfo(LndSettings.FromBytes(Endpoint, certificate, null));
        // Act / Assert
        Assert.Equal(WalletState.ServerActive, (await none.StateClient.GetStateAsync(new GetStateRequest(), cancellationToken: Ct)).State);
        using var stream = none.StateClient.SubscribeState(new SubscribeStateRequest(), cancellationToken: Ct);
        Assert.True(await stream.ResponseStream.MoveNext(Ct));
        Assert.Equal(WalletState.ServerActive, stream.ResponseStream.Current.State);
        var unauthorized = await Assert.ThrowsAsync<RpcException>(() => none.VersionerClient.GetVersionAsync(new VersionRequest(), cancellationToken: Ct).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, unauthorized.StatusCode);
        using var reader = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);
        var version = await reader.VersionerClient.GetVersionAsync(new VersionRequest(), cancellationToken: Ct);
        Assert.Equal(21U, version.AppMinor);
        Assert.Equal(4U, version.AppPatch);
        Assert.All(new[] { "signrpc", "walletrpc", "chainrpc", "invoicesrpc" }, tag => Assert.Contains(tag, version.BuildTags));
        Assert.Contains("nlightning", version.Version_);
    }
}