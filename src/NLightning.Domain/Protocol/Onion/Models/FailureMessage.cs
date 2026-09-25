using System.Buffers.Binary;

namespace NLightning.Domain.Protocol.Onion.Models;

using Crypto.Constants;
using Enums;
using Factories;
using Money;
using Protocol.Models;
using Protocol.Tlv;
using Protocol.ValueObjects;

/// <summary>
/// A BOLT 4 <c>failuremsg</c>: <c>u16 failure_code || data || [tlv_stream]</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Data"/> holds exactly the code-specific data (without the code and without the TLV extension). For every
/// code defined in BOLT 4 the constructor checks that the data has the layout of that code (fixed fields, and for the
/// UPDATE codes a <c>u16 len || channel_update</c> whose length matches). Codes this implementation does not know keep
/// all bytes after the code as <see cref="Data"/>, since there is no way to tell where their data ends.
/// </para>
/// <para>
/// No failure TLV types are defined yet, so <see cref="Extension"/> holds raw <see cref="BaseTlv"/> records.
/// </para>
/// </remarks>
public sealed class FailureMessage
{
    /// <summary>
    /// The length of the <c>sha256_of_onion</c> carried by BADONION failures.
    /// </summary>
    public const int Sha256OfOnionLength = CryptoConstants.Sha256HashLen;

    /// <summary>
    /// The length of the <c>failure_code</c> field.
    /// </summary>
    public const int CodeLength = sizeof(ushort);

    /// <summary>
    /// The failure code.
    /// </summary>
    public FailureCode Code { get; }

    /// <summary>
    /// The code-specific data, without the code or the TLV extension.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// The optional TLV stream following the data, if any.
    /// </summary>
    public TlvStream? Extension { get; }

    /// <summary>
    /// True when <see cref="Code"/> is one of the failure codes defined in BOLT 4.
    /// </summary>
    public bool IsKnownCode => IsKnown(Code);

    /// <summary>
    /// The <c>sha256_of_onion</c> of a BADONION failure (<c>invalid_onion_version</c>, <c>invalid_onion_hmac</c>,
    /// <c>invalid_onion_key</c>, <c>invalid_onion_blinding</c>); otherwise <c>null</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? Sha256OfOnion =>
        Code is FailureCode.InvalidOnionVersion or FailureCode.InvalidOnionHmac or FailureCode.InvalidOnionKey
                 or FailureCode.InvalidOnionBlinding
            ? Data
            : (ReadOnlyMemory<byte>?)null;

    /// <summary>
    /// The <c>channel_update</c> of an UPDATE failure (possibly empty: <c>len = 0</c> is allowed); otherwise
    /// <c>null</c>.
    /// </summary>
    public ReadOnlyMemory<byte>? ChannelUpdate
    {
        get
        {
            var prefixLength = GetChannelUpdatePrefixLength(Code);
            return prefixLength < 0 ? (ReadOnlyMemory<byte>?)null : Data[(prefixLength + sizeof(ushort))..];
        }
    }

    /// <summary>
    /// The <c>htlc_msat</c> of <c>amount_below_minimum</c>, <c>fee_insufficient</c> and
    /// <c>incorrect_or_unknown_payment_details</c>, or the <c>incoming_htlc_amt</c> of
    /// <c>final_incorrect_htlc_amount</c>; otherwise <c>null</c>.
    /// </summary>
    public LightningMoney? HtlcAmount =>
        Code is FailureCode.AmountBelowMinimum or FailureCode.FeeInsufficient
                 or FailureCode.IncorrectOrUnknownPaymentDetails or FailureCode.FinalIncorrectHtlcAmount
            ? LightningMoney.MilliSatoshis(BinaryPrimitives.ReadUInt64BigEndian(Data.Span))
            : null;

    /// <summary>
    /// The <c>cltv_expiry</c> of <c>incorrect_cltv_expiry</c> and <c>final_incorrect_cltv_expiry</c>; otherwise
    /// <c>null</c>.
    /// </summary>
    public uint? CltvExpiry =>
        Code is FailureCode.IncorrectCltvExpiry or FailureCode.FinalIncorrectCltvExpiry
            ? BinaryPrimitives.ReadUInt32BigEndian(Data.Span)
            : null;

    /// <summary>
    /// The <c>height</c> of <c>incorrect_or_unknown_payment_details</c>; otherwise <c>null</c>.
    /// </summary>
    public uint? Height =>
        Code == FailureCode.IncorrectOrUnknownPaymentDetails
            ? BinaryPrimitives.ReadUInt32BigEndian(Data.Span[sizeof(ulong)..])
            : null;

