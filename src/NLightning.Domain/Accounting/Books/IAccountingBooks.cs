namespace NLightning.Domain.Accounting.Books;

/// <summary>
/// The operational books (plan §6.1): the projector over the sealed feed, rebuild and reconcile.
/// </summary>
public interface IAccountingBooks
{
    /// <summary>Whether the books run (<c>Accounting:Enabled</c>, unset = on).</summary>
    bool IsEnabled { get; }

    /// <summary>Projects every sealed event after the cursor now (serialized with the background loop); returns how many
    /// entries were written. Nothing when the books are off.</summary>
    Task<int> ProjectNowAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the books and projects the whole feed again.</summary>
    Task<int> RebuildAsync(CancellationToken cancellationToken = default);

    /// <summary>Compares the books with a live snapshot (after projecting what is sealed).</summary>
    Task<AccountingReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default);
}