namespace NLightning.Domain.Protocol.Onion.Models;

/// <summary>
/// A blinded path to pay through and what it costs (a BOLT 12 <c>invoice_paths</c> entry with its
/// <c>invoice_blindedpay</c>, or a blinded path of an LND invoice).
/// </summary>
/// <param name="Path">The blinded path, introduction node first, recipient last.</param>
/// <param name="PayInfo">Its aggregated fees, CLTV delta and HTLC limits.</param>
public sealed record BlindedPaymentPath(BlindedPath Path, BlindedPayInfo PayInfo);