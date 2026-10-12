using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Protocol.Constants;

[ExcludeFromCodeCoverage]
internal static class ProtocolConstants
{
    /// <summary>
    /// Maximum size of a Lightning message (the plaintext of a BOLT 8 transport message) in bytes.
    /// </summary>
    public const int MaxMessageLength = 65535;

    /// <summary>
    /// Maximum size of an encrypted BOLT 8 message body (plaintext plus the 16-byte MAC) in bytes.
    /// </summary>
    public const int MaxEncryptedMessageLength = MaxMessageLength + 16;

    /// <summary>
    /// Maximum size of a full BOLT 8 packet (encrypted length header plus encrypted body): 2 + 16 + 65535 + 16.
    /// </summary>
    public const int MaxEncryptedPacketLength = MessageHeaderSize + MaxEncryptedMessageLength;

    /// <summary>
    /// The size of the Message Header.
    /// </summary>
    public const int MessageHeaderSize = 18;

    /// <summary>
    /// The byte[] representation of the Prologue for the Lightning Network.
    /// </summary>
    public static readonly byte[] Prologue = "lightning"u8.ToArray();

    /// <summary>
    /// The byte[] representations of the name of the Noise protocol.
    /// </summary>
    public static readonly byte[] Name = "Noise_XK_secp256k1_ChaChaPoly_SHA256"u8.ToArray();

    /// <summary>
    /// Empty message used throughout the Noise protocol.
    /// </summary>
    public static readonly byte[] EmptyMessage = [];
}