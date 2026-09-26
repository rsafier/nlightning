using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Crypto.Constants;
using Crypto.ValueObjects;
using Interfaces;
using ValueObjects;

/// <summary>
/// Represents the payload of the channel_update message (BOLT 7, type 258).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout: <c>signature(64) || chain_hash(32) || short_channel_id(8) || u32 timestamp || byte message_flags ||
/// byte channel_flags || u16 cltv_expiry_delta || u64 htlc_minimum_msat || u32 fee_base_msat ||
/// u32 fee_proportional_millionths || u64 htlc_maximum_msat</c>, optionally followed by unknown fields
/// (<see cref="ExtraData"/>). The signature covers the double-SHA256 of everything after it, <b>including</b> the
/// unknown fields, so they are kept verbatim.
/// </para>
/// <para>
/// This class is also the single codec of the message body (<see cref="GetBytes"/>, <see cref="GetSignedData"/>,
/// <see cref="Parse"/>): the signature hash has to be computed in the Domain/Application layers, and the
/// <c>channel_update</c> field of BOLT 4 UPDATE failures is built in the Domain
/// (<see cref="Onion.Factories.FailureChannelUpdateFactory"/>). The wire serializer delegates to it, so all three
/// always agree byte for byte. Amounts are raw msat values (not the mutable <c>LightningMoney</c>) so a signed payload
/// cannot be changed after the fact.
/// </para>
/// </remarks>
public sealed class ChannelUpdatePayload : IMessagePayload
{
    /// <summary>
    /// The length of the <c>signature</c> field.
    /// </summary>
    public const int SignatureLength = CryptoConstants.MaxSignatureSize;

    /// <summary>
    /// The length of the known fields after the signature (the minimum length of <see cref="GetSignedData"/>).
    /// </summary>
    public const int SignedFieldsLength = 32 + ShortChannelId.Length + 4 + 1 + 1 + 2 + 8 + 4 + 4 + 8;

    /// <summary>
    /// The minimum length of a <c>channel_update</c> payload (without the message type).
    /// </summary>
    public const int MinLength = SignatureLength + SignedFieldsLength;

    /// <summary>
    /// <c>message_flags</c> bit 0 (<c>must_be_one</c>): always set by the origin, ignored by receivers.
    /// </summary>
    public const byte MessageFlagMustBeOne = 0b0000_0001;

    /// <summary>
    /// <c>message_flags</c> bit 1 (<c>dont_forward</c>): the update is for the channel peer only (unannounced channel).
    /// </summary>
    public const byte MessageFlagDontForward = 0b0000_0010;

    /// <summary>
    /// <c>channel_flags</c> bit 0 (<c>direction</c>): 0 when the origin is <c>node_id_1</c>, 1 otherwise.
    /// </summary>
    public const byte ChannelFlagDirection = 0b0000_0001;

    /// <summary>
    /// <c>channel_flags</c> bit 1 (<c>disable</c>): the channel is temporarily or permanently unavailable.
    /// </summary>
    public const byte ChannelFlagDisable = 0b0000_0010;

    private readonly byte[] _signature;
    private readonly byte[] _extraData;

    /// <summary>
    /// The origin's signature (64-byte compact r||s) of <see cref="GetSignatureHash"/>, made with its node key.
    /// </summary>
    /// <remarks>
    /// <see cref="CompactSignature.Value"/> is a mutable array, so the payload keeps its own copy and every read
    /// returns a fresh one: editing the returned (or the originally passed) signature never changes the payload.
    /// </remarks>
    public CompactSignature Signature => new(_signature.ToArray());

    /// <summary>
    /// The chain the channel is on.
    /// </summary>
    public ChainHash ChainHash { get; }

    /// <summary>
    /// The real short channel id, or an alias received from the peer (unannounced channels).
    /// </summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>
    /// The update's timestamp (normally a UNIX timestamp); must increase with every update of the channel.
    /// </summary>
    public uint Timestamp { get; }

    /// <summary>
    /// The raw <c>message_flags</c> byte (see <see cref="MessageFlagMustBeOne"/>, <see cref="MessageFlagDontForward"/>).
    /// </summary>
    public byte MessageFlags { get; }

    /// <summary>
    /// The raw <c>channel_flags</c> byte (see <see cref="ChannelFlagDirection"/>, <see cref="ChannelFlagDisable"/>).
    /// </summary>
    public byte ChannelFlags { get; }

