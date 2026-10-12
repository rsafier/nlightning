namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The book an entry belongs to (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> D-A7): the operational book is always
/// projected from the feed; the financial book (<c>Accounting:Profile=Financial</c>) is projected from the operational
/// entries next to it, never instead of it. The values are persisted (column <c>Book</c> of the books' tables): never
/// renumber them.
/// </summary>
public enum AccountingBook : byte
{
    Operational = 0,
    Financial = 1
}