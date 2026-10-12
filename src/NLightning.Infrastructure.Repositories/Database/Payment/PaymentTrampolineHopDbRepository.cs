using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Payment;

using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Interfaces;
using Domain.Payments.Trampoline;
using Persistence.Contexts;
using Persistence.Entities.Payment;

/// <summary>
/// Stores the trampoline hops of our own payments' attempts (payer side, NL-875, migration <c>AddTrampolineRelays</c>).
/// </summary>
/// <remarks>Writes are staged on the unit of work; <see cref="GetByPaymentAsync"/> sees what this unit of work staged.
/// </remarks>
public class PaymentTrampolineHopDbRepository(NLightningDbContext context)
    : BaseDbRepository<PaymentTrampolineHopEntity>(context), IPaymentTrampolineHopDbRepository
{
    /// <inheritdoc />
    public async Task AddRangeAsync(IEnumerable<PaymentTrampolineHopModel> hops)
    {
        ArgumentNullException.ThrowIfNull(hops);

        foreach (var hop in hops)
        {
            ArgumentNullException.ThrowIfNull(hop);
            if (await DbSet.FindAsync(hop.PaymentHash, hop.Attempt, hop.HopIndex) is not null)
                throw new InvalidOperationException(
                    $"Trampoline hop {hop.HopIndex} of attempt {hop.Attempt} of payment {hop.PaymentHash} exists");

            Insert(new PaymentTrampolineHopEntity
            {
                PaymentHash = hop.PaymentHash,
                Attempt = hop.Attempt,
                HopIndex = hop.HopIndex,
                NodeId = hop.NodeId,
                SharedSecret = ((byte[])hop.SharedSecret).ToArray(),
                AmountMsat = checked((long)hop.Amount.MilliSatoshi),
                CltvExpiry = hop.CltvExpiry
            });
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentTrampolineHopModel>> GetByPaymentAsync(Hash paymentHash,
                                                                                  int? attempt = null)
    {
        var query = DbSet.Where(h => h.PaymentHash == paymentHash);
        if (attempt is { } only)
            query = query.Where(h => h.Attempt == only);

        // A tracking query sees the saved rows; the rows this unit of work added are only in the change tracker
        var saved = await query.ToListAsync();
        var staged = DbSet.Local.Where(h => h.PaymentHash == paymentHash
                                         && (attempt is null || h.Attempt == attempt)
                                         && DbSet.Entry(h).State == EntityState.Added);
        return saved.Concat(staged)
                    .DistinctBy(h => (h.Attempt, h.HopIndex))
                    .OrderBy(h => h.Attempt)
                    .ThenBy(h => h.HopIndex)
                    .Select(h => new PaymentTrampolineHopModel(h.PaymentHash, h.Attempt, h.HopIndex, h.NodeId,
                                                               new Secret(h.SharedSecret),
                                                               LightningMoney.MilliSatoshis(checked((ulong)h.AmountMsat)),
                                                               h.CltvExpiry))
                    .ToList();
    }
}