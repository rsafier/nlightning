using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>The most invoices one replay step reads.</summary>
    private const int SubscriptionBatch = 200;

    /// <summary>
    /// <c>SubscribeInvoices</c> (NL-1165's dense indexes): first the invoices added after <c>add_index</c> and the ones
    /// settled after <c>settle_index</c> (when non-zero; LND replays the same way), then every invoice added or settled
    /// from now on, oldest first, until the caller leaves. New invoices raise no event, so the store is re-read every
    /// second and on every payment event.
    /// </summary>
    public override async Task SubscribeInvoices(InvoiceSubscription request, IServerStreamWriter<Invoice> responseStream,
                                                 ServerCallContext context)
    {
        using var subscription = _paymentEvents?.Subscribe(256);
        var (lastAdd, lastSettle) = await ReadAsync(r => r.GetMaxIndexesAsync());
        if (request.AddIndex != 0)
            lastAdd = request.AddIndex;
        if (request.SettleIndex != 0)
            lastSettle = request.SettleIndex;

        try
        {
            while (!context.CancellationToken.IsCancellationRequested)
            {
                while (true)
                {
                    var added = await ReadAsync(r => r.ListByIndexAsync(
                                                    new LndIndexQuery(lastAdd, null, true, SubscriptionBatch), false));
                    foreach (var invoice in added)
                    {
                        await responseStream.WriteAsync(ToLndInvoice(invoice), context.CancellationToken);
                        lastAdd = Math.Max(lastAdd, invoice.AddIndex ?? 0);
                    }

                    var settled = await ReadAsync(r => r.ListSettledAfterAsync(lastSettle, SubscriptionBatch));
                    foreach (var invoice in settled)
                    {
                        await responseStream.WriteAsync(ToLndInvoice(invoice), context.CancellationToken);
                        lastSettle = Math.Max(lastSettle, invoice.SettleIndex ?? 0);
                    }

                    if (added.Count < SubscriptionBatch && settled.Count < SubscriptionBatch)
                        break;
                }

                await InvoiceStreams.WaitForChangeAsync(subscription, _timeProvider, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The caller left
        }
    }

    private async Task<T> ReadAsync<T>(Func<IInvoiceDbRepository, Task<T>> read)
    {
        await using var scope = CreateScope();
        return await read(UnitOfWork(scope).InvoiceDbRepository);
    }
}