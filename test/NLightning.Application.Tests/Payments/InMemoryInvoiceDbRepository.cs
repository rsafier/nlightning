namespace NLightning.Application.Tests.Payments;

using Domain.Crypto.ValueObjects;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;

/// <summary>
/// An <see cref="IInvoiceDbRepository"/> that commits immediately (no unit of work), for payment tests.
/// </summary>
internal sealed class InMemoryInvoiceDbRepository : IInvoiceDbRepository
{
    private readonly Dictionary<Hash, InvoiceModel> _invoices = [];

    public int AddCalls { get; private set; }
    public int UpdateCalls { get; private set; }

    public IReadOnlyCollection<InvoiceModel> Invoices => _invoices.Values;

    public Task AddAsync(InvoiceModel invoice)
    {
        AddCalls++;
        if (!_invoices.TryAdd(invoice.PaymentHash, invoice))
            throw new InvalidOperationException("Duplicate payment hash.");

        return Task.CompletedTask;
    }

    public Task UpdateAsync(InvoiceModel invoice)
    {
        UpdateCalls++;
        _invoices[invoice.PaymentHash] = invoice;
        return Task.CompletedTask;
    }

    public Task<InvoiceModel?> GetByPaymentHashAsync(Hash paymentHash) =>
        Task.FromResult(_invoices.GetValueOrDefault(paymentHash));

    public Task<IReadOnlyList<InvoiceModel>> ListAsync(int skip, int take) =>
        Task.FromResult<IReadOnlyList<InvoiceModel>>(_invoices.Values.OrderByDescending(i => i.CreatedAt)
                                                              .Skip(skip).Take(take).ToList());
}