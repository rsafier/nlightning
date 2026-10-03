namespace NLightning.Daemon.Handlers;

using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Interfaces;

/// <summary>
/// Waits until one of our invoices leaves <c>Open</c> (ClientCommand 47, <c>waitinvoice</c>; Cashu plan C0, NL-991).
/// </summary>
/// <remarks>
/// <para>The handler subscribes to <see cref="IPaymentEventSource"/> before it reads the invoice, so a settle committed
/// between the read and the wait is never missed. It reads the invoice again on each event for its hash and at least
/// every <see cref="RecheckInterval"/>, which also catches a cancel (no event) and a subscription that overflowed. A
/// node without the event source falls back to that periodic read alone.</para>
/// <para>The wait is bounded by <see cref="WaitInvoiceClientRequest.TimeoutSeconds"/> (default
/// <see cref="DefaultTimeoutSeconds"/>, at most <see cref="MaxTimeoutSeconds"/>, as for <c>payinvoice</c>: the call
/// holds one IPC pipe instance for the whole wait). A wait that ends with the invoice still open answers with it and
/// <see cref="WaitInvoiceClientResponse.TimedOut"/>; an unknown payment hash is
/// <see cref="ErrorCodes.InvalidOperation"/>.</para>
/// </remarks>
public sealed class WaitInvoiceClientHandler
    : IClientCommandHandler<WaitInvoiceClientRequest, WaitInvoiceClientResponse>
{
    /// <summary>The wait when the request does not choose one.</summary>
    public const uint DefaultTimeoutSeconds = 60;

    /// <summary>The longest wait a request may ask for.</summary>
    public const uint MaxTimeoutSeconds = 300;

    /// <summary>How often the invoice is read again without an event.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);

    private readonly IPaymentEventSource? _eventSource;
    private readonly IInvoiceService _invoiceService;
    private readonly TimeProvider _timeProvider;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.WaitInvoice;

    public WaitInvoiceClientHandler(IInvoiceService invoiceService, IPaymentEventSource? eventSource,
                                    TimeProvider timeProvider)
    {
        _invoiceService = invoiceService;
        _eventSource = eventSource;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task<WaitInvoiceClientResponse> HandleAsync(WaitInvoiceClientRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var timeoutSeconds = request.TimeoutSeconds ?? DefaultTimeoutSeconds;
        if (timeoutSeconds is 0 or > MaxTimeoutSeconds)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The timeout must be 1 to {MaxTimeoutSeconds} seconds.");

        // Subscribe before the first read: an outcome committed in between is then in the read or in the queue
        using var subscription = _eventSource?.Subscribe(64);
        var invoice = await _invoiceService.GetInvoiceAsync(request.PaymentHash, ct)
                   ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                                $"No invoice of ours has payment hash {request.PaymentHash}.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await using var events = subscription?.ReadAllAsync(linked.Token).GetAsyncEnumerator(linked.Token);
        var eventsEnded = events is null;
        Task<bool>? nextEvent = null;
        try
        {
            while (invoice.Status == InvoiceStatus.Open)
            {
                if (!eventsEnded)
                    nextEvent ??= events!.MoveNextAsync().AsTask();
                var recheck = Task.Delay(RecheckInterval, _timeProvider, linked.Token);
                var first = nextEvent is null ? recheck : await Task.WhenAny(nextEvent, recheck);
                await first; // throws when the deadline or the caller canceled
                if (first == nextEvent)
                {
                    var hasEvent = await nextEvent;
                    nextEvent = null;
                    if (!hasEvent)
                        eventsEnded = true;
                    else if (events!.Current.PaymentHash != request.PaymentHash)
                        continue;
                }

                invoice = await _invoiceService.GetInvoiceAsync(request.PaymentHash, linked.Token) ?? invoice;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return Respond(invoice, timedOut: true);
        }
        finally
        {
            // End a pending MoveNextAsync before the enumerator is disposed
            if (nextEvent is not null)
            {
                await linked.CancelAsync();
                await Task.WhenAny(nextEvent);
            }
        }

        return Respond(invoice, timedOut: false);
    }

    private WaitInvoiceClientResponse Respond(InvoiceModel invoice, bool timedOut) =>
        new(InvoiceInfoClientResponse.FromModel(invoice, _timeProvider.GetUtcNow()), timedOut);
}