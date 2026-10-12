using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Constants;

[ExcludeFromCodeCoverage]
public static class InvoiceConstants
{
    public const string Prefix = "ln";
    public const char Separator = '1';
    public const string PrefixMainet = "bc";
    public const string PrefixTestnet = "tb";
    public const string PrefixSignet = "tbs";
    public const string PrefixRegtest = "bcrt";
    public const char MultiplierMilli = 'm';
    public const char MultiplierMicro = 'u';
    public const char MultiplierNano = 'n';
    public const char MultiplierPico = 'p';
    public const decimal BtcInSatoshis = 100_000_000m;
    public const decimal BtcInMillisatoshis = 100_000_000_000m;
    public const int DefaultExpirationSeconds = 3600;

    /// <summary>
    /// The min_final_cltv_expiry_delta a payer must use when the invoice has no <c>c</c> field (BOLT 11)
    /// </summary>
    public const ushort DefaultMinFinalCltvExpiryDelta = 18;
}