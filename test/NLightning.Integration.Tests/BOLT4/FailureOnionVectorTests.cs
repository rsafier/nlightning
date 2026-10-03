using NLightning.Tests.Utils.Vectors;

namespace NLightning.Integration.Tests.BOLT4;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.Models;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Models;
using Domain.Protocol.Tlv;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Crypto.Ciphers;
using Infrastructure.Serialization.Onion;

/// <summary>
/// BOLT 4 "Returning Errors" vectors: <c>onion-error-test.json</c> (temporary_node_failure from hops[4], padded to 256)
/// and the inline spec trace (<c>returning-errors-trace.json</c>: incorrect_or_unknown_payment_details + TLV 34001,
/// padded to 1024), checked byte for byte at every hop in both directions.
/// </summary>
public class FailureOnionVectorTests
{
    private const int TraceFailurePadLength = 1024;
    private const ulong TraceTlvType = 34001;

    private readonly FailureMessageSerializer _serializer = new();
    private readonly FailureOnionService _failureOnionService;
    private readonly SphinxService _sphinxService = new(new Secp256K1Math());

    public FailureOnionVectorTests()
    {
        _failureOnionService = new FailureOnionService(_serializer);
    }

    #region onion-error-test.json

    [Fact]
    public void Given_OnionErrorTest_When_SerializingTemporaryNodeFailure_Then_MessageAndFramingMatch()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        var message = FailureMessage.TemporaryNodeFailure();

        // Act
        var failureMessage = _serializer.Serialize(message);
        var payload = _serializer.SerializeErrorPayload(message);

