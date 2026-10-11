using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Mocks;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;

/// <summary>
/// The local signer's durable guard store in memory (NL-1345, NL-1376), shared by several signers as one database is
/// shared by several processes. <see cref="FailLoads"/>, <see cref="FailRaises"/> and <see cref="FailClaims"/> make the
/// store unavailable (every call of that kind throws) until they are cleared.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class InMemoryChannelSignerGuardStore : IChannelSignerGuardStore
{
    private readonly Dictionary<ChannelId, ChannelSignerGuard> _guards = new();
    private readonly Dictionary<string, ChannelBroadcastSession> _sessions = new();

    public bool FailLoads { get; set; }
    public bool FailRaises { get; set; }
    public bool FailClaims { get; set; }

    /// <summary>The broadcast sessions claimed so far.</summary>
    public int ClaimedSessions
    {
        get
        {
            lock (_sessions)
                return _sessions.Count;
        }
    }

    public ChannelSignerGuard? Load(ChannelId channelId)
    {
        if (FailLoads)
            throw new InvalidOperationException("The database is unavailable");

        lock (_guards)
            return _guards.TryGetValue(channelId, out var guard) ? guard : null;
    }

    public void Raise(ChannelId channelId, ChannelSignerGuard guard)
    {
        if (FailRaises)
            throw new InvalidOperationException("The process died before the write");

        lock (_guards)
            _guards[channelId] = _guards.TryGetValue(channelId, out var current) ? current.Merge(guard) : guard;
    }

    public bool ClaimBroadcastSession(ChannelId channelId, ChannelBroadcastSession session)
    {
        if (FailClaims)
            throw new InvalidOperationException("The process died before the write");

        var key = $"{channelId}:{session.NonceFundingTxId?.ToString() ?? "-"}:{session.CommitmentNumber}";
        lock (_sessions)
        {
            if (_sessions.TryGetValue(key, out var claimed))
                return claimed.SessionDigest == session.SessionDigest;

            _sessions[key] = session;
            return true;
        }
    }
}