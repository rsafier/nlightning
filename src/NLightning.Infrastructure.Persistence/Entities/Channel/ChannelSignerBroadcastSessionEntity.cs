// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The MuSig2 session a simple taproot channel's verification nonce signed for the broadcast of our commitment
/// (NL-1376, migration <c>AddSignerBroadcastSessions</c>, Domain <c>ChannelBroadcastSession</c>). One row per nonce
/// (channel, nonce context, commitment number), written once by <c>ChannelSignerGuardStore</c> and never changed; no
/// foreign key to the channel row, like <see cref="ChannelSignerGuardEntity"/>.
/// </summary>
public class ChannelSignerBroadcastSessionEntity
{
    public required ChannelId ChannelId { get; set; }

    /// <summary>The funding txid the nonce is bound to; <see cref="TxId.Zero"/> for the txid-free context of a v1
    /// open's commitment 0 (a key column cannot be null).</summary>
    public required TxId NonceFundingTxId { get; set; }

    /// <summary>The local commitment number signed for broadcast.</summary>
    public required long CommitmentNumber { get; set; }

    /// <summary>SHA-256 of the session's sighash and aggregate nonce.</summary>
    public required Hash SessionDigest { get; set; }

    internal ChannelSignerBroadcastSessionEntity() { }
}