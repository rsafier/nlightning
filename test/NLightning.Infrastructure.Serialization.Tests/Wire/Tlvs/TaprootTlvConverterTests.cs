namespace NLightning.Infrastructure.Serialization.Tests.Wire.Tlvs;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Tlv;
using NLightning.Infrastructure.Serialization.Wire;

/// <summary>
/// The simple taproot channels and BOLTs PR #1324 TLV converters (NL-877).
/// </summary>
public class TaprootTlvConverterTests
{
    private static readonly byte[] s_nonce = Enumerable.Range(0, 66).Select(i => (byte)(0x40 + i)).ToArray();
    private static readonly byte[] s_nonce2 = Enumerable.Range(0, 66).Select(i => (byte)(0x90 + i)).ToArray();
    private static readonly byte[] s_psig = Enumerable.Range(0, 98).Select(i => (byte)(0x01 + i)).ToArray();

    public static TheoryData<string, ulong> NonceConverters => new()
    {
        { nameof(NextLocalNonceTlv), 4 },
        { nameof(ShutdownNonceTlv), 8 },
        { nameof(NextCloseeNonceTlv), 22 },
        { nameof(FundingNonceTlv), 6 },
        { nameof(CurrentCommitNonceTlv), 24 }
    };

    [Theory]
    [MemberData(nameof(NonceConverters))]
    public void Given_NonceTlv_When_ConvertingToBaseAndBack_Then_RoundTrips(string name, ulong type)
    {
        // Arrange
        var (converter, tlv) = NonceConverter(name);
        var expectedBase = new BaseTlv(type, s_nonce);

        // Act
        var baseTlv = converter.Encode(tlv);
        var read = (PublicNonceTlv)converter.Decode(expectedBase)!;

        // Assert
        Assert.Equal(expectedBase, baseTlv);
        Assert.Equal(tlv.GetType(), read.GetType());
        Assert.Equal(s_nonce, (byte[])read.Nonce);
    }

    [Theory]
    [MemberData(nameof(NonceConverters))]
    public void Given_NonceOfWrongLength_When_ConvertFromBase_Then_Throws(string name, ulong type)
    {
        // Arrange
        var (converter, _) = NonceConverter(name);

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(type, new byte[65])));
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(type, new byte[67])));
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(type + 2, s_nonce)));
    }

    [Fact]
    public void Given_PartialSignatureWithNonce_When_ConvertingToBaseAndBack_Then_RoundTrips()
    {
        // Arrange (type 2: s(32) || nonce(66))
        var converter = new WireRegistry().GetTlvDefinition<PartialSignatureWithNonceTlv>()!;
        var tlv = new PartialSignatureWithNonceTlv(new MusigPartialSignatureWithNonce(s_psig));

        // Act
        var baseTlv = converter.Encode(tlv);
        var read = converter.Decode(new BaseTlv(2, s_psig));

        // Assert
        Assert.Equal(new BaseTlv(2, s_psig), baseTlv);
        Assert.Equal(s_psig[..32], (byte[])read.PartialSignatureWithNonce.PartialSignature);
        Assert.Equal(s_psig[32..], (byte[])read.PartialSignatureWithNonce.PublicNonce);
    }

    [Theory]
    [InlineData(97)]
    [InlineData(99)]
    [InlineData(64)]
    public void Given_PartialSignatureWithNonceOfWrongLength_When_ConvertFromBase_Then_Throws(int length)
    {
        // Act & Assert
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<PartialSignatureWithNonceTlv>()!
                                                .Decode(new BaseTlv(2, new byte[length])));
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<SharedInputPartialSignatureTlv>()!
                                                .Decode(new BaseTlv(2, new byte[length])));
    }

    [Fact]
    public void Given_SharedInputPartialSignature_When_ConvertFromBase_Then_IsItsOwnType()
    {
        // Act
        var read = new WireRegistry().GetTlvDefinition<SharedInputPartialSignatureTlv>()!.Decode(new BaseTlv(2, s_psig));

        // Assert
        Assert.IsType<SharedInputPartialSignatureTlv>(read);
        Assert.Equal(s_psig, read.Value);
    }

    [Fact]
    public void Given_CommitNonces_When_ConvertingToBaseAndBack_Then_CurrentThenNext()
    {
        // Arrange (tx_complete type 4: commit nonce then next commit nonce)
        var converter = new WireRegistry().GetTlvDefinition<CommitNoncesTlv>()!;
        byte[] value = [.. s_nonce, .. s_nonce2];

        // Act
        var baseTlv = converter.Encode(new CommitNoncesTlv(s_nonce, s_nonce2));
        var read = converter.Decode(new BaseTlv(4, value));

        // Assert
        Assert.Equal(new BaseTlv(4, value), baseTlv);
        Assert.Equal(s_nonce, (byte[])read.CommitNonce);
        Assert.Equal(s_nonce2, (byte[])read.NextCommitNonce);
        Assert.Throws<InvalidCastException>(() => converter.Decode(new BaseTlv(4, s_nonce)));
    }

    [Fact]
    public void Given_HandWrittenLndNonceMap_When_ConvertFromBase_Then_TxIdInInternalOrderThenNonce()
    {
        // Arrange (LND lnwire/local_nonces.go: txid as raw chainhash bytes (internal order), then the 66-byte nonce,
        // entries sorted by txid bytes)
        var txidA = Enumerable.Range(0, 32).Select(i => (byte)(0x10 + i)).ToArray();
        var txidB = Enumerable.Range(0, 32).Select(i => (byte)(0xA0 + i)).ToArray();
        var hex = Convert.ToHexString(txidA) + Convert.ToHexString(s_nonce)
                + Convert.ToHexString(txidB) + Convert.ToHexString(s_nonce2);
        var value = Convert.FromHexString(hex);
        var converter = new WireRegistry().GetTlvDefinition<NextLocalNoncesTlv>()!;

        // Act
        var read = converter.Decode(new BaseTlv(22, value));

        // Assert
        Assert.Equal(2, read.Nonces.Count);
        Assert.True(read.Nonces.TryGetNonce(new TxId(txidA), out var nonceA));
        Assert.Equal(s_nonce, (byte[])nonceA);
        Assert.True(read.Nonces.TryGetNonce(new TxId(txidB), out var nonceB));
        Assert.Equal(s_nonce2, (byte[])nonceB);
        Assert.Equal(value, converter.Encode(read).Value);
        // the display txid is the reverse of the wire bytes
        Assert.Equal(Convert.ToHexStringLower(txidA.Reverse().ToArray()), new TxId(txidA).ToString());
    }

    [Fact]
    public void Given_UnsortedMap_When_ConvertFromBase_Then_AcceptedAndWrittenSorted()
    {
        // Arrange (the spec does not order the entries; we accept any order and write them sorted)
        var low = Enumerable.Repeat((byte)0x01, 32).ToArray();
        var high = Enumerable.Repeat((byte)0xfe, 32).ToArray();
        byte[] unsorted = [.. high, .. s_nonce2, .. low, .. s_nonce];

        // Act
        var read = new WireRegistry().GetTlvDefinition<NextLocalNoncesTlv>()!.Decode(new BaseTlv(22, unsorted));

        // Assert
        Assert.Equal([.. low, .. s_nonce, .. high, .. s_nonce2], read.Value);
    }

    [Theory]
    [InlineData(97)]
    [InlineData(99)]
    [InlineData(196 + 1)]
    [InlineData(66)]
    public void Given_MapLengthNotAMultipleOf98_When_ConvertFromBase_Then_Throws(int length)
    {
        // Act & Assert
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<NextLocalNoncesTlv>()!
                                                .Decode(new BaseTlv(22, new byte[length])));
    }

    [Fact]
    public void Given_MapWithSeventeenEntries_When_ConvertFromBase_Then_Throws()
    {
        // Arrange (LND refuses more than 16)
        var value = Enumerable.Range(0, 17)
                              .SelectMany(i => Enumerable.Repeat((byte)i, 32).Concat(s_nonce))
                              .ToArray();

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<NextLocalNoncesTlv>()!
                                                .Decode(new BaseTlv(22, value)));
    }

    [Fact]
    public void Given_MapWithSameTxIdTwice_When_ConvertFromBase_Then_Throws()
    {
        // Arrange
        var txid = Enumerable.Repeat((byte)0x05, 32).ToArray();
        byte[] value = [.. txid, .. s_nonce, .. txid, .. s_nonce2];

        // Act & Assert
        Assert.Throws<InvalidCastException>(() => new WireRegistry().GetTlvDefinition<NextLocalNoncesTlv>()!
                                                .Decode(new BaseTlv(22, value)));
    }

    [Fact]
    public void Given_EmptyMap_When_ConvertFromBase_Then_NoEntries()
    {
        // Act
        var read = new WireRegistry().GetTlvDefinition<NextLocalNoncesTlv>()!.Decode(new BaseTlv(22, []));

        // Assert
        Assert.Equal(0, read.Nonces.Count);
    }

    [Fact]
    public void Given_Factory_When_GetConverter_Then_EveryTaprootTlvIsRegistered()
    {
        // Arrange
        var factory = new WireRegistry();

        // Act & Assert
        Assert.NotNull(factory.GetTlvDefinition<NextLocalNonceTlv>());
        Assert.NotNull(factory.GetTlvDefinition<ShutdownNonceTlv>());
        Assert.NotNull(factory.GetTlvDefinition<NextCloseeNonceTlv>());
        Assert.NotNull(factory.GetTlvDefinition<FundingNonceTlv>());
        Assert.NotNull(factory.GetTlvDefinition<CurrentCommitNonceTlv>());
        Assert.NotNull(factory.GetTlvDefinition<CommitNoncesTlv>());
        Assert.NotNull(factory.GetTlvDefinition<NextLocalNoncesTlv>());
        Assert.NotNull(factory.GetTlvDefinition<PartialSignatureWithNonceTlv>());
        Assert.NotNull(factory.GetTlvDefinition<SharedInputPartialSignatureTlv>());
    }

    private static (TlvDef Converter, PublicNonceTlv Tlv) NonceConverter(string name) => name switch
    {
        nameof(NextLocalNonceTlv) => (new WireRegistry().GetTlvDefinition<NextLocalNonceTlv>()!, new NextLocalNonceTlv(s_nonce)),
        nameof(ShutdownNonceTlv) => (new WireRegistry().GetTlvDefinition<ShutdownNonceTlv>()!, new ShutdownNonceTlv(s_nonce)),
        nameof(NextCloseeNonceTlv) => (new WireRegistry().GetTlvDefinition<NextCloseeNonceTlv>()!, new NextCloseeNonceTlv(s_nonce)),
        nameof(FundingNonceTlv) => (new WireRegistry().GetTlvDefinition<FundingNonceTlv>()!, new FundingNonceTlv(s_nonce)),
        nameof(CurrentCommitNonceTlv) => (new WireRegistry().GetTlvDefinition<CurrentCommitNonceTlv>()!, new CurrentCommitNonceTlv(s_nonce)),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
    };
}