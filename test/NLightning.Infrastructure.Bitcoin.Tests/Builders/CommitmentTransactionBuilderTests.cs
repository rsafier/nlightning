using Microsoft.Extensions.Options;
using NBitcoin;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Protocol.Models;
using NLightning.Tests.Utils.Mocks;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Money;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Builders;

public class CommitmentTransactionBuilderTests
{
    [Fact]
    public void Given_ValidInput_When_Build_Then_ReturnsCorrectValues()
    {
        // Given
        var expectedTx = Bolt3AppendixCVectors.ExpectedCommitTx0;
        expectedTx.Inputs[0].WitScript = null;

        var sha256Mock = new Mock<FakeSha256>();
        sha256Mock.Setup(x => x.GetHashAndReset())
                  .Returns(Convert.FromHexString("C8BFEA84214B45899482A4BAD1D85C42130743ED78BA3711F5532BB038521914"));

        var nodeOptions = new NodeOptions();
        var builder = new CommitmentTransactionBuilder(new OptionsWrapper<NodeOptions>(nodeOptions));

        var commitmentNumber = new CommitmentNumber(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                    Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                    sha256Mock.Object);
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
        {
            TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
            Index = 0,
        };

        var localOutput = new ToLocalOutputInfo(Bolt3AppendixCVectors.ExpectedCommitTx0ToLocalAmount,
                                                Bolt3AppendixCVectors.NodeADelayedPubkey.ToBytes(),
                                                Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes(),
                                                Bolt3AppendixCVectors.LocalDelay);
        var remoteOutput = new ToRemoteOutputInfo(Bolt3AppendixCVectors.ExpectedCommitTx0ToRemoteAmount,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());
        var commitmentTransactionModel =
            new CommitmentTransactionModel(commitmentNumber, Bolt3AppendixCVectors.CommitmentNumber,
                                           LightningMoney.Satoshis(15000), fundingOutputInfo, null, null, localOutput,
                                           remoteOutput);

        // When
        var unsignedTx = builder.Build(commitmentTransactionModel);

        // Then
        Assert.NotNull(unsignedTx);
        Assert.Equal(expectedTx.ToBytes(), unsignedTx.RawTxBytes);
    }

    [Fact]
    public void Given_SameAmountAndHash_When_BuildWithOutputMap_Then_HtlcOrderByCltv()
    {
        // Given - Appendix C "2 offered having the same amount and preimage": HTLCs 5 (5000 sat, cltv 506) and
        // 6 (5000.001 sat, cltv 505) have identical offered scripts and amounts, so cltv_expiry decides: 6 before 5.
        var builder = new CommitmentTransactionBuilder(new OptionsWrapper<NodeOptions>(new NodeOptions()));
        var commitmentNumber = new CommitmentNumber(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                    Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                    new FakeSha256());
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
        {
            TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
            Index = 0,
        };
        var htlc5 = new Htlc(LightningMoney.MilliSatoshis(5_000_000), null!, HtlcDirection.Outgoing, 506, 5, 0,
                             Bolt3AppendixCVectors.Htlc5PaymentHash, HtlcState.Offered);
        var htlc6 = new Htlc(LightningMoney.MilliSatoshis(5_000_001), null!, HtlcDirection.Outgoing, 505, 6, 0,
                             Bolt3AppendixCVectors.Htlc6PaymentHash, HtlcState.Offered);
        var htlc1 = new Htlc(LightningMoney.MilliSatoshis(2_000_000), null!, HtlcDirection.Incoming, 501, 1, 0,
                             Bolt3AppendixCVectors.Htlc1PaymentHash, HtlcState.Offered);
        var localHtlcKey = Bolt3AppendixCVectors.NodeAHtlcPubkey.ToBytes();
        var remoteHtlcKey = Bolt3AppendixCVectors.NodeBHtlcPubkey.ToBytes();
        var revocationKey = Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes();
        var model = new CommitmentTransactionModel(
            commitmentNumber, Bolt3AppendixCVectors.CommitmentNumber, LightningMoney.Zero, fundingOutputInfo,
            offeredHtlcOutputs:
            [
                new OfferedHtlcOutputInfo(htlc5, localHtlcKey, remoteHtlcKey, revocationKey),
                new OfferedHtlcOutputInfo(htlc6, localHtlcKey, remoteHtlcKey, revocationKey)
            ],
            receivedHtlcOutputs: [new ReceivedHtlcOutputInfo(htlc1, localHtlcKey, remoteHtlcKey, revocationKey)]);

        // When
        var result = builder.BuildWithOutputMap(model);
        var plain = builder.Build(model);

        // Then - Appendix C: output #0 success for htlc #1, #1 timeout for htlc #6, #2 timeout for htlc #5
        Assert.Equal([(1UL, 0U), (6UL, 1U), (5UL, 2U)],
                     result.HtlcOutputsInTxOrder.Select(h => (h.Output.Htlc.Id, h.Vout)).ToArray());
        Assert.Equal(plain.RawTxBytes, result.Transaction.RawTxBytes);
        var tx = Transaction.Load(result.Transaction.RawTxBytes, Network.Main);
        Assert.Equal(tx.Outputs[1].ScriptPubKey, tx.Outputs[2].ScriptPubKey);
        Assert.Equal(tx.Outputs[1].Value, tx.Outputs[2].Value);
    }

    [Fact]
    public void Given_NoHtlcs_When_BuildWithOutputMap_Then_MapIsEmpty()
    {
        // Given
        var builder = new CommitmentTransactionBuilder(new OptionsWrapper<NodeOptions>(new NodeOptions()));
        var commitmentNumber = new CommitmentNumber(Bolt3AppendixCVectors.NodeAPaymentBasepoint.ToBytes(),
                                                    Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(),
                                                    new FakeSha256());
        var fundingOutputInfo = new FundingOutputInfo(Bolt3AppendixBVectors.FundingSatoshis,
                                                      Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                                      Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes())
        {
            TransactionId = Bolt3AppendixBVectors.ExpectedTxId.ToBytes(),
            Index = 0,
        };
        var remoteOutput = new ToRemoteOutputInfo(Bolt3AppendixCVectors.ExpectedCommitTx0ToRemoteAmount,
                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());
        var model = new CommitmentTransactionModel(commitmentNumber, Bolt3AppendixCVectors.CommitmentNumber,
                                                   LightningMoney.Zero, fundingOutputInfo, toRemoteOutput: remoteOutput);

        // When
        var result = builder.BuildWithOutputMap(model);

        // Then
        Assert.Empty(result.HtlcOutputsInTxOrder);
    }
}