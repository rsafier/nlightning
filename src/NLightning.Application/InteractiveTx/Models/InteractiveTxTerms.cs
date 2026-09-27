namespace NLightning.Application.InteractiveTx.Models;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// What the dependent protocol (a splice, a dual-funded open, or an RBF of either) agreed before the first
/// <c>tx_add_*</c>: the input of <see cref="Interfaces.IInteractiveTxDriver.StartAsync"/>. The driver turns it into
/// <see cref="InteractiveTxSessionParameters"/> with the host's <see cref="SharedFundingSpec"/> and our contribution.
/// </summary>
/// <param name="ChannelId">The channel (every message of the negotiation carries it).</param>
/// <param name="LocalNodeId">Our node id (the <c>tx_signatures</c> order tie-break, IT-SIG-01).</param>
/// <param name="RemoteNodeId">The peer's node id; messages of another peer never reach the negotiation.</param>
/// <param name="IsInitiator">Whether we send the first message and even <c>serial_id</c>s (IT-S-01/02).</param>
/// <param name="FeeratePerKw">The agreed feerate (sat/kw).</param>
/// <param name="Locktime">The agreed <c>nLockTime</c>.</param>
/// <param name="LocalRequiresConfirmedInputs">We sent <c>require_confirmed_inputs</c>: the driver checks every input
/// the peer adds with <see cref="Domain.Protocol.InteractiveTx.Interfaces.IPrevTxInspector.IsConfirmedAsync"/>.</param>
/// <param name="RemoteRequiresConfirmedInputs">The peer sent <c>require_confirmed_inputs</c>.</param>
/// <param name="ContributionRequest">What the wallet contributor must fund, or null (see
/// <paramref name="Contribution"/>).</param>
/// <param name="Contribution">A contribution chosen by the host itself (for example an RBF that re-adds an input of
/// the previous attempt, IT-RBF-01); it wins over <paramref name="ContributionRequest"/>. Both null: we add nothing
/// (<see cref="InteractiveTxContribution.Empty"/>, splicing plan D10).</param>
public sealed record InteractiveTxTerms(
    ChannelId ChannelId,
    CompactPubKey LocalNodeId,
    CompactPubKey RemoteNodeId,
    bool IsInitiator,
    uint FeeratePerKw,
    uint Locktime,
    bool LocalRequiresConfirmedInputs = false,
    bool RemoteRequiresConfirmedInputs = false,
    InteractiveTxContributionRequest? ContributionRequest = null,
    InteractiveTxContribution? Contribution = null);