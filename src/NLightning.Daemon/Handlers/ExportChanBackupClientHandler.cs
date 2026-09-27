using Microsoft.Extensions.Options;

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
/// Exports the static channel backup (ClientCommand 21, like LND's <c>exportchanbackup</c>): every channel whose
/// funding outpoint is known and that is not closed, or one channel, encrypted to the node key.
/// </summary>
public sealed class ExportChanBackupClientHandler
    : IClientCommandHandler<ExportChanBackupClientRequest, ExportChanBackupClientResponse>
{
    private readonly IChannelBackupService _backupService;
    private readonly ChannelBackupOptions _options;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.ExportChanBackup;

    public ExportChanBackupClientHandler(IChannelBackupService backupService,
                                         IOptions<ChannelBackupOptions>? options = null)
    {
        _backupService = backupService;
        _options = options?.Value ?? new ChannelBackupOptions();
    }

    /// <inheritdoc/>
    public async Task<ExportChanBackupClientResponse> HandleAsync(ExportChanBackupClientRequest request,
                                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var export = await _backupService.ExportAsync(request.ChannelId, ct);
            var filePath = _options.Enabled && !string.IsNullOrWhiteSpace(_options.FilePath)
                               ? _options.FilePath
                               : null;
            return new ExportChanBackupClientResponse(export.Backup, export.Snapshot.Channels.Select(c => c.ChannelId).ToList(),
                                                      filePath);
        }
        catch (KeyNotFoundException e)
        {
            throw new ClientException(ErrorCodes.InvalidChannel, e.Message, e);
        }
    }
}