using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.LiquidityAds;
using Domain.LiquidityAds.Models;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// The seller's <c>will_fund</c> signature (NL-771): the node key's RFC 6979 ECDSA signature of
/// <see cref="LiquidityAdsRules.SignedData"/>, byte-exact against Eclair 0.14.3's vectors.
/// </summary>
public class LocalLightningSignerLiquidityAdsTests
{
    private static readonly byte[] s_nodeKey = Convert.FromHexString(LiquidityAdsEclairVectors.NodeKey);
    private readonly Mock<ISecureKeyManager> _keyManager = new();
    private readonly CompactPubKey _nodeId = Convert.FromHexString(LiquidityAdsEclairVectors.NodeId);

    public LocalLightningSignerLiquidityAdsTests()
    {
        _keyManager.Setup(x => x.GetNodeKeyPair()).Returns(() => new CryptoKeyPair(s_nodeKey.ToArray(), _nodeId));
    }

    [Fact]
    public void Given_EclairsSpecVector_When_TheSellerSigns_Then_TheSignatureEqualsEclairs()
    {
        // Arrange: LiquidityAdsSpec's rate and funding script
        var rate = new FundingRate(100_000, 1_000_000, 500, 100, 10, 1_000);
        var script = Convert.FromHexString(LiquidityAdsEclairVectors.SpecFundingScript);
        var signer = CreateSigner();

        // Act
        var signature = signer.SignNodeMessage(LiquidityAdsRules.SignedData(rate, script));

        // Assert
        Assert.Equal(LiquidityAdsEclairVectors.SpecSignature, Convert.ToHexStringLower(signature.Value));
        Assert.True(signer.VerifyNodeMessage(LiquidityAdsRules.SignedData(rate, script), signature, _nodeId));
    }

    [Fact]
    public void Given_EclairsAcceptChannel2Vector_When_TheBuyerChecksIt_Then_TheSignatureVerifiesAndWeSignTheSame()
    {
        // Arrange: the 2-of-2 P2WSH of the vector's open and accept funding keys (publicKey(1) twice in Eclair's test)
        LiquidityAdsCodec.TryDecodeWillFund(Convert.FromHexString(LiquidityAdsEclairVectors.AcceptWillFund),
                                            out var willFund);
        LiquidityAdsCodec.TryDecodeRequestFunding(Convert.FromHexString(LiquidityAdsEclairVectors.OpenRequest),
                                                  out var request);
        var signer = CreateSigner();

        // Act
        var refusal = LiquidityAdsRules.ValidateWillFund(request!, willFund, willFund!.FundingScript, 750_000, 5_000,
                                                         true,
                                                         (hash, sig) => signer.VerifyNodeMessage(hash, sig, _nodeId),
                                                         null, out var fees);
        var ours = signer.SignNodeMessage(LiquidityAdsRules.SignedData(request!.Rate, willFund.FundingScript));

        // Assert
        Assert.Equal(Domain.LiquidityAds.Enums.LiquidityAdsRefusal.None, refusal);
        Assert.Equal(willFund.Signature.Value, ours.Value);
        // 5000 sat/kw x 1100 / 1000 + 1500 + 750,000 x 75 / 10,000
        Assert.Equal(5_500UL + 1_500 + 5_625, fees.TotalSat);
        var fundingKey = new PubKey("031b84c5567b126440995d3ed5aaba0565d71e1834604819ff9c17f5e9d5dd078f");
        var redeem = PayToMultiSigTemplate.Instance.GenerateScriptPubKey(2, fundingKey, fundingKey);
        Assert.Equal(redeem.WitHash.ScriptPubKey.ToBytes(), willFund.FundingScript);
    }

    private LocalLightningSigner CreateSigner() =>
        new(new Mock<IFundingOutputBuilder>().Object,
            new Mock<IKeyDerivationService>().Object, new Mock<ILogger<LocalLightningSigner>>().Object,
            new NodeOptions(), _keyManager.Object, new Mock<IUtxoMemoryRepository>().Object);
}