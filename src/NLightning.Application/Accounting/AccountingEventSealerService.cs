using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting;

using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Persistence.Interfaces;

/// <summary>
/// The accounting feed's single sealer (NL-602, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §3 principle 3, §10): every
/// <see cref="AccountingOptions.SealInterval"/> (once at start, and early on <see cref="Nudge"/>) a round reads the
/// committed, unsealed events in storage id order, gives each its dense ledger sequence and chain hash after the chain
/// tip (<see cref="AccountingEventSealer"/>) and marks repeated keys as duplicates, one scope and one save per batch of
/// <see cref="AccountingOptions.SealBatchSize"/>, until a batch comes back short or <see cref="MaxBatchesPerRound"/>
/// ran.
/// </summary>
/// <remarks>
/// <para>Rounds never overlap (one gate for the loop and <see cref="SealNowAsync"/>), so the ledger sequence stays
/// dense and the chain linear. A row whose save committed after a round read the table is simply sealed by the next
/// round, after the rows sealed before it: readers that follow the ledger sequence never skip it.</para>
/// <para>A duplicate is logged as a warning with its key and counted on <c>nlightning.accounting.events.duplicate</c>
/// (a writer bug, never a failed core save); sealed events are counted on
/// <c>nlightning.accounting.events.sealed</c>. A failed round (the database unavailable) is logged and retried at
/// the next interval; nothing is lost, the rows stay unsealed.</para>
/// </remarks>
public sealed class AccountingEventSealerService : IAccountingEventSealer, IAsyncDisposable, IDisposable
{
    /// <summary>The meter's name.</summary>
    public const string MeterName = "NLightning.Accounting";

    /// <summary>The most batches (saves) of one round; the next round continues.</summary>
    public const int MaxBatchesPerRound = 100;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly ILogger<AccountingEventSealerService> _logger;
    private readonly AccountingOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeProvider _timeProvider;
    private readonly Counter<long> _sealedCounter;
    private readonly Counter<long> _duplicateCounter;

    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private bool _started;
    private bool _stopped;
    private long _totalSealed;
    private long _totalDuplicates;

    public AccountingEventSealerService(IServiceScopeFactory scopeFactory,
                                        ILogger<AccountingEventSealerService> logger,
                                        IOptions<AccountingOptions>? options = null,
                                        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new AccountingOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;

        Meter = new Meter(MeterName);
        _sealedCounter = Meter.CreateCounter<long>("nlightning.accounting.events.sealed", "{event}",
                                                   "Accounting events given a ledger sequence");
        _duplicateCounter = Meter.CreateCounter<long>("nlightning.accounting.events.duplicate", "{event}",
                                                      "Accounting events whose key was already sealed (a writer bug)");
    }

    /// <summary>The interval between rounds (<see cref="AccountingOptions.SealInterval"/>, the default when not
    /// positive: the feed is always sealed).</summary>
    public TimeSpan Interval => _options.SealInterval > TimeSpan.Zero
                                    ? _options.SealInterval
                                    : AccountingOptions.DefaultSealInterval;

    /// <summary>The events sealed since the process started.</summary>
    public long TotalSealed => Interlocked.Read(ref _totalSealed);

    /// <summary>The duplicates marked since the process started.</summary>
    public long TotalDuplicates => Interlocked.Read(ref _totalDuplicates);

    /// <summary>The meter, for tests that assert the instruments.</summary>
    internal Meter Meter { get; }

    /// <summary>Starts the rounds (the first one at once). Idempotent; nothing after <see cref="StopAsync"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _stopped)
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

    /// <inheritdoc />
    public void Nudge()
    {
        lock (_gate)
            _wake.TrySetResult();
    }

    /// <inheritdoc />
    public async Task<AccountingSealRoundResult> SealNowAsync(CancellationToken cancellationToken = default)
    {
        await _roundGate.WaitAsync(cancellationToken);
        try
        {
            return await SealRoundAsync(cancellationToken);
        }
        finally
        {
            _roundGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        Meter.Dispose();
    }

    /// <summary>Cancels the loop without waiting for it (a container disposed synchronously); prefer
    /// <see cref="DisposeAsync"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
            _stopped = true;

        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed asynchronously before
        }

        Meter.Dispose();
    }

    private async Task<AccountingSealRoundResult> SealRoundAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Max(1, _options.SealBatchSize);
        var sealedCount = 0;
        var duplicateCount = 0;
        var tip = AccountingChainTip.Genesis;
        var tipRead = false;
        for (var batchNumber = 0; batchNumber < MaxBatchesPerRound; batchNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int batchCount;
            using (var scope = _scopeFactory.CreateScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var repository = unitOfWork.AccountingEventDbRepository;
                var batch = await repository.GetUnsealedAsync(batchSize, cancellationToken);
                batchCount = batch.Count;
                if (batchCount == 0)
                {
                    if (!tipRead)
                        tip = await repository.GetChainTipAsync(cancellationToken);
                    break;
                }

                var keys = batch.Select(e => e.EventKey).ToList();
                var sealedKeys = await repository.GetSealedKeysAsync(keys, cancellationToken);
                var currentTip = await repository.GetChainTipAsync(cancellationToken);
                var seals = AccountingEventSealer.Seal(currentTip, batch, sealedKeys, out var newTip);
                await repository.ApplySealsAsync(seals, cancellationToken);
                await unitOfWork.SaveChangesAsync();

                tip = newTip;
                tipRead = true;
                var keysById = batch.ToDictionary(e => e.Id, e => e.EventKey);
                foreach (var seal in seals)
                {
                    if (!seal.IsDuplicate)
                    {
                        sealedCount++;
                        continue;
                    }

                    duplicateCount++;
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning("Accounting event {Id} repeats the key {EventKey}: marked duplicate (a "
                                         + "writer recorded one fact twice)", seal.Id, keysById[seal.Id]);
                }
            }

            if (batchCount < batchSize)
                break;
        }

        if (sealedCount > 0)
        {
            _sealedCounter.Add(sealedCount);
            Interlocked.Add(ref _totalSealed, sealedCount);
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Sealed {Count} accounting events up to ledger sequence {LedgerSeq}", sealedCount,
                                 tip.LedgerSeq);
        }

        if (duplicateCount > 0)
        {
            _duplicateCounter.Add(duplicateCount);
            Interlocked.Add(ref _totalDuplicates, duplicateCount);
        }

        return new AccountingSealRoundResult(sealedCount, duplicateCount, tip);
    }

    private async Task RunAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            // A nudge that arrives during the round wakes the next wait at once
            Task wake;
            lock (_gate)
            {
                if (_wake.Task.IsCompleted)
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wake = _wake.Task;
            }

            try
            {
                await SealNowAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // The rows stay unsealed: the next round seals them
                _logger.LogError(e, "Could not seal the accounting events");
            }

            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var delay = Task.Delay(Interval, _timeProvider, delayCancellation.Token);
            await Task.WhenAny(delay, wake);
            await delayCancellation.CancelAsync();
        }
    }
}