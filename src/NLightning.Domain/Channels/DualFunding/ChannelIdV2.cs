namespace NLightning.Domain.Channels.DualFunding;

using Crypto.Hashes;
using Crypto.ValueObjects;
using ValueObjects;

/// <summary>
/// The v2 channel id (BOLT 2 "<c>channel_id</c>, v2"): <c>SHA256(lesser-revocation-basepoint ||
/// greater-revocation-basepoint)</c>, the basepoints ordered as their 33-byte compressed encodings (lexicographic).
/// </summary>
/// <remarks>
/// <para>When sending <c>open_channel2</c> the peer's basepoint is unknown: the <c>temporary_channel_id</c> is computed
/// with a zeroed (33 zero bytes) basepoint for the non-initiator (<see cref="DeriveTemporary"/>). <c>accept_channel2</c>
/// echoes that temporary id; both sides then switch to <see cref="Derive"/>.</para>
/// <para>Signatures only in the SP1 contracts; lane SP1-F (wave DF, DF1) implements and tests them.</para>
/// </remarks>
public static class ChannelIdV2
{
    /// <summary>The v2 channel id from both revocation basepoints (order does not matter).</summary>
    public static ChannelId Derive(ISha256 sha256, CompactPubKey localRevocationBasepoint,
                                   CompactPubKey remoteRevocationBasepoint) =>
        throw new NotImplementedException("Lane SP1-F (DF1)");

    /// <summary>
    /// The <c>temporary_channel_id</c> of our <c>open_channel2</c>: <see cref="Derive"/> with 33 zero bytes as the
    /// non-initiator's basepoint.
    /// </summary>
    public static ChannelId DeriveTemporary(ISha256 sha256, CompactPubKey initiatorRevocationBasepoint) =>
        throw new NotImplementedException("Lane SP1-F (DF1)");
}