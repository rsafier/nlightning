namespace NLightning.Application.Channels.Fees;

using Domain.Channels.ValueObjects;

/// <summary>
/// Sends <c>update_fee</c> on the channels we fund when the fee estimate moves (BOLT2 plan N9-T1; see
/// <see cref="FeeUpdateScheduler"/>).
/// </summary>
public interface IFeeUpdateScheduler
{
    /// <summary>
    /// Starts the periodic rounds (one every <c>Node:FeeUpdates:Interval</c>, the first one after an interval), unless
    /// <c>Node:FeeUpdates:Enabled</c> is false. Call it once the channels are loaded (after
    /// <c>IPeerManager.StartAsync</c>).
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops the rounds and waits for a running one.</summary>
    Task StopAsync();

    /// <summary>
    /// Runs one round now (waits for a running one first) and returns what it decided for each channel we fund.
    /// </summary>
    Task<IReadOnlyList<FeeUpdateOutcome>> RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// What one round did for one channel.
/// </summary>
/// <param name="ChannelId">The channel.</param>
/// <param name="Decision">The policy's decision.</param>
/// <param name="Sent">True when the <c>update_fee</c> was persisted and queued.</param>
/// <param name="Reason">Why nothing was sent (the decision's or the refusal's reason), or a note.</param>
public sealed record FeeUpdateOutcome(ChannelId ChannelId, FeeUpdateDecision Decision, bool Sent, string? Reason);