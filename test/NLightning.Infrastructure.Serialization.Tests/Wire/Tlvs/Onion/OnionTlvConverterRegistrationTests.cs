namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs.Onion;

using Domain.Protocol.Onion.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

public class OnionTlvConverterRegistrationTests
{
    [Fact]
    public void Given_TlvConverterFactory_When_GettingOnionConverters_Then_AllAreRegistered()
    {
        // Arrange
        var factory = new WireRegistry();

        // Act & Assert
        Assert.NotNull(factory.GetTlvDefinition<AmtToForwardTlv>());
        Assert.NotNull(factory.GetTlvDefinition<OutgoingCltvValueTlv>());
        Assert.NotNull(factory.GetTlvDefinition<OnionShortChannelIdTlv>());
        Assert.NotNull(factory.GetTlvDefinition<PaymentDataTlv>());
        Assert.NotNull(factory.GetTlvDefinition<EncryptedRecipientDataTlv>());
        Assert.NotNull(factory.GetTlvDefinition<CurrentPathKeyTlv>());
        Assert.NotNull(factory.GetTlvDefinition<PaymentMetadataTlv>());
        Assert.NotNull(factory.GetTlvDefinition<TotalAmountMsatTlv>());
        Assert.NotNull(factory.GetTlvDefinition<OutgoingNodeIdTlv>());
        Assert.NotNull(factory.GetTlvDefinition<TrampolineOnionPacketTlv>());
        Assert.NotNull(factory.GetTlvDefinition<RecipientFeaturesTlv>());
        Assert.NotNull(factory.GetTlvDefinition<RecipientBlindedPathsTlv>());
    }
}