    /// <summary>
    /// The number of blocks the origin subtracts from an incoming HTLC's <c>cltv_expiry</c>.
    /// </summary>
    public ushort CltvExpiryDelta { get; }

    /// <summary>
    /// The minimum HTLC value, in msat, the channel peer will accept.
    /// </summary>
    public ulong HtlcMinimumMsat { get; }

    /// <summary>
    /// The base fee, in msat, charged for any HTLC.
    /// </summary>
    public uint FeeBaseMsat { get; }

    /// <summary>
    /// The proportional fee, in millionths of the forwarded amount.
    /// </summary>
    public uint FeeProportionalMillionths { get; }

    /// <summary>
    /// The maximum value, in msat, the origin will send through this channel in a single HTLC.
    /// </summary>
    public ulong HtlcMaximumMsat { get; }

    /// <summary>
    /// Unknown fields after <c>htlc_maximum_msat</c>, kept verbatim because the signature covers them.
    /// </summary>
    public ReadOnlyMemory<byte> ExtraData => _extraData;

    /// <summary>
    /// The <c>direction</c> bit: <c>false</c> when the origin is <c>node_id_1</c>, <c>true</c> when it is
    /// <c>node_id_2</c>.
    /// </summary>
    public bool Direction => (ChannelFlags & ChannelFlagDirection) != 0;

    /// <summary>
    /// The <c>disable</c> bit.
    /// </summary>
    public bool IsDisabled => (ChannelFlags & ChannelFlagDisable) != 0;

    /// <summary>
    /// The <c>dont_forward</c> bit.
    /// </summary>
    public bool DontForward => (MessageFlags & MessageFlagDontForward) != 0;

