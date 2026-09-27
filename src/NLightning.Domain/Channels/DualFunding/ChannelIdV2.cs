namespace NLightning.Domain.Channels.DualFunding;

using Crypto.Constants;
using Crypto.Hashes;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The v2 channel id (BOLT 2 "<c>channel_id</c>, v2"): <c>SHA256(lesser-revocation-basepoint ||
/// greater-revocation-basepoint)</c>, the basepoints ordered as their 33-byte compressed encodings (lexicographic).
/// </summary>
/// <remarks>
/// When sending <c>open_channel2</c> the peer's basepoint is unknown: the <c>temporary_channel_id</c> is computed with
/// a zeroed (33 zero bytes) basepoint for the non-initiator (<see cref="DeriveTemporary"/>), which is always the lesser
/// one, so the zeroes come first (as Core Lightning's <c>derive_tmp_channel_id</c>). <c>accept_channel2</c> echoes that
/// temporary id; both sides then switch to <see cref="Derive"/>.
/// </remarks>
public static class ChannelIdV2
{
    /// <summary>The v2 channel id from both revocation basepoints (order does not matter).</summary>
    /// <exception cref="ArgumentException">Both basepoints are the same key.</exception>
    public static ChannelId Derive(ISha256 sha256, CompactPubKey localRevocationBasepoint,
                                   CompactPubKey remoteRevocationBasepoint)
    {
        ArgumentNullException.ThrowIfNull(sha256);

        ReadOnlySpan<byte> local = localRevocationBasepoint;
        ReadOnlySpan<byte> remote = remoteRevocationBasepoint;
        var order = local.SequenceCompareTo(remote);
        if (order == 0)
            throw new ArgumentException("Both revocation basepoints are the same key", nameof(remoteRevocationBasepoint));

        return order < 0 ? Hash(sha256, local, remote) : Hash(sha256, remote, local);
    }

    /// <summary>
    /// The <c>temporary_channel_id</c> of our <c>open_channel2</c>: <see cref="Derive"/> with 33 zero bytes as the
    /// non-initiator's basepoint.
    /// </summary>
    public static ChannelId DeriveTemporary(ISha256 sha256, CompactPubKey initiatorRevocationBasepoint)
    {
        ArgumentNullException.ThrowIfNull(sha256);

        Span<byte> zeroes = stackalloc byte[CryptoConstants.CompactPubkeyLen];
        zeroes.Clear();
        return Hash(sha256, zeroes, initiatorRevocationBasepoint);
    }

    private static ChannelId Hash(ISha256 sha256, ReadOnlySpan<byte> lesser, ReadOnlySpan<byte> greater)
    {
        Span<byte> hash = stackalloc byte[CryptoConstants.Sha256HashLen];
        sha256.AppendData(lesser);
        sha256.AppendData(greater);
        sha256.GetHashAndReset(hash);
        return new ChannelId(hash);
    }
}