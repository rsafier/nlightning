using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.Gossip.Sync;

using Domain.Channels.ValueObjects;
using Domain.Gossip.Interfaces;
using Interfaces;

/// <summary>
/// The payment retry path's <see cref="IGossipScidRefresher"/> over the gossip sync (BOLT 7 plan G3-T5, D9): a request
/// becomes one <see cref="IGossipSyncManager.QueryScidAsync"/> on the thread pool, never awaited by the caller, and
/// requests for a channel whose query is still in flight are coalesced (dropped).
/// </summary>
public sealed class GossipSyncScidRefresher : IGossipScidRefresher
{
    /// <summary>The longest a coalesced query holds its channel; the sync's own reply timeout normally ends it first.</summary>
    internal static readonly TimeSpan MaxQueryWait = TimeSpan.FromMinutes(5);

    private readonly IGossipSyncManager _syncManager;
    private readonly ILogger<GossipSyncScidRefresher>? _logger;
    private readonly ConcurrentDictionary<ShortChannelId, Task> _inFlight = new();

    public GossipSyncScidRefresher(IGossipSyncManager syncManager, ILogger<GossipSyncScidRefresher>? logger = null)
    {
        _syncManager = syncManager;
        _logger = logger;
    }

    /// <summary>The queries still in flight (tests).</summary>
    internal int InFlightCount => _inFlight.Count;

    /// <inheritdoc />
    public bool RequestRefresh(ShortChannelId shortChannelId)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(shortChannelId, gate.Task))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(MaxQueryWait);
                var answered = await _syncManager.QueryScidAsync(shortChannelId, cts.Token);
                _logger?.LogDebug("Gossip refresh of {ShortChannelId}: {Outcome}", shortChannelId,
                                  answered ? "answered" : "not answered");
            }
            catch (Exception e)
            {
                _logger?.LogWarning(e, "Gossip refresh of {ShortChannelId} failed", shortChannelId);
            }
            finally
            {
                _inFlight.TryRemove(shortChannelId, out _);
                gate.TrySetResult();
            }
        });
        return true;
    }
}