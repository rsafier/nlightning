using Microsoft.Extensions.Logging;

namespace NLightning.Daemon.Handlers;

using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Persistence.Interfaces;
using Interfaces;

/// <summary>
/// Lists the sealed accounting events in ledger order after a cursor (ClientCommand 41, <c>listaccountingevents</c>,
/// NL-602).
/// </summary>
/// <remarks>
/// The events committed so far are sealed first (<see cref="IAccountingEventSealer.SealNowAsync"/>), so the operator
/// sees the rows written a moment ago; a failed seal is logged and the sealed rows are listed anyway. A
/// <c>short_channel_id</c> filter is resolved through the loaded channels (a closed channel is named by its channel
/// id).
/// </remarks>
public sealed class ListAccountingEventsClientHandler
    : IClientCommandHandler<ListAccountingEventsClientRequest, ListAccountingEventsClientResponse>
{
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly ILogger<ListAccountingEventsClientHandler> _logger;
    private readonly IAccountingEventSealer? _sealer;
    private readonly IUnitOfWork _unitOfWork;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ListAccountingEvents;

    public ListAccountingEventsClientHandler(IUnitOfWork unitOfWork, ILogger<ListAccountingEventsClientHandler> logger,
                                             IAccountingEventSealer? sealer = null,
                                             IChannelMemoryRepository? channelMemoryRepository = null)
    {
        _unitOfWork = unitOfWork;
        _logger = logger;
        _sealer = sealer;
        _channelMemoryRepository = channelMemoryRepository;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">A negative cursor, a page outside 1 to
    /// <see cref="ClientRequestGuards.MaxPageSize"/>, an empty time window or a short channel id of no loaded
    /// channel.</exception>
    public async Task<ListAccountingEventsClientResponse> HandleAsync(ListAccountingEventsClientRequest request,
                                                                      CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.AfterLedgerSeq < 0)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"after must not be negative (was {request.AfterLedgerSeq}).");
        ClientRequestGuards.ThrowIfInvalidPage(0, request.Take);
        if (request.Since is { } since && request.Until is { } until && until <= since)
            throw new ClientException(ErrorCodes.InvalidOperation, "until must be after since.");

        var channelId = request.ChannelId ?? ResolveScid(request.ChannelScid);

        if (_sealer is not null)
        {
            try
            {
                await _sealer.SealNowAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The rows stay unsealed for the background round; the sealed ones can still be listed
                _logger.LogWarning(e, "Could not seal the accounting events before listing them");
            }
        }

        var repository = _unitOfWork.AccountingEventDbRepository;
        var events = await repository.ListAsync(new AccountingEventQuery(request.AfterLedgerSeq, request.Take,
                                                                         request.Kinds, channelId, request.Since,
                                                                         request.Until), ct);
        var tip = await repository.GetChainTipAsync(ct);
        var nextAfter = events.Count > 0 ? events[^1].LedgerSeq ?? request.AfterLedgerSeq : request.AfterLedgerSeq;
        return new ListAccountingEventsClientResponse(events, nextAfter, events.Count == request.Take, tip.LedgerSeq);
    }

    private ChannelId? ResolveScid(ShortChannelId? scid)
    {
        if (scid is not { } value)
            return null;

        var channel = _channelMemoryRepository?.FindChannels(c => c.ShortChannelId != default
                                                               && c.ShortChannelId == value)
                                               .FirstOrDefault();
        return channel?.ChannelId
            ?? throw new ClientException(ErrorCodes.InvalidOperation,
                                         $"No loaded channel has the short channel id {value}: name the channel by "
                                       + "its 64-hex channel id.");
    }
}

/// <summary>
/// The node's live balances by bucket (ClientCommand 42, <c>accountingsnapshot</c>, NL-602).
/// </summary>
public sealed class AccountingSnapshotClientHandler
    : IClientCommandHandler<AccountingSnapshotClientRequest, AccountingSnapshotClientResponse>
{
    private readonly INodeSnapshotSource? _snapshotSource;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.AccountingSnapshot;

    public AccountingSnapshotClientHandler(INodeSnapshotSource? snapshotSource)
    {
        _snapshotSource = snapshotSource;
    }

    /// <inheritdoc/>
    /// <exception cref="ClientException">The node has no snapshot source.</exception>
    public async Task<AccountingSnapshotClientResponse> HandleAsync(AccountingSnapshotClientRequest request,
                                                                    CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_snapshotSource is null)
            throw new ClientException(ErrorCodes.InvalidOperation, "Accounting snapshots are not available on this node.");

        return new AccountingSnapshotClientResponse(await _snapshotSource.TakeSnapshotAsync(ct));
    }
}