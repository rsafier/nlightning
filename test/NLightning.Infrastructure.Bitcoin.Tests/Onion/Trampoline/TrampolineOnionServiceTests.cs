using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onion.Trampoline;

using Domain.Exceptions;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Constants;
using Domain.Protocol.Onion.Enums;
using Domain.Protocol.Onion.Factories;
using Domain.Protocol.Onion.Interfaces;
using Domain.Protocol.Onion.Models;
using Domain.Serialization.Interfaces;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Onion.Trampoline;
using Onion;

public class TrampolineOnionServiceTests
{
    private static readonly byte[] s_paymentHash = Enumerable.Repeat((byte)0x42, 32).ToArray();

    private readonly Ecdh _ecdh = new();
    private readonly TrampolineOnionService _service = new(new SphinxService(new Secp256K1Math()));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void Given_Route_When_BuildingExactAndPeeling_Then_EveryHopRecoversItsPayloadAndSecret(int hopCount)
    {
        // Arrange
        var random = new Random(hopCount);
        var nodeKeys = Enumerable.Range(0, hopCount).Select(_ => _ecdh.GenerateKeyPair()).ToList();
        var hops = nodeKeys.Select(k => new OnionHop(k.CompactPubKey, RandomPayload(random, 2, 120))).ToList();
        var expectedLength = hops.Sum(h => _service.GetFramedPayloadLength(h.Payload.Length));

        // Act
        var onion = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Exact);

