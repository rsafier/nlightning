using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Payloads;

using Channels.ValueObjects;
using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;
using GossipV2;
using Interfaces;
using ValueObjects;
using Tlvs = GossipV2.GossipV2Constants.ChannelUpdate2;

/// <summary>
/// The payload of <c>channel_update_2</c> (taproot gossip, BOLTs PR #1059, type 271): a pure TLV stream of
/// <c>chain_hash</c> 0, <c>short_channel_id</c> 2 (the <c>sciddir</c> form: direction byte || scid), <c>block_height</c>
/// 4, <c>disable_flags</c> 6, <c>cltv_expiry_delta</c> 10, <c>htlc_minimum_msat</c> 12 (tu64), <c>htlc_maximum_msat</c>
/// 14 (tu64), <c>fee_base_msat</c> 16 (tu32), <c>fee_proportional_millionths</c> 18 (tu32), the positive-only inbound
/// fees 20 and 22 (tu32) and the origin's BIP 340 <c>signature</c> 240.
/// </summary>
/// <remarks>
/// Absent records take the draft's defaults (<c>cltv_expiry_delta</c> 80, <c>htlc_minimum_msat</c> 1,
/// <c>fee_base_msat</c> 1000, <c>fee_proportional_millionths</c> 1, inbound fees 0, no disable flag);
/// <c>htlc_maximum_msat</c> defaults to half the capacity, which the message does not carry, so
/// <see cref="HtlcMaximumMsat"/> stays null and the graph applies the default. The records are kept as received
/// (<see cref="Stream"/>), so the message re-serializes byte for byte. The timestamp is a block height.
/// </remarks>
public sealed class ChannelUpdate2Payload : IMessagePayload
{
    /// <summary>The length of the <c>sciddir</c> record (direction byte || scid).</summary>
    public const int SciddirLength = 1 + ShortChannelId.Length;

    /// <summary><c>disable_flags</c> bit 0 (<c>permanent</c>): the channel can be considered closed.</summary>
    public const byte DisablePermanent = 0b001;

    /// <summary><c>disable_flags</c> bit 1 (<c>incoming</c>): the node can't receive via this channel.</summary>
    public const byte DisableIncoming = 0b010;

    /// <summary><c>disable_flags</c> bit 2 (<c>outgoing</c>): the node can't forward or send via this channel.</summary>
    public const byte DisableOutgoing = 0b100;

    /// <summary>The record types the message defines (an unknown even type fails it).</summary>
    public static readonly IReadOnlySet<ulong> KnownTypes = new HashSet<ulong>
    {
        Tlvs.ChainHash, Tlvs.ShortChannelId, Tlvs.BlockHeight, Tlvs.DisableFlags, Tlvs.CltvExpiryDelta,
        Tlvs.HtlcMinimumMsat, Tlvs.HtlcMaximumMsat, Tlvs.FeeBaseMsat, Tlvs.FeeProportionalMillionths,
        Tlvs.InboundFeeBaseMsat, Tlvs.InboundFeeProportionalMillionths, Tlvs.Signature
    };

    private ChannelUpdate2Payload(PureTlvStream stream)
    {
        Stream = stream;
        var chainHash = PureTlvFields.Fixed(stream, Tlvs.ChainHash, CryptoConstants.Sha256HashLen);
        HasChainHash = chainHash is not null;
        ChainHash = chainHash is { } c ? new ChainHash(c.Span) : ChainConstants.Main;

        // BOLTs PR #1059: the sciddir form only (direction 0 or 1); the pubkey form MUST NOT be used
        var sciddir = PureTlvFields.RequiredFixed(stream, Tlvs.ShortChannelId, SciddirLength).Span;
        if (sciddir[0] > 1)
            throw new FormatException("channel_update_2 short_channel_id is not in the sciddir form.");
        Direction = sciddir[0];
        ShortChannelId = new ShortChannelId(sciddir[1..].ToArray());

        BlockHeight = PureTlvFields.U32(stream, Tlvs.BlockHeight) ?? throw PureTlvFields.Missing(Tlvs.BlockHeight);
        DisableFlags = PureTlvFields.Fixed(stream, Tlvs.DisableFlags, 1) is { } flags ? flags.Span[0] : (byte)0;
        CltvExpiryDelta = PureTlvFields.U16(stream, Tlvs.CltvExpiryDelta) ?? Tlvs.DefaultCltvExpiryDelta;
        HtlcMinimumMsat = PureTlvFields.Tu64(stream, Tlvs.HtlcMinimumMsat) ?? Tlvs.DefaultHtlcMinimumMsat;
        HtlcMaximumMsat = PureTlvFields.Tu64(stream, Tlvs.HtlcMaximumMsat);
        FeeBaseMsat = PureTlvFields.Tu32(stream, Tlvs.FeeBaseMsat) ?? Tlvs.DefaultFeeBaseMsat;
        FeeProportionalMillionths = PureTlvFields.Tu32(stream, Tlvs.FeeProportionalMillionths)
                                 ?? Tlvs.DefaultFeeProportionalMillionths;
        InboundFeeBaseMsat = PureTlvFields.Tu32(stream, Tlvs.InboundFeeBaseMsat) ?? 0;
        InboundFeeProportionalMillionths = PureTlvFields.Tu32(stream, Tlvs.InboundFeeProportionalMillionths) ?? 0;
        Signature = new CompactSignature(PureTlvFields.RequiredFixed(stream, Tlvs.Signature,
                                                                     GossipV2Constants.SignatureLength).ToArray());
    }

