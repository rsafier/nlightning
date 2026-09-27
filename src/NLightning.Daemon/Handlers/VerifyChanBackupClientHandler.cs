namespace NLightning.Daemon.Handlers;

using Application.Channels.Backup.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Interfaces;

/// <summary>
/// Checks a static channel backup (ClientCommand 22, like LND's <c>verifychanbackup</c>): it must decrypt with the
/// node's key, be for this node and chain, and every channel's key index must derive the keys it recorded. A bad
/// backup is an answer (<c>IsValid</c> false with the reason), not an error.
/// </summary>
public sealed class VerifyChanBackupClientHandler
    : IClientCommandHandler<VerifyChanBackupClientRequest, VerifyChanBackupClientResponse>
{
    /// <summary>The largest backup accepted (8 MiB, under the 10 MB IPC frame; about 12,000 channels).</summary>
    public const int MaxBackupLength = 8 * 1024 * 1024;

    private readonly IChannelBackupService _backupService;

    /// <inheritdoc/>
    public ClientCommand Command => ClientCommand.VerifyChanBackup;

    public VerifyChanBackupClientHandler(IChannelBackupService backupService)
    {
        _backupService = backupService;
    }

    /// <inheritdoc/>
    public async Task<VerifyChanBackupClientResponse> HandleAsync(VerifyChanBackupClientRequest request,
                                                                  CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Backup is not { Length: > 0 })
            throw new ClientException(ErrorCodes.InvalidOperation, "The backup is empty.");
        if (request.Backup.Length > MaxBackupLength)
            throw new ClientException(ErrorCodes.InvalidOperation,
                                      $"The backup is larger than {MaxBackupLength} bytes.");

        var verification = await _backupService.VerifyAsync(request.Backup, ct);
        var channels = verification.Channels.Count > 0
                           ? verification.Channels
                                         .Select(c => new ChanBackupChannelInfo(
                                                     c.Entry.ChannelId, c.Entry.RemoteNodeId,
                                                     c.Entry.Addresses.Select(a => $"{a.Host}:{a.Port}").ToList(),
                                                     c.Entry.FundingTxId, c.Entry.FundingOutputIndex,
                                                     c.Entry.CapacitySat, c.Entry.ShortChannelId,
                                                     c.Entry.IsInitiator, c.Entry.OptionAnchorOutputs, c.KeysMatch,
                                                     c.LocalState))
                                         .ToList()
                           : [];
        return new VerifyChanBackupClientResponse(verification.IsValid, verification.Error,
                                                  verification.Snapshot?.CreatedAt, channels);
    }
}