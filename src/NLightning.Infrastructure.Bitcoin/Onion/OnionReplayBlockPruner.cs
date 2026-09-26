using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Bitcoin.Onion;

using Domain.Bitcoin.Events;
using Domain.Protocol.Onion.Interfaces;
using Wallet.Interfaces;

/// <summary>
/// Prunes the onion replay set on every new block (NL-327): <see cref="IOnionReplayStore.PruneAsync"/> at the block's
/// height, so the entries whose HTLC <c>cltv_expiry</c> the chain passed go even when no HTLC arrives.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IBlockchainMonitor.OnNewBlockDetected"/> is raised on the monitor's processing path after the block's
/// save, so the handler only records the height and returns: the prune runs on its own task. Prunes run one at a time
/// and coalesce: blocks that arrive while one runs are covered by a single prune at the highest height seen. A lower
/// height (a replayed block at start, or a reorg) prunes nothing new and is ignored; the entries already pruned are
/// not brought back, as with the store's lazy pruning. A failed prune is logged and retried by the next block (the
/// store's lazy prune in <c>TryAddAsync</c> still runs too).
/// </para>
/// <para>
/// Lifecycle: <see cref="Start"/> after the chain monitor is started, <see cref="StopAsync"/> before it is stopped
/// (waits for a running prune). Registered by <c>AddOnionReplayBlockPruner()</c>.
/// </para>
/// </remarks>
public sealed class OnionReplayBlockPruner
{
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IOnionReplayStore _replayStore;
    private readonly ILogger<OnionReplayBlockPruner> _logger;
    private readonly Lock _gate = new();

    private CancellationTokenSource? _stopping;
    private Task _round = Task.CompletedTask;
    private uint _requestedHeight;
    private uint _prunedHeight;
    private bool _roundScheduled;

    public OnionReplayBlockPruner(IBlockchainMonitor blockchainMonitor, IOnionReplayStore replayStore,
                                  ILogger<OnionReplayBlockPruner> logger)
    {
        _blockchainMonitor = blockchainMonitor;
        _replayStore = replayStore;
        _logger = logger;
    }

    /// <summary>Subscribes to new blocks. A second call does nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_stopping is not null)
                return;

            _stopping = new CancellationTokenSource();
        }

        _blockchainMonitor.OnNewBlockDetected += HandleNewBlockDetected;
    }

    /// <summary>Unsubscribes and waits for a running prune. Does nothing when not started.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        Task round;
        lock (_gate)
        {
            stopping = _stopping;
            _stopping = null;
            round = _round;
        }

        if (stopping is null)
            return;

        _blockchainMonitor.OnNewBlockDetected -= HandleNewBlockDetected;
        await stopping.CancelAsync();
        try
        {
            await round;
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }
        finally
        {
            stopping.Dispose();
        }
    }

    /// <summary>Waits until no prune is running or scheduled (tests).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task round;
            lock (_gate)
                round = _round;

            await round;

            lock (_gate)
            {
                if (ReferenceEquals(round, _round) && !_roundScheduled)
                    return;
            }
        }
    }

    private void HandleNewBlockDetected(object? sender, NewBlockEventArgs args)
    {
        lock (_gate)
        {
            if (_stopping is null || args.Height <= _requestedHeight)
                return;

            _requestedHeight = args.Height;
            if (_roundScheduled)
                return;

            _roundScheduled = true;
            var token = _stopping.Token;
            var previous = _round;
            _round = Task.Run(() => RunRoundAsync(previous, token), CancellationToken.None);
        }
    }

    private async Task RunRoundAsync(Task previous, CancellationToken cancellationToken)
    {
        await previous.ConfigureAwait(false);
        while (true)
        {
            uint height;
            lock (_gate)
            {
                if (cancellationToken.IsCancellationRequested || _requestedHeight <= _prunedHeight)
                {
                    _roundScheduled = false;
                    return;
                }

                height = _requestedHeight;
            }

            try
            {
                var removed = await _replayStore.PruneAsync(height, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                    _prunedHeight = Math.Max(_prunedHeight, height);

                if (removed > 0 && _logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Pruned {Count} onion replay entries at block {Height}", removed, height);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                    _roundScheduled = false;
                return;
            }
            catch (Exception ex)
            {
                // Retried by the next block
                _logger.LogError(ex, "Failed to prune the onion replay set at block {Height}", height);
                lock (_gate)
                {
                    _requestedHeight = _prunedHeight;
                    _roundScheduled = false;
                }

                return;
            }
        }
    }
}