namespace NLightning.Domain.Cashu.Enums;

/// <summary>Whether a Cashu quote receives (a mint quote) or pays (a melt quote).</summary>
public enum CashuQuoteDirection : byte
{
    /// <summary>A mint quote: the mint's user pays us.</summary>
    Incoming = 1,

    /// <summary>A melt quote: we pay for the mint.</summary>
    Outgoing = 2
}