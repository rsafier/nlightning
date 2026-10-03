using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Node.Services;

using Channels.Safety;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.Policies;
using Domain.Channels.Quiescence;
using Domain.Node.Options;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using InteractiveTx.Interfaces;

/// <summary>
/// What still keeps the node busy for a <c>shutdown --wait</c> (NL-592): the HTLCs in flight on every channel that is
/// not Closed or Stale, and the negotiations that are mid-flight.
/// </summary>
/// <param name="ChannelCount">The channels that are not Closed or Stale.</param>
/// <param name="HtlcsInFlight">Their HTLCs in flight (<see cref="ChannelHtlcs.InFlight"/>, the same count the first
/// pass refused on, NL-591).</param>
/// <param name="NegotiationCount">How many channels carry a negotiation that is mid-flight.</param>
/// <param name="Channels">The busy channels: the ones with HTLCs in flight and/or a mid-flight negotiation.</param>
/// <param name="NearestCltvExpiry">The nearest <c>cltv_expiry</c> among the in-flight HTLCs, 0 when none.</param>
/// <param name="BlocksUntilDeadline">
/// The blocks that remain until our deadline to act on the nearest-deadline HTLC
/// (<see cref="HtlcDeadlinePolicy"/>'s rule), -1 when the height is unknown.</param>
/// <remarks>
/// <para>"Mid-flight" for a negotiation: an interactive-tx attempt in progress (or our <c>tx_abort</c> waiting for its
/// echo, or our <c>tx_init_rbf</c> waiting for <c>tx_ack_rbf</c>), any quiescence (<c>stfu</c> exchanged or pending —
/// a splice, an RBF of one, or the driver's probe), or an open that is not signed yet (<c>V1Opening</c>,
/// <c>V1FundingCreated</c>, <c>V2Opening</c>). A signed-but-unconfirmed open or splice is not mid-flight: it is
/// persisted and survives the restart (reestablish, <c>next_funding</c>, the splice depth watcher).</para>
/// <para>Reads are taken without the channel locks (the driver's and the quiescence service's contract allows stale
/// reads for diagnostics): a shutdown waits for the clear state to hold for a settle period, which covers a read taken
/// while a transition lands.</para>
/// </remarks>
public sealed record NodeBusyState(int ChannelCount, int HtlcsInFlight, int NegotiationCount,
                                   IReadOnlyList<NodeBusyChannel> Channels, uint NearestCltvExpiry,
                                   int BlocksUntilDeadline)
{
    /// <summary>Whether anything would keep a <c>shutdown --wait</c> waiting.</summary>
    public bool IsBusy => HtlcsInFlight > 0 || NegotiationCount > 0;
}

/// <summary>One busy channel of a <see cref="NodeBusyState"/>.</summary>
/// <param name="ChannelId">The channel id, as <c>listchannels</c> prints it.</param>
/// <param name="HtlcsInFlight">Its HTLCs in flight.</param>
/// <param name="Negotiating">Whether a negotiation is mid-flight on it.</param>
public sealed record NodeBusyChannel(string ChannelId, int HtlcsInFlight, bool Negotiating);

/// <summary>
/// Takes <see cref="NodeBusyState"/> snapshots for the shutdown drain (NL-592); see the record for what "busy" means.
/// </summary>
public interface INodeBusyStateMonitor
{
    /// <summary>The node's busy state, read now.</summary>
    NodeBusyState Snapshot();
}

/// <summary>The default <see cref="INodeBusyStateMonitor"/>; see <see cref="NodeBusyState"/>.</summary>
public sealed class NodeBusyStateMonitor : INodeBusyStateMonitor
{
    private readonly IBlockchainMonitor? _blockchainMonitor;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly IInteractiveTxDriver? _interactiveTxDriver;
    private readonly ILogger<NodeBusyStateMonitor>? _logger;
    private readonly HtlcDeadlinePolicy _policy;
    private readonly IQuiescenceService? _quiescenceService;

