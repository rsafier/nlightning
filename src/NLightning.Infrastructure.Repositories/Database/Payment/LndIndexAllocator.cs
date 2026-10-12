using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Payments.Enums;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Hands out LND's dense indexes (NL-1165): <c>add_index</c> to new invoice rows, <c>settle_index</c> to invoices that
/// become <c>Settled</c>, <c>payment_index</c> to new payment rows (not trampoline relay legs, which LND lists nowhere), each 1, 2, 3, ... in commit order. One per node
/// (singleton): <see cref="UnitOfWork"/> holds <see cref="Gate"/> across the assignment and the save that commits it,
/// so two saves never take the same value and a failed save gives its values back (no gaps).
/// </summary>
/// <remarks>The next values are read from the database (the largest stored index) the first time, and again after a
/// failed save.</remarks>
public sealed class LndIndexAllocator
{
    private const byte SettledStatus = (byte)InvoiceStatus.Settled;

    private long? _lastAdd;
    private long? _lastSettle;
    private long? _lastPayment;
    private readonly List<Action> _undo = [];

    /// <summary>Held by a unit of work from <see cref="AssignAsync"/> to the end of its save.</summary>
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Whether <paramref name="context"/> tracks a row that needs an index (checked before taking the gate).</summary>
    internal static bool NeedsIndexes(NLightningDbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case InvoiceEntity invoice when entry.State == EntityState.Added && invoice.AddIndex is null:
                case InvoiceEntity { SettleIndex: null, Status: SettledStatus }
                    when entry.State is EntityState.Added or EntityState.Modified:
                case PaymentEntity { PaymentIndex: null, IsTrampolineRelay: false } when entry.State == EntityState.Added:
                    return true;
            }
        }

        return false;
    }

    /// <summary>Assigns the next indexes to the tracked rows that need one. Call with <see cref="Gate"/> held.</summary>
    internal async Task AssignAsync(NLightningDbContext context)
    {
        _undo.Clear();
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            switch (entry.Entity)
            {
                case InvoiceEntity invoice when entry.State is EntityState.Added or EntityState.Modified:
                    if (entry.State == EntityState.Added && invoice.AddIndex is null)
                    {
                        _lastAdd ??= await context.Invoices.MaxAsync(e => e.AddIndex) ?? 0;
                        invoice.AddIndex = ++_lastAdd;
                        _undo.Add(() => invoice.AddIndex = null);
                    }

                    if (invoice is { SettleIndex: null, Status: SettledStatus })
                    {
                        _lastSettle ??= await context.Invoices.MaxAsync(e => e.SettleIndex) ?? 0;
                        invoice.SettleIndex = ++_lastSettle;
                        _undo.Add(() => invoice.SettleIndex = null);
                    }

                    break;
                case PaymentEntity { PaymentIndex: null, IsTrampolineRelay: false } payment when entry.State == EntityState.Added:
                    _lastPayment ??= await context.Payments.MaxAsync(e => e.PaymentIndex) ?? 0;
                    payment.PaymentIndex = ++_lastPayment;
                    _undo.Add(() => payment.PaymentIndex = null);
                    break;
            }
        }
    }

    /// <summary>The save failed: what it took is given back (the rows lose their values, the next ones are read from the
    /// database again).</summary>
    internal void Rollback()
    {
        foreach (var undo in _undo)
            undo();
        _undo.Clear();
        _lastAdd = null;
        _lastSettle = null;
        _lastPayment = null;
    }
}