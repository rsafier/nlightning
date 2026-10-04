namespace NLightning.Domain.Channels.ValueObjects;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Splicing;

/// <summary>
/// Information needed by the signer for a specific channel
/// </summary>
public record struct ChannelSigningInfo
{
    public TxId FundingTxId { get; init; }
    public ushort FundingOutputIndex { get; init; }
    public ulong FundingSatoshis { get; init; }
    public CompactPubKey LocalFundingPubKey { get; init; }
    public CompactPubKey RemoteFundingPubKey { get; init; }
    public uint ChannelKeyIndex { get; init; } // For deterministic key derivation

    /// <summary>
    /// True for a simple taproot channel (<c>option_simple_taproot</c>): the funding output is the MuSig2 key-path
    /// P2TR output of both funding keys, commitments are signed with MuSig2 partial signatures and HTLC signatures are
    /// BIP 340 (taproot wave t02).
    /// </summary>
    public bool IsSimpleTaproot { get; init; }

    /// <summary>
    /// True for a channel opened with <c>open_channel2</c>/<c>accept_channel2</c> (<c>ChannelModel.Version</c> V2). A
    /// simple taproot channel's verification nonce for commitment 0 then binds the funding txid like every other
    /// commitment's: each RBF attempt of the open is another funding with its own commitment 0, and a nonce shared by
    /// them could sign two of them for broadcast (NL-972). Only a v1 open's commitment 0, whose nonce goes out in
    /// <c>open_channel</c>/<c>accept_channel</c> before the txid exists, uses the context without a txid.
    /// </summary>
    public bool IsDualFunded { get; init; }

    /// <summary>
    /// The peer's <c>htlc_basepoint</c>, used to verify the HTLC signatures it sends for our commitments. Null until
    /// the peer's basepoints are known; HTLC signature validation then fails.
    /// </summary>
    public CompactPubKey? RemoteHtlcBasepoint { get; init; }

    /// <summary>
    /// Our current (latest persisted) local commitment number. The signer reveals the per-commitment secret of
    /// commitment <c>n</c> only when <c>n &lt; LocalCommitmentNumber</c> (NL-189); it is advanced with
    /// <c>ILightningSigner.AdvanceLocalCommitment</c> after the new commitment is persisted.
    /// </summary>
    public ulong LocalCommitmentNumber { get; init; }

    /// <summary>
    /// True when <c>channel_reestablish</c> proved we lost data on the channel (<c>ChannelModel.DataLossDetected</c>):
    /// the signer then refuses every signature for it (invariant I12, BOLT2 plan N9-T4).
    /// </summary>
    public bool DataLossDetected { get; init; }

    /// <summary>
    /// The local commitment number the channel's persisted commitment broadcast was signed at, if any (invariant S1,
    /// BOLT 5 plan O2-T1). <c>ILightningSigner.RegisterChannel</c> applies it with
    /// <c>ILightningSigner.MarkBroadcastSigned</c>, so after a restart the secret of that commitment is still never
    /// released. The host fills it from the persisted commitment broadcast row (O2-T2).
    /// </summary>
    public ulong? BroadcastSignedCommitmentNumber { get; init; }

    /// <summary>
    /// The peer's node id, when known. <c>ILightningSigner.SignChannelAnnouncement</c> then requires the announcement
    /// to name it as the other node.
    /// </summary>
    public CompactPubKey? RemoteNodeId { get; init; }

    /// <summary>
    /// The channel's real short channel id once the funding transaction confirmed, else null.
    /// <c>ILightningSigner.SignChannelAnnouncement</c> signs only an announcement of this short channel id.
    /// </summary>
    public ShortChannelId? ShortChannelId { get; init; }

    /// <summary>
    /// The channel's <c>announce_channel</c> bit (<c>ChannelParams.AnnounceChannel</c>). BOLT 7 forbids
    /// <c>announcement_signatures</c> for a private channel, so <c>ILightningSigner.SignChannelAnnouncement</c> refuses
    /// a channel where it is false.
    /// </summary>
    public bool AnnounceChannel { get; init; }

    /// <summary>
    /// The derivation index of our funding key in the current funding (splicing plan D5): 0 for a channel that was
    /// never spliced, the locked splice's <see cref="ChannelFunding.LocalFundingKeyIndex"/> after a splice.
    /// </summary>
    public uint LocalFundingKeyIndex { get; init; }

    /// <summary>
    /// The channel's other fundings (splicing plan SP1-C-T1): the pending splices commitments are signed for, and the
    /// retired (replaced or discarded) ones whose keys the on-chain resolution may still need (SP-I5). Null or empty for
    /// a channel that was never spliced. <c>ILightningSigner.RegisterChannel</c> registers each of them.
    /// </summary>
    public IReadOnlyList<ChannelFunding>? Fundings { get; init; }

    /// <summary>
    /// Invariant SP-I1 across restarts: per pending splice funding txid, the local commitment number at which our
    /// commitment spending it, with the peer's verified signatures, is persisted. <c>ILightningSigner.RegisterChannel</c>
    /// applies each entry as <c>MarkSpliceCommitmentPersisted</c> would, so the shared input can be signed again after a
    /// restart only when that commitment was saved.
    /// </summary>
    public IReadOnlyDictionary<TxId, ulong>? PersistedSpliceCommitments { get; init; }

    public ChannelSigningInfo(TxId fundingTxId, ushort fundingOutputIndex, ulong fundingSatoshis,
                              CompactPubKey localFundingPubKey, CompactPubKey remoteFundingPubKey,
                              uint channelKeyIndex, CompactPubKey? remoteHtlcBasepoint = null,
                              ulong localCommitmentNumber = 0, bool dataLossDetected = false)
    {
        FundingTxId = fundingTxId;
        FundingOutputIndex = fundingOutputIndex;
        FundingSatoshis = fundingSatoshis;
        LocalFundingPubKey = localFundingPubKey;
        RemoteFundingPubKey = remoteFundingPubKey;
        ChannelKeyIndex = channelKeyIndex;
        RemoteHtlcBasepoint = remoteHtlcBasepoint;
        LocalCommitmentNumber = localCommitmentNumber;
        DataLossDetected = dataLossDetected;
    }
}