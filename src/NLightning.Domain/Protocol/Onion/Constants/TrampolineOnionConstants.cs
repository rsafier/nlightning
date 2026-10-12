using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Constants;

/// <summary>
/// Constants for the BOLT 4 trampoline onion (<c>trampoline_onion_packet</c>, BOLTs PR 836).
/// </summary>
/// <remarks>
/// The trampoline onion uses the payment onion's Sphinx construction with a variable <c>hop_payloads</c> length and
/// travels as the value of TLV 20 in the outer onion's final hop payload:
/// <c>version(1) || public_key(33) || hop_payloads(L) || hmac(32)</c>.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class TrampolineOnionConstants
{
    /// <summary>
    /// The <c>hop_payloads</c> length an automatically sized trampoline onion is padded to when its framed payloads
    /// fit (eclair and the PR 836 examples use 650 bytes for privacy, so the packet does not reveal its hop count).
    /// </summary>
    public const int RecommendedHopPayloadsLength = 650;

    /// <summary>
    /// The length of the associated data of a trampoline onion: the payment hash.
    /// </summary>
    public const int AssociatedDataLength = 32;

    /// <summary>
    /// The smallest valid <c>trampoline_onion_packet</c> TLV value: the packet overhead plus one byte of
    /// <c>hop_payloads</c>.
    /// </summary>
    public const int MinPacketLength = OnionConstants.PacketOverheadLength + 1;
}