    /// <summary>The records as received or built, in wire order.</summary>
    public PureTlvStream Stream { get; }

    /// <summary>The chain (bitcoin mainnet when the record is absent).</summary>
    public ChainHash ChainHash { get; }

    /// <summary>Whether the <c>chain_hash</c> record is present.</summary>
    public bool HasChainHash { get; }

    /// <summary>The channel's real scid (or an agreed alias, for an unannounced channel).</summary>
    public ShortChannelId ShortChannelId { get; }

    /// <summary>The direction byte: 0 when the origin is <c>node_id_1</c>, 1 when it is <c>node_id_2</c>.</summary>
    public byte Direction { get; }

    /// <summary>The update's timestamp: a block height.</summary>
    public uint BlockHeight { get; }

    /// <summary>The raw <c>disable_flags</c> (0 when the record is absent).</summary>
    public byte DisableFlags { get; }

    /// <summary>Whether any disable bit is set (the channel must not be used for routing).</summary>
    public bool IsDisabled => DisableFlags != 0;

    /// <summary>The <c>cltv_expiry_delta</c> (80 when absent).</summary>
    public ushort CltvExpiryDelta { get; }

    /// <summary>The <c>htlc_minimum_msat</c> (1 when absent).</summary>
    public ulong HtlcMinimumMsat { get; }

    /// <summary>The <c>htlc_maximum_msat</c>, or null when absent (then half the channel capacity).</summary>
    public ulong? HtlcMaximumMsat { get; }

    /// <summary>The outbound <c>fee_base_msat</c> (1000 when absent).</summary>
    public uint FeeBaseMsat { get; }

    /// <summary>The outbound <c>fee_proportional_millionths</c> (1 when absent).</summary>
    public uint FeeProportionalMillionths { get; }

    /// <summary>The inbound base surcharge for HTLCs arriving on this channel (0 when absent).</summary>
    public uint InboundFeeBaseMsat { get; }

    /// <summary>The inbound proportional surcharge (0 when absent).</summary>
    public uint InboundFeeProportionalMillionths { get; }

    /// <summary>The origin's BIP 340 signature.</summary>
    public CompactSignature Signature { get; }

    /// <summary>The bytes the signature covers.</summary>
    public byte[] GetSignedData() => Stream.GetSignedBytes();

    /// <summary>The BIP 340 message: <c>MsgHash("channel_update_2", "signature", m)</c>.</summary>
    public Hash GetSignatureHash() =>
        GossipV2MsgHash.ComputeSignatureHash(GossipV2Constants.ChannelUpdate2Name, Stream);

    /// <summary>The wire bytes of the payload (without the message type).</summary>
    public byte[] GetBytes() => Stream.GetBytes();

    /// <summary>
    /// The policy fields without the signature and the block height, the BOLT 7 checksum input of an update
    /// (<c>reply_channel_range</c> checksums; the draft extends BOLT 7's rule to <c>channel_update_2</c>).
    /// </summary>
    public byte[] GetChecksumData() =>
        new PureTlvStream(Stream.Records.Where(r => r.Type != Tlvs.Signature && r.Type != Tlvs.BlockHeight))
           .GetBytes();

