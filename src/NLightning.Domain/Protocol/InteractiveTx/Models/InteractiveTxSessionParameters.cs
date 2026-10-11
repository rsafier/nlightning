namespace NLightning.Domain.Protocol.InteractiveTx.Models;

using Channels.ValueObjects;
using Crypto.ValueObjects;

/// <summary>
/// What an <see cref="InteractiveTxSession"/> is created with: the terms agreed before the first <c>tx_add_*</c>
/// (in <c>splice_init</c>/<c>splice_ack</c>, <c>open_channel2</c>/<c>accept_channel2</c> or
/// <c>tx_init_rbf</c>/<c>tx_ack_rbf</c>).
/// </summary>
/// <param name="ChannelId">The channel (the <c>channel_id</c> of every message of the negotiation).</param>
/// <param name="IsInitiator">Whether we are the initiator: we send the first message and even <c>serial_id</c>s, and pay
/// the common fields (IT-S-01..03).</param>
/// <param name="FeeratePerKw">The agreed feerate (sat/kw) each side's contribution must pay (IT-R-04).</param>
/// <param name="Locktime">The agreed <c>nLockTime</c>.</param>
/// <param name="DustLimitSatoshis">The channel's negotiated dust limit (sat), the larger of both sides'
/// <c>dust_limit_satoshis</c>: a received <c>tx_add_output</c> below it fails the negotiation (IT-R-02, NL-473). 0
/// when none is known (only Bitcoin Core's standardness floor applies, see
/// <see cref="InteractiveTxRules.CheckOutput"/>).</param>
/// <param name="LocalContribution">What we add.</param>
/// <param name="SharedFunding">The shared input/output, or null when there is none.</param>
/// <param name="LocalRequiresConfirmedInputs">We sent <c>require_confirmed_inputs</c>: every input the peer adds must be
/// confirmed (checked by the driver with <see cref="Interfaces.IPrevTxInspector.IsConfirmedAsync"/> before it hands the
/// <c>tx_add_input</c> to the session).</param>
/// <param name="RemoteRequiresConfirmedInputs">The peer sent <c>require_confirmed_inputs</c>: our contributor must only
/// pick confirmed inputs.</param>
/// <param name="LocalNodeId">Our node id (the IT-SIG-01 tie-break: the lower node id sends <c>tx_signatures</c> first
/// when both contributed the same amount).</param>
/// <param name="RemoteNodeId">The peer's node id.</param>
/// <param name="PreviousAttempts">For an RBF (<c>tx_init_rbf</c>), every earlier signed attempt of this negotiation:
/// the new one must double-spend each of them (IT-RBF-01). Empty otherwise.</param>
public sealed record InteractiveTxSessionParameters(
    ChannelId ChannelId,
    bool IsInitiator,
    uint FeeratePerKw,
    uint Locktime,
    ulong DustLimitSatoshis,
    InteractiveTxContribution LocalContribution,
    SharedFundingSpec? SharedFunding,
    bool LocalRequiresConfirmedInputs,
    bool RemoteRequiresConfirmedInputs,
    CompactPubKey LocalNodeId,
    CompactPubKey RemoteNodeId,
    IReadOnlyList<ConstructedInteractiveTx> PreviousAttempts);