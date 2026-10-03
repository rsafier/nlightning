namespace NLightning.Infrastructure.Bitcoin.Tests.Taproot;

using Domain.Bitcoin.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// The counter-based verification nonces of simple taproot channels (D-T4, NL-904 item 3): derived from the channel's
/// shachain and bound to the funding txid, so nothing is stored and a splice never reuses one.
/// </summary>
public class SimpleTaprootVerificationNonceTests
{
    private static readonly TxId s_otherFundingTxId = Enumerable.Repeat((byte)0x55, 32).ToArray();

    [Fact]
    public void Given_TheSameInputs_When_DerivingTwice_Then_TheNonceIsTheSame()
    {
        // Arrange
        var kit = new TaprootSignerKit();

        // Act
        var first = kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 7);
        var second = kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 7);
        var byChannel = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, kit.FundingTxId, 7);
        var current = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 7);

        // Assert: by key index and by channel id, with or without naming the current funding
        Assert.Equal(first, second);
        Assert.Equal(first, byChannel);
        Assert.Equal(first, current);
    }

    [Fact]
    public void Given_AnotherFundingTxIdOrNumber_When_Deriving_Then_TheNonceDiffers()
    {
        // Arrange
        var kit = new TaprootSignerKit();
        var nonce = kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 7);

        // Act
        var otherFunding = kit.Alice.GetLocalVerificationNonce(0u, s_otherFundingTxId, 7);
        var otherNumber = kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 8);
        var otherNode = kit.Bob.GetLocalVerificationNonce(0u, kit.FundingTxId, 7);

        // Assert
        Assert.NotEqual(nonce, otherFunding);
        Assert.NotEqual(nonce, otherNumber);
        Assert.NotEqual(nonce, otherNode);
    }

    [Fact]
    public void Given_CommitmentZero_When_Deriving_Then_TheFundingTxIdIsIgnored()
    {
        // Arrange: commitment 0's nonce goes out in open_channel/accept_channel, before the funding txid exists
        var kit = new TaprootSignerKit();

        // Act
        var withoutTxId = kit.Alice.GetLocalVerificationNonce(0u, null, 0);
        var withTxId = kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 0);
        var otherTxId = kit.Alice.GetLocalVerificationNonce(0u, s_otherFundingTxId, 0);
        var byChannel = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 0);

        // Assert
        Assert.Equal(withoutTxId, withTxId);
        Assert.Equal(withoutTxId, otherTxId);
        Assert.Equal(withoutTxId, byChannel);
        Assert.NotEqual(withoutTxId, kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 1));
    }

    [Fact]
    public void Given_ANonZeroNumberWithoutFundingTxId_When_DerivingByKeyIndex_Then_ItThrows()
    {
        // Arrange
        var kit = new TaprootSignerKit();

        // Act / Assert: only commitment 0 has no funding context
        Assert.Throws<SignerException>(() => kit.Alice.GetLocalVerificationNonce(0u, null, 1));
    }

    [Fact]
    public void Given_ANonTaprootChannel_When_DerivingByChannelId_Then_ItThrows()
    {
        // Arrange
        var kit = new TaprootSignerKit(isSimpleTaproot: false);

        // Act / Assert
        Assert.Throws<SignerException>(() => kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId,
                                                                                  null, 1));
    }

    [Fact]
    public void Given_APendingSpliceFunding_When_Deriving_Then_ItsNonceIsBoundToItsTxIdAndKey()
    {
        // Arrange
        var kit = new TaprootSignerKit(aliceLocalNumber: 3, bobLocalNumber: 3);
        var (spliceTxId, _) = kit.RegisterPendingFunding();

        // Act
        var current = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 4);
        var splice = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, spliceTxId, 4);

        // Assert: the same number on two fundings never shares a nonce
        Assert.NotEqual(current, splice);
    }
}