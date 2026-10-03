using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NLightning.Domain.Accounting.Financial;

using Books;

/// <summary>
/// The financial book's state at a period close (<c>AccountingPeriods.ClosingState</c>, A3-T5, D-A8): what a rebuild
/// starts from instead of replaying the closed periods.
/// </summary>
/// <param name="ReplayAfterLedgerSeq">The financial cursor a rebuild from this close starts at: every operational entry
/// after it is projected again (the ones of a closed period are skipped as already closed, through
/// <see cref="IAccountingAdjustmentSink"/>), and none at or before it has a financial entry in the open period.</param>
/// <param name="Balances">The financial book's running balance of every account over the closed periods (this close and
/// every earlier one), msat and fiat, ordered by role then name.</param>
/// <remarks>
/// The lots need no copy here: the remaining amount of a lot at the close is its current one plus the reliefs made
/// since (<see cref="IAccountingLotDbRepository.SumReliefsSinceAsync"/>). Stored as a small JSON object written and read
/// without reflection (AOT); <see cref="Encode"/> is canonical (the close digest covers its text).
/// </remarks>
public sealed record AccountingClosingState(long ReplayAfterLedgerSeq, IReadOnlyList<AccountingAccountBalance> Balances)
{
    /// <summary>The encoding version.</summary>
    public const int Version = 1;

    /// <summary>The empty state before the first close.</summary>
    public static AccountingClosingState Empty { get; } = new(0, []);

    /// <summary>The balances keyed by role and name (null name = empty), summed where a key repeats.</summary>
    public static Dictionary<(AccountRole Account, string Name), (long Msat, decimal Fiat)> ToMap(
        IEnumerable<AccountingAccountBalance> balances)
    {
        var map = new Dictionary<(AccountRole, string), (long, decimal)>();
        foreach (var balance in balances)
        {
            var key = (balance.Account, balance.AccountName ?? string.Empty);
            var current = map.GetValueOrDefault(key);
            map[key] = (checked(current.Item1 + balance.BalanceMsat), current.Item2 + balance.FiatAmount);
        }

        return map;
    }

    /// <summary>The balances of a map, in canonical order (role, then name ordinal), zero lines left out.</summary>
    public static IReadOnlyList<AccountingAccountBalance> FromMap(
        AccountingBook book, IReadOnlyDictionary<(AccountRole Account, string Name), (long Msat, decimal Fiat)> map) =>
        map.Where(p => p.Value.Msat != 0 || p.Value.Fiat != 0m)
           .OrderBy(p => p.Key.Account)
           .ThenBy(p => p.Key.Name, StringComparer.Ordinal)
           .Select(p => new AccountingAccountBalance(book, p.Key.Account,
                                                     p.Key.Name.Length == 0 ? null : p.Key.Name, p.Value.Msat,
                                                     p.Value.Fiat))
           .ToList();

    /// <summary>The canonical text of a fiat amount (no trailing zeros, invariant culture).</summary>
    public static string FormatFiat(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>The stored JSON (canonical: balances in role and name order, fiat without trailing zeros).</summary>
    public string Encode()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteNumber("replayAfter", ReplayAfterLedgerSeq);
            writer.WriteStartArray("balances");
            foreach (var balance in Balances.OrderBy(b => b.Account)
                                            .ThenBy(b => b.AccountName ?? string.Empty, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("account", (int)balance.Account);
                if (balance.AccountName is not null)
                    writer.WriteString("name", balance.AccountName);
                writer.WriteNumber("msat", balance.BalanceMsat);
                writer.WriteString("fiat", FormatFiat(balance.FiatAmount));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads a stored state of the financial book.</summary>
    /// <exception cref="FormatException">Not a closing state this version reads.</exception>
    public static AccountingClosingState Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException("The period has no closing state");

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != Version)
                throw new FormatException($"Unknown closing state version {root.GetProperty("version")}");

            var balances = new List<AccountingAccountBalance>();
            foreach (var line in root.GetProperty("balances").EnumerateArray())
            {
                var name = line.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
                var fiat = decimal.Parse(line.GetProperty("fiat").GetString() ?? "0", NumberStyles.Number,
                                         CultureInfo.InvariantCulture);
                balances.Add(new AccountingAccountBalance(AccountingBook.Financial,
                                                          (AccountRole)line.GetProperty("account").GetInt32(), name,
                                                          line.GetProperty("msat").GetInt64(), fiat));
            }

            return new AccountingClosingState(root.GetProperty("replayAfter").GetInt64(), balances);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
                                       or OverflowException)
        {
            throw new FormatException("The closing state is not valid JSON of this version", e);
        }
    }
}