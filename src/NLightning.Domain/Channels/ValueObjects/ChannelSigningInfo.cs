namespace NLightning.Domain.Channels.ValueObjects;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;

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