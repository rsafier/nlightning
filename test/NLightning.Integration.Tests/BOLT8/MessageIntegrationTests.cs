namespace NLightning.Integration.Tests.BOLT8;

using Infrastructure.Protocol.Constants;
using Vectors;

public class MessageIntegrationTests
{
    [Fact]
    public void Given_TwoParties_When_MessageIsSent_Then_MessageIsReceived()
    {
        // Arrange
        var initializedParties = new InitializedPartiesVector();

        try
        {
            // Make sure keys match
            Assert.Equal(((Span<byte>)ValidMessagesVector.InitiatorSk).ToArray(),
                         ((Span<byte>)initializedParties.InitiatorSk).ToArray());
            Assert.Equal(((Span<byte>)ValidMessagesVector.InitiatorRk).ToArray(),
                         ((Span<byte>)initializedParties.InitiatorRk).ToArray());

            var message = "hello"u8.ToArray();
            var messageBuffer = new byte[ProtocolConstants.MaxMessageLength];
            var receivedMessageBuffer = new byte[ProtocolConstants.MaxMessageLength];

            for (var i = 0; i < 1002; i++)
            {
                // Act
                var messageSize = initializedParties.InitiatorTransport.WriteMessage(message, messageBuffer);
                var receivedMessageLength =
                    initializedParties.ResponderTransport.ReadMessageLength(messageBuffer.AsSpan(0, 18));

                Assert.Equal(18 + receivedMessageLength, messageSize);

                var receivedMessageSize =
                    initializedParties.ResponderTransport.ReadMessagePayload(
                        messageBuffer.AsSpan(18, receivedMessageLength), receivedMessageBuffer);

                // Assert
                Assert.Equal(message, receivedMessageBuffer[..receivedMessageSize]);

                switch (i)
                {
                    case 0:
                        Assert.Equal(ValidMessagesVector.Message0, messageBuffer[..messageSize]);
                        break;
                    case 1:
                        Assert.Equal(ValidMessagesVector.Message1, messageBuffer[..messageSize]);
                        break;
                    case 500:
                        Assert.Equal(ValidMessagesVector.Message500, messageBuffer[..messageSize]);
                        break;
                    case 501:
                        Assert.Equal(ValidMessagesVector.Message501, messageBuffer[..messageSize]);
                        break;
                    case 1000:
                        Assert.Equal(ValidMessagesVector.Message1000, messageBuffer[..messageSize]);
                        break;
                    case 1001:
                        Assert.Equal(ValidMessagesVector.Message1001, messageBuffer[..messageSize]);
                        break;
                }
            }
        }
        finally
        {
            initializedParties.InitiatorTransport.Dispose();
            initializedParties.ResponderTransport.Dispose();
        }
    }

    [Fact]
    public void Given_MaximumSizeMessage_When_SentAndReceived_Then_MessageRoundTrips()
    {
        // Arrange - BOLT 8 caps the plaintext (not the ciphertext) at 65535 bytes
        var initializedParties = new InitializedPartiesVector();

        try
        {
            var message = Enumerable.Range(0, ProtocolConstants.MaxMessageLength).Select(i => (byte)i).ToArray();
            var messageBuffer = new byte[ProtocolConstants.MaxEncryptedPacketLength];
            var receivedMessageBuffer = new byte[ProtocolConstants.MaxEncryptedMessageLength];

            // Act
            var messageSize = initializedParties.InitiatorTransport.WriteMessage(message, messageBuffer);
            var receivedMessageLength =
                initializedParties.ResponderTransport.ReadMessageLength(messageBuffer.AsSpan(0, 18));
            var receivedMessageSize =
                initializedParties.ResponderTransport.ReadMessagePayload(
                    messageBuffer.AsSpan(18, receivedMessageLength), receivedMessageBuffer);

            // Assert
            Assert.Equal(2 + 16 + 65535 + 16, messageSize);
            Assert.Equal(65535 + 16, receivedMessageLength);
            Assert.Equal(message, receivedMessageBuffer[..receivedMessageSize]);
        }
        finally
        {
            initializedParties.InitiatorTransport.Dispose();
            initializedParties.ResponderTransport.Dispose();
        }
    }

    [Fact]
    public void Given_OversizedMessage_When_Writing_Then_ThrowsWithoutConsumingANonce()
    {
        // Arrange
        var initializedParties = new InitializedPartiesVector();

        try
        {
            var oversized = new byte[ProtocolConstants.MaxMessageLength + 1];
            var message = "hello"u8.ToArray();
            var messageBuffer = new byte[ProtocolConstants.MaxEncryptedPacketLength + 1];
            var receivedMessageBuffer = new byte[ProtocolConstants.MaxEncryptedMessageLength];

            // Act
            Assert.Throws<ArgumentException>(() =>
                                                 initializedParties.InitiatorTransport.WriteMessage(
                                                     oversized, messageBuffer));
            var messageSize = initializedParties.InitiatorTransport.WriteMessage(message, messageBuffer);
            var receivedMessageLength =
                initializedParties.ResponderTransport.ReadMessageLength(messageBuffer.AsSpan(0, 18));
            var receivedMessageSize =
                initializedParties.ResponderTransport.ReadMessagePayload(
                    messageBuffer.AsSpan(18, receivedMessageLength), receivedMessageBuffer);

            // Assert - the first valid message is still the spec's message 0
            Assert.Equal(ValidMessagesVector.Message0, messageBuffer[..messageSize]);
            Assert.Equal(message, receivedMessageBuffer[..receivedMessageSize]);
        }
        finally
        {
            initializedParties.InitiatorTransport.Dispose();
            initializedParties.ResponderTransport.Dispose();
        }
    }
}