    /// <summary>
    /// The <c>disabled_flags</c> of <c>channel_disabled</c>; otherwise <c>null</c>.
    /// </summary>
    public ushort? DisabledFlags =>
        Code == FailureCode.ChannelDisabled ? BinaryPrimitives.ReadUInt16BigEndian(Data.Span) : null;

    /// <summary>
    /// The TLV <c>type</c> reported by <c>invalid_onion_payload</c>; otherwise <c>null</c>.
    /// </summary>
    public BigSize? InvalidPayloadType =>
        Code == FailureCode.InvalidOnionPayload
     && InvalidOnionPayloadFailureFactory.TryDecodeData(Data.Span, out var type, out _)
            ? type
            : null;

    /// <summary>
    /// The byte <c>offset</c> reported by <c>invalid_onion_payload</c>; otherwise <c>null</c>.
    /// </summary>
    public ushort? InvalidPayloadOffset =>
        Code == FailureCode.InvalidOnionPayload
     && InvalidOnionPayloadFailureFactory.TryDecodeData(Data.Span, out _, out var offset)
            ? offset
            : null;

    /// <summary>
    /// Creates a failure message.
    /// </summary>
    /// <param name="code">The failure code.</param>
    /// <param name="data">The code-specific data (without the code or the TLV extension).</param>
    /// <param name="extension">The optional TLV extension; an empty stream is stored as <c>null</c>.</param>
    /// <exception cref="ArgumentException">
    /// If <paramref name="code"/> is defined in BOLT 4 and <paramref name="data"/> does not have its layout.
    /// </exception>
    public FailureMessage(FailureCode code, ReadOnlyMemory<byte> data, TlvStream? extension = null)
    {
        if (!TryGetDataLength(code, data.Span, out var dataLength) || dataLength != data.Length)
            throw new ArgumentException($"Invalid data for failure code 0x{(ushort)code:x4} ({code}).", nameof(data));

        Code = code;
        Data = data.ToArray();
        Extension = extension is not null && extension.Any() ? extension : null;
    }

    /// <summary>
    /// Returns a copy of this message with the given TLV extension.
    /// </summary>
    public FailureMessage WithExtension(TlvStream? extension)
    {
        return new FailureMessage(Code, Data, extension);
    }

    /// <summary>
    /// True when <see cref="Code"/> carries a <c>u16 len || channel_update</c> field (the UPDATE codes defined in
    /// BOLT 4: <c>temporary_channel_failure</c>, <c>amount_below_minimum</c>, <c>fee_insufficient</c>,
    /// <c>incorrect_cltv_expiry</c>, <c>expiry_too_soon</c> and <c>channel_disabled</c>).
    /// </summary>
    public bool HasChannelUpdateField => GetChannelUpdatePrefixLength(Code) >= 0;

    /// <summary>
    /// Returns a copy of this UPDATE failure whose <c>channel_update</c> field is replaced by
    /// <paramref name="channelUpdate"/> (the fixed fields before it and the TLV extension are kept).
    /// </summary>
    /// <remarks>
    /// Pass the bytes exactly as they go on the wire, e.g. from
    /// <see cref="Factories.FailureChannelUpdateFactory.Encode"/>. An empty span writes <c>len = 0</c>, which BOLT 4
    /// now recommends (a <c>channel_update</c> in an onion error is a fingerprinting vector).
    /// </remarks>
    /// <exception cref="InvalidOperationException">If <see cref="Code"/> has no channel_update field.</exception>
    /// <exception cref="ArgumentException">If <paramref name="channelUpdate"/> is longer than 65535 bytes.</exception>
    public FailureMessage WithChannelUpdate(ReadOnlySpan<byte> channelUpdate)
    {
        var prefixLength = GetChannelUpdatePrefixLength(Code);
        if (prefixLength < 0)
            throw new InvalidOperationException(
                $"Failure code 0x{(ushort)Code:x4} ({Code}) does not carry a channel_update.");

        var updated = CreateWithChannelUpdate(Code, Data.Span[..prefixLength], channelUpdate);
        return new FailureMessage(Code, updated.Data, Extension);
    }

