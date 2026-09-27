using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace NLightning.Application.OnionMessages;

using Domain.Protocol.OnionMessages;
using Domain.Protocol.OnionMessages.Constants;

/// <summary>
/// The replies we wait for (plan D4: in memory, deadline-bounded), keyed by the <c>path_id</c> of the reply path we
/// published (BOLT 4: the writer MAY put a secret in <c>path_id</c> to recognize its reply path).
/// </summary>
/// <remarks>
/// <para>A <c>path_id</c> is <c>nonce(16) || HMAC-SHA256(secret, nonce)[0..16]</c> with a per-process secret, so
/// <see cref="IsOurs"/> recognizes every reply path this process published, also after its wait ended. A message
/// through one of them is only ever a reply to that wait: of the expected types while it waits, ignored otherwise
/// (BOLT 4 reader, OM-R-06). After a restart the secret is new, so an old path_id is unknown and handled as if we had
/// never sent the request (the spec's rule for unknown path_ids).</para>
/// <para>Thread-safe.</para>
/// </remarks>
public sealed class PendingReplyRegistry
{
    /// <summary>The <c>path_id</c> length.</summary>
    public const int PathIdLength = 32;

    private const int NonceLength = 16;
    private static ReadOnlySpan<byte> Label => "nltg_onion_message_reply_path"u8;

    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly int _maxPending;

    public PendingReplyRegistry(int maxPending = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, 1);
        _maxPending = maxPending;
    }

    /// <summary>The waits in progress.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Starts a wait for a reply of one of <paramref name="expectedTypes"/>.
    /// </summary>
    /// <returns>The wait, or null when <see cref="Count"/> is at the limit.</returns>
    public PendingReply? TryRegister(IReadOnlyCollection<ulong> expectedTypes)
    {
        ArgumentNullException.ThrowIfNull(expectedTypes);
        if (_pending.Count >= _maxPending)
            return null;

        var pathId = new byte[PathIdLength];
        RandomNumberGenerator.Fill(pathId.AsSpan(0, NonceLength));
        ComputeTag(pathId.AsSpan(0, NonceLength)).AsSpan(0, PathIdLength - NonceLength)
                                                  .CopyTo(pathId.AsSpan(NonceLength));
        var pending = new Pending(expectedTypes.ToHashSet());
        _pending[Convert.ToHexString(pathId)] = pending;
        return new PendingReply(this, pathId, pending.Completion.Task);
    }

    /// <summary>
    /// Whether <paramref name="pathId"/> is the path_id of a reply path this process published (constant time).
    /// </summary>
    public bool IsOurs(ReadOnlySpan<byte> pathId)
    {
        if (pathId.Length != PathIdLength)
            return false;
        var tag = ComputeTag(pathId[..NonceLength]);
        return CryptographicOperations.FixedTimeEquals(pathId[NonceLength..],
                                                       tag.AsSpan(0, PathIdLength - NonceLength));
    }

    /// <summary>
    /// Hands <paramref name="message"/>, which came through our reply path <paramref name="pathId"/>, to its wait.
    /// </summary>
    /// <returns>True when a wait took it; false when none waits for that path or the type is not expected (the reader
    /// then ignores the message).</returns>
    public bool TryComplete(ReadOnlySpan<byte> pathId, ReceivedOnionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var key = Convert.ToHexString(pathId);
        if (!_pending.TryGetValue(key, out var pending))
            return false;

        var fields = message.Contents.Records
                            .Where(r => r.Type >= OnionMessageConstants.FirstPayloadFieldType)
                            .ToList();
        if (fields.Count != 1 || !pending.ExpectedTypes.Contains(fields[0].Type))
            return false;

        if (!_pending.TryRemove(key, out _))
            return false;

        return pending.Completion.TrySetResult(message);
    }

    internal void Remove(byte[] pathId) => _pending.TryRemove(Convert.ToHexString(pathId), out _);

    private byte[] ComputeTag(ReadOnlySpan<byte> nonce)
    {
        Span<byte> input = stackalloc byte[Label.Length + NonceLength];
        Label.CopyTo(input);
        nonce.CopyTo(input[Label.Length..]);
        return HMACSHA256.HashData(_secret, input);
    }

    private sealed class Pending(HashSet<ulong> expectedTypes)
    {
        public HashSet<ulong> ExpectedTypes { get; } = expectedTypes;

        public TaskCompletionSource<ReceivedOnionMessage> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// One wait of <see cref="PendingReplyRegistry"/>. Dispose it when the wait ends (a late reply is then ignored).
/// </summary>
public sealed class PendingReply : IDisposable
{
    private readonly PendingReplyRegistry _registry;

    internal PendingReply(PendingReplyRegistry registry, byte[] pathId, Task<ReceivedOnionMessage> reply)
    {
        _registry = registry;
        PathId = pathId;
        Reply = reply;
    }

    /// <summary>The <c>path_id</c> to put in our hop of the reply path.</summary>
    public byte[] PathId { get; }

    /// <summary>Completes with the reply.</summary>
    public Task<ReceivedOnionMessage> Reply { get; }

    public void Dispose() => _registry.Remove(PathId);
}