        // Assert
        Assert.Equal(vector.FailureMessage, failureMessage);
        Assert.Equal(Convert.ToHexStringLower(vector.Hops[^1].Payload!), Convert.ToHexStringLower(payload));
    }

    [Fact]
    public void Given_OnionErrorTest_When_CreatingAndWrappingAtEveryHop_Then_OriginPacketMatches()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();

        // Act
        var packet = _failureOnionService.CreateErrorPacket(vector.Hops[^1].SharedSecret,
                                                           FailureMessage.TemporaryNodeFailure());
        for (var i = vector.Hops.Count - 2; i >= 0; i--)
            packet = _failureOnionService.WrapErrorPacket(vector.Hops[i].SharedSecret, packet);

        // Assert
        Assert.Equal(292, packet.Length);
        Assert.Equal(Convert.ToHexStringLower(vector.ErrorPacket), Convert.ToHexStringLower(packet));
    }

    [Fact]
    public void Given_OnionErrorTest_When_OriginDecrypts_Then_Hop4AndTemporaryNodeFailure()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        var sharedSecrets =
            _sphinxService.ComputeSharedSecrets(vector.Hops.Select(h => new CompactPubKey(h.PubKey)).ToList(),
                                                vector.SessionKey);

        // Act
        var decrypted = _failureOnionService.DecryptErrorPacket(sharedSecrets, vector.ErrorPacket);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(4, decrypted.ErringHopIndex);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Code);
        Assert.NotNull(decrypted.Message);
        Assert.Equal(FailureCode.TemporaryNodeFailure, decrypted.Message.Code);
        Assert.True(decrypted.Message.Data.IsEmpty);
        Assert.Null(decrypted.Message.Extension);
        Assert.Equal(vector.FailureMessage, decrypted.RawMessage.ToArray());
    }

    [Fact]
    public void Given_OnionErrorTest_When_OriginDecryptsWithWrongRoute_Then_ReturnsNull()
    {
        // Arrange
        var vector = Bolt4Vectors.LoadOnionErrorTest();
        var sharedSecrets = vector.Hops.Take(4).Select(h => new Secret(h.SharedSecret)).ToList();

        // Act
        var decrypted = _failureOnionService.DecryptErrorPacket(sharedSecrets, vector.ErrorPacket);

        // Assert
        Assert.Null(decrypted);
    }

    #endregion

    #region Inline "Returning Errors" trace

    [Fact]
    public void Given_ReturningErrorsTrace_When_ComputingSharedSecretsFromSessionKey_Then_EveryHopMatches()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var route = Bolt4Vectors.LoadOnionErrorTest().Hops.Select(h => new CompactPubKey(h.PubKey)).ToList();

        // Act
        var sharedSecrets = _sphinxService.ComputeSharedSecrets(route, Bolt4Vectors.SessionKey);

        // Assert
        Assert.Equal(trace.ErringSharedSecret, (byte[])sharedSecrets[trace.ErringNode]);
        foreach (var hop in trace.Hops)
            Assert.Equal(hop.SharedSecret, (byte[])sharedSecrets[hop.Node]);
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_DerivingKeysAndStreams_Then_UmAmmagAndStreamsMatch()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        using var keyGenerator = new SphinxKeyGenerator();
        using var chaCha20 = new ChaCha20Stream();

        // Act / Assert
        Assert.Equal(trace.UmKey, keyGenerator.DeriveKey(OnionConstants.Um, trace.ErringSharedSecret));
        foreach (var hop in trace.Hops)
        {
            var ammagKey = keyGenerator.DeriveKey(OnionConstants.Ammag, hop.SharedSecret);
            var stream = new byte[hop.Stream.Length];
            chaCha20.GenerateStream(ammagKey, stream);

            Assert.Equal(hop.AmmagKey, ammagKey);
            Assert.Equal(Convert.ToHexStringLower(hop.Stream), Convert.ToHexStringLower(stream));
        }
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_DeserializingFailureMessage_Then_FieldsAndTlvMatchAndRoundTrip()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();

        // Act
        var message = _serializer.Deserialize(trace.FailureMessage);

        // Assert
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, message.Code);
        Assert.Equal(LightningMoney.MilliSatoshis(100UL), message.HtlcAmount);
        Assert.Equal(800_000u, message.Height);
        Assert.NotNull(message.Extension);
        var tlv = Assert.Single(message.Extension.GetTlvs());
        Assert.Equal(TraceTlvType, tlv.Type.Value);
        Assert.Equal(Enumerable.Repeat((byte)0x80, 300), tlv.Value);
        Assert.Equal(trace.FailureMessage, _serializer.Serialize(message));
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_FramingFailureMessage_Then_PayloadMatches()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var message = BuildTraceFailureMessage();

        // Act
        var payload = _serializer.SerializeErrorPayload(message, TraceFailurePadLength);

        // Assert
        Assert.Equal(Convert.ToHexStringLower(trace.Payload), Convert.ToHexStringLower(payload));
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_ErringNodeCreatesPacket_Then_RawAndObfuscatedPacketsMatch()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var erringHop = trace.Hops[0];

        // Act
        var packet = _failureOnionService.CreateErrorPacket(trace.ErringSharedSecret, BuildTraceFailureMessage(),
                                                           TraceFailurePadLength);

        // Assert: raw packet = hmac(um) || payload, then XORed with the ammag stream
        var raw = packet.Zip(erringHop.Stream, (a, b) => (byte)(a ^ b)).ToArray();
        Assert.Equal(Convert.ToHexStringLower(trace.RawErrorPacket), Convert.ToHexStringLower(raw));
        Assert.Equal(Convert.ToHexStringLower(erringHop.ErrorPacket), Convert.ToHexStringLower(packet));
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_WrappingAtEveryHop_Then_EachHopPacketMatches()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var packet = _failureOnionService.CreateErrorPacket(trace.ErringSharedSecret, BuildTraceFailureMessage(),
                                                           TraceFailurePadLength);

        // Act / Assert: hops[0] is the erring node itself, the others return-forward
        foreach (var hop in trace.Hops.Skip(1))
        {
            packet = _failureOnionService.WrapErrorPacket(hop.SharedSecret, packet);
            Assert.Equal(Convert.ToHexStringLower(hop.ErrorPacket), Convert.ToHexStringLower(packet));
        }
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_UnwrappingFromOrigin_Then_EachPreviousHopPacketMatches()
    {
        // Arrange: the ammag XOR is its own inverse, so peeling a hop's layer yields the packet it received
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();

        for (var i = trace.Hops.Count - 1; i >= 1; i--)
        {
            // Act
            var unwrapped = _failureOnionService.WrapErrorPacket(trace.Hops[i].SharedSecret, trace.Hops[i].ErrorPacket);

            // Assert
            Assert.Equal(Convert.ToHexStringLower(trace.Hops[i - 1].ErrorPacket), Convert.ToHexStringLower(unwrapped));
        }

        var raw = _failureOnionService.WrapErrorPacket(trace.Hops[0].SharedSecret, trace.Hops[0].ErrorPacket);
        Assert.Equal(Convert.ToHexStringLower(trace.RawErrorPacket), Convert.ToHexStringLower(raw));
    }

    [Fact]
    public void Given_ReturningErrorsTrace_When_OriginDecrypts_Then_Node4AndFullMessage()
    {
        // Arrange
        var trace = Bolt4Vectors.LoadReturningErrorsTrace();
        var routeSecrets = trace.Hops.OrderBy(h => h.Node).Select(h => new Secret(h.SharedSecret)).ToList();
        var originPacket = trace.Hops[^1].ErrorPacket;

        // Act
        var decrypted = _failureOnionService.DecryptErrorPacket(routeSecrets, originPacket);

        // Assert
        Assert.NotNull(decrypted);
        Assert.Equal(trace.ErringNode, decrypted.ErringHopIndex);
        Assert.Equal(trace.FailureMessage, decrypted.RawMessage.ToArray());
        Assert.NotNull(decrypted.Message);
        Assert.Equal(FailureCode.IncorrectOrUnknownPaymentDetails, decrypted.Message.Code);
        Assert.Equal(800_000u, decrypted.Message.Height);
        Assert.Equal(LightningMoney.MilliSatoshis(100UL), decrypted.Message.HtlcAmount);
        Assert.NotNull(decrypted.Message.Extension);
        Assert.True(decrypted.Message.Extension.TryGetTlv(TraceTlvType, out _));
    }

    #endregion

    private static FailureMessage BuildTraceFailureMessage()
    {
        var extension = new TlvStream();
        extension.Add(new BaseTlv(TraceTlvType, Enumerable.Repeat((byte)0x80, 300).ToArray()));
        return FailureMessage.IncorrectOrUnknownPaymentDetails(LightningMoney.MilliSatoshis(100UL), 800_000)
                             .WithExtension(extension);
    }
}