namespace NLightning.Infrastructure.Crypto.Hashes;

using Domain.Crypto.Hashes;

/// <summary>
/// An <see cref="ISha256"/> that is safe to share between concurrent callers: every thread hashes with its own
/// <see cref="Sha256"/> state (NL-247).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Sha256"/> keeps its state between <see cref="AppendData"/> and <see cref="GetHashAndReset"/>, so one
/// instance shared by concurrent callers (the DI singleton, or a singleton consumer such as <c>ChannelFactory</c>
/// holding it) could mix their data and return wrong hashes. This is the instance the container hands out.
/// </para>
/// <para>
/// A caller must append and read its hash on the same thread, without an <c>await</c> in between (every caller does:
/// the span parameters make that the natural shape). Unlike a private <see cref="Sha256"/>, which is thrown away, a
/// thread's state outlives its caller: one that failed between the two would leave its bytes for the next, unrelated
/// caller on that thread, and that caller would get a wrong hash. So hash through
/// <see cref="Sha256Extensions.ComputeHash(ISha256, ReadOnlySpan{byte}, Span{byte})"/> (or its two-part overload),
/// which resets the state even when appending throws.
/// </para>
/// </remarks>
public sealed class ThreadLocalSha256 : ISha256
{
    private readonly ThreadLocal<Sha256> _perThread = new(() => new Sha256(), trackAllValues: true);
    private int _disposed;

    /// <inheritdoc />
    public void AppendData(ReadOnlySpan<byte> data) => Current.AppendData(data);

    /// <inheritdoc />
    public void GetHashAndReset(Span<byte> hash) => Current.GetHashAndReset(hash);

    private Sha256 Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            return _perThread.Value!;
        }
    }

    /// <summary>Frees the state of every thread that used this instance.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        foreach (var sha256 in _perThread.Values)
            sha256.Dispose();
        _perThread.Dispose();
    }
}