using System.Reflection;
using NLightning.Tests.Utils.Mocks;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Tests.Transport.States;

using Domain.Crypto.ValueObjects;
using Infrastructure.Crypto.Interfaces;
using Infrastructure.Protocol.Constants;
using Infrastructure.Transport.Handshake.States;

/// <summary>
/// NL-560: the handshake state's finalizer disposed its SymmetricState from the finalizer thread, after the
/// <c>Sha256</c> objects under it could already have freed their native state in their own finalizers, and the second
/// <c>sodium_free</c> crashed the process (SIGSEGV, exit 139) for every undisposed state.
/// </summary>
public class HandshakeStateDisposeTests
{
    // The BOLT 8 responder's static and ephemeral keys (the initiator's remote static is the public key of the first)
    private static readonly byte[] s_responderStaticPrivateKey =
        Convert.FromHexString("2121212121212121212121212121212121212121212121212121212121212121");

    private static readonly byte[] s_responderEphemeralPrivateKey =
        Convert.FromHexString("2222222222222222222222222222222222222222222222222222222222222222");

    [Fact]
    public void Given_HandshakeState_When_InspectingIt_Then_ItHasNoFinalizer()
    {
        // Arrange
        var finalize = typeof(HandshakeState).GetMethod("Finalize",
                                                        BindingFlags.Instance | BindingFlags.NonPublic
                                                                              | BindingFlags.DeclaredOnly);

        // Act & Assert
        Assert.Null(finalize);
    }

    [Fact]
    public void Given_ADisposedState_When_DisposedAgain_Then_NothingHappensAndItStaysDisposed()
    {
        // Arrange
        var state = CreateInitiator();
        state.Dispose();

        // Act
        state.Dispose();

        // Assert
        Assert.Throws<ObjectDisposedException>(() => state.WriteMessage(ProtocolConstants.EmptyMessage,
                                                                         new byte[ProtocolConstants
                                                                            .MaxMessageLength]));
    }

    [Fact]
    public void Given_AStateDisposedFromManyThreads_When_Disposing_Then_NoneThrows()
    {
        // Arrange
        var state = CreateInitiator();

        // Act
        Parallel.For(0, 16, _ => state.Dispose());

        // Assert
        Assert.Throws<ObjectDisposedException>(() => state.ReadMessage(new byte[50], new byte[50]));
    }

    [Fact]
    public void Given_ACompletedHandshake_When_TheStatesAreDisposed_Then_TheSplitStateIsNotFreedTwice()
    {
        // Arrange: the last act's Split already disposed the symmetric state
        var initiator = CreateInitiator();
        var responder = new HandshakeState(false, s_responderStaticPrivateKey,
                                           InitiatorValidKeysVector.RemoteStaticPublicKey,
                                           new FakeFixedKeyDh(s_responderEphemeralPrivateKey));
        var initiatorBuffer = new byte[ProtocolConstants.MaxMessageLength];
        var responderBuffer = new byte[ProtocolConstants.MaxMessageLength];
        var (actOne, _, _) = initiator.WriteMessage(ProtocolConstants.EmptyMessage, initiatorBuffer);
        _ = responder.ReadMessage(initiatorBuffer.AsSpan(0, actOne), responderBuffer);
        var (actTwo, _, _) = responder.WriteMessage(ProtocolConstants.EmptyMessage, responderBuffer);
        _ = initiator.ReadMessage(responderBuffer.AsSpan(0, actTwo), initiatorBuffer);
        var (actThree, _, initiatorTransport) = initiator.WriteMessage(ProtocolConstants.EmptyMessage,
                                                                        initiatorBuffer);
        var (_, _, responderTransport) = responder.ReadMessage(initiatorBuffer.AsSpan(0, actThree),
                                                               responderBuffer);

        // Act
        initiator.Dispose();
        responder.Dispose();
        initiator.Dispose();

        // Assert
        Assert.NotNull(initiatorTransport);
        Assert.NotNull(responderTransport);
        initiatorTransport.Dispose();
        responderTransport.Dispose();
    }

    [Fact]
    public void Given_AKeyPairThatFails_When_Constructing_Then_TheExceptionReachesTheCaller()
    {
        // Arrange: the constructor frees its symmetric state before it rethrows
        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => CreateInitiator(new FailingKeyPairDh()));
        Assert.Equal("bad key", exception.Message);
    }

    [Fact]
    public void Given_UndisposedStates_When_TheyAreFinalized_Then_TheProcessSurvives()
    {
        // Arrange: before NL-560 these states' finalizers double-freed their hash states and the test host died
        // with SIGSEGV; half-used states and constructors that throw after the symmetric state was built included
        var failingDh = new FailingKeyPairDh();

        for (var round = 0; round < 5; round++)
        {
            // Act
            AbandonStates(failingDh);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        // Assert: reaching this line is the proof; a fresh state still works
        using var state = CreateInitiator();
        var (size, _, _) = state.WriteMessage(ProtocolConstants.EmptyMessage,
                                              new byte[ProtocolConstants.MaxMessageLength]);
        Assert.Equal(50, size);
    }

    private static void AbandonStates(IEcdh failingDh)
    {
        for (var i = 0; i < 500; i++)
        {
            var state = CreateInitiator();
            if (i % 2 == 0)
                _ = state.WriteMessage(ProtocolConstants.EmptyMessage, new byte[ProtocolConstants.MaxMessageLength]);

            try
            {
                _ = CreateInitiator(failingDh);
            }
            catch (InvalidOperationException)
            {
                // Expected: the constructor refused the key pair
            }
        }
    }

    private static HandshakeState CreateInitiator(IEcdh? dh = null)
    {
        return new HandshakeState(true, InitiatorValidKeysVector.LocalStaticPrivateKey,
                                  InitiatorValidKeysVector.RemoteStaticPublicKey,
                                  dh ?? new FakeFixedKeyDh(InitiatorValidKeysVector.EphemeralPrivateKey));
    }

    /// <summary>
    /// Refuses the local static key, so the constructor throws after it built its symmetric state.
    /// </summary>
    private sealed class FailingKeyPairDh : IEcdh
    {
        public void SecP256K1Dh(PrivKey k, ReadOnlySpan<byte> rk, Span<byte> sharedKey)
        {
            throw new InvalidOperationException("bad key");
        }

        public CryptoKeyPair GenerateKeyPair()
        {
            throw new InvalidOperationException("bad key");
        }

        public CryptoKeyPair GenerateKeyPair(ReadOnlySpan<byte> privateKey)
        {
            throw new InvalidOperationException("bad key");
        }
    }
}