using System.Reflection;
using System.Text;
using NLightning.Bolts.BOLT8.Noise.Primitives;
using NLightning.Bolts.BOLT8.Noise.States;
using NLightning.Bolts.Tests.BOLT8.Noise.Utils;

namespace NLightning.Bolts.Tests.BOLT8.Noise.IntegrationTests;

public partial class EndToEntIntegrationTests
{
    private static readonly InitiatorValidKeysUtil _initiatorValidKeys = new();
    private static readonly ResponderValidKeysUtil _responderValidKeys = new();

    [Fact]
    public void Given_TwoParties_When_HandshakeEnds_Then_KeysMatch()
    {
        // Arrange
        var protocol = new Protocol();
        var initiator = protocol.Create(true, _initiatorValidKeys.LocalStaticPrivateKey, _responderValidKeys.LocalStaticPublicKey);
        var responder = protocol.Create(false, _responderValidKeys.LocalStaticPrivateKey, _responderValidKeys.LocalStaticPublicKey);

        var initiatorMessageBuffer = new byte[Protocol.MAX_MESSAGE_LENGTH];
        Transport? initiatorTransport;
        byte[]? initiatorHandshakeHash;
        Span<byte> initiatorMessage;
        int initiatorMessageSize;

        var responderMessageBuffer = new byte[Protocol.MAX_MESSAGE_LENGTH];
        Transport? responderTransport;
        byte[]? responderHandshakeHash;
        Span<byte> responderMessage;
        int responderMessageSize;

        // Act
        // - Initiator writes act one
        (initiatorMessageSize, initiatorHandshakeHash, initiatorTransport) = initiator.WriteMessage(Encoding.ASCII.GetBytes(string.Empty), initiatorMessageBuffer);
        initiatorMessage = initiatorMessageBuffer.AsSpan(0, initiatorMessageSize);

        // - Responder reads act one
        (responderMessageSize, responderHandshakeHash, responderTransport) = responder.ReadMessage(initiatorMessage.ToArray(), responderMessageBuffer);
        responderMessage = responderMessageBuffer.AsSpan(0, responderMessageSize);

        // - Responder writes act two
        (responderMessageSize, responderHandshakeHash, responderTransport) = responder.WriteMessage(responderMessage.ToArray(), responderMessageBuffer);
        responderMessage = responderMessageBuffer.AsSpan(0, responderMessageSize);

        // - Initiator reads act two
        (initiatorMessageSize, initiatorHandshakeHash, initiatorTransport) = initiator.ReadMessage(responderMessage.ToArray(), initiatorMessageBuffer);
        initiatorMessage = initiatorMessageBuffer.AsSpan(0, initiatorMessageSize);

        // - Initiator writes act three
        (initiatorMessageSize, initiatorHandshakeHash, initiatorTransport) = initiator.WriteMessage(initiatorMessage.ToArray(), initiatorMessageBuffer);
        initiatorMessage = initiatorMessageBuffer.AsSpan(0, initiatorMessageSize);

        // - Responder reads act three
        (responderMessageSize, responderHandshakeHash, responderTransport) = responder.ReadMessage(initiatorMessage.ToArray(), responderMessageBuffer);
        _ = responderMessageBuffer.AsSpan(0, responderMessageSize);

        // Assert
        Assert.NotNull(initiatorHandshakeHash);
        Assert.NotNull(responderHandshakeHash);
        Assert.NotNull(initiatorTransport);
        Assert.NotNull(responderTransport);

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        // Get initiator sk
        var c1 = ((CipherState?)initiatorTransport.GetType().GetField("_sendingKey", flags)?.GetValue(initiatorTransport) ?? throw new MissingFieldException("_sendingKey")) ?? throw new ArgumentNullException("_sendingKey");
        var initiatorSk = ((byte[]?)c1.GetType().GetField("_k", flags)?.GetValue(c1) ?? throw new MissingFieldException("_sendingKey._k")) ?? throw new ArgumentNullException("_sendingKey._k");
        // Get initiator rk
        var c2 = ((CipherState?)initiatorTransport.GetType().GetField("_receivingKey", flags)?.GetValue(initiatorTransport) ?? throw new MissingFieldException("_receivingKey")) ?? throw new ArgumentNullException("_receivingKey");
        var initiatorRk = ((byte[]?)c2.GetType().GetField("_k", flags)?.GetValue(c2) ?? throw new MissingFieldException("_receivingKey._k")) ?? throw new ArgumentNullException("_receivingKey._k");
        // Get responder sk
        c1 = ((CipherState?)responderTransport.GetType().GetField("_sendingKey", flags)?.GetValue(responderTransport) ?? throw new MissingFieldException("_sendingKey")) ?? throw new ArgumentNullException("_sendingKey");
        var responderSk = ((byte[]?)c1.GetType().GetField("_k", flags)?.GetValue(c1) ?? throw new MissingFieldException("_sendingKey._k")) ?? throw new ArgumentNullException("_sendingKey._k");
        // Get responder rk
        c2 = ((CipherState?)responderTransport.GetType().GetField("_receivingKey", flags)?.GetValue(responderTransport) ?? throw new MissingFieldException("_receivingKey")) ?? throw new ArgumentNullException("_receivingKey");
        var responderRk = ((byte[]?)c2.GetType().GetField("_k", flags)?.GetValue(c2) ?? throw new MissingFieldException("_receivingKey._k")) ?? throw new ArgumentNullException("_receivingKey._k");

        Assert.Equal(initiatorHandshakeHash, responderHandshakeHash);
        Assert.Equal(initiatorSk, responderSk);
        Assert.Equal(responderRk, initiatorRk);
    }
}