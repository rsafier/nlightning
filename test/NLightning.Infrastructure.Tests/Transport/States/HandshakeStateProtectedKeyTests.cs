using System.Text;
using NLightning.Tests.Utils.Mocks;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Tests.Transport.States;

using Domain.Crypto.Constants;
using Domain.Transport;
using Infrastructure.Protocol.Constants;
using Infrastructure.Transport.Handshake.States;

public class HandshakeStateProtectedKeyTests
{
    // Distinct valid keys for the two sides of the local handshake
    private static readonly byte[] s_initiatorStaticPrivateKey =
        InitiatorValidKeysVector.LocalStaticPrivateKey;

    private static readonly byte[] s_responderStaticPrivateKey =
        InitiatorValidKeysVector.EphemeralPrivateKey;

    private static byte[] PublicKeyOf(byte[] privateKey)
    {
        return new FakeFixedKeyDh(privateKey).GenerateKeyPair(privateKey).CompactPubKey;
    }

    [Fact]
    public void Given_ProtectedResponder_When_HandshakeWithClassicInitiator_Then_TransportsAgree()
    {
        // Arrange
        var counter = new[] { 0 };
        var initiatorDh = new FakeFixedKeyDh(s_initiatorStaticPrivateKey);
        var responderDh = new FakeFixedKeyDh(s_responderStaticPrivateKey);
        var initiator = new HandshakeState(true, s_initiatorStaticPrivateKey, PublicKeyOf(s_responderStaticPrivateKey),
                                           initiatorDh);
        var responder = new HandshakeState(false, PublicKeyOf(s_responderStaticPrivateKey),
                                           PublicKeyOf(s_responderStaticPrivateKey), responderDh,
                                           (publicKey, sharedSecret) =>
                                           {
                                               counter[0]++;
                                               responderDh.SecP256K1Dh(s_responderStaticPrivateKey, publicKey,
                                                                       sharedSecret);
                                           });

        // Act
        var (initiatorTransport, responderTransport) = RunHandshake(initiator, responder);
        try
        {
            // Assert
            ExchangeAndAssert(initiatorTransport, responderTransport);
            Assert.Equal(1, counter[0]);
        }
        finally
        {
            DisposeAndSettle(initiatorTransport, responderTransport, initiator, responder);
        }
    }

    [Fact]
    public void Given_ProtectedInitiator_When_HandshakeWithClassicResponder_Then_TransportsAgree()
    {
        // Arrange
        var counter = new[] { 0 };
        var initiatorDh = new FakeFixedKeyDh(s_initiatorStaticPrivateKey);
        var responderDh = new FakeFixedKeyDh(s_responderStaticPrivateKey);
        var initiator = new HandshakeState(true, PublicKeyOf(s_initiatorStaticPrivateKey),
                                           PublicKeyOf(s_responderStaticPrivateKey), initiatorDh,
                                           (publicKey, sharedSecret) =>
                                           {
                                               counter[0]++;
                                               initiatorDh.SecP256K1Dh(s_initiatorStaticPrivateKey, publicKey,
                                                                       sharedSecret);
                                           });
        var responder = new HandshakeState(false, s_responderStaticPrivateKey,
                                           PublicKeyOf(s_responderStaticPrivateKey), responderDh);

        // Act
        var (initiatorTransport, responderTransport) = RunHandshake(initiator, responder);
        try
        {
            // Assert
            ExchangeAndAssert(initiatorTransport, responderTransport);
            Assert.Equal(1, counter[0]);
        }
        finally
        {
            DisposeAndSettle(initiatorTransport, responderTransport, initiator, responder);
        }
    }

    [Fact]
    public void Given_InvalidLocalStaticPublicKey_When_CreatingProtectedState_Then_ThrowsArgumentException()
    {
        // Arrange
        var dh = new FakeFixedKeyDh(s_initiatorStaticPrivateKey);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => new HandshakeState(true, new byte[32],
                                                                  PublicKeyOf(s_responderStaticPrivateKey), dh,
                                                                  (_, _) => { }));
    }

    private static (ITransport, ITransport) RunHandshake(HandshakeState initiator, HandshakeState responder)
    {
        var initiatorBuffer = new byte[ProtocolConstants.MaxMessageLength];
        var responderBuffer = new byte[ProtocolConstants.MaxMessageLength];
        var emptyPayload = Encoding.ASCII.GetBytes(string.Empty);

        // Act one: the initiator's ephemeral
        var (actOneSize, _, _) = initiator.WriteMessage(emptyPayload, initiatorBuffer);
        var (_, _, _) = responder.ReadMessage(initiatorBuffer.AsSpan(0, actOneSize).ToArray(), responderBuffer);

        // Act two: the responder's ephemeral and its Es ECDH (the responder's protected static key)
        var (actTwoSize, _, _) = responder.WriteMessage(emptyPayload, responderBuffer);
        var (_, _, _) = initiator.ReadMessage(responderBuffer.AsSpan(0, actTwoSize).ToArray(), initiatorBuffer);

        // Act three: the initiator's static and its Se ECDH (the initiator's protected static key)
        var (actThreeSize, initiatorHash, initiatorTransport) = initiator.WriteMessage(emptyPayload, initiatorBuffer);
        var (_, responderHash, responderTransport) =
            responder.ReadMessage(initiatorBuffer.AsSpan(0, actThreeSize).ToArray(), responderBuffer);

        // Assert: the same handshake hash means the same chaining key, so the delegate produced the same static ECDH
        Assert.NotNull(initiatorTransport);
        Assert.NotNull(responderTransport);
        Assert.NotNull(initiatorHash);
        Assert.NotNull(responderHash);
        Assert.Equal(initiatorHash, responderHash);

        return (initiatorTransport!, responderTransport!);
    }

    /// <summary>
    /// Disposes the handshake's transports and states, so no locked-memory object is left for the finalizer thread
    /// to settle while other test collections run.
    /// </summary>
    private static void DisposeAndSettle(ITransport initiatorTransport, ITransport responderTransport,
                                         HandshakeState initiator, HandshakeState responder)
    {
        initiatorTransport.Dispose();
        responderTransport.Dispose();
        initiator.Dispose();
        responder.Dispose();
    }

    private static void ExchangeAndAssert(ITransport sender, ITransport receiver)
    {
        var payload = "nltg handshake transport"u8.ToArray();
        var wire = new byte[ProtocolConstants.MaxEncryptedPacketLength];

        var size = sender.WriteMessage(payload, wire);
        var bodyLength = receiver.ReadMessageLength(wire.AsSpan(0, ProtocolConstants.MessageHeaderSize));
        var received = new byte[bodyLength - CryptoConstants.Chacha20Poly1305TagLen];
        _ = receiver.ReadMessagePayload(wire.AsSpan(ProtocolConstants.MessageHeaderSize, bodyLength), received);

        Assert.Equal(payload, received);

        // The reverse direction shares the same keying material
        var wire2 = new byte[ProtocolConstants.MaxEncryptedPacketLength];
        var size2 = receiver.WriteMessage(payload, wire2);
        var bodyLength2 = sender.ReadMessageLength(wire2.AsSpan(0, ProtocolConstants.MessageHeaderSize));

        Assert.Equal(payload.Length + CryptoConstants.Chacha20Poly1305TagLen, bodyLength2);
    }
}