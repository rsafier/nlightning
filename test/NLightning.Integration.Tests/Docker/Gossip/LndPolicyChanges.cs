using Lnrpc;
using LNUnit.LND;

namespace NLightning.Integration.Tests.Docker.Gossip;

using Domain.Channels.ValueObjects;
using Utils;

/// <summary>
/// Policy changes a proof makes on the shared LND nodes (<c>UpdateChannelPolicy</c>, plan Proofs G2 (d) and G3 (b)),
/// remembered so <see cref="RestoreAsync"/> puts every changed policy back (the fixture is shared by the whole
/// collection). Each change is a new <c>channel_update</c> with a new timestamp.
/// </summary>
public sealed class LndPolicyChanges
{
    private static readonly TimeSpan s_seenTimeout = TimeSpan.FromMinutes(2);

    private readonly Dictionary<(string Node, ulong Scid), (LNDNodeConnection Lnd, RoutingPolicy Original, string Point)>
        _changed = [];

    /// <summary>
    /// When each (node, channel) policy was last changed.
    /// </summary>
    private readonly Dictionary<(string Node, ulong Scid), DateTimeOffset> _changedAt = [];

    /// <summary>
    /// The policy <paramref name="lnd"/> had on <paramref name="shortChannelId"/> before the first change.
    /// </summary>
    public RoutingPolicy Original(LNDNodeConnection lnd, ulong shortChannelId) =>
        _changed[(lnd.LocalNodePubKey.ToLowerInvariant(), shortChannelId)].Original;

    /// <summary>
    /// Sets <paramref name="lnd"/>'s fee on <paramref name="shortChannelId"/> (other fields unchanged).
    /// </summary>
    public async Task SetAsync(LNDNodeConnection lnd, ulong shortChannelId, long feeBaseMsat, uint feePpm,
                               CancellationToken cancellationToken)
    {
        var (original, point) = await RememberAsync(lnd, shortChannelId, cancellationToken);
        await LndRoutingProbe.UpdatePolicyAsync(lnd, point, original, feeBaseMsat, feePpm, cancellationToken);
        _changedAt[(lnd.LocalNodePubKey.ToLowerInvariant(), shortChannelId)] = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// A fresh <c>channel_update</c> for each of <paramref name="shortChannelIds"/> from <paramref name="lnd"/>: the
    /// original base fee on even rounds and one msat more on odd ones, so every round really changes the policy.
    /// </summary>
    public async Task RefreshAsync(LNDNodeConnection lnd, IEnumerable<ulong> shortChannelIds, int round,
                                   CancellationToken cancellationToken)
    {
        foreach (var scid in shortChannelIds)
        {
            var (original, _) = await RememberAsync(lnd, scid, cancellationToken);
            await SetAsync(lnd, scid, original.FeeBaseMsat + round % 2, (uint)original.FeeRateMilliMsat,
                           cancellationToken);
        }
    }

    /// <summary>
    /// Waits until <paramref name="observer"/>'s graph has every change made so far (the announced policy's
    /// <c>last_update</c> is not older than the change).
    /// </summary>
    public async Task WaitSeenByAsync(LNDNodeConnection observer, CancellationToken cancellationToken)
    {
        foreach (var ((node, scid), at) in _changedAt)
            await Poll.UntilAsync(async () =>
                                  {
                                      var policy = await LndRoutingProbe.GetPolicyAsync(observer, scid, node,
                                                                                        cancellationToken);
                                      return policy.LastUpdate >= at.ToUnixTimeSeconds() - 1;
                                  }, s_seenTimeout,
                                  $"{observer.LocalAlias} has {node[..16]}…'s new policy on {new ShortChannelId(scid)}",
                                  cancellationToken, GossipGraphProbe.PollInterval);
    }

    /// <summary>
    /// Puts every changed policy back to its original fee. Never throws (it runs in <c>finally</c>); failures are
    /// logged.
    /// </summary>
    public async Task RestoreAsync()
    {
        foreach (var ((_, scid), (lnd, original, point)) in _changed)
        {
            try
            {
                await LndRoutingProbe.UpdatePolicyAsync(lnd, point, original, original.FeeBaseMsat,
                                                        (uint)original.FeeRateMilliMsat, CancellationToken.None);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Restoring {lnd.LocalAlias}'s policy on {new ShortChannelId(scid)} failed: "
                                + e.Message);
            }
        }
    }

    private async Task<(RoutingPolicy Original, string Point)> RememberAsync(LNDNodeConnection lnd, ulong scid,
                                                                            CancellationToken cancellationToken)
    {
        var key = (lnd.LocalNodePubKey.ToLowerInvariant(), scid);
        if (_changed.TryGetValue(key, out var known))
            return (known.Original, known.Point);

        var original = await LndRoutingProbe.GetPolicyAsync(lnd, scid, lnd.LocalNodePubKey, cancellationToken);
        var point = await LndRoutingProbe.GetChannelPointAsync(lnd, scid, cancellationToken);
        _changed[key] = (lnd, original, point);
        return (original, point);
    }
}