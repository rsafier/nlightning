namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// The kind of onion packet being built or peeled (BOLT 4 "Onion Decryption").
/// </summary>
/// <remarks>
/// The Sphinx processing is identical for both kinds; they differ in the minimum hop payload length and in how
/// failures are reported.
/// </remarks>
public enum OnionPacketKind
{
    /// <summary>
    /// An <c>onion_routing_packet</c> in <c>update_add_htlc</c>. A payload length below 2 is invalid (0 is the
    /// unsupported legacy format, 1 is reserved), and every failure inside a blinded route (a <c>path_key</c> in
    /// <c>update_add_htlc</c>) is reported as <c>invalid_onion_blinding</c>.
    /// </summary>
    Payment = 0,

    /// <summary>
    /// An <c>onion_message_packet</c> in <c>onion_message</c>. There is no legacy length, so any payload length
    /// (including 0) is valid, and failures are never returned to the sender.
    /// </summary>
    OnionMessage = 1
}