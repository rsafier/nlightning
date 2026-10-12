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
    public void Given_AV1OpensCommitmentZero_When_Deriving_Then_ItUsesTheContextWithoutTxId()
    {
        // Arrange: a v1 open's commitment 0 nonce goes out in open_channel/accept_channel, before the txid exists
        var kit = new TaprootSignerKit();

        // Act
        var withoutTxId = kit.Alice.GetLocalVerificationNonce(0u, null, 0);
        var byChannel = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 0);
        var namingTheFunding = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, kit.FundingTxId, 0);

        // Assert: the channel id overload of a v1 channel finds the nonce sent in open_channel
        Assert.Equal(withoutTxId, byChannel);
        Assert.Equal(withoutTxId, namingTheFunding);
        Assert.NotEqual(withoutTxId, kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 1));
    }

    [Fact]
    public void Given_ADualFundedOpensAttempts_When_DerivingCommitmentZero_Then_EachAttemptHasItsOwnNonce()
    {
        // Arrange: every RBF attempt of a dual-funded open is another funding on the original keys (NL-972)
        var kit = new TaprootSignerKit(isDualFunded: true);
        var (otherTxId, _) = kit.RegisterPendingFunding(keyIndex: 0);

        // Act
        var first = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 0);
        var other = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, otherTxId, 0);

        // Assert: bound to each attempt's txid, and the same as the key index overload given that txid
        Assert.NotEqual(first, other);
        Assert.Equal(first, kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 0));
        Assert.Equal(other, kit.Alice.GetLocalVerificationNonce(0u, otherTxId, 0));
        Assert.NotEqual(first, kit.Alice.GetLocalVerificationNonce(0u, null, 0));
    }

    [Fact]
    public void Given_ADualFundedChannelsNonZeroNumber_When_Deriving_Then_TheKeyIndexAndChannelOverloadsAgree()
    {
        // Arrange: only commitment 0 differs between the v1 and the dual-funded rule
        var kit = new TaprootSignerKit(isDualFunded: true);

        // Act
        var byKeyIndex = kit.Alice.GetLocalVerificationNonce(0u, kit.FundingTxId, 1);
        var byChannel = kit.Alice.GetLocalVerificationNonce(TaprootSignerKit.ChannelId, null, 1);

        // Assert
        Assert.Equal(byKeyIndex, byChannel);
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