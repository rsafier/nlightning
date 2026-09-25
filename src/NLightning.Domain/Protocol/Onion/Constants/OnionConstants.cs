using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Constants;

/// <summary>
/// Constants for BOLT 4 onion routing.
/// </summary>
[ExcludeFromCodeCoverage]
public static class OnionConstants
{
    /// <summary>
    /// The only onion packet version currently defined.
    /// </summary>
    public const byte Version = 0x00;

    /// <summary>
    /// The length of the version field.
    /// </summary>
    public const int VersionLength = 1;

    /// <summary>
    /// The length of the (compressed) ephemeral public key in the packet.
    /// </summary>
    public const int PublicKeyLength = 33;

    /// <summary>
    /// The length of the hop_payloads field of a payment onion.
    /// </summary>
    public const int HopPayloadsLength = 1300;

    /// <summary>
    /// The length of an HMAC in the onion packet and in each hop payload.
    /// </summary>
    public const int HmacLength = 32;

    /// <summary>
    /// The fixed overhead of a packet: version + public_key + hmac.
    /// </summary>
    public const int PacketOverheadLength = VersionLength + PublicKeyLength + HmacLength;

    /// <summary>
    /// The total length of a payment onion_packet (1366 bytes).
    /// </summary>
    public const int PacketLength = PacketOverheadLength + HopPayloadsLength;

    /// <summary>
    /// The maximum length of an error packet.
    /// </summary>
    public const int MaxErrorPacketLength = 32768;

    /// <summary>
    /// The minimum value of failure_len + pad_len in an error packet.
    /// </summary>
    public const int MinFailurePadLength = 256;

    /// <summary>
    /// The max_htlc_cltv, in blocks, above which an HTLC fails with expiry_too_far.
    /// </summary>
    public const int MaxHtlcCltv = 2016;

    /// <summary>
    /// The number of constant iterations the origin node should run when decrypting an error packet.
    /// </summary>
    public const int ErrorDecryptionIterations = 27;

    /// <summary>
    /// Key type used to generate the hop_payloads obfuscation stream ("rho").
    /// </summary>
    public static ReadOnlySpan<byte> Rho => "rho"u8;

    /// <summary>
    /// Key type used to generate the packet HMAC ("mu").
    /// </summary>
    public static ReadOnlySpan<byte> Mu => "mu"u8;

    /// <summary>
    /// Key type used to generate the error packet HMAC ("um").
    /// </summary>
    public static ReadOnlySpan<byte> Um => "um"u8;

    /// <summary>
    /// Key type used to generate the initial mix-header filler from the session key ("pad").
    /// </summary>
    public static ReadOnlySpan<byte> Pad => "pad"u8;

    /// <summary>
    /// Key type used to obfuscate error packets ("ammag").
    /// </summary>
    public static ReadOnlySpan<byte> Ammag => "ammag"u8;

    /// <summary>
    /// Key type used to obfuscate attribution data ("ammagext").
    /// </summary>
    public static ReadOnlySpan<byte> AmmagExt => "ammagext"u8;

    /// <summary>
    /// Key type used to tweak node ids in route blinding ("blinded_node_id").
    /// </summary>
    public static ReadOnlySpan<byte> BlindedNodeId => "blinded_node_id"u8;

    /// <summary>
    /// Key type used to encrypt the fulfillment payload ("fulfillment").
    /// </summary>
    public static ReadOnlySpan<byte> Fulfillment => "fulfillment"u8;
}