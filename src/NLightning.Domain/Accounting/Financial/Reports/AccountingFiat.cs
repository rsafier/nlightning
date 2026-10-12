using System.Globalization;

namespace NLightning.Domain.Accounting.Financial.Reports;

using Books;
using Books.Reports;
using Constants;

/// <summary>
/// The fiat arithmetic and conventions of the financial reports and exports (NL-602 A3-T6, D-A1, D-A11): currency codes,
/// the value of an msat amount at a BTC price, the exact text of a decimal and the currency's minor unit for display.
/// </summary>
public static class AccountingFiat
{
    /// <summary>The base currency when neither the request nor the configuration names one (D-A1).</summary>
    public const string DefaultCurrency = "USD";

    /// <summary>Millisatoshi per bitcoin: a price per BTC over this is the price of one msat.</summary>
    public const decimal MsatPerBitcoin = 100_000_000_000m;

    /// <summary>
    /// How far back the reports look for a stored price when none is given (the latest price at or before the time,
    /// however old; the report shows its time, so a stale price is visible).
    /// </summary>
    public static readonly TimeSpan StoredPriceLookback = TimeSpan.FromDays(3_650);

    private static readonly CultureInfo s_invariant = CultureInfo.InvariantCulture;

    // ISO 4217 minor units that are not 2 (the rest of the codes in use have 2)
    private static readonly IReadOnlyDictionary<string, int> s_minorUnits = new Dictionary<string, int>
    {
        ["BIF"] = 0,
        ["CLP"] = 0,
        ["DJF"] = 0,
        ["GNF"] = 0,
        ["ISK"] = 0,
        ["JPY"] = 0,
        ["KMF"] = 0,
        ["KRW"] = 0,
        ["PYG"] = 0,
        ["RWF"] = 0,
        ["UGX"] = 0,
        ["VND"] = 0,
        ["VUV"] = 0,
        ["XAF"] = 0,
        ["XOF"] = 0,
        ["XPF"] = 0,
        ["BHD"] = 3,
        ["IQD"] = 3,
        ["JOD"] = 3,
        ["KWD"] = 3,
        ["LYD"] = 3,
        ["OMR"] = 3,
        ["TND"] = 3
    };

    /// <summary>
    /// The currency code in canonical form (three upper-case ASCII letters), or <see cref="DefaultCurrency"/> when
    /// <paramref name="currency"/> is null or blank.
    /// </summary>
    /// <exception cref="ArgumentException">Not three ASCII letters.</exception>
    public static string NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            return DefaultCurrency;

        var code = currency.Trim().ToUpperInvariant();
        if (code.Length != AccountingSchemaLimits.CurrencyLength || !code.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"'{currency}' is not a currency code (three letters such as USD).",
                                        nameof(currency));

        return code;
    }

    /// <summary>The number of decimals the currency shows (ISO 4217 minor unit; 2 when unknown).</summary>
    public static int MinorUnits(string currency) =>
        s_minorUnits.TryGetValue(currency.ToUpperInvariant(), out var units) ? units : 2;

    /// <summary>The value of <paramref name="msat"/> at <paramref name="pricePerBitcoin"/>, exact (no rounding: a
    /// decimal holds the 19 decimals of an 8-decimal price over 10^11).</summary>
    public static decimal Value(long msat, decimal pricePerBitcoin) => msat * PricePerMsat(pricePerBitcoin);

    /// <summary>The price of one msat (the price per BTC over 10^11), exact.</summary>
    public static decimal PricePerMsat(decimal pricePerBitcoin) => pricePerBitcoin / MsatPerBitcoin;

    /// <summary>
    /// <paramref name="value"/> rounded to <see cref="AccountingSchemaLimits.FiatScale"/> places (the stored precision,
    /// D-A11), away from zero at the midpoint.
    /// </summary>
    public static decimal RoundStored(decimal value) =>
        Math.Round(value, AccountingSchemaLimits.FiatScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The exact text of a decimal: invariant culture, no exponent, no trailing zeros after the point, no point for a
    /// whole number (<c>86.05</c>, <c>0.00000086048</c>, <c>-3</c>).
    /// </summary>
    public static string Format(decimal value) =>
        value.ToString("0.############################", s_invariant);

    /// <summary>A decimal parsed from <see cref="Format"/>'s text (or any invariant number without exponent), or null.</summary>
    public static decimal? Parse(string? text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, s_invariant,
                         out var value)
            ? value
            : null;

    /// <summary>
    /// The category of a financial account: the root of its name (<c>assets</c>, <c>liabilities</c>, <c>equity</c>,
    /// <c>income</c>, <c>expenses</c>, case does not matter), else the category of the operational role the line
    /// derives from.
    /// </summary>
    public static AccountingAccountCategory CategoryOf(string? accountName, AccountRole role)
    {
        var root = accountName?.Split(':', 2)[0].Trim().ToLowerInvariant();
        return root switch
        {
            "assets" or "asset" => AccountingAccountCategory.Assets,
            "liabilities" or "liability" => AccountingAccountCategory.Liabilities,
            "equity" => AccountingAccountCategory.Equity,
            "income" or "revenue" or "revenues" => AccountingAccountCategory.Income,
            "expenses" or "expense" => AccountingAccountCategory.Expenses,
            _ => AccountingAccountCategories.Of(role)
        };
    }
}