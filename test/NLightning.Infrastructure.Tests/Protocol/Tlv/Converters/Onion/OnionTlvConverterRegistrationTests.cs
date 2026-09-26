namespace NLightning.Infrastructure.Tests.Protocol.Tlv.Converters.Onion;

using Domain.Protocol.Onion.Tlv;
using Infrastructure.Protocol.Factories;

public class OnionTlvConverterRegistrationTests
{
    [Fact]
    public void Given_TlvConverterFactory_When_GettingOnionConverters_Then_AllAreRegistered()
    {
        // Arrange
        var factory = new TlvConverterFactory();

        // Act & Assert
        Assert.NotNull(factory.GetConverter<AmtToForwardTlv>());
        Assert.NotNull(factory.GetConverter<OutgoingCltvValueTlv>());
        Assert.NotNull(factory.GetConverter<OnionShortChannelIdTlv>());
        Assert.NotNull(factory.GetConverter<PaymentDataTlv>());
        Assert.NotNull(factory.GetConverter<EncryptedRecipientDataTlv>());
        Assert.NotNull(factory.GetConverter<CurrentPathKeyTlv>());
        Assert.NotNull(factory.GetConverter<PaymentMetadataTlv>());
        Assert.NotNull(factory.GetConverter<TotalAmountMsatTlv>());
    }
}