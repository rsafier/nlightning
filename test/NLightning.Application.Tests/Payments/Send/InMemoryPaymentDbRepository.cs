namespace NLightning.Application.Tests.Payments.Send;

using Domain.Crypto.ValueObjects;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;

/// <summary>
/// An <see cref="IPaymentDbRepository"/> that commits immediately and stores copies (a caller's later mutation of its
/// model is not stored until it calls <see cref="UpdateAsync"/>, as with the EF repository), for payment tests.
/// </summary>
internal sealed class InMemoryPaymentDbRepository : IPaymentDbRepository
{
    private readonly Dictionary<Hash, PaymentModel> _payments = [];
    private readonly Lock _lock = new();

    public int AddCalls { get; private set; }
    public int UpdateCalls { get; private set; }

    public IReadOnlyList<PaymentModel> Payments
    {
        get
        {
            lock (_lock)
                return _payments.Values.Select(Copy).ToList();
        }
    }

    public Task AddAsync(PaymentModel payment)
    {
        lock (_lock)
        {
            AddCalls++;
            if (_payments.TryGetValue(payment.PaymentHash, out var existing) && existing.Status != PaymentStatus.Failed)
                throw new InvalidOperationException($"A payment for {payment.PaymentHash} is {existing.Status}.");

            _payments[payment.PaymentHash] = Copy(payment);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(PaymentModel payment)
    {
        lock (_lock)
        {
            UpdateCalls++;
            if (!_payments.ContainsKey(payment.PaymentHash))
                throw new InvalidOperationException($"No payment for {payment.PaymentHash}.");

            _payments[payment.PaymentHash] = Copy(payment);
        }

        return Task.CompletedTask;
    }

    public Task<PaymentModel?> GetByPaymentHashAsync(Hash paymentHash)
    {
        lock (_lock)
            return Task.FromResult(_payments.TryGetValue(paymentHash, out var payment) ? Copy(payment) : null);
    }

    public Task<IReadOnlyList<PaymentModel>> GetInFlightAsync()
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PaymentModel>>(
                _payments.Values.Where(p => p.Status == PaymentStatus.InFlight).OrderBy(p => p.CreatedAt)
                         .Select(Copy).ToList());
    }

    public Task<IReadOnlyList<PaymentModel>> ListAsync(int skip, int take)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PaymentModel>>(
                _payments.Values.OrderByDescending(p => p.CreatedAt).Skip(skip).Take(take).Select(Copy).ToList());
    }

    private static PaymentModel Copy(PaymentModel p) =>
        PaymentModel.Restore(p.PaymentHash, p.Bolt11, p.PayeeNodeId, p.Amount, p.Fee, p.CreatedAt, p.Status,
                             p.OutgoingChannelId, p.OutgoingHtlcId, p.Preimage, p.FailureCode, p.FailureSourceIndex,
                             p.FailureReason, p.CompletedAt, p.Route);
}