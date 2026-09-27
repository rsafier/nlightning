namespace NLightning.Domain.Channels.Splicing;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using ValueObjects;

/// <summary>
/// The splice-level terms of one negotiation (splicing plan §3.5, SP-W-01/02): what <c>splice_init</c> and
/// <c>splice_ack</c> carried, and how far the negotiation got. The interactive-tx part (inputs, outputs, the
/// constructed transaction, witnesses) is the <c>InteractiveTxSessionModel</c> of the same channel.
/// </summary>
/// <remarks>
/// Held by <see cref="Interfaces.ISpliceService"/> (lane SP1-D) under the channel's lock; lane SP1-C decides whether it
/// is persisted with <c>AddSpliceFundings</c> (SP-I7: the terms must be restorable from "commitment_signed sent" on,
/// which the interactive-tx row already covers for the transaction).
/// </remarks>
/// <param name="ChannelId">The channel.</param>
/// <param name="IsInitiator">We sent <c>splice_init</c> (we are the quiescence initiator, SP-S-01).</param>
/// <param name="LocalContributionSatoshis">Our signed contribution (positive splice-in, negative splice-out).</param>
/// <param name="RemoteContributionSatoshis">The peer's signed contribution, once known.</param>
/// <param name="FeeratePerKw">The splice transaction's feerate (<c>funding_feerate_perkw</c>).</param>
/// <param name="Locktime">The splice transaction's <c>nLockTime</c>.</param>
/// <param name="LocalFundingPubKey">Our funding key for the new funding.</param>
/// <param name="LocalFundingKeyIndex">Its derivation index (D5).</param>
/// <param name="RemoteFundingPubKey">The peer's funding key for the new funding, once known.</param>
/// <param name="LocalRequiresConfirmedInputs">We sent <c>require_confirmed_inputs</c>.</param>
/// <param name="RemoteRequiresConfirmedInputs">The peer sent <c>require_confirmed_inputs</c>.</param>
/// <param name="SpliceOutScript">The scriptPubKey our splice-out goes to (null for a splice-in, or our wallet chosen
/// later).</param>
/// <param name="State">How far the negotiation got.</param>
/// <param name="SpliceTxId">The splice transaction id, once constructed.</param>
/// <param name="CreatedAt">When the negotiation started.</param>
public sealed record SpliceNegotiationModel(
    ChannelId ChannelId,
    bool IsInitiator,
    long LocalContributionSatoshis,
    long? RemoteContributionSatoshis,
    uint FeeratePerKw,
    uint Locktime,
    CompactPubKey LocalFundingPubKey,
    uint LocalFundingKeyIndex,
    CompactPubKey? RemoteFundingPubKey,
    bool LocalRequiresConfirmedInputs,
    bool RemoteRequiresConfirmedInputs,
    BitcoinScript? SpliceOutScript,
    SpliceNegotiationState State,
    TxId? SpliceTxId,
    DateTimeOffset CreatedAt);