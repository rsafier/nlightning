using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NLightning.Domain.Signing.Vls;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Recovery;

/// <summary>
/// Snapshot fingerprints of the VLS close workflows. A mutual close is bound to the exact outputs VLS signs (recovery
/// recomputes it from the saved request, since the negotiation that chose them is memory-only); a force close is bound
/// to the commitment number and the unsigned commitment transaction rebuilt from the saved channel.
/// </summary>
public static class VlsCloseFingerprint
{
    public static byte[] MutualClose(ulong holderSatoshis, ulong peerSatoshis, byte[]? holderScript, byte[]? peerScript)
    {
        using var stream = new MemoryStream();
        stream.Write("nltg-vls-mutual-close/1"u8);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(number, holderSatoshis);
        stream.Write(number);
        BinaryPrimitives.WriteUInt64BigEndian(number, peerSatoshis);
        stream.Write(number);
        WriteScript(stream, holderScript);
        WriteScript(stream, peerScript);
        return SHA256.HashData(stream.ToArray());
    }

    public static byte[] ForceClose(ulong commitmentNumber, TxId unsignedCommitmentTxId)
    {
        using var stream = new MemoryStream();
        stream.Write("nltg-vls-force-close/1"u8);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(number, commitmentNumber);
        stream.Write(number);
        stream.Write((byte[])unsignedCommitmentTxId);
        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteScript(Stream stream, byte[]? script)
    {
        Span<byte> length = stackalloc byte[4];
        // A missing output is distinct from an empty script
        BinaryPrimitives.WriteInt32BigEndian(length, script?.Length ?? -1);
        stream.Write(length);
        if (script is not null)
            stream.Write(script);
    }
}

/// <summary>Recovery of interrupted VLS close signatures under their original request IDs.</summary>
public interface IVlsCloseSigningRecovery
{
    /// <summary>
    /// Opens a saved mutual close intent exactly as it was saved: the negotiation that chose its outputs is gone, so
    /// the intent's fingerprint is checked against the outputs of its saved request instead. A mismatch blocks it.
    /// </summary>
    Task<ISigningWorkflowScope> ResumeSavedMutualCloseAsync(SigningWorkflow saved);

    /// <summary>Replays the single saved close request of an activated workflow and returns VLS's signature.</summary>
    CompactSignature ReplayCloseSignature(ISigningWorkflowScope workflow);
}