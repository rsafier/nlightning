using System.Diagnostics;

namespace NLightning.Bolts.BOLT8.Primitives;

using Ciphers;
using Common.Utils;
using Constants;
using Interfaces;
using States;

/// <inheritdoc/>
internal sealed class Transport : ITransport
{
    private readonly bool _initiator;
    private readonly CipherState _sendingKey;
    private readonly CipherState _receivingKey;

    private bool _disposed;

    public Transport(bool initiator, CipherState c1, CipherState c2)
    {
        ExceptionUtils.ThrowIfNull(c1, nameof(c1));
        ExceptionUtils.ThrowIfNull(c2, nameof(c2));

        _initiator = initiator;
        _sendingKey = c1;
        _receivingKey = c2;
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown if the current instance has already been disposed.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the responder has attempted to write a message to a one-way stream.</exception>
    /// <exception cref="ArgumentException">Thrown if the encrypted payload was greater than <see cref="ProtocolConstants.MAX_MESSAGE_LENGTH"/> bytes in length, or if the output buffer did not have enough space to hold the ciphertext.</exception>
    public int WriteMessage(ReadOnlySpan<byte> payload, Span<byte> messageBuffer)
    {
        // Serialize length into 2 bytes encoded as a big-endian integer
        var l = BitConverter.GetBytes((ushort)payload.Length);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(l);
        }

        // Encrypt the payload length into the message buffer
        var lcLen = WriteMessagePart(l, messageBuffer);

        // Encrypt the payload into the message buffer
        var mLen = WriteMessagePart(payload, messageBuffer[lcLen..]);

        return lcLen + mLen;
    }

    /// <inheritdoc/>
    public int ReadMessageLength(ReadOnlySpan<byte> lc)
    {
        ExceptionUtils.ThrowIfDisposed(_disposed, nameof(Transport));

        if (lc.Length != ProtocolConstants.MESSAGE_HEADER_SIZE)
        {
            throw new ArgumentException($"Lightning Message Header must be {ProtocolConstants.MESSAGE_HEADER_SIZE} bytes in length.");
        }

        // Decrypt the payload length from the message buffer
        var l = new byte[2];
        var lcLen = ReadMessagePart(lc, l); // TODO: Check lcLen == 2
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(l);
        }
        return BitConverter.ToUInt16(l, 0) + ChaCha20Poly1305.TAG_SIZE;
    }

    /// <inheritdoc/>
    public int ReadMessagePayload(ReadOnlySpan<byte> message, Span<byte> payloadBuffer)
    {
        // Decrypt the payload from the message buffer
        return ReadMessagePart(message, payloadBuffer);
    }

    /// <summary>
    /// Encrypts the <paramref name="payload"/> and writes the result into <paramref name="messageBuffer"/>.
    /// </summary>
    /// <param name="payload">The payload to encrypt.</param>
    /// <param name="messageBuffer">The buffer for the encrypted message.</param>
    /// <returns>The ciphertext size in bytes.</returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the current instance has already been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the responder has attempted to write a message to a one-way stream.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the encrypted payload was greater than <see cref="ProtocolConstants.MAX_MESSAGE_LENGTH"/>
    /// bytes in length, or if the output buffer did not have enough space to hold the ciphertext.
    /// </exception>
    private int WriteMessagePart(ReadOnlySpan<byte> payload, Span<byte> messageBuffer)
    {
        ExceptionUtils.ThrowIfDisposed(_disposed, nameof(Transport));

        if (payload.Length + ChaCha20Poly1305.TAG_SIZE > ProtocolConstants.MAX_MESSAGE_LENGTH)
        {
            throw new ArgumentException($"Noise message must be less than or equal to {ProtocolConstants.MAX_MESSAGE_LENGTH} bytes in length.");
        }

        if (payload.Length + ChaCha20Poly1305.TAG_SIZE > messageBuffer.Length)
        {
            throw new ArgumentException("Message buffer does not have enough space to hold the ciphertext.");
        }

        var cipher = _initiator ? _sendingKey : _receivingKey;
        Debug.Assert(cipher.HasKey());

        return cipher.Encrypt(payload, messageBuffer);
    }

    /// <summary>
    /// Decrypts the <paramref name="message"/> and writes the result into <paramref name="payloadBuffer"/>.
    /// </summary>
    /// <param name="message">The message to decrypt.</param>
    /// <param name="payloadBuffer">The buffer for the decrypted payload.</param>
    /// <returns>The plaintext size in bytes.</returns>
    /// <exception cref="ObjectDisposedException">
    /// Thrown if the current instance has already been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the initiator has attempted to read a message from a one-way stream.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown if the message was greater than <see cref="ProtocolConstants.MAX_MESSAGE_LENGTH"/>
    /// bytes in length, or if the output buffer did not have enough space to hold the plaintext.
    /// </exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// Thrown if the decryption of the message has failed.
    /// </exception>
    private int ReadMessagePart(ReadOnlySpan<byte> message, Span<byte> payloadBuffer)
    {
        ExceptionUtils.ThrowIfDisposed(_disposed, nameof(Transport));

        if (message.Length > ProtocolConstants.MAX_MESSAGE_LENGTH)
        {
            throw new ArgumentException($"Noise message must be less than or equal to {ProtocolConstants.MAX_MESSAGE_LENGTH} bytes in length.");
        }

        if (message.Length < ChaCha20Poly1305.TAG_SIZE)
        {
            throw new ArgumentException($"Noise message must be greater than or equal to {ChaCha20Poly1305.TAG_SIZE} bytes in length.");
        }

        if (message.Length - ChaCha20Poly1305.TAG_SIZE > payloadBuffer.Length)
        {
            throw new ArgumentException("Payload buffer does not have enough space to hold the plaintext.");
        }

        var cipher = _initiator ? _receivingKey : _sendingKey;
        Debug.Assert(cipher.HasKey());

        return cipher.Decrypt(message, payloadBuffer);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _sendingKey.Dispose();
            _receivingKey.Dispose();
            _disposed = true;
        }
    }
}