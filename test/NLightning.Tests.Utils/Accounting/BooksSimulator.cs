using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Accounting;

using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;

/// <summary>
/// The operational books in memory for proofs (NL-602 A2): feeds accounting events, in order, through
/// <see cref="AccountingPostingRules"/> (a reversal finds its original's entry by key) and keeps the entries and a
/// running balance per account, as the projector does.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class BooksSimulator
{
    private readonly Dictionary<string, AccountingEntry> _entries = new(StringComparer.Ordinal);
    private readonly List<AccountingEntry> _ledger = [];
    private readonly Dictionary<AccountRole, long> _balances = [];
    private long _sequence;

    /// <summary>The entries in the order the events were applied.</summary>
    public IReadOnlyList<AccountingEntry> Entries => _ledger;

    /// <summary>The running balance of every account with postings (zero balances included).</summary>
    public IReadOnlyDictionary<AccountRole, long> Balances => _balances;

    /// <summary>The balance of <paramref name="account"/> (0 when it has no postings).</summary>
    public long this[AccountRole account] => _balances.GetValueOrDefault(account);

    /// <summary>A new simulator that applied <paramref name="events"/>.</summary>
    public static BooksSimulator Of(IEnumerable<AccountingEventModel> events)
    {
        var books = new BooksSimulator();
        books.ApplyAll(events);
        return books;
    }

    /// <summary>Applies every event in order; duplicates (a key already applied) are skipped, as the sealer marks
    /// them.</summary>
    public void ApplyAll(IEnumerable<AccountingEventModel> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var accountingEvent in events)
            Apply(accountingEvent);
    }

    /// <summary>Applies one event; returns its entry, or null for a key already applied or a duplicate row.</summary>
    public AccountingEntry? Apply(AccountingEventModel accountingEvent)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);
        if (accountingEvent.Flags.HasFlag(AccountingEventFlags.Duplicate)
         || _entries.ContainsKey(accountingEvent.EventKey))
            return null;

        var result = AccountingPostingRules.Evaluate(accountingEvent, key => _entries.GetValueOrDefault(key));
        var entry = new AccountingEntry(accountingEvent.LedgerSeq ?? ++_sequence, accountingEvent.EventKey,
                                        accountingEvent.Kind, accountingEvent.OccurredAt, accountingEvent.ChannelId,
                                        accountingEvent.PaymentHash, result.Postings, result.Note);
        if (!entry.IsBalanced)
            throw new InvalidOperationException($"The entry of {accountingEvent.EventKey} does not balance");

        _entries[entry.EventKey] = entry;
        _ledger.Add(entry);
        foreach (var posting in entry.Postings)
            _balances[posting.Account] = checked(_balances.GetValueOrDefault(posting.Account) + posting.AmountMsat);
        return entry;
    }

    /// <summary>The entry of <paramref name="eventKey"/>, or null.</summary>
    public AccountingEntry? Entry(string eventKey) => _entries.GetValueOrDefault(eventKey);

    /// <summary>The sum of every balance (always 0: each entry balances).</summary>
    public long Total => _balances.Values.Sum();

    /// <summary>A readable dump of the balances, for assertion messages.</summary>
    public override string ToString() =>
        string.Join(", ", _balances.OrderBy(b => b.Key).Select(b => $"{b.Key}={b.Value}"));
}