    /// <summary>
    /// Builds the failure an upstream node returns (in <c>update_fail_htlc</c>) when its outgoing HTLC was failed with
    /// <c>update_fail_malformed_htlc</c>: BOLT 2 says to use the <c>failure_code</c> given and set the data to
    /// <c>sha256_of_onion</c>.
    /// </summary>
    /// <param name="failureCode">The <c>failure_code</c> of the <c>update_fail_malformed_htlc</c>.</param>
    /// <param name="sha256OfOnion">The <c>sha256_of_onion</c> of the <c>update_fail_malformed_htlc</c>.</param>
    /// <exception cref="ArgumentException">
    /// If the BADONION bit of <paramref name="failureCode"/> is not set (BOLT 2: the receiver MUST send a
    /// <c>warning</c> and close the connection, or fail the channel), or <paramref name="sha256OfOnion"/> is not 32
    /// bytes.
    /// </exception>
    public static FailureMessage FromMalformed(FailureCode failureCode, ReadOnlySpan<byte> sha256OfOnion)
    {
        if (((ushort)failureCode & (ushort)FailureCodeFlags.BadOnion) == 0)
            throw new ArgumentException(
                $"update_fail_malformed_htlc failure_code 0x{(ushort)failureCode:x4} does not have the BADONION bit.",
                nameof(failureCode));

        // Unknown BADONION codes are carried through too: their data is the sha256_of_onion as well
        return CreateBadOnion(failureCode, sha256OfOnion);
    }

    /// <summary>
    /// Gets the length of the code-specific data at the start of <paramref name="dataAndTail"/>, which may be followed
    /// by a TLV stream or other trailing bytes.
    /// </summary>
    /// <remarks>
    /// For a code not defined in BOLT 4 the whole of <paramref name="dataAndTail"/> is data.
    /// </remarks>
    /// <returns><c>false</c> if the data is truncated or malformed for <paramref name="code"/>.</returns>
    public static bool TryGetDataLength(FailureCode code, ReadOnlySpan<byte> dataAndTail, out int length)
    {
        length = 0;

        var channelUpdatePrefixLength = GetChannelUpdatePrefixLength(code);
        if (channelUpdatePrefixLength >= 0)
        {
            if (dataAndTail.Length < channelUpdatePrefixLength + sizeof(ushort))
                return false;

            var channelUpdateLength =
                BinaryPrimitives.ReadUInt16BigEndian(dataAndTail.Slice(channelUpdatePrefixLength, sizeof(ushort)));
            length = channelUpdatePrefixLength + sizeof(ushort) + channelUpdateLength;
            return dataAndTail.Length >= length;
        }

        switch (code)
        {
            case FailureCode.TemporaryNodeFailure:
            case FailureCode.PermanentNodeFailure:
            case FailureCode.RequiredNodeFeatureMissing:
            case FailureCode.PermanentChannelFailure:
            case FailureCode.RequiredChannelFeatureMissing:
            case FailureCode.UnknownNextPeer:
            case FailureCode.ExpiryTooFar:
            case FailureCode.MppTimeout:
                length = 0;
                return true;
            case FailureCode.InvalidOnionVersion:
            case FailureCode.InvalidOnionHmac:
            case FailureCode.InvalidOnionKey:
            case FailureCode.InvalidOnionBlinding:
                length = Sha256OfOnionLength;
                break;
            case FailureCode.IncorrectOrUnknownPaymentDetails:
                length = sizeof(ulong) + sizeof(uint);
                break;
            case FailureCode.FinalIncorrectCltvExpiry:
                length = sizeof(uint);
                break;
            case FailureCode.FinalIncorrectHtlcAmount:
                length = sizeof(ulong);
                break;
            case FailureCode.InvalidOnionPayload:
                if (!BigSizeCodec.TryRead(dataAndTail, out _, out var typeLength))
                    return false;

                length = typeLength + sizeof(ushort);
                break;
            default:
                length = dataAndTail.Length;
                return true;
        }

        return dataAndTail.Length >= length;
    }

    /// <summary>
    /// True when <paramref name="code"/> is one of the failure codes defined in BOLT 4.
    /// </summary>
    public static bool IsKnown(FailureCode code) => Enum.IsDefined(code);

    #region Factories

    /// <summary>NODE|2 <c>temporary_node_failure</c>.</summary>
    public static FailureMessage TemporaryNodeFailure() => WithoutData(FailureCode.TemporaryNodeFailure);

    /// <summary>PERM|NODE|2 <c>permanent_node_failure</c>.</summary>
    public static FailureMessage PermanentNodeFailure() => WithoutData(FailureCode.PermanentNodeFailure);

