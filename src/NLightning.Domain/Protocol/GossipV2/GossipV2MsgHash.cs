using System.Security.Cryptography;
using System.Text;

namespace NLightning.Domain.Protocol.GossipV2;

using Crypto.ValueObjects;

/// <summary>
/// The message hash of v2 gossip signatures (BOLTs PR #1059 "Signature Message Construction"), the BOLT 12 tagged
/// hash: <c>tag = "lightning" || message_name || field_name</c>,
/// <c>MsgHash = SHA256(SHA256(tag) || SHA256(tag) || m)</c>, where <c>m</c> is the stream's signed range
/// (<see cref="PureTlvStream.GetSignedBytes"/>). The result is the 32-byte message of the BIP 340 signature (or the
/// MuSig2 session of <c>channel_announcement_2</c>).
/// </summary>
public static class GossipV2MsgHash
{
    /// <summary>The field name of the signature record (type 240) of every v2 gossip message.</summary>
    public const string SignatureFieldName = "signature";

    /// <summary>Computes the message hash of <paramref name="signedBytes"/>.</summary>
    public static Hash Compute(string messageName, string fieldName, ReadOnlySpan<byte> signedBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageName);
        ArgumentException.ThrowIfNullOrEmpty(fieldName);

        var tag = SHA256.HashData(Encoding.UTF8.GetBytes("lightning" + messageName + fieldName));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(tag);
        sha.AppendData(tag);
        sha.AppendData(signedBytes);
        return new Hash(sha.GetHashAndReset());
    }

    /// <summary>The message hash of a v2 gossip message's <c>signature</c> record over its signed range.</summary>
    public static Hash ComputeSignatureHash(string messageName, PureTlvStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Compute(messageName, SignatureFieldName, stream.GetSignedBytes());
    }
}