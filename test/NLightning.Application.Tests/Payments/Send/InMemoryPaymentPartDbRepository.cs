namespace NLightning.Application.Tests.Payments.Send;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;

/// <summary>
/// An <see cref="IPaymentPartDbRepository"/> that commits immediately and stores copies (a caller's later mutation of
/// its model is not stored until it calls <see cref="UpdateAsync"/>, as with the EF repository), for payment tests
/// (NL-321).
/// </summary>
internal sealed class InMemoryPaymentPartDbRepository : IPaymentPartDbRepository
{
    private readonly Dictionary<(Hash PaymentHash, byte PartIndex), PaymentPartModel> _parts = [];
    private readonly Lock _lock = new();

    public Task AddAsync(PaymentPartModel part)
    {
        lock (_lock)
        {
            if (_parts.ContainsKey((part.PaymentHash, part.PartIndex)))
                throw new InvalidOperationException(
                    $"Part {part.PartIndex} of {part.PaymentHash} is already stored.");

            _parts[(part.PaymentHash, part.PartIndex)] = Copy(part);
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(PaymentPartModel part)
    {
        lock (_lock)
        {
            if (!_parts.ContainsKey((part.PaymentHash, part.PartIndex)))
                throw new InvalidOperationException($"No part {part.PartIndex} of {part.PaymentHash} is stored.");

            _parts[(part.PaymentHash, part.PartIndex)] = Copy(part);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentPartModel>> GetForPaymentAsync(Hash paymentHash)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<PaymentPartModel>>(_parts
                .Where(p => p.Key.PaymentHash == paymentHash)
                .OrderBy(p => p.Key.PartIndex)
                .Select(p => Copy(p.Value))
                .ToList());
    }

    public Task<PaymentPartModel?> GetByHtlcAsync(Hash paymentHash, ChannelId channelId, ulong htlcId)
    {
        lock (_lock)
            return Task.FromResult(_parts.Values
                                             .Where(p => p.PaymentHash == paymentHash && p.ChannelId == channelId
                                                       && p.HtlcId == htlcId)
                                             .Select(Copy)
                                             .FirstOrDefault());
    }

    public Task DeleteForPaymentAsync(Hash paymentHash)
    {
        lock (_lock)
            foreach (var key in _parts.Keys.Where(k => k.PaymentHash == paymentHash).ToList())
                _parts.Remove(key);

        return Task.CompletedTask;
    }

    private static PaymentPartModel Copy(PaymentPartModel part) =>
        new(part.PaymentHash, part.PartIndex, part.ChannelId, part.HtlcId, part.State, part.Hops);
}