    /// <summary>PERM|NODE|3 <c>required_node_feature_missing</c>.</summary>
    public static FailureMessage RequiredNodeFeatureMissing() => WithoutData(FailureCode.RequiredNodeFeatureMissing);

    /// <summary>BADONION|PERM|4 <c>invalid_onion_version</c> with <c>sha256_of_onion</c>.</summary>
    public static FailureMessage InvalidOnionVersion(ReadOnlySpan<byte> sha256OfOnion) =>
        CreateBadOnion(FailureCode.InvalidOnionVersion, sha256OfOnion);

    /// <summary>BADONION|PERM|5 <c>invalid_onion_hmac</c> with <c>sha256_of_onion</c>.</summary>
    public static FailureMessage InvalidOnionHmac(ReadOnlySpan<byte> sha256OfOnion) =>
        CreateBadOnion(FailureCode.InvalidOnionHmac, sha256OfOnion);

    /// <summary>BADONION|PERM|6 <c>invalid_onion_key</c> with <c>sha256_of_onion</c>.</summary>
    public static FailureMessage InvalidOnionKey(ReadOnlySpan<byte> sha256OfOnion) =>
        CreateBadOnion(FailureCode.InvalidOnionKey, sha256OfOnion);

    /// <summary>UPDATE|7 <c>temporary_channel_failure</c>; <paramref name="channelUpdate"/> may be empty.</summary>
    public static FailureMessage TemporaryChannelFailure(ReadOnlySpan<byte> channelUpdate = default) =>
        CreateWithChannelUpdate(FailureCode.TemporaryChannelFailure, [], channelUpdate);

    /// <summary>PERM|8 <c>permanent_channel_failure</c>.</summary>
    public static FailureMessage PermanentChannelFailure() => WithoutData(FailureCode.PermanentChannelFailure);

    /// <summary>PERM|9 <c>required_channel_feature_missing</c>.</summary>
    public static FailureMessage RequiredChannelFeatureMissing() =>
        WithoutData(FailureCode.RequiredChannelFeatureMissing);

    /// <summary>PERM|10 <c>unknown_next_peer</c>.</summary>
    public static FailureMessage UnknownNextPeer() => WithoutData(FailureCode.UnknownNextPeer);

    /// <summary>UPDATE|11 <c>amount_below_minimum</c>: <c>htlc_msat</c> and an optional channel_update.</summary>
    public static FailureMessage AmountBelowMinimum(LightningMoney htlcAmount,
                                                    ReadOnlySpan<byte> channelUpdate = default) =>
        CreateWithChannelUpdate(FailureCode.AmountBelowMinimum, EncodeU64(htlcAmount), channelUpdate);

    /// <summary>UPDATE|12 <c>fee_insufficient</c>: <c>htlc_msat</c> and an optional channel_update.</summary>
    public static FailureMessage FeeInsufficient(LightningMoney htlcAmount,
                                                 ReadOnlySpan<byte> channelUpdate = default) =>
        CreateWithChannelUpdate(FailureCode.FeeInsufficient, EncodeU64(htlcAmount), channelUpdate);

    /// <summary>UPDATE|13 <c>incorrect_cltv_expiry</c>: <c>cltv_expiry</c> and an optional channel_update.</summary>
    public static FailureMessage IncorrectCltvExpiry(uint cltvExpiry, ReadOnlySpan<byte> channelUpdate = default) =>
        CreateWithChannelUpdate(FailureCode.IncorrectCltvExpiry, EncodeU32(cltvExpiry), channelUpdate);

    /// <summary>UPDATE|14 <c>expiry_too_soon</c>; <paramref name="channelUpdate"/> may be empty.</summary>
    public static FailureMessage ExpiryTooSoon(ReadOnlySpan<byte> channelUpdate = default) =>
        CreateWithChannelUpdate(FailureCode.ExpiryTooSoon, [], channelUpdate);

