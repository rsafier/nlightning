namespace NLightning.Domain.Protocol.Onion.Models;

/// <summary>
/// The <c>payment_constraints</c> record (type 12) of a route-blinding <c>encrypted_data_tlv</c> (BOLT 4 "Route
/// Blinding").
/// </summary>
/// <param name="MaxCltvExpiry">The largest incoming <c>cltv_expiry</c> the blinded hop accepts (u32).</param>
/// <param name="HtlcMinimumMsat">The smallest incoming <c>amount_msat</c> the blinded hop accepts (tu64).</param>
public sealed record BlindedPaymentConstraints(uint MaxCltvExpiry, ulong HtlcMinimumMsat);