    /// <summary>Returns a copy with <paramref name="signature"/>; the rest keeps its bytes.</summary>
    public ChannelUpdate2Payload WithSignature(CompactSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        if (signature.Value.Length != GossipV2Constants.SignatureLength)
            throw new ArgumentException($"A BIP 340 signature is {GossipV2Constants.SignatureLength} bytes.",
                                        nameof(signature));

        return new ChannelUpdate2Payload(Stream.With(new PureTlvRecord(Tlvs.Signature, signature.Value)));
    }

    /// <summary>
    /// Builds an update (our own) with an all-zero signature, to be signed with <see cref="WithSignature"/>. Fields
    /// equal to the draft's defaults are left out ("SHOULD not include the field ... if the default value is
    /// desired"); <c>chain_hash</c> is written only off mainnet; <paramref name="htlcMaximumMsat"/> is always written.
    /// </summary>
    public static ChannelUpdate2Payload Create(ChainHash chainHash, ShortChannelId shortChannelId, byte direction,
                                               uint blockHeight, byte disableFlags, ushort cltvExpiryDelta,
                                               ulong htlcMinimumMsat, ulong htlcMaximumMsat, uint feeBaseMsat,
                                               uint feeProportionalMillionths, uint inboundFeeBaseMsat = 0,
                                               uint inboundFeeProportionalMillionths = 0)
    {
        if (direction > 1)
            throw new ArgumentOutOfRangeException(nameof(direction), "The direction is 0 or 1.");

        var records = new List<PureTlvRecord>();
        if (chainHash != ChainConstants.Main)
            records.Add(new PureTlvRecord(Tlvs.ChainHash, chainHash.Value));

        var sciddir = new byte[SciddirLength];
        sciddir[0] = direction;
        ((ReadOnlySpan<byte>)shortChannelId).CopyTo(sciddir.AsSpan(1));
        records.Add(new PureTlvRecord(Tlvs.ShortChannelId, sciddir));
        records.Add(PureTlvFields.U32Record(Tlvs.BlockHeight, blockHeight));
        if (disableFlags != 0)
            records.Add(new PureTlvRecord(Tlvs.DisableFlags, [disableFlags]));
        if (cltvExpiryDelta != Tlvs.DefaultCltvExpiryDelta)
            records.Add(PureTlvFields.U16Record(Tlvs.CltvExpiryDelta, cltvExpiryDelta));
        if (htlcMinimumMsat != Tlvs.DefaultHtlcMinimumMsat)
            records.Add(PureTlvFields.Tu64Record(Tlvs.HtlcMinimumMsat, htlcMinimumMsat));
        records.Add(PureTlvFields.Tu64Record(Tlvs.HtlcMaximumMsat, htlcMaximumMsat));
        if (feeBaseMsat != Tlvs.DefaultFeeBaseMsat)
            records.Add(PureTlvFields.Tu32Record(Tlvs.FeeBaseMsat, feeBaseMsat));
        if (feeProportionalMillionths != Tlvs.DefaultFeeProportionalMillionths)
            records.Add(PureTlvFields.Tu32Record(Tlvs.FeeProportionalMillionths, feeProportionalMillionths));
        if (inboundFeeBaseMsat != 0)
            records.Add(PureTlvFields.Tu32Record(Tlvs.InboundFeeBaseMsat, inboundFeeBaseMsat));
        if (inboundFeeProportionalMillionths != 0)
            records.Add(PureTlvFields.Tu32Record(Tlvs.InboundFeeProportionalMillionths,
                                                 inboundFeeProportionalMillionths));
        records.Add(new PureTlvRecord(Tlvs.Signature, new byte[GossipV2Constants.SignatureLength]));

        return new ChannelUpdate2Payload(new PureTlvStream(records));
    }

    /// <summary>Builds the payload from parsed records.</summary>
    /// <exception cref="FormatException">A required record is missing or a known one is malformed.</exception>
    public static ChannelUpdate2Payload FromStream(PureTlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new ChannelUpdate2Payload(stream);
    }

    /// <summary>Parses the payload (without the message type).</summary>
    /// <exception cref="FormatException">The stream or a known record is malformed, or a required record is missing.</exception>
    public static ChannelUpdate2Payload Parse(ReadOnlySpan<byte> payload) =>
        new(PureTlvStream.Parse(payload, KnownTypes));

    /// <summary>
    /// Parses the payload, or returns false when it is malformed (stored bytes read back by the graph, NL-878).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ChannelUpdate2Payload? result)
    {
        try
        {
            result = Parse(payload);
            return true;
        }
        catch (FormatException)
        {
            result = null;
            return false;
        }
    }
}