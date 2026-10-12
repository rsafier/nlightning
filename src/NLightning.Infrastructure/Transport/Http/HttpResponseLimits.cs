using System.Buffers;
using System.Text;

namespace NLightning.Infrastructure.Transport.Http;

/// <summary>
/// Bounded reads of the node's own HTTP answers (fee estimation, Esplora, the accounting price source; NL-678,
/// SECURITY_REVIEW SR-22): a body longer than the caller's cap is refused with
/// <see cref="HttpResponseTooLargeException"/> after reading at most one byte past the cap, whatever its
/// <c>Content-Length</c> says (a declared length over the cap is refused before reading). <see cref="HttpClient"/>'s
/// own buffer limit (2 GiB by default) applies only to buffered reads, so these clients read with
/// <see cref="HttpCompletionOption.ResponseHeadersRead"/> and then through here.
/// </summary>
public static class HttpResponseLimits
{
    /// <summary>The cap of a small JSON answer (a fee estimate, a price, a merkle proof, a txid): 64 KiB.</summary>
    public const int SmallResponseMaxBytes = 64 * 1024;

    /// <summary>
    /// The cap of an Esplora block txid list: 4 MiB (a block holds at most about 17,000 transactions within its weight,
    /// about 1.1 MB as a JSON list of txids).
    /// </summary>
    public const int TxIdListMaxBytes = 4 * 1024 * 1024;

    /// <summary>The body of <paramref name="content"/>, at most <paramref name="maxBytes"/> bytes.</summary>
    /// <exception cref="HttpResponseTooLargeException">The body is longer than <paramref name="maxBytes"/>.</exception>
    public static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
            throw new HttpResponseTooLargeException(maxBytes, declared);

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var room = maxBytes + 1 - (int)buffer.Length;
                var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, room)), cancellationToken);
                if (read == 0)
                    break;

                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                    throw new HttpResponseTooLargeException(maxBytes, null);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    /// <summary>The body of <paramref name="content"/> as UTF-8 text (a byte order mark is skipped), at most
    /// <paramref name="maxBytes"/> bytes.</summary>
    /// <exception cref="HttpResponseTooLargeException">The body is longer than <paramref name="maxBytes"/>.</exception>
    public static async Task<string> ReadBoundedStringAsync(HttpContent content, int maxBytes,
                                                            CancellationToken cancellationToken = default)
    {
        var bytes = await ReadBoundedAsync(content, maxBytes, cancellationToken);
        var preamble = Encoding.UTF8.Preamble;
        var start = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return Encoding.UTF8.GetString(bytes, start, bytes.Length - start);
    }
}

/// <summary>An HTTP answer longer than its reader's cap (<see cref="HttpResponseLimits"/>, NL-678).</summary>
public sealed class HttpResponseTooLargeException : HttpRequestException
{
    /// <param name="maxBytes">The cap.</param>
    /// <param name="declaredLength">The <c>Content-Length</c> over the cap, or null when the body itself ran past it.</param>
    public HttpResponseTooLargeException(int maxBytes, long? declaredLength)
        : base(declaredLength is { } length
                   ? $"The response declares {length} bytes, more than the {maxBytes} accepted"
                   : $"The response is longer than the {maxBytes} bytes accepted")
    {
        MaxBytes = maxBytes;
        DeclaredLength = declaredLength;
    }

    /// <summary>The cap.</summary>
    public int MaxBytes { get; }

    /// <summary>The declared <c>Content-Length</c> over the cap, or null.</summary>
    public long? DeclaredLength { get; }
}