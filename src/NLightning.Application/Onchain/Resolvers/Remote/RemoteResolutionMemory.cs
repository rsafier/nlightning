using System.Collections.Concurrent;

namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Channels.ValueObjects;

/// <summary>
/// Process-wide state of the remote-commitment resolution (singleton): which upstream events this process already
/// handed to the switch, and the CRITICAL alerts raised so far (for an IPC flag).
/// </summary>
/// <remarks>
/// A row's "upstream raised" flag is saved before the event is raised (after the save). A crash between that save and
/// the raise would lose the event, so the planner is told the upstream is resolved only when the flag is saved
/// <b>and</b> this process raised the event: after a restart every flagged event is raised once more (the switch is
/// idempotent, <c>IHtlcSwitch</c> remarks), then never again.
/// </remarks>
public sealed class RemoteResolutionMemory
{
    private readonly ConcurrentDictionary<(ChannelId ChannelId, ulong HtlcId), byte> _raised = new();
    private readonly ConcurrentDictionary<ChannelId, string> _criticalAlerts = new();

    /// <summary>The CRITICAL alerts per channel (B5-RMT-03 data loss, B5-GEN-06 lost funds).</summary>
    public IReadOnlyDictionary<ChannelId, string> CriticalAlerts => _criticalAlerts;

    /// <summary>True when this process raised the upstream event of our offered HTLC <paramref name="htlcId"/>.</summary>
    public bool WasRaised(ChannelId channelId, ulong htlcId) => _raised.ContainsKey((channelId, htlcId));

    /// <summary>Records that the upstream event of <paramref name="htlcId"/> reached the switch.</summary>
    public void MarkRaised(ChannelId channelId, ulong htlcId) => _raised.TryAdd((channelId, htlcId), 0);

    /// <summary>Records a CRITICAL alert for the channel (the last one wins).</summary>
    public void RaiseCriticalAlert(ChannelId channelId, string message) => _criticalAlerts[channelId] = message;
}