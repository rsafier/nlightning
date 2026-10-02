namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// The financial book's projector as the reports and exports see it (NL-602 A3-T6 seam for A3-T4): whether the
/// financial book runs (<c>Accounting:Profile=Financial</c>) and a way to project the operational entries it has not
/// seen yet before a report reads it.
/// </summary>
/// <remarks>
/// A3-T4's financial projector implements it (registered as this interface next to itself). When nothing implements it,
/// the financial reports read the financial book as stored (useful for the tests that seed it, and harmless: without a
/// projector the book is empty).
/// </remarks>
public interface IAccountingFinancialProjection
{
    /// <summary>Whether the financial book runs.</summary>
    bool IsEnabled { get; }

    /// <summary>Projects every operational entry after the financial cursor now; returns how many entries it wrote.
    /// </summary>
    Task<int> ProjectNowAsync(CancellationToken cancellationToken = default);
}