        // Assert
        Assert.Equal(expectedLength, onion.HopPayloadsLength);
        Assert.Equal(hopCount, onion.SharedSecrets.Count);
        Assert.Equal(onion.Packet.ToBytes(), onion.ToTlvValue());
        ReadOnlyMemory<byte>? current = onion.ToTlvValue();
        for (var i = 0; i < hopCount; i++)
        {
            Assert.NotNull(current);
            var peeled = _service.PeelWithNodeKey(current.Value, s_paymentHash, nodeKeys[i].PrivKey);

            Assert.Equal(hops[i].Payload.ToArray(), peeled.Payload.ToArray());
            Assert.Equal(onion.SharedSecrets[i], peeled.SharedSecret);
            Assert.Equal(i == hopCount - 1, peeled.IsFinal);
            if (peeled.NextPacket is { } next)
                Assert.Equal(expectedLength, next.HopPayloadsLength);

            current = peeled.NextPacket?.ToBytes();
        }
    }

    [Fact]
    public void Given_SmallPayloads_When_BuildingAuto_Then_PaddedTo650AndStillPeels()
    {
        // Arrange
        var nodeKeys = new[] { _ecdh.GenerateKeyPair(), _ecdh.GenerateKeyPair() };
        var hops = nodeKeys.Select(k => new OnionHop(k.CompactPubKey, RandomNumberGenerator.GetBytes(50))).ToList();

        // Act
        var onion = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Auto(1000));

        // Assert
        Assert.Equal(TrampolineOnionConstants.RecommendedHopPayloadsLength, onion.HopPayloadsLength);
        var first = _service.PeelWithNodeKey(onion.ToTlvValue(), s_paymentHash, nodeKeys[0].PrivKey);
        Assert.NotNull(first.NextPacket);
        Assert.Equal(TrampolineOnionConstants.RecommendedHopPayloadsLength, first.NextPacket.Value.HopPayloadsLength);
        var last = _service.PeelWithNodeKey(first.NextPacket.Value.ToBytes(), s_paymentHash, nodeKeys[1].PrivKey);
        Assert.True(last.IsFinal);
        Assert.Equal(hops[1].Payload.ToArray(), last.Payload.ToArray());
    }

    [Theory]
    [InlineData(1000, 161, 650)] // fits in 650: padded
    [InlineData(650, 650, 650)] // exactly 650
    [InlineData(400, 161, 161)] // 650 above the maximum: exact
    [InlineData(1000, 700, 700)] // framed payloads above 650 but within the maximum: exact
    public void Given_AutoPolicy_When_Resolving_Then_PaddedOnlyWhenPossible(int max, int framed, int expected)
    {
        // Act
        var length = TrampolineOnionSizePolicy.Auto(max).ResolveHopPayloadsLength(framed);

        // Assert
        Assert.Equal(expected, length);
    }

    [Fact]
    public void Given_AutoPolicy_When_FramedPayloadsExceedTheMaximum_Then_Throws()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[400]) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                                              TrampolineOnionSizePolicy.Auto(400)));
    }

    [Fact]
    public void Given_FixedPolicy_When_Building_Then_PacketHasThatLength()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 }) };

        // Act
        var onion = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Fixed(300));
        var peeled = _service.PeelWithNodeKey(onion.ToTlvValue(), s_paymentHash, nodeKey.PrivKey);

        // Assert
        Assert.Equal(300, onion.HopPayloadsLength);
        Assert.Equal(OnionConstants.PacketOverheadLength + 300, onion.ToTlvValue().Length);
        Assert.True(peeled.IsFinal);
    }

    [Fact]
    public void Given_FixedPolicyTooSmall_When_Building_Then_Throws()
    {
        // Arrange: 1 + 40 + 32 = 73 framed bytes
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[40]) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                                              TrampolineOnionSizePolicy.Fixed(72)));
    }

    [Fact]
    public void Given_InvalidPolicyLengths_When_Creating_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => TrampolineOnionSizePolicy.Fixed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TrampolineOnionSizePolicy.Auto(-1));
    }

    [Fact]
    public void Given_EmptyRoute_When_Building_Then_Throws()
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.Build([], _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                                              TrampolineOnionSizePolicy.Exact));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Given_PayloadShorterThanTwoBytes_When_Building_Then_Throws(int length)
    {
        // Arrange
        var hops = new List<OnionHop> { new(_ecdh.GenerateKeyPair().CompactPubKey, new byte[length]) };

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                                              TrampolineOnionSizePolicy.Exact));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Given_PaymentHashOfWrongLength_When_BuildingOrPeeling_Then_Throws(int length)
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 }) };
        var onion = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Exact);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey,
                                                              new byte[length], TrampolineOnionSizePolicy.Exact));
        Assert.Throws<ArgumentException>(() => _service.PeelWithNodeKey(onion.ToTlvValue(), new byte[length],
                                                                        nodeKey.PrivKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(66)]
    public void Given_ValueTooShortForAPacket_When_Peeling_Then_InvalidOnionPayloadNamingTlv20(int length)
    {
        // Act
        var exception = Assert.Throws<OnionException>(
            () => _service.PeelWithNodeKey(new byte[length], s_paymentHash, _ecdh.GenerateKeyPair().PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionPayload, exception.FailureCode);
        Assert.NotNull(exception.FailureData);
        Assert.True(InvalidOnionPayloadFailureFactory.TryDecodeData(exception.FailureData.Value.Span, out var type,
                                                                    out var offset));
        Assert.Equal(20UL, type.Value);
        Assert.Equal(0, offset);
    }

    [Fact]
    public void Given_OtherPaymentHash_When_Peeling_Then_InvalidOnionHmacWithTheTrampolinePacketHash()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 }) };
        var value = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Exact).ToTlvValue();

        // Act
        var exception = Assert.Throws<OnionException>(
            () => _service.PeelWithNodeKey(value, new byte[32], nodeKey.PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionHmac, exception.FailureCode);
        Assert.Equal(SHA256.HashData(value), exception.FailureData!.Value.ToArray());
    }

    [Fact]
    public void Given_UnknownVersion_When_Peeling_Then_InvalidOnionVersion()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 }) };
        var value = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Exact).ToTlvValue();
        value[0] = 1;

        // Act
        var exception = Assert.Throws<OnionException>(
            () => _service.PeelWithNodeKey(value, s_paymentHash, nodeKey.PrivKey));

        // Assert
        Assert.Equal(FailureCode.InvalidOnionVersion, exception.FailureCode);
    }

    [Fact]
    public void Given_KeyManager_When_PeelingAsLocalNode_Then_MatchesExplicitKeyPeel()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var keyManager = new EcdhOnlyKeyManager(nodeKey.PrivKey);
        var service = new TrampolineOnionService(new SphinxService(new Secp256K1Math(), keyManager));
        var hops = new List<OnionHop>
        {
            new(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 }),
            new(_ecdh.GenerateKeyPair().CompactPubKey, new byte[] { 0x02, 0x01 })
        };
        var value = service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                  TrampolineOnionSizePolicy.Exact).ToTlvValue();
        var expected = _service.PeelWithNodeKey(value, s_paymentHash, nodeKey.PrivKey);

        // Act
        var peeled = service.Peel(value, s_paymentHash);

        // Assert
        Assert.Equal(expected.SharedSecret, peeled.SharedSecret);
        Assert.Equal(expected.Payload.ToArray(), peeled.Payload.ToArray());
        Assert.Equal(expected.NextPacket, peeled.NextPacket);
        Assert.Equal(1, keyManager.EcdhCalls);
    }

    [Fact]
    public void Given_NoKeyManager_When_PeelingAsLocalNode_Then_ThrowsInvalidOperationException()
    {
        // Arrange
        var nodeKey = _ecdh.GenerateKeyPair();
        var hops = new List<OnionHop> { new(nodeKey.CompactPubKey, new byte[] { 0x02, 0x00 }) };
        var value = _service.Build(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash,
                                   TrampolineOnionSizePolicy.Exact).ToTlvValue();

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => _service.Peel(value, s_paymentHash));
    }

    [Theory]
    [InlineData(0, 33)]
    [InlineData(2, 35)]
    [InlineData(252, 285)]
    [InlineData(253, 288)]
    [InlineData(1000, 1035)]
    public void Given_PayloadLength_When_Framing_Then_BigSizePlusPayloadPlusHmac(int payloadLength, int expected)
    {
        // Act
        var framed = _service.GetFramedPayloadLength(payloadLength);

        // Assert
        Assert.Equal(expected, framed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 50)]
    [InlineData(80, 47)]
    [InlineData(160, 90)]
    [InlineData(400, 47)]
    [InlineData(1000, 100)]
    [InlineData(1100, 10)]
    public void Given_OuterRoute_When_UsingTheMaximumTrampolineLength_Then_OuterOnionFitsAndOneMoreByteDoesNot(
        int outerHopsFramedLength, int otherTlvsLength)
    {
        // Arrange
        var max = _service.GetMaxHopPayloadsLength(outerHopsFramedLength, otherTlvsLength);
        Assert.True(max > 0);

        // Act & Assert
        Assert.True(OuterOnionFits(outerHopsFramedLength, otherTlvsLength, max));
        Assert.False(OuterOnionFits(outerHopsFramedLength, otherTlvsLength, max + 1));
    }

    [Fact]
    public void Given_NoRoomLeft_When_GettingTheMaximumLength_Then_Zero()
    {
        // Act & Assert
        Assert.Equal(0, _service.GetMaxHopPayloadsLength(1300, 0));
        Assert.Equal(0, _service.GetMaxHopPayloadsLength(1150, 60));
        Assert.Throws<ArgumentOutOfRangeException>(() => _service.GetMaxHopPayloadsLength(-1, 0));
    }

    [Fact]
    public void Given_BitcoinInfrastructure_When_ResolvingTrampolineServices_Then_ReturnsSingletons()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IFailureMessageSerializer>().Object);
        services.AddSingleton<ISecureKeyManager>(new EcdhOnlyKeyManager(_ecdh.GenerateKeyPair().PrivKey));
        services.AddBitcoinInfrastructure();
        using var provider = services.BuildServiceProvider();

        // Act
        var onionService = provider.GetRequiredService<ITrampolineOnionService>();
        var failureService = provider.GetRequiredService<ITrampolineFailureOnionService>();

        // Assert
        Assert.IsType<TrampolineOnionService>(onionService);
        Assert.Same(onionService, provider.GetRequiredService<ITrampolineOnionService>());
        Assert.IsType<TrampolineFailureOnionService>(failureService);
        Assert.Same(failureService, provider.GetRequiredService<ITrampolineFailureOnionService>());
    }

    /// <summary>
    /// Builds a real outer onion whose final payload carries <paramref name="otherTlvsLength"/> bytes of other TLVs and
    /// a trampoline packet of <paramref name="trampolineHopPayloadsLength"/>, behind outer hops taking
    /// <paramref name="outerHopsFramedLength"/> bytes, and reports whether it fits in 1300 bytes.
    /// </summary>
    private bool OuterOnionFits(int outerHopsFramedLength, int otherTlvsLength, int trampolineHopPayloadsLength)
    {
        var sphinx = new SphinxService(new Secp256K1Math());
        var hops = new List<OnionHop>();

        // Outer hops before the final one: 80 framed bytes each (a 47-byte payload), then the remainder (35-79)
        var remainder = outerHopsFramedLength % 80;
        Assert.True(remainder is 0 or >= 35, "The test splits the outer route into 80-byte hops and a remainder.");
        for (var i = 0; i < outerHopsFramedLength / 80; i++)
            hops.Add(new OnionHop(_ecdh.GenerateKeyPair().CompactPubKey, new byte[47]));

        if (remainder > 0)
            hops.Add(new OnionHop(_ecdh.GenerateKeyPair().CompactPubKey,
                                  new byte[remainder - 1 - OnionConstants.HmacLength]));

        var tlvValueLength = OnionConstants.PacketOverheadLength + trampolineHopPayloadsLength;
        var tlvRecord = new List<byte> { 0x14 };
        if (tlvValueLength < 253)
        {
            tlvRecord.Add((byte)tlvValueLength);
        }
        else
        {
            tlvRecord.Add(0xfd);
            tlvRecord.Add((byte)(tlvValueLength >> 8));
            tlvRecord.Add((byte)tlvValueLength);
        }

        tlvRecord.AddRange(new byte[tlvValueLength]);
        var finalPayload = new byte[otherTlvsLength].Concat(tlvRecord).ToArray();
        hops.Add(new OnionHop(_ecdh.GenerateKeyPair().CompactPubKey, finalPayload));

        try
        {
            sphinx.Construct(hops, _ecdh.GenerateKeyPair().PrivKey, s_paymentHash);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] RandomPayload(Random random, int min, int max)
    {
        var payload = new byte[random.Next(min, max + 1)];
        random.NextBytes(payload);
        return payload;
    }
}