    /// <summary>
    /// Every dependency but the channel repository is optional, so a lean node (and a test) can take snapshots of the
    /// parts it has.
    /// </summary>
    public NodeBusyStateMonitor(IChannelMemoryRepository channelMemoryRepository,
                                ILogger<NodeBusyStateMonitor>? logger = null,
                                IInteractiveTxDriver? interactiveTxDriver = null,
                                IQuiescenceService? quiescenceService = null,
                                IOptions<ChannelSafetyOptions>? safetyOptions = null,
                                IOptions<NodeOptions>? nodeOptions = null,
                                IBlockchainMonitor? blockchainMonitor = null)
    {
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
        _interactiveTxDriver = interactiveTxDriver;
        _quiescenceService = quiescenceService;
        _blockchainMonitor = blockchainMonitor;
        _policy = (safetyOptions?.Value ?? new ChannelSafetyOptions())
                  .CreatePolicy(nodeOptions?.Value.Routing);
    }

    /// <inheritdoc />
    public NodeBusyState Snapshot()
    {
        var channels = _channelMemoryRepository.FindChannels(IsActive);
        var busy = new List<NodeBusyChannel>();
        var htlcsInFlight = 0;
        var negotiations = 0;
        uint nearestExpiry = 0;
        uint? earliestDeadline = null;

        foreach (var channel in channels)
        {
            var htlcs = ChannelHtlcs.InFlight(channel);
            var negotiating = IsNegotiating(channel);
            if (htlcs > 0 || negotiating)
            {
                busy.Add(new NodeBusyChannel(channel.ChannelId.ToString(), htlcs, negotiating));
                htlcsInFlight += htlcs;
                if (negotiating)
                    negotiations++;
            }

            if (channel.Commitments is not { } commitments)
                continue;

            foreach (var htlc in commitments.Htlcs.Values)
            {
                if (HtlcStateTable.IsFinal(htlc.State))
                    continue;

                if (htlc.CltvExpiry != 0 && (nearestExpiry == 0 || htlc.CltvExpiry < nearestExpiry))
                    nearestExpiry = htlc.CltvExpiry;

                var deadline = DeadlineOf(htlc);
                if (deadline is { } d && (earliestDeadline is null || d < earliestDeadline))
                    earliestDeadline = d;
            }
        }

        var height = _blockchainMonitor?.LastProcessedBlockHeight ?? 0;
        var blocksUntilDeadline = earliestDeadline is null || height == 0
            ? -1
            : (int)Math.Max(0, earliestDeadline.Value - height);

        return new NodeBusyState(channels.Count, htlcsInFlight, negotiations, busy, nearestExpiry,
                                 blocksUntilDeadline);
    }

    private static bool IsActive(ChannelModel channel) =>
        channel.State is not (ChannelState.Closed or ChannelState.Stale);

    /// <summary>
    /// Whether a negotiation is mid-flight on the channel; see <see cref="NodeBusyState"/>'s remarks for what counts.
    /// </summary>
    private bool IsNegotiating(ChannelModel channel)
    {
        if (channel.State is ChannelState.V1Opening or ChannelState.V1FundingCreated or ChannelState.V2Opening)
            return true;

        if (_quiescenceService is not null && !QuiescenceState.None.Equals(_quiescenceService.GetState(
                channel.ChannelId)))
            return true;

        if (_interactiveTxDriver is null)
            return false;

        if (_interactiveTxDriver.IsNegotiating(channel.ChannelId))
            return true;

        return _interactiveTxDriver.GetInfo(channel.ChannelId) is
        { AwaitingAbortEcho: true } or { RbfRequested: true };
    }

    /// <summary>
    /// Our deadline to act on the HTLC, without resolving what its incoming side waits for: the earliest deadline the
    /// policy could give it (a fail-back comes before every fulfillment deadline, so an incoming HTLC that is not a
    /// sent fulfill is due at its fail-back height at the latest).
    /// </summary>
    private uint DeadlineOf(HtlcRecord htlc) =>
        htlc.Direction == HtlcDirection.Outgoing
            ? _policy.OfferedDeadline(htlc.CltvExpiry)
            : htlc.Removal is { IsFulfill: true }
                ? _policy.FulfillDeadline(htlc.CltvExpiry)
                : _policy.FailBackHeight(htlc.CltvExpiry);
}