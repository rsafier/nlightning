namespace NLightning.Application.Tests.OnionMessages.Harness;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// A per-peer messages-per-second limiter on a clock the test controls (lane M6-C ships the production token
/// buckets): at most <paramref name="messagesPerSecond"/> messages per peer in each whole second.
/// </summary>
internal sealed class MessageCountRateLimiter(int messagesPerSecond, TimeProvider timeProvider)
    : IOnionMessageRateLimiter
{
    private readonly Dictionary<CompactPubKey, (long Second, int Count)> _windows = new();

    public bool TryAdmit(CompactPubKey peerNodeId, int messageLength)
    {
        var second = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        lock (_windows)
        {
            var (windowSecond, count) = _windows.GetValueOrDefault(peerNodeId);
            if (windowSecond != second)
                count = 0;
            if (count >= messagesPerSecond)
                return false;
            _windows[peerNodeId] = (second, count + 1);
            return true;
        }
    }

    public void RemovePeer(CompactPubKey peerNodeId)
    {
        lock (_windows)
            _windows.Remove(peerNodeId);
    }
}