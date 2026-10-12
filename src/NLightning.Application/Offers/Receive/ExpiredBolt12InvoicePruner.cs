using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Offers.Receive;

using Domain.Persistence.Interfaces;
using Payments.Switch;

/// <summary>
/// Deletes expired unpaid BOLT 12 invoices (NL-448, plan §3.7 step 6): every answered invoice_request writes an invoice
/// row, and the D11 caps count only the unexpired (and Accepted) ones, so without this the expired rows would pile up
/// at the node-wide invoice_request rate. Every <see cref="OfferOptions.ExpiredInvoicePruneInterval"/> (and once at
/// start) a round calls <see cref="Domain.Payments.Interfaces.IInvoiceDbRepository.PruneExpiredBolt12InvoicesAsync"/>
/// with <see cref="OfferOptions.ExpiredInvoicePruneBatchSize"/>, one scope and one save per batch, until a batch comes
/// back short or <see cref="MaxBatchesPerRound"/> batches ran. Only <c>Open</c> rows are deleted (an Accepted invoice
/// holds an HTLC set; BOLT 11 rows are never pruned), and only once they are expired by at least
/// <see cref="EffectiveGrace"/> (<see cref="OfferOptions.ExpiredInvoicePruneGrace"/>, never less than the switch's MPP
/// timeout): the final hop checks the expiry when each HTLC arrives, not at the settle, so an HTLC set held across the
/// expiry, or an HTLC between its check and the fulfill's save, must still find its invoice. An HTLC that arrives after
/// the expiry is refused before and after the prune alike (<c>incorrect_or_unknown_payment_details</c>).
/// </summary>
public sealed class ExpiredBolt12InvoicePruner : IAsyncDisposable
{
    /// <summary>The most batches (saves) of one round.</summary>
    public const int MaxBatchesPerRound = 100;

    private readonly Lock _gate = new();
    private readonly ILogger<ExpiredBolt12InvoicePruner> _logger;
    private readonly OfferOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;

    private Task? _loop;
    private bool _started;
    private bool _stopped;

    public ExpiredBolt12InvoicePruner(IServiceScopeFactory scopeFactory, ILogger<ExpiredBolt12InvoicePruner> logger,
                                      IOptions<OfferOptions>? options = null, TimeProvider? timeProvider = null,
                                      IOptions<HtlcSwitchOptions>? switchOptions = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new OfferOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        var mppTimeout = switchOptions?.Value.MppTimeout ?? HtlcSwitchOptions.DefaultMppTimeout;
        if (mppTimeout <= TimeSpan.Zero)
            mppTimeout = HtlcSwitchOptions.DefaultMppTimeout;
        var grace = _options.ExpiredInvoicePruneGrace < TimeSpan.Zero ? TimeSpan.Zero : _options.ExpiredInvoicePruneGrace;
        EffectiveGrace = grace > mppTimeout ? grace : mppTimeout;
    }

    /// <summary>
    /// How long past its expiry an invoice is kept: <see cref="OfferOptions.ExpiredInvoicePruneGrace"/>, at least the
    /// switch's MPP timeout.
    /// </summary>
    public TimeSpan EffectiveGrace { get; }

    /// <summary>
    /// Starts the rounds (the first one at once); nothing when the interval is zero. Idempotent.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped || _options.ExpiredInvoicePruneInterval <= TimeSpan.Zero)
                return;

            _started = true;
            _loop = Task.Run(RunAsync);
        }
    }

    /// <summary>Stops the rounds; a batch in progress finishes first.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            loop = _loop;
        }

        await _stopping.CancelAsync();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
                // Stopping
            }
        }
    }

    /// <summary>
    /// One round: batches of at most <see cref="OfferOptions.ExpiredInvoicePruneBatchSize"/> expired unpaid BOLT 12
    /// invoices, each deleted in its own save, until a batch is short or <see cref="MaxBatchesPerRound"/> ran.
    /// </summary>
    /// <returns>How many invoices were deleted.</returns>
    public async Task<int> PruneOnceAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = Math.Max(1, _options.ExpiredInvoicePruneBatchSize);
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerRound; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int pruned;
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                pruned = await unitOfWork.InvoiceDbRepository
                                         .PruneExpiredBolt12InvoicesAsync(_timeProvider.GetUtcNow() - EffectiveGrace,
                                                                          batchSize);
                if (pruned > 0)
                    await unitOfWork.SaveChangesAsync();
            }

            total += pruned;
            if (pruned < batchSize)
                break;
        }

        if (total > 0 && _logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Deleted {Count} expired unpaid BOLT 12 invoices", total);

        return total;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await PruneOnceAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A failed round (the database unavailable, a concurrent change) is retried at the next interval
                _logger.LogError(e, "Could not delete expired BOLT 12 invoices");
            }

            try
            {
                await Task.Delay(_options.ExpiredInvoicePruneInterval, _timeProvider, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}