    /// <summary>
    /// Creates a <c>channel_update</c> payload.
    /// </summary>
    /// <param name="signature">
    /// The 64-byte signature. Pass <see cref="EmptySignature"/> for an update that is signed afterwards with
    /// <see cref="WithSignature"/>.
    /// </param>
    /// <param name="chainHash">The chain hash.</param>
    /// <param name="shortChannelId">The short channel id (or alias).</param>
    /// <param name="timestamp">The timestamp.</param>
    /// <param name="messageFlags">The raw <c>message_flags</c>.</param>
    /// <param name="channelFlags">The raw <c>channel_flags</c>.</param>
    /// <param name="cltvExpiryDelta">The cltv_expiry_delta.</param>
    /// <param name="htlcMinimumMsat">The htlc_minimum_msat.</param>
    /// <param name="feeBaseMsat">The fee_base_msat.</param>
    /// <param name="feeProportionalMillionths">The fee_proportional_millionths.</param>
    /// <param name="htlcMaximumMsat">The htlc_maximum_msat.</param>
    /// <param name="extraData">Unknown trailing fields (normally empty).</param>
    /// <exception cref="ArgumentException">The signature is not 64 bytes.</exception>
    public ChannelUpdatePayload(CompactSignature signature, ChainHash chainHash, ShortChannelId shortChannelId,
                                uint timestamp, byte messageFlags, byte channelFlags, ushort cltvExpiryDelta,
                                ulong htlcMinimumMsat, uint feeBaseMsat, uint feeProportionalMillionths,
                                ulong htlcMaximumMsat, ReadOnlyMemory<byte> extraData = default)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != SignatureLength)
            throw new ArgumentException($"A channel_update signature is {SignatureLength} bytes.",
                                        nameof(signature));

        _signature = signature.Value.ToArray();
        ChainHash = chainHash;
        ShortChannelId = shortChannelId;
        Timestamp = timestamp;
        MessageFlags = messageFlags;
        ChannelFlags = channelFlags;
        CltvExpiryDelta = cltvExpiryDelta;
        HtlcMinimumMsat = htlcMinimumMsat;
        FeeBaseMsat = feeBaseMsat;
        FeeProportionalMillionths = feeProportionalMillionths;
        HtlcMaximumMsat = htlcMaximumMsat;
        _extraData = extraData.ToArray();
    }

    /// <summary>
    /// A 64-byte all-zero signature, the placeholder of an update that is not signed yet.
    /// </summary>
    public static CompactSignature EmptySignature => new(new byte[SignatureLength]);

    /// <summary>
    /// Returns a copy of this payload with <paramref name="signature"/>.
    /// </summary>
    public ChannelUpdatePayload WithSignature(CompactSignature signature) =>
        new(signature, ChainHash, ShortChannelId, Timestamp, MessageFlags, ChannelFlags, CltvExpiryDelta,
            HtlcMinimumMsat, FeeBaseMsat, FeeProportionalMillionths, HtlcMaximumMsat, _extraData);

    /// <summary>
    /// The bytes the signature covers: every field after <c>signature</c>, including <see cref="ExtraData"/>.
    /// </summary>
    public byte[] GetSignedData()
    {
        var data = new byte[SignedFieldsLength + _extraData.Length];
        WriteSignedData(data);
        return data;
    }

    /// <summary>
    /// The hash to sign or verify with the origin's node key: <c>SHA256(SHA256(</c><see cref="GetSignedData"/><c>))</c>.
    /// </summary>
    public Hash GetSignatureHash()
    {
        return SHA256.HashData(SHA256.HashData(GetSignedData()));
    }

    /// <summary>
    /// The wire bytes of the payload (without the message type): <c>signature || </c><see cref="GetSignedData"/>.
    /// </summary>
    public byte[] GetBytes()
    {
        var bytes = new byte[MinLength + _extraData.Length];
        _signature.CopyTo(bytes, 0);
        WriteSignedData(bytes.AsSpan(SignatureLength));
        return bytes;
    }

    /// <summary>
    /// Parses a <c>channel_update</c> payload (without the message type). Bytes after <c>htlc_maximum_msat</c> are kept
    /// as <see cref="ExtraData"/>. Semantic rules (flags, htlc_minimum/maximum) are not checked.
    /// </summary>
    /// <exception cref="ArgumentException">The payload is shorter than <see cref="MinLength"/>.</exception>
    public static ChannelUpdatePayload Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MinLength)
            throw new ArgumentException($"A channel_update payload is at least {MinLength} bytes, got {payload.Length}.",
                                        nameof(payload));

        var offset = 0;
        var signature = new CompactSignature(payload.Slice(offset, SignatureLength).ToArray());
        offset += SignatureLength;
        var chainHash = new ChainHash(payload.Slice(offset, CryptoConstants.Sha256HashLen));
        offset += CryptoConstants.Sha256HashLen;
        var shortChannelId = new ShortChannelId(payload.Slice(offset, ShortChannelId.Length).ToArray());
        offset += ShortChannelId.Length;
        var timestamp = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        offset += sizeof(uint);
        var messageFlags = payload[offset++];
        var channelFlags = payload[offset++];
        var cltvExpiryDelta = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += sizeof(ushort);
        var htlcMinimumMsat = BinaryPrimitives.ReadUInt64BigEndian(payload[offset..]);
        offset += sizeof(ulong);
        var feeBaseMsat = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        offset += sizeof(uint);
        var feeProportionalMillionths = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
        offset += sizeof(uint);
        var htlcMaximumMsat = BinaryPrimitives.ReadUInt64BigEndian(payload[offset..]);
        offset += sizeof(ulong);

        return new ChannelUpdatePayload(signature, chainHash, shortChannelId, timestamp, messageFlags, channelFlags,
                                        cltvExpiryDelta, htlcMinimumMsat, feeBaseMsat, feeProportionalMillionths,
                                        htlcMaximumMsat, payload[offset..].ToArray());
    }

    /// <summary>
    /// Parses a <c>channel_update</c> payload (without the message type).
    /// </summary>
    /// <returns><c>false</c> if the payload is shorter than <see cref="MinLength"/>.</returns>
    public static bool TryParse(ReadOnlySpan<byte> payload,
                                [NotNullWhen(true)] out ChannelUpdatePayload? channelUpdate)
    {
        channelUpdate = payload.Length < MinLength ? null : Parse(payload);
        return channelUpdate is not null;
    }

    private void WriteSignedData(Span<byte> destination)
    {
        var offset = 0;
        ChainHash.Value.CopyTo(destination[offset..]);
        offset += CryptoConstants.Sha256HashLen;
        ((ReadOnlySpan<byte>)ShortChannelId).CopyTo(destination[offset..]);
        offset += ShortChannelId.Length;
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], Timestamp);
        offset += sizeof(uint);
        destination[offset++] = MessageFlags;
        destination[offset++] = ChannelFlags;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], CltvExpiryDelta);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], HtlcMinimumMsat);
        offset += sizeof(ulong);
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], FeeBaseMsat);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], FeeProportionalMillionths);
        offset += sizeof(uint);
        BinaryPrimitives.WriteUInt64BigEndian(destination[offset..], HtlcMaximumMsat);
        offset += sizeof(ulong);
        _extraData.CopyTo(destination[offset..]);
    }
}