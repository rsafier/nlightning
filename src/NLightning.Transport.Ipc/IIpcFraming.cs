namespace NLightning.Transport.Ipc;

/// <summary>
/// Reads and writes length-prefixed <see cref="IpcEnvelope"/> frames on a stream. One connection carries exactly one
/// request and one response, with no server push.
/// </summary>
public interface IIpcFraming
{
    /// <summary>
    /// Reads one frame and deserializes its envelope.
    /// </summary>
    /// <exception cref="EndOfStreamException">The stream ended inside a frame.</exception>
    /// <exception cref="IOException">The length prefix is not a valid frame length.</exception>
    Task<IpcEnvelope> ReadAsync(Stream stream, CancellationToken ct);

    /// <summary>
    /// Serializes the envelope and writes it as one frame.
    /// </summary>
    Task WriteAsync(Stream stream, IpcEnvelope envelope, CancellationToken ct);
}