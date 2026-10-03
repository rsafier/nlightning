using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Application.Payments;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Payments.Trampoline;
using Interfaces;

/// <summary>
/// Lists a page of our forwarded payments, newest first, with the totals over the whole filtered set
/// (<c>ClientCommand 40</c>, NL-597) and the HTLCs refused before a forward circuit since the process started
/// (NL-598).
/// </summary>
/// <remarks>
/// A <c>short_channel_id</c> given as the channel filter also matches the incoming side, through the channel it
/// names in <see cref="IChannelMemoryRepository"/>. Every channel of a forward is shown by its scid when the channel
/// is loaded and has one, falling back to the channel id (<c>ForwardInfoClientResponse.*Scid</c> is null then).
/// </remarks>
public sealed class ListForwardsClientHandler
    : IClientCommandHandler<ListForwardsClientRequest, ListForwardsClientResponse>
{
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IForwardCircuitDbRepository _forwardCircuitRepository;
    private readonly IRefusedHtlcCounter? _refusedHtlcCounter;
    private readonly ITrampolineRelayDbRepository? _trampolineRelayRepository;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListForwards;

    public ListForwardsClientHandler(IForwardCircuitDbRepository forwardCircuitRepository,
                                     ILogger<ListForwardsClientHandler> logger,
                                     IChannelMemoryRepository? channelMemoryRepository = null,
                                     IRefusedHtlcCounter? refusedHtlcCounter = null,
                                     ITrampolineRelayDbRepository? trampolineRelayRepository = null)
    {
        _trampolineRelayRepository = trampolineRelayRepository;
        _ = logger;
        _forwardCircuitRepository = forwardCircuitRepository;
        _channelMemoryRepository = channelMemoryRepository;
        _refusedHtlcCounter = refusedHtlcCounter;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The page is invalid (negative skip, take outside 1 to
    /// <see cref="ClientRequestGuards.MaxPageSize"/>).</exception>
    public async Task<ListForwardsClientResponse> HandleAsync(ListForwardsClientRequest request,
                                                              CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ClientRequestGuards.ThrowIfInvalidPage(request.Skip, request.Take);

        // A scid given as --channel also matches the incoming side through the channel it names
        var channelId = request.ChannelId ?? ScidToChannelId(request.ChannelScid);
        var query = new ForwardCircuitListQuery(request.Skip, request.Take, request.Since, request.Until,
                                                request.Status, channelId, request.ChannelScid);

        var forwards = await _forwardCircuitRepository.ListAsync(query, ct);
        var totals = await _forwardCircuitRepository.SummarizeAsync(query, ct);
        var (relays, relayTotals) = await ListTrampolineRelaysAsync(request, channelId, ct);

        // NL-981: the totals count the trampoline relays with the forwards (collecting = pending, sending = offered)
        var refused = _refusedHtlcCounter?.Snapshot() ?? new Dictionary<RefusedHtlcReason, long>();
        var summary = new ForwardSummaryClientResponse
        {
            Pending = totals.Pending + relayTotals.Collecting,
            Offered = totals.Offered + relayTotals.Sending,
            Fulfilled = totals.Fulfilled + relayTotals.Fulfilled,
            Failed = totals.Failed + relayTotals.Failed,
            FulfilledFeesMsat = totals.FulfilledFeesMsat + relayTotals.FulfilledFeesMsat,
            TrampolineRelays = relayTotals,
            RefusedTotal = refused.Values.Sum(),
            RefusedByReason = refused
                              .OrderBy(kvp => (int)kvp.Key)
                              .Select(kvp => new RefusedReasonCount(kvp.Key.ToString(), kvp.Value))
                              .ToList()
        };

        return new ListForwardsClientResponse(
            forwards.Select(c => ForwardInfoClientResponse.FromModel(c, ScidOf(c.IncomingChannelId),
                                             ScidOf(c.OutgoingChannelId), ScidOf(c.FailureSource)))
                    .ToList(), summary, relays);
    }

    /// <summary>
    /// The page of trampoline relays (NL-875) under the same filters, with their totals over the whole filtered set
    /// (NL-981): the forward statuses map to the relay's (pending = collecting, offered = sending), a channel filter
    /// matches a relay with an incoming part on it, and a scid that names none of our channels matches no relay (a
    /// relay has no single outgoing channel). The failed attempts a payer's retry replaced (NL-899) are listed with
    /// the relays, newest first, as failed relays with their attempt number.
    /// </summary>
    private async Task<(IReadOnlyList<TrampolineRelayInfoClientResponse> Relays, TrampolineRelayTotals Totals)>
        ListTrampolineRelaysAsync(ListForwardsClientRequest request, ChannelId? channelId, CancellationToken ct)
    {
        if (_trampolineRelayRepository is null || (request.ChannelScid is not null && channelId is null))
            return ([], default);

        TrampolineRelayStatus? status = request.Status switch
        {
            null => null,
            ForwardCircuitStatus.Pending => TrampolineRelayStatus.Collecting,
            ForwardCircuitStatus.Offered => TrampolineRelayStatus.Sending,
            ForwardCircuitStatus.Fulfilled => TrampolineRelayStatus.Fulfilled,
            _ => TrampolineRelayStatus.Failed
        };

        // Both sources newest first, each up to the end of the page; merged, then the page is cut
        var through = (int)Math.Min((long)request.Skip + request.Take, int.MaxValue);
        var query = new TrampolineRelayListQuery(0, through, request.Since, request.Until, status, channelId);
        IReadOnlyList<TrampolineRelayModel> relays;
        try
        {
            relays = await _trampolineRelayRepository.ListAsync(query, ct);
        }
        catch (NotSupportedException)
        {
            return ([], default);
        }

        var replaced = await _trampolineRelayRepository.ListReplacedAttemptsAsync(query, ct) ?? [];
        TrampolineRelayTotals totals;
        try
        {
            totals = await _trampolineRelayRepository.SummarizeAsync(query, ct);
        }
        catch (NotSupportedException)
        {
            totals = default;
        }

        var rows = relays.Select(r => (r.CreatedAt, Relay: (TrampolineRelayModel?)r,
                                       Attempt: (TrampolineRelayAttemptModel?)null))
                         .Concat(replaced.Select(a => (a.CreatedAt, Relay: (TrampolineRelayModel?)null,
                                                       Attempt: (TrampolineRelayAttemptModel?)a)))
                         .OrderByDescending(row => row.CreatedAt)
                         .Skip(request.Skip)
                         .Take(request.Take)
                         .ToList();

        var result = new List<TrampolineRelayInfoClientResponse>(rows.Count);
        foreach (var (_, relay, attempt) in rows)
        {
            if (relay is not null)
            {
                var parts = await _trampolineRelayRepository.GetPartsAsync(relay.PaymentHash);
                result.Add(TrampolineRelayInfoClientResponse.FromModel(relay, parts, id => ScidOf(id)));
            }
            else
            {
                result.Add(TrampolineRelayInfoClientResponse.FromAttempt(attempt!, id => ScidOf(id)));
            }
        }

        return (result, totals);
    }

    /// <summary>
    /// The channel of a <c>short_channel_id</c> filter, when it names one of our open channels; the repository's own
    /// scid match covers the outgoing side either way.
    /// </summary>
    private ChannelId? ScidToChannelId(ShortChannelId? scid)
    {
        if (scid is not { } value || _channelMemoryRepository is null)
            return null;

        return _channelMemoryRepository
              .FindChannels(c => c.State == Domain.Channels.Enums.ChannelState.Open
                              && c.ShortChannelId != default && c.ShortChannelId == value)
              .FirstOrDefault()?.ChannelId;
    }

    /// <summary>The channel's <c>short_channel_id</c> as text, when the channel is loaded and announced one.</summary>
    private string? ScidOf(ChannelId? channelId) =>
        channelId is { } id
        && _channelMemoryRepository?.TryGetChannel(id, out var channel) is true
        && channel.ShortChannelId != default
            ? channel.ShortChannelId.ToString()
            : null;
}