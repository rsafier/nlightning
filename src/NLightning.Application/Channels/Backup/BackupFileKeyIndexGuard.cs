using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Backup;

using Domain.Channels.Interfaces;
using Domain.Channels.KeyIndexes;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;
using Interfaces;

/// <summary>
/// The automatic half of NL-1392: at a start, before any peer connects, the channel key index is raised past every
/// channel of the node's own <c>channel.backup</c> that the database does not have (a node set up again on a fresh
/// database with a key file older than its lost database). The next open then never takes a backed-up channel's keys,
/// with or without <c>Node:Backup:HoldOpensUntilRestore</c>.
/// </summary>
/// <remarks>
/// A file that is missing, not this node's, of another chain or unreadable is skipped (logged). A raise that fails holds
/// the channel key index gate until a restore or <c>openhold --release</c> (fail closed). The monitor never replaces a
/// file with channels while the database has none, so the file still holds the lost channels.
/// </remarks>
public sealed class BackupFileKeyIndexGuard
{
    private readonly IChannelBackupService _backupService;
    private readonly IChannelKeyIndexReserver _reserver;
    private readonly IChannelKeyIndexGate? _gate;
    private readonly ILogger _logger;

    public BackupFileKeyIndexGuard(IChannelBackupService backupService, IChannelKeyIndexReserver reserver,
                                   IChannelKeyIndexGate? gate, ILogger logger)
    {
        _backupService = backupService;
        _reserver = reserver;
        _gate = gate;
        _logger = logger;
    }

    /// <summary>
    /// Raises the key index past the backed-up channels the database lacks. Returns the highest index raised to, or
    /// null when nothing was raised.
    /// </summary>
    /// <param name="filePath">The backup file (<c>Node:Backup:FilePath</c>).</param>
    /// <param name="chainHash">Our chain.</param>
    /// <param name="nodeId">Our node id.</param>
    /// <param name="channelRepository">The database's channels.</param>
    /// <param name="cancellationToken">Cancels the raise.</param>
    public async Task<uint?> RaiseAsync(string? filePath, ChainHash chainHash, CompactPubKey nodeId,
                                        IChannelDbRepository channelRepository, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        Models.ChannelBackupSnapshot snapshot;
        try
        {
            snapshot = _backupService.Decrypt(await File.ReadAllBytesAsync(filePath, cancellationToken));
            ChannelRestorePlanner.Validate(snapshot, chainHash, nodeId);
        }
        catch (Exception e) when (e is ChannelBackupException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("The channel backup file {File} was not read for the channel key index check: {Reason}",
                               filePath, e.Message);
            return null;
        }

        var stored = (await channelRepository.GetLocalKeyIndexesAsync() ?? []).Select(r => r.ChannelId).ToHashSet();
        var missing = snapshot.Channels.Where(c => !stored.Contains(c.ChannelId)).ToList();
        if (missing.Count == 0)
            return null;

        var highest = missing.Max(c => c.KeyIndex);
        try
        {
            await _reserver.ReserveThroughAsync(highest, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogError(e, "The channel backup file holds {Count} channel(s) the database lacks, and the channel key "
                              + "index could not be raised past them ({Index}): channel opens are held until "
                              + "restorechanbackup or 'openhold --release'", missing.Count, highest);
            _gate?.HoldUntilRestore();
            return null;
        }

        _logger.LogWarning("The channel backup file holds {Count} channel(s) the database lacks: the channel key index "
                         + "is raised past them ({Index}); restore them with restorechanbackup", missing.Count, highest);
        return highest;
    }
}