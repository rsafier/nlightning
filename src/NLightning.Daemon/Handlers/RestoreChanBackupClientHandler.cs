namespace NLightning.Daemon.Handlers;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

/// <summary>
/// Restores a static channel backup (ClientCommand 23, like LND's <c>restorechanbackup</c>): each backed-up channel
/// not in the database becomes a recovery channel, and its peer is asked to force close so our output of its
/// commitment is swept. A backup that is not this node's (another key, node or chain), tampered with or malformed is
/// <c>invalid_operation</c>; channels already in the database are reported and left alone.
/// </summary>
public sealed class RestoreChanBackupClientHandler
    : IClientCommandHandler<RestoreChanBackupClientRequest, RestoreChanBackupClientResponse>
{
    private readonly IChannelRestoreService _restoreService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.RestoreChanBackup;

    public RestoreChanBackupClientHandler(IChannelRestoreService restoreService)
    {
        _restoreService = restoreService;
    }

    /// <inheritdoc/>
    public async Task<RestoreChanBackupClientResponse> HandleAsync(RestoreChanBackupClientRequest request,
                                                                   CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Backup is not { Length: > 0 })
            throw new ClientException(ErrorCodes.InvalidOperation, "The backup is empty.");
        if (request.Backup.Length > VerifyChanBackupClientHandler.MaxBackupLength)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The backup is larger than {VerifyChanBackupClientHandler.MaxBackupLength} bytes.");

        try
        {
            var result = await _restoreService.RestoreAsync(request.Backup, ct);
            return new RestoreChanBackupClientResponse(
                result.Snapshot.CreatedAt,
                result.Channels.Select(c => new ChanRestoreChannelInfo(c.Entry.ChannelId, c.Entry.RemoteNodeId,
                                                                       c.Entry.CapacitySat,
                                                                       c.Entry.OptionAnchorOutputs,
                                                                       c.Action.ToString(), c.Detail,
                                                                       c.Entry.OptionSimpleTaproot))
                      .ToList(),
                result.Peers.Select(p => new ChanRestorePeerInfo(p.NodeId, p.Address, p.Connected, p.Error)).ToList());
        }
        catch (ChannelBackupException e)
        {
            throw new ClientException(ErrorCodes.InvalidOperation, e.Message);
        }
    }
}