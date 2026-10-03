namespace NLightning.Application.Payments.Routing;

using Domain.Crypto.ValueObjects;

/// <summary>
/// What the outer onion's last payload carries besides <c>amt_to_forward</c>, <c>outgoing_cltv_value</c> and
/// <c>payment_data</c> when that hop is a trampoline node (NL-875, BOLTs PR 836).
/// </summary>
/// <param name="TrampolinePacket">The <c>trampoline_onion_packet</c> (TLV 20) value:
/// <c>version || public_key || hop_payloads || hmac</c>.</param>
/// <param name="NextPathKey">For a blinded trampoline path past its introduction node: the <c>current_path_key</c>
/// (TLV 12) the trampoline node peels with; null otherwise.</param>
public sealed record TrampolineFinalHop(ReadOnlyMemory<byte> TrampolinePacket, CompactPubKey? NextPathKey = null);