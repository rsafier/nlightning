namespace NLightning.Domain.Cashu.Enums;

/// <summary>The payment method of a Cashu mint's quote (NUT-04/05: <c>bolt11</c>, <c>bolt12</c>, <c>onchain</c>).</summary>
public enum CashuQuoteMethod : byte
{
    Bolt11 = 1,
    Bolt12 = 2,
    Onchain = 3
}