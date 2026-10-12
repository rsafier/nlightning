namespace NLightning.Domain.Offers.Models;

/// <summary>
/// One BOLT 12 <c>fallback_address</c> of <c>invoice_fallbacks</c>: <c>byte version || u16 len || len*byte address</c>.
/// </summary>
/// <param name="Version">The witness version.</param>
/// <param name="Address">The witness program.</param>
public sealed record FallbackAddress(byte Version, ReadOnlyMemory<byte> Address)
{
    /// <summary>
    /// Whether a bitcoin reader may use it (BOLT 12 "Invoices" reader: ignore a version above 16, or an address shorter
    /// than 2 or longer than 40 bytes). Version-specific program rules are the wallet's.
    /// </summary>
    public bool IsUsable => Version <= 16 && Address.Length is >= 2 and <= 40;
}