    /// <summary>PERM|15 <c>incorrect_or_unknown_payment_details</c>: <c>htlc_msat</c> and <c>height</c>.</summary>
    public static FailureMessage IncorrectOrUnknownPaymentDetails(LightningMoney htlcAmount, uint height)
    {
        ArgumentNullException.ThrowIfNull(htlcAmount);

        var data = new byte[sizeof(ulong) + sizeof(uint)];
        BinaryPrimitives.WriteUInt64BigEndian(data, htlcAmount.MilliSatoshi);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(sizeof(ulong)), height);
        return new FailureMessage(FailureCode.IncorrectOrUnknownPaymentDetails, data);
    }

    /// <summary>18 <c>final_incorrect_cltv_expiry</c>: <c>cltv_expiry</c>.</summary>
    public static FailureMessage FinalIncorrectCltvExpiry(uint cltvExpiry) =>
        new(FailureCode.FinalIncorrectCltvExpiry, EncodeU32(cltvExpiry));

    /// <summary>19 <c>final_incorrect_htlc_amount</c>: <c>incoming_htlc_amt</c>.</summary>
    public static FailureMessage FinalIncorrectHtlcAmount(LightningMoney incomingHtlcAmount) =>
        new(FailureCode.FinalIncorrectHtlcAmount, EncodeU64(incomingHtlcAmount));

    /// <summary>
    /// UPDATE|20 <c>channel_disabled</c>: <c>disabled_flags</c> (none defined, so 0) and an optional channel_update.
    /// </summary>
    public static FailureMessage ChannelDisabled(ushort disabledFlags = 0, ReadOnlySpan<byte> channelUpdate = default)
    {
        var flags = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(flags, disabledFlags);
        return CreateWithChannelUpdate(FailureCode.ChannelDisabled, flags, channelUpdate);
    }

    /// <summary>21 <c>expiry_too_far</c>.</summary>
    public static FailureMessage ExpiryTooFar() => WithoutData(FailureCode.ExpiryTooFar);

    /// <summary>PERM|22 <c>invalid_onion_payload</c>: the offending TLV <c>type</c> and its <c>offset</c>.</summary>
    public static FailureMessage InvalidOnionPayload(BigSize type, ushort offset) =>
        new(FailureCode.InvalidOnionPayload, InvalidOnionPayloadFailureFactory.EncodeData(type, offset));

    /// <summary>23 <c>mpp_timeout</c>.</summary>
    public static FailureMessage MppTimeout() => WithoutData(FailureCode.MppTimeout);

    /// <summary>BADONION|PERM|24 <c>invalid_onion_blinding</c> with <c>sha256_of_onion</c>.</summary>
    public static FailureMessage InvalidOnionBlinding(ReadOnlySpan<byte> sha256OfOnion) =>
        CreateBadOnion(FailureCode.InvalidOnionBlinding, sha256OfOnion);

    #endregion

    /// <summary>
    /// Gets the length of the fixed fields that precede <c>u16 len || channel_update</c> for UPDATE codes, or -1 when
    /// <paramref name="code"/> carries no channel_update.
    /// </summary>
    private static int GetChannelUpdatePrefixLength(FailureCode code)
    {
        return code switch
        {
            FailureCode.TemporaryChannelFailure or FailureCode.ExpiryTooSoon => 0,
            FailureCode.AmountBelowMinimum or FailureCode.FeeInsufficient => sizeof(ulong),
            FailureCode.IncorrectCltvExpiry => sizeof(uint),
            FailureCode.ChannelDisabled => sizeof(ushort),
            _ => -1
        };
    }

    private static FailureMessage WithoutData(FailureCode code) => new(code, ReadOnlyMemory<byte>.Empty);

    private static FailureMessage CreateBadOnion(FailureCode code, ReadOnlySpan<byte> sha256OfOnion)
    {
        if (sha256OfOnion.Length != Sha256OfOnionLength)
            throw new ArgumentException($"sha256_of_onion must be {Sha256OfOnionLength} bytes.",
                                        nameof(sha256OfOnion));

        return new FailureMessage(code, sha256OfOnion.ToArray());
    }

    private static FailureMessage CreateWithChannelUpdate(FailureCode code, ReadOnlySpan<byte> prefix,
                                                          ReadOnlySpan<byte> channelUpdate)
    {
        if (channelUpdate.Length > ushort.MaxValue)
            throw new ArgumentException($"channel_update must be at most {ushort.MaxValue} bytes.",
                                        nameof(channelUpdate));

        var data = new byte[prefix.Length + sizeof(ushort) + channelUpdate.Length];
        prefix.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(prefix.Length), (ushort)channelUpdate.Length);
        channelUpdate.CopyTo(data.AsSpan(prefix.Length + sizeof(ushort)));
        return new FailureMessage(code, data);
    }

    private static byte[] EncodeU64(LightningMoney amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        var data = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(data, amount.MilliSatoshi);
        return data;
    }

    private static byte[] EncodeU32(uint value)
    {
        var data = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(data, value);
        return data;
    }
}