namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// Which books the node keeps (<c>Accounting:Profile</c>, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, §7, D-A5,
/// D-A7). The values go on the wire (IPC 45): never renumber them.
/// </summary>
public enum AccountingProfile
{
    /// <summary>The operational book only (the default).</summary>
    Operational = 0,

    /// <summary>The operational book and, next to it, the financial book (classification, fiat values, lots).</summary>
    Financial = 1
}