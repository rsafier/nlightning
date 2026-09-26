using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Fees;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Exceptions;
using Domain.Node.Options;

/// <summary>
/// Keeps the commitment feerate of the channels we fund in line with the node's fee estimate (BOLT2 plan N9-T1,
/// B2-FEE-S01): every <see cref="FeeUpdateOptions.Interval"/> it asks <see cref="FeeUpdatePolicy"/> about each open
/// channel we fund and sends the <c>update_fee</c> it decides through
/// <see cref="IChannelOperations.UpdateFeeAsync"/>, which persists it, sends it and has the commit scheduler sign it
/// (one update, then one <c>commitment_signed</c>).
/// </summary>
/// <remarks>
/// <para>Channels are skipped when we are not the funder (BOLT 2: MUST NOT send <c>update_fee</c>), when they are not
/// <c>Open</c>, have no commitment state or detected data loss. A refused update (<see cref="CommitmentRefusedException"/>:
/// HTLCs disabled, the peer is away or not reestablished, or the engine's sender rules) is logged and tried again the
/// next round; nothing was persisted. The decision reads the channel's snapshot outside its lock; the engine checks
/// the update again under the lock (the dust exposure check of a feerate increase is advisory, BOLT 2 "MAY NOT").</para>
/// <para>Rounds never overlap: <see cref="RunOnceAsync"/> waits for a running round.</para>
/// </remarks>
public sealed class FeeUpdateScheduler : IFeeUpdateScheduler, IAsyncDisposable, IDisposable
{
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IChannelOperations _channelOperations;
    private readonly IFeeService _feeService;
    private readonly ILogger<FeeUpdateScheduler> _logger;
    private readonly IOptions<NodeOptions> _nodeOptions;
    private readonly SemaphoreSlim _roundLock = new(1, 1);
    private readonly TimeProvider _timeProvider;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public FeeUpdateScheduler(IChannelMemoryRepository channelMemoryRepository, IChannelOperations channelOperations,
                              IFeeService feeService, ILogger<FeeUpdateScheduler> logger,
                              IOptions<NodeOptions> nodeOptions, TimeProvider? timeProvider = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _channelOperations = channelOperations;
        _feeService = feeService;
        _logger = logger;
        _nodeOptions = nodeOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
            throw new InvalidOperationException("The fee update scheduler is already running");

        var options = _nodeOptions.Value.FeeUpdates;
        if (!options.Enabled)
        {
            _logger.LogInformation("Fee updates are disabled (Node:FeeUpdates:Enabled)");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = RunLoopAsync(options.Interval, _cts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        if (_cts is null || _loop is null)
            return;

        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeeUpdateOutcome>> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await _roundLock.WaitAsync(cancellationToken);
        try
        {
            return await RunRoundAsync(cancellationToken);
        }
        finally
        {
            _roundLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _roundLock.Dispose();
    }

    /// <summary>
    /// Cancels the rounds without waiting for a running one (a container disposed synchronously); prefer
    /// <see cref="StopAsync"/>.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task RunLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(interval, _timeProvider, cancellationToken);
            try
            {
                await RunOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Fee update round failed");
            }
        }
    }

    private async Task<IReadOnlyList<FeeUpdateOutcome>> RunRoundAsync(CancellationToken cancellationToken)
    {
        var nodeOptions = _nodeOptions.Value;
        var channels = _channelMemoryRepository.FindChannels(IsCandidate);
        if (channels.Count == 0)
            return [];

        uint estimate;
        try
        {
            var feeRate = await _feeService.GetFeeRatePerKwAsync(cancellationToken);
            estimate = (uint)Math.Clamp(feeRate.Satoshi, 0, uint.MaxValue);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "No fee estimate: channel feerates stay as they are");
            return [];
        }

        if (estimate == 0)
        {
            _logger.LogWarning("The fee estimate is 0 (no estimate yet): channel feerates stay as they are");
            return [];
        }

        var outcomes = new List<FeeUpdateOutcome>(channels.Count);
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcomes.Add(await UpdateChannelAsync(channel, estimate, nodeOptions, cancellationToken));
        }

        return outcomes;
    }

    private async Task<FeeUpdateOutcome> UpdateChannelAsync(ChannelModel channel, uint estimate, NodeOptions options,
                                                            CancellationToken cancellationToken)
    {
        var commitments = channel.Commitments!;
        var maxDust = DustExposurePolicy.Resolve(commitments, options.MaxDustHtlcExposureMsat);
        var decision = FeeUpdatePolicy.Decide(commitments, estimate, options.FeeUpdates, maxDust);
        if (decision.FeeratePerKw is not { } feerate)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("No update_fee on channel {ChannelId} (feerate {Current}, estimate {Estimate} sat/kw): "
                               + "{Reason}", channel.ChannelId, decision.CurrentFeeratePerKw, estimate,
                                 decision.Reason);
            return new FeeUpdateOutcome(channel.ChannelId, decision, false, decision.Reason);
        }

        try
        {
            await _channelOperations.UpdateFeeAsync(channel.ChannelId, feerate, cancellationToken);
        }
        catch (Exception e) when (e is CommitmentRefusedException or KeyNotFoundException)
        {
            _logger.LogInformation("update_fee {Feerate} sat/kw on channel {ChannelId} refused: {Reason}", feerate,
                                   channel.ChannelId, e.Message);
            return new FeeUpdateOutcome(channel.ChannelId, decision, false, e.Message);
        }

        _logger.LogInformation("Sent update_fee on channel {ChannelId}: {Current} -> {Feerate} sat/kw (estimate "
                             + "{Estimate} sat/kw){Note}", channel.ChannelId, decision.CurrentFeeratePerKw, feerate,
                               estimate, decision.Reason is null ? string.Empty : $", {decision.Reason}");
        return new FeeUpdateOutcome(channel.ChannelId, decision, true, decision.Reason);
    }

    private static bool IsCandidate(ChannelModel channel) =>
        channel is { State: ChannelState.Open, IsInitiator: true, Commitments: not null, DataLossDetected: false };
}