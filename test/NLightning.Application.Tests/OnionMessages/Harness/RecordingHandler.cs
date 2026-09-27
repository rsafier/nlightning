using System.Collections.Concurrent;

namespace NLightning.Application.Tests.OnionMessages.Harness;

using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Interfaces;

/// <summary>
/// A handler that records what it gets and, when <see cref="ReplyWith"/> is set, answers through the message's
/// <c>reply_path</c>.
/// </summary>
internal sealed class RecordingHandler(params ulong[] payloadTypes) : IOnionMessageHandler
{
    private readonly ConcurrentQueue<ReceivedOnionMessage> _received = new();
    private readonly SemaphoreSlim _signal = new(0);

    public IReadOnlyCollection<ulong> PayloadTypes { get; } = payloadTypes;

    /// <summary>The service of the node the handler runs on (set by the harness).</summary>
    public IOnionMessageService? Service { get; set; }

    /// <summary>The contents to reply with, or null for no reply.</summary>
    public OnionMessageContents? ReplyWith { get; set; }

    public IReadOnlyList<ReceivedOnionMessage> Received => _received.ToList();

    public async Task HandleAsync(ReceivedOnionMessage message, CancellationToken cancellationToken)
    {
        _received.Enqueue(message);
        if (ReplyWith is { } reply && message.ReplyPath is { } replyPath)
            await Service!.SendAsync(OnionMessageDestination.ToBlindedPath(replyPath), reply, null, cancellationToken);
        _signal.Release();
    }

    /// <summary>Waits until <paramref name="count"/> messages in total have been handled.</summary>
    public async Task WaitForAsync(int count, CancellationToken cancellationToken, int timeoutMs = 10_000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutMs);
        while (_received.Count < count)
            await _signal.WaitAsync(timeout.Token);
    }
}