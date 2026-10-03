namespace NLightning.Application.Tests.Payments.Trampoline;

using Domain.Crypto.ValueObjects;
using Domain.Payments.Interfaces;
using Domain.Payments.Trampoline;

/// <summary>
/// An <see cref="IPaymentTrampolineHopDbRepository"/> that commits immediately, for the payer-side trampoline proofs
/// (NL-875): a restarted payment service reads the hops another instance stored.
/// </summary>
internal sealed class InMemoryPaymentTrampolineHopDbRepository : IPaymentTrampolineHopDbRepository
{
    private readonly List<PaymentTrampolineHopModel> _hops = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<PaymentTrampolineHopModel> All
    {
        get
        {
            lock (_lock)
                return _hops.ToList();
        }
    }

    public Task AddRangeAsync(IEnumerable<PaymentTrampolineHopModel> hops)
    {
        lock (_lock)
        {
            foreach (var hop in hops)
            {
                if (_hops.Any(h => h.PaymentHash == hop.PaymentHash && h.Attempt == hop.Attempt
                                                                    && h.HopIndex == hop.HopIndex))
                    throw new InvalidOperationException(
                        $"Trampoline hop {hop.HopIndex} of attempt {hop.Attempt} of {hop.PaymentHash} exists");

                _hops.Add(hop);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentTrampolineHopModel>> GetByPaymentAsync(Hash paymentHash, int? attempt = null)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PaymentTrampolineHopModel>>(
                _hops.Where(h => h.PaymentHash == paymentHash && (attempt is null || h.Attempt == attempt))
                     .OrderBy(h => h.Attempt)
                     .ThenBy(h => h.HopIndex)
                     .ToList());
    }
}