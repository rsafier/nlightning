namespace NLightning.Domain.Accounting.Financial.Classification;

using Books.Reports;
using Constants;

/// <summary>
/// What a financial account name may be (NL-602 A3-T3): a chart name, a rule's target or an override's target.
/// </summary>
/// <remarks>
/// A name is hledger style, <c>root:component[:component...]</c>: the root is one of <c>assets</c>,
/// <c>liabilities</c>, <c>equity</c>, <c>income</c>, <c>expenses</c> (lower case; it is the account's category in
/// the financial reports) and every component starts with an ASCII letter or digit and holds letters, digits, <c>-</c>
/// and <c>_</c>, so the name survives the hledger and beancount exports unchanged (beancount only capitalizes it). At
/// most <see cref="AccountingSchemaLimits.AccountNameMaxLength"/> characters. A rule or an override never targets an
/// <c>assets</c> account: classification decides where income and expenses go, while the assets are the node's real
/// buckets and must reconcile.
/// </remarks>
public static class FinancialAccountNameRules
{
    private static readonly string[] s_roots = ["assets", "liabilities", "equity", "income", "expenses"];

    /// <summary>Whether <paramref name="name"/> is a valid financial account name.</summary>
    /// <returns>True, or false with <paramref name="error"/> set.</returns>
    public static bool TryValidate(string? name, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(name))
        {
            error = "An account name is required.";
            return false;
        }

        if (name.Length > AccountingSchemaLimits.AccountNameMaxLength)
        {
            error = $"An account name is at most {AccountingSchemaLimits.AccountNameMaxLength} characters.";
            return false;
        }

        var components = name.Split(':');
        if (components.Length < 2)
        {
            error = $"'{name}' needs a root and at least one component (e.g. income:sales).";
            return false;
        }

        if (!s_roots.Contains(components[0], StringComparer.Ordinal))
        {
            error = $"'{name}' must start with one of {string.Join(", ", s_roots)}.";
            return false;
        }

        foreach (var component in components.Skip(1))
        {
            if (component.Length == 0 || !char.IsAsciiLetterOrDigit(component[0])
                                      || !component.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                error = $"'{name}' has an invalid component '{component}': start with a letter or digit and use letters, "
                      + "digits, '-' and '_' only.";
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="name"/> may be a rule's or an override's target: valid and not an asset.</summary>
    /// <returns>True, or false with <paramref name="error"/> set.</returns>
    public static bool TryValidateTarget(string? name, out string? error)
    {
        if (!TryValidate(name, out error))
            return false;

        if (CategoryOf(name!) == AccountingAccountCategory.Assets)
        {
            error = $"'{name}' is an asset account: a rule or an override moves income and expenses, never the assets.";
            return false;
        }

        return true;
    }

    /// <summary>The category of a name by its root (a name with an unknown root is equity, as the reports treat an
    /// unknown operational role).</summary>
    public static AccountingAccountCategory CategoryOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var separator = name.IndexOf(':');
        return (separator < 0 ? name : name[..separator]) switch
        {
            "assets" => AccountingAccountCategory.Assets,
            "liabilities" => AccountingAccountCategory.Liabilities,
            "income" => AccountingAccountCategory.Income,
            "expenses" => AccountingAccountCategory.Expenses,
            _ => AccountingAccountCategory.Equity
        };
    }
}