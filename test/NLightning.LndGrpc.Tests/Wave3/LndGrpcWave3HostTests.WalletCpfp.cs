using Moq;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Bitcoin.ValueObjects;
using LndGrpc.Macaroons;
using Testing.Lnd.Walletrpc;

public sealed partial class LndGrpcWave3HostTests
{
    [Fact]
    public async Task Given_WalletMempoolOutpoint_When_BumpFee_Then_ExactOutpointPackageRateAndBudgetReachWallet()
    {
        // Arrange
        var parent = new TxId(Enumerable.Repeat((byte)0xD8, 32).ToArray());
        var child = new TxId(Enumerable.Repeat((byte)0xD9, 32).ToArray());
        _psbt.Setup(p => p.BumpOutputAsync(parent, 3, 2_500, 20_000, It.IsAny<CancellationToken>())).ReturnsAsync(child);
        using var connection = Connect(LndMacaroonFiles.AdminFileName);
        // Act
        var result = await connection.WalletKitClient.BumpFeeAsync(new BumpFeeRequest
        {
            Outpoint = new Testing.Lnd.Lnrpc.OutPoint { TxidStr = parent.ToString(), OutputIndex = 3 },
            SatPerVbyte = 10,
            Budget = 20_000,
            Immediate = true
        }, cancellationToken: Ct);
        // Assert
        Assert.Equal("Successfully published wallet CPFP transaction", result.Status);
        _psbt.Verify(p => p.BumpOutputAsync(parent, 3, 2_500, 20_000, It.IsAny<CancellationToken>()), Times.Once);
    }
}