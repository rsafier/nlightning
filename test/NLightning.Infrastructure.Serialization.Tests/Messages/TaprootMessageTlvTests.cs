namespace NLightning.Infrastructure.Serialization.Tests.Messages;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Exceptions;
using Factories;
using Helpers;

/// <summary>
/// The simple taproot channels TLVs (and BOLTs PR #1324's interactive-tx ones) on the wire, from hand-written bytes
/// (NL-877): each message is read from its BOLT layout, its TLVs checked, and written back byte for byte.
/// </summary>
public class TaprootMessageTlvTests
{
    private const string Point = "02C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";
    private const string Zero32 = "0000000000000000000000000000000000000000000000000000000000000000";
    private const string Secret = "C93CA7DCA44D2E45E3CC5419D92750F7FB3A0F180852B73A621F4051C0193A75";

    // channel_type {80}: 11 bytes, bit 80 set (BOLT 9 big-endian bitfield)
    private const string TaprootChannelTypeTlvHex = "010B" + "01" + "00000000000000000000";

    private static readonly string s_zeroSignature = new('0', 128);
    private static readonly string s_nonce1 = string.Concat(Enumerable.Repeat("A1", 66));
    private static readonly string s_nonce2 = string.Concat(Enumerable.Repeat("B2", 66));
    private static readonly string s_nonce3 = string.Concat(Enumerable.Repeat("C3", 66));
    private static readonly string s_partialSig = string.Concat(Enumerable.Repeat("5A", 32)) + s_nonce1;
    private static readonly string s_partialSig2 = string.Concat(Enumerable.Repeat("6B", 32)) + s_nonce2;
    private static readonly string s_partial32 = string.Concat(Enumerable.Repeat("7C", 32));
    private static readonly string s_txIdLow = "01" + string.Concat(Enumerable.Repeat("EE", 31));
    private static readonly string s_txIdHigh = "F0" + string.Concat(Enumerable.Repeat("11", 31));

    private static readonly string s_openChannelPayloadHex =
        Zero32 + Zero32 + new string('0', 6 * 16) + "000003E8" + "0090" + "01E3"
      + string.Concat(Enumerable.Repeat(Point, 6)) + "00";

    private static readonly string s_acceptChannelPayloadHex =
        Zero32 + new string('0', 4 * 16) + "00000003" + "0090" + "01E3" + string.Concat(Enumerable.Repeat(Point, 6));

    private readonly MessageTypeSerializerFactory _factory = new(SerializerHelper.PayloadSerializerFactory,

                                                                 SerializerHelper.TlvStreamSerializer);

    [Fact]
    public async Task Given_OpenChannelWithNextLocalNonce_When_RoundTrip_Then_NonceReadAndBytesKept()
    {
        // Act
        var message = await RoundTripAsync<OpenChannel1Message>(
            MessageTypes.OpenChannel, s_openChannelPayloadHex + TaprootChannelTypeTlvHex + "0442" + s_nonce1);

        // Assert
        Assert.NotNull(message.NextLocalNonceTlv);
        Assert.Equal(Convert.FromHexString(s_nonce1), (byte[])message.NextLocalNonceTlv.Nonce);
        Assert.NotNull(message.ChannelTypeTlv);
    }

    [Fact]
    public async Task Given_AcceptChannelWithNextLocalNonce_When_RoundTrip_Then_NonceReadAndBytesKept()
    {
        // Act
        var message = await RoundTripAsync<AcceptChannel1Message>(
            MessageTypes.AcceptChannel, s_acceptChannelPayloadHex + TaprootChannelTypeTlvHex + "0442" + s_nonce1);

        // Assert
        Assert.Equal(Convert.FromHexString(s_nonce1), (byte[])message.NextLocalNonceTlv!.Nonce);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_ChannelReadyWithNextLocalNonce_When_RoundTrip_Then_NonceRead(bool withAlias)
    {
        // Arrange
        var aliasHex = withAlias ? "0108" + "0000010000020003" : "";

        // Act
        var message = await RoundTripAsync<ChannelReadyMessage>(
            MessageTypes.ChannelReady, Zero32 + Point + aliasHex + "0442" + s_nonce1);

        // Assert
        Assert.Equal(withAlias, message.ShortChannelIdTlv is not null);
        Assert.Equal(Convert.FromHexString(s_nonce1), (byte[])message.NextLocalNonceTlv!.Nonce);
    }

    [Fact]
    public async Task Given_FundingCreatedWithZeroSignatureAndPartialSignature_When_RoundTrip_Then_BothKept()
    {
        // Act (temporary_channel_id, funding_txid, funding_output_index, signature = 64 zero bytes, TLV 2)
        var message = await RoundTripAsync<FundingCreatedMessage>(
            MessageTypes.FundingCreated, Zero32 + s_txIdLow + "0001" + s_zeroSignature + "0262" + s_partialSig);

        // Assert
        Assert.True(message.Payload.Signature.IsZero);
        Assert.Equal(Convert.FromHexString(s_partialSig),
                     message.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.ToBytes());
    }

    [Fact]
    public async Task Given_FundingSignedWithZeroSignatureAndPartialSignature_When_RoundTrip_Then_BothKept()
    {
        // Act
        var message = await RoundTripAsync<FundingSignedMessage>(
            MessageTypes.FundingSigned, Zero32 + s_zeroSignature + "0262" + s_partialSig);

        // Assert
        Assert.True(message.Payload.Signature.IsZero);
        Assert.Equal(Convert.FromHexString(s_nonce1),
                     (byte[])message.PartialSignatureWithNonceTlv!.PartialSignatureWithNonce.PublicNonce);
    }

    [Fact]
    public async Task Given_CommitmentSignedWithZeroSignature_When_RoundTrip_Then_FundingTxIdAndPartialSignatureKept()
    {
        // Act (channel_id, signature = zeros, num_htlcs = 1 (a 64-byte schnorr HTLC signature), TLV 1, TLV 2)
        var htlcSignature = string.Concat(Enumerable.Repeat("3D", 64));
        var message = await RoundTripAsync<CommitmentSignedMessage>(
            MessageTypes.CommitmentSigned,
            Zero32 + s_zeroSignature + "0001" + htlcSignature + "0120" + s_txIdLow + "0262" + s_partialSig);

        // Assert
        Assert.True(message.Payload.Signature.IsZero);
        Assert.Single(message.Payload.HtlcSignatures);
        Assert.Equal(Convert.FromHexString(s_txIdLow), (byte[])message.FundingTxIdTlv!.FundingTxId);
        Assert.Equal(Convert.FromHexString(s_partialSig), message.PartialSignatureWithNonceTlv!.Value);
    }

    [Fact]
    public async Task Given_RevokeAndAckWithLndNonceMapOfTwoEntries_When_RoundTrip_Then_EntriesByTxId()
    {
        // Arrange (LND layout: 22, length 196, entries txid (internal order) || nonce sorted by txid)
        var hex = Zero32 + Secret + Point + "16C4" + s_txIdLow + s_nonce1 + s_txIdHigh + s_nonce2;

        // Act
        var message = await RoundTripAsync<RevokeAndAckMessage>(MessageTypes.RevokeAndAck, hex);

        // Assert
        var nonces = message.NextLocalNoncesTlv!.Nonces;
        Assert.Equal(2, nonces.Count);
        Assert.True(nonces.TryGetNonce(new TxId(Convert.FromHexString(s_txIdHigh)), out var high));
        Assert.Equal(Convert.FromHexString(s_nonce2), (byte[])high);
        Assert.Equal(new TxId(Convert.FromHexString(s_txIdLow)), nonces.Entries[0].FundingTxId);
    }

    [Fact]
    public async Task Given_RevokeAndAckWithOneEntry_When_RoundTrip_Then_EntryRead()
    {
        // Act
        var message = await RoundTripAsync<RevokeAndAckMessage>(
            MessageTypes.RevokeAndAck, Zero32 + Secret + Point + "1662" + s_txIdHigh + s_nonce3);

        // Assert
        var (fundingTxId, nonce) = Assert.Single(message.NextLocalNoncesTlv!.Nonces.Entries);
        Assert.Equal(Convert.FromHexString(s_txIdHigh), (byte[])fundingTxId);
        Assert.Equal(Convert.FromHexString(s_nonce3), (byte[])nonce);
    }

    [Fact]
    public async Task Given_RevokeAndAckWithUnsortedMap_When_Read_Then_AcceptedAndWrittenSorted()
    {
        // Arrange
        var serializer = Serializer(MessageTypes.RevokeAndAck);
        var unsorted = Zero32 + Secret + Point + "16C4" + s_txIdHigh + s_nonce2 + s_txIdLow + s_nonce1;

        // Act
        var message = await serializer.DeserializeAsync(new MemoryStream(Convert.FromHexString(unsorted)));
        using var output = new MemoryStream();
        await serializer.SerializeAsync(message, output);

        // Assert
        Assert.Equal(Zero32 + Secret + Point + "16C4" + s_txIdLow + s_nonce1 + s_txIdHigh + s_nonce2,
                     Convert.ToHexString(output.ToArray()));
    }

    [Theory]
    [InlineData(97)]
    [InlineData(99)]
    [InlineData(66)]
    public async Task Given_RevokeAndAckWithMapLengthNotAMultipleOf98_When_Read_Then_Throws(int length)
    {
        // Arrange
        var hex = Zero32 + Secret + Point + "16" + length.ToString("X2") + new string('1', 2 * length);

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(
            () => Serializer(MessageTypes.RevokeAndAck).DeserializeAsync(new MemoryStream(Convert.FromHexString(hex))));
    }

    [Fact]
    public async Task Given_RevokeAndAckWithSeventeenEntries_When_Read_Then_Throws()
    {
        // Arrange (17 x 98 = 1666 = 0xfd0682)
        var entries = string.Concat(Enumerable.Range(0, 17).Select(i => i.ToString("X2") + s_txIdLow[2..] + s_nonce1));
        var hex = Zero32 + Secret + Point + "16FD0682" + entries;

        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(
            () => Serializer(MessageTypes.RevokeAndAck).DeserializeAsync(new MemoryStream(Convert.FromHexString(hex))));
    }

    [Fact]
    public async Task Given_ChannelReestablishWithEveryTlv_When_RoundTrip_Then_AllRead()
    {
        // Arrange (1 next_funding, 5 my_current_funding_locked, 22 next_local_nonces, 24 current_commit_nonce)
        var hex = Zero32 + "0000000000000001" + "0000000000000002" + Zero32 + Point
                + "0121" + s_txIdHigh + "01" + "0521" + s_txIdLow + "00" + "1662" + s_txIdLow + s_nonce1
                + "1842" + s_nonce2;

        // Act
        var message = await RoundTripAsync<ChannelReestablishMessage>(MessageTypes.ChannelReestablish, hex);

        // Assert
        Assert.NotNull(message.NextFundingTlv);
        Assert.NotNull(message.MyCurrentFundingLockedTlv);
        Assert.True(message.NextLocalNoncesTlv!.Nonces.Contains(new TxId(Convert.FromHexString(s_txIdLow))));
        Assert.Equal(Convert.FromHexString(s_nonce2), (byte[])message.CurrentCommitNonceTlv!.Nonce);
    }

    [Fact]
    public async Task Given_ShutdownWithShutdownNonce_When_RoundTrip_Then_NonceRead()
    {
        // Act (channel_id, len, P2TR scriptpubkey, TLV 8)
        var script = "5120" + string.Concat(Enumerable.Repeat("42", 32));
        var message = await RoundTripAsync<ShutdownMessage>(MessageTypes.Shutdown,
                                                           Zero32 + "0022" + script + "0842" + s_nonce1);

        // Assert
        Assert.Equal(Convert.FromHexString(s_nonce1), (byte[])message.ShutdownNonceTlv!.Nonce);
    }

    [Fact]
    public async Task Given_TxCompleteWithCommitAndFundingNonces_When_RoundTrip_Then_BothRead()
    {
        // Act (4 commit_nonces = current || next (132 bytes), 6 funding_nonce)
        var message = await RoundTripAsync<TxCompleteMessage>(
            MessageTypes.TxComplete, Zero32 + "0484" + s_nonce1 + s_nonce2 + "0642" + s_nonce3);

        // Assert
        Assert.Equal(Convert.FromHexString(s_nonce1), (byte[])message.CommitNoncesTlv!.CommitNonce);
        Assert.Equal(Convert.FromHexString(s_nonce2), (byte[])message.CommitNoncesTlv.NextCommitNonce);
        Assert.Equal(Convert.FromHexString(s_nonce3), (byte[])message.FundingNonceTlv!.Nonce);
    }

    [Fact]
    public async Task Given_TxSignaturesWithSharedInputPartialSignature_When_RoundTrip_Then_Read()
    {
        // Act (channel_id, txid, num_witnesses = 0, TLV 2)
        var message = await RoundTripAsync<TxSignaturesMessage>(
            MessageTypes.TxSignatures, Zero32 + s_txIdLow + "0000" + "0262" + s_partialSig);

        // Assert
        Assert.Null(message.SharedInputSignatureTlv);
        Assert.Equal(Convert.FromHexString(s_partialSig), message.SharedInputPartialSignatureTlv!.Value);
    }

    [Fact]
    public async Task Given_ClosingCompleteWithTaprootSignatures_When_RoundTrip_Then_EcdsaAndPartialSignaturesRead()
    {
        // Act (3 an ECDSA signature, then 5 and 7 98-byte partial signatures with the closer's nonce)
        var message = await RoundTripAsync<ClosingCompleteMessage>(
            MessageTypes.ClosingComplete,
            ClosingCompleteMessageTests.FixedHex + "0340" + ClosingCompleteMessageTests.Sig3Hex + "0562" + s_partialSig
          + "0762" + s_partialSig2);

        // Assert
        Assert.Equal([ClosingSigKind.CloserAndCloseeOutputs], message.Signatures.Kinds);
        Assert.Equal([ClosingSigKind.CloserOutputOnly, ClosingSigKind.CloserAndCloseeOutputs],
                     message.PartialSignatures.Kinds);
        Assert.Equal(Convert.FromHexString(s_partialSig2),
                     message.PartialSignatures.Get(ClosingSigKind.CloserAndCloseeOutputs)!.Value.ToBytes());
    }

    [Fact]
    public async Task Given_ClosingSigWithPartialSignatureAndNextCloseeNonce_When_RoundTrip_Then_Read()
    {
        // Act (6 a 32-byte partial signature, 22 next_closee_nonce)
        var message = await RoundTripAsync<ClosingSigMessage>(
            MessageTypes.ClosingSig,
            ClosingCompleteMessageTests.FixedHex + "0620" + s_partial32 + "1642" + s_nonce3);

        // Assert
        Assert.Empty(message.Signatures.Kinds);
        Assert.Equal(Convert.FromHexString(s_partial32),
                     (byte[])message.PartialSignatures.CloseeOutputOnly!.Value);
        Assert.Equal(Convert.FromHexString(s_nonce3), (byte[])message.NextCloseeNonceTlv!.Nonce);
    }

    public static TheoryData<MessageTypes, string> MalformedTaprootTlvs => new()
    {
        // a 67-byte next_local_nonce
        { MessageTypes.ChannelReady, Zero32 + Point + "0443" + "00" + s_nonce1 },
        // a 65-byte next_local_nonce in open_channel
        { MessageTypes.OpenChannel, s_openChannelPayloadHex + TaprootChannelTypeTlvHex + "0441" + s_nonce1[2..] },
        // a 99-byte partial_signature_with_nonce
        { MessageTypes.FundingSigned, Zero32 + s_zeroSignature + "0263" + s_partialSig + "00" },
        { MessageTypes.CommitmentSigned, Zero32 + s_zeroSignature + "0000" + "0261" + s_partialSig[2..] },
        // a 65-byte shutdown_nonce
        { MessageTypes.Shutdown, Zero32 + "0016" + "0014" + new string('0', 40) + "0841" + s_nonce1[2..] },
        // a 66-byte commit_nonces (needs 132)
        { MessageTypes.TxComplete, Zero32 + "0442" + s_nonce1 },
        // a 67-byte current_commit_nonce
        {
            MessageTypes.ChannelReestablish,
            Zero32 + "0000000000000001" + "0000000000000002" + Zero32 + Point + "1843" + s_nonce1 + "00"
        },
        // a 32-byte closer_no_closee in closing_complete (needs 98)
        { MessageTypes.ClosingComplete, ClosingCompleteMessageTests.FixedHex + "0520" + s_partial32 },
        // a 98-byte no_closer_closee in closing_sig (needs 32)
        { MessageTypes.ClosingSig, ClosingCompleteMessageTests.FixedHex + "0662" + s_partialSig },
        // a 65-byte next_closee_nonce
        { MessageTypes.ClosingSig, ClosingCompleteMessageTests.FixedHex + "1641" + s_nonce1[2..] },
        // a 97-byte shared_input_partial_signature
        { MessageTypes.TxSignatures, Zero32 + s_txIdLow + "0000" + "0261" + s_partialSig[2..] }
    };

    [Theory]
    [MemberData(nameof(MalformedTaprootTlvs))]
    public async Task Given_TaprootTlvOfWrongLength_When_Read_Then_ThrowsMessageSerializationException(
        MessageTypes type, string hex)
    {
        // Act & Assert
        await Assert.ThrowsAsync<MessageSerializationException>(
            () => Serializer(type).DeserializeAsync(new MemoryStream(Convert.FromHexString(hex))));
    }

    [Fact]
    public async Task Given_TaprootMessagesBuiltInCode_When_Serialized_Then_TlvsInAscendingOrder()
    {
        // Arrange
        var nonce = new MusigPublicNonce(Convert.FromHexString(s_nonce1));
        var psig = new MusigPartialSignatureWithNonce(Convert.FromHexString(s_partialSig));
        var payload = new CommitmentSignedPayload(Domain.Channels.ValueObjects.ChannelId.Zero, [],
                                                  CompactSignature.Zero);
        var message = new CommitmentSignedMessage(payload,
                                                  new Domain.Protocol.Tlv.FundingTxIdTlv(
                                                      new TxId(Convert.FromHexString(s_txIdLow))),
                                                  new Domain.Protocol.Tlv.PartialSignatureWithNonceTlv(psig));
        var close = new ClosingSigMessage(
            new ClosingSigPayload(ClosingCompleteMessageTests.Payload().ChannelId,
                                  ClosingCompleteMessageTests.Payload().CloserScriptPubKey,
                                  ClosingCompleteMessageTests.Payload().CloseeScriptPubKey,
                                  ClosingCompleteMessageTests.Payload().FeeSatoshis,
                                  ClosingCompleteMessageTests.Payload().LockTime),
            new ClosingSignatures(),
            ClosingPartialSignatures.Single(ClosingSigKind.CloseeOutputOnly,
                                            new MusigPartialSignature(Convert.FromHexString(s_partial32))),
            new Domain.Protocol.Tlv.NextCloseeNonceTlv(nonce));
        using var commitStream = new MemoryStream();
        using var closeStream = new MemoryStream();

        // Act
        await Serializer(MessageTypes.CommitmentSigned).SerializeAsync(message, commitStream);
        await Serializer(MessageTypes.ClosingSig).SerializeAsync(close, closeStream);

        // Assert
        Assert.Equal(Zero32 + s_zeroSignature + "0000" + "0120" + s_txIdLow + "0262" + s_partialSig,
                     Convert.ToHexString(commitStream.ToArray()));
        Assert.Equal(ClosingCompleteMessageTests.FixedHex + "0620" + s_partial32 + "1642" + s_nonce1,
                     Convert.ToHexString(closeStream.ToArray()));
    }

    private Domain.Serialization.Interfaces.IMessageTypeSerializer Serializer(MessageTypes type) =>
        _factory.GetSerializer(type) ?? throw new InvalidOperationException($"No serializer for {type}");

    private async Task<TMessage> RoundTripAsync<TMessage>(MessageTypes type, string hex) where TMessage : IMessage
    {
        var serializer = Serializer(type);
        var bytes = Convert.FromHexString(hex);

        var message = Assert.IsType<TMessage>(await serializer.DeserializeAsync(new MemoryStream(bytes)));
        using var output = new MemoryStream();
        await serializer.SerializeAsync(message, output);

        Assert.Equal(hex, Convert.ToHexString(output.ToArray()));
        return message;
    }
}