namespace NLightning.Application.Channels.Backup.Models;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;

/// <summary>
/// One channel of a static channel backup: what a node with the same key file needs to find the channel, reach the
/// peer, ask it to force close (<c>option_data_loss_protect</c>) and sweep our output of its commitment. It holds
/// public data only: our keys are re-derived from <see cref="KeyIndex"/>, never stored.
/// </summary>
public sealed record ChannelBackupEntry
{
    /// <summary>The channel id.</summary>
    public required ChannelId ChannelId { get; init; }

    /// <summary>The peer's node id.</summary>
    public required CompactPubKey RemoteNodeId { get; init; }

    /// <summary>The peer's last known addresses (may be empty).</summary>
    public required IReadOnlyList<ChannelBackupAddress> Addresses { get; init; }

    /// <summary>The funding transaction id (internal byte order).</summary>
    public required TxId FundingTxId { get; init; }

    /// <summary>The funding output index.</summary>
    public required ushort FundingOutputIndex { get; init; }

    /// <summary>The channel capacity (the funding output amount), in sat.</summary>
    public required ulong CapacitySat { get; init; }

    /// <summary>The height the funding was created at (a hint for a rescan; 0 when unknown).</summary>
    public uint FundingHeight { get; init; }

    /// <summary>The short channel id, once the funding confirmed.</summary>
    public ShortChannelId? ShortChannelId { get; init; }

    /// <summary>Whether we funded the channel.</summary>
    public required bool IsInitiator { get; init; }

    /// <summary>Whether the channel uses <c>option_anchors</c> (our <c>to_remote</c> then waits one block).</summary>
    public required bool OptionAnchorOutputs { get; init; }

    /// <summary>Whether the channel is announced (public).</summary>
    public bool AnnounceChannel { get; init; }

    /// <summary>Whether the channel parameters were partly inferred by a migration.</summary>
    public bool HasInferredParams { get; init; }

    /// <summary>The channel version.</summary>
    public ChannelVersion Version { get; init; } = ChannelVersion.V1;

    /// <summary>The negotiated <c>option_scid_alias</c> support.</summary>
    public FeatureSupport UseScidAlias { get; init; }

    /// <summary>The funding depth the channel waited for.</summary>
    public uint MinimumDepth { get; init; }

    /// <summary>The channel_type, as its wire bytes (BOLT 9 big-endian feature bits; empty when none).</summary>
    public required byte[] ChannelType { get; init; }

    /// <summary>Our channel key index: our basepoints are re-derived from it and the node's key.</summary>
    public required uint KeyIndex { get; init; }

    /// <summary>Our funding public key (checks that <see cref="KeyIndex"/> derives the right keys).</summary>
    public required CompactPubKey LocalFundingPubKey { get; init; }

    /// <summary>Our payment basepoint (the key of our <c>to_remote</c> output on the peer's commitment).</summary>
    public required CompactPubKey LocalPaymentBasepoint { get; init; }

    /// <summary>The peer's funding public key.</summary>
    public required CompactPubKey RemoteFundingPubKey { get; init; }

    /// <summary>The peer's revocation basepoint.</summary>
    public required CompactPubKey RemoteRevocationBasepoint { get; init; }

    /// <summary>The peer's payment basepoint.</summary>
    public required CompactPubKey RemotePaymentBasepoint { get; init; }

    /// <summary>The peer's delayed payment basepoint.</summary>
    public required CompactPubKey RemoteDelayedPaymentBasepoint { get; init; }

    /// <summary>The peer's HTLC basepoint.</summary>
    public required CompactPubKey RemoteHtlcBasepoint { get; init; }

    /// <summary>The parameters we announced.</summary>
    public required ChannelBackupParty Local { get; init; }

    /// <summary>The parameters the peer announced.</summary>
    public required ChannelBackupParty Remote { get; init; }

    /// <summary>
    /// Our funding key index of the funding in <see cref="FundingTxId"/> (splicing plan D5: 0 for the original funding,
    /// rotated by each locked splice). With <see cref="KeyIndex"/> it re-derives <see cref="LocalFundingPubKey"/>, which,
    /// like <see cref="FundingTxId"/>, <see cref="RemoteFundingPubKey"/> and <see cref="CapacitySat"/>, describes the
    /// channel's <b>current</b> funding (NL-478). Splicing plan SP2-0; written by lane SP2-E as a trailing field of the
    /// record (a minor revision: a reader that predates it skips it and reads 0).
    /// </summary>
    public uint LocalFundingKeyIndex { get; init; }

    /// <summary>
    /// The channel's pending splices at backup time (empty when none), so a restore from a backup written before the
    /// lock follows the splice (splicing plan SP2-0, lane SP2-E; trailing field of the record).
    /// </summary>
    public IReadOnlyList<ChannelBackupFunding> PendingFundings { get; init; } = [];
}