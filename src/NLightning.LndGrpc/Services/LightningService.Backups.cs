using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Application.Channels.Backup;
using Application.Channels.Backup.Interfaces;
using Application.Channels.Backup.Models;
using Domain.Channels.Events;
using Lnrpc;
using LndChannelPoint = Lnrpc.ChannelPoint;

/// <summary>
/// LND's static channel backup RPCs over the node's own backups (NL-1248). The bytes are NLightning backups
/// (<see cref="ChannelBackupCipher"/>: <c>NLSCB</c>, encrypted to the node key), not LND's format: LND cannot read
/// them and this node cannot read LND's. They travel in LND's message shapes, so tools that store and hand back the
/// blob (RTL's backup page, bos <c>report</c>) work; only <c>nltg restorechanbackup</c> or this server's
/// <c>RestoreChannelBackups</c> restores them.
/// </summary>
public sealed partial class LightningService
{
    /// <summary>The longest backup accepted (the IPC's limit, about 12,000 channels).</summary>
    private const int MaxBackupLength = 8 * 1024 * 1024;

    private const string ForeignBackupError =
        "not an NLightning static channel backup of this node (LND-format backups are not supported; restore those "
      + "with LND)";

    /// <summary><c>ExportChannelBackup</c>: the backup of the channel at <c>chan_point</c>.</summary>
    public override async Task<ChannelBackup> ExportChannelBackup(ExportChannelBackupRequest request,
                                                                  ServerCallContext context)
    {
        var service = BackupService;
        var channel = FindByChannelPoint(request.ChanPoint)
                   ?? throw NotFound("channel not found");
        try
        {
            var export = await service.ExportAsync(channel.ChannelId, context.CancellationToken);
            return new ChannelBackup
            {
                ChanPoint = ToChannelPoint(channel.FundingOutput?.TransactionId, channel.FundingOutput?.Index),
                ChanBackup = ByteString.CopyFrom(export.Backup)
            };
        }
        catch (KeyNotFoundException e)
        {
            throw NotFound(e.Message);
        }
    }

    /// <summary>
    /// <c>ExportAllChannelBackups</c>: the backup of every channel with a known funding outpoint that is not closed,
    /// as <c>multi_chan_backup</c> (one blob, its <c>chan_points</c>) and as one single backup per channel.
    /// </summary>
    public override async Task<ChanBackupSnapshot> ExportAllChannelBackups(ChanBackupExportRequest request,
                                                                           ServerCallContext context) =>
        await ExportSnapshotAsync(BackupService, context.CancellationToken);

    /// <summary>
    /// <c>VerifyChanBackup</c>: the multi backup or each single backup must be this node's NLightning backup (it
    /// decrypts with the node key, is for this node and chain, and our key index derives each channel's recorded keys);
    /// the answer lists the channel points it holds. Anything else is <c>INVALID_ARGUMENT</c> with the reason; an LND
    /// backup is named as such.
    /// </summary>
    public override async Task<VerifyChanBackupResponse> VerifyChanBackup(ChanBackupSnapshot request,
                                                                           ServerCallContext context)
    {
        var service = BackupService;
        var blobs = BackupBlobs(request.MultiChanBackup?.MultiChanBackup_,
                                request.SingleChanBackups?.ChanBackups.Select(b => b.ChanBackup));
        var response = new VerifyChanBackupResponse();
        foreach (var blob in blobs)
        {
            var verification = await service.VerifyAsync(blob, context.CancellationToken);
            if (!verification.IsValid)
                throw InvalidArgument($"invalid channel backup: {verification.Error}");

            response.ChanPoints.AddRange(verification.Channels.Select(c => $"{c.Entry.FundingTxId}:"
                                                                         + c.Entry.FundingOutputIndex));
        }

        return response;
    }

    /// <summary>
    /// <c>RestoreChannelBackups</c>: refused unless <c>LndGrpc:AllowChannelBackupRestore</c> (a restore makes recovery
    /// channels whose peers are asked to force close; the operator's path is <c>nltg restorechanbackup</c>). When
    /// allowed, it does what that command does with the multi backup or each single backup (channels already in the
    /// database are left alone) and answers how many recovery channels it made. A foreign or LND backup is
    /// <c>INVALID_ARGUMENT</c>.
    /// </summary>
    public override async Task<RestoreBackupResponse> RestoreChannelBackups(RestoreChanBackupRequest request,
                                                                            ServerCallContext context)
    {
        if (!_options.AllowChannelBackupRestore)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                                              "restoring channel backups over gRPC is disabled "
                                            + "(LndGrpc:AllowChannelBackupRestore); use nltg restorechanbackup"));

        await using var scope = CreateScope();
        var restore = scope.ServiceProvider.GetService<IChannelRestoreService>()
                   ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                        "channel backups are not available on this node"));
        var blobs = BackupBlobs(request.BackupCase == RestoreChanBackupRequest.BackupOneofCase.MultiChanBackup
                                    ? request.MultiChanBackup
                                    : null,
                                request.ChanBackups?.ChanBackups.Select(b => b.ChanBackup));
        uint restored = 0;
        foreach (var blob in blobs)
        {
            try
            {
                var result = await restore.RestoreAsync(blob, context.CancellationToken);
                restored += (uint)result.RestoredCount;
            }
            catch (ChannelBackupException e)
            {
                throw InvalidArgument($"invalid channel backup: {e.Message}");
            }
        }

        _logger.LogWarning("RestoreChannelBackups over gRPC made {Count} recovery channel(s)", restored);
        return new RestoreBackupResponse { NumRestored = restored };
    }

    /// <summary>
    /// <c>SubscribeChannelBackups</c>: a new snapshot (as <c>ExportAllChannelBackups</c>) every time the set of
    /// backed-up channels changes (a channel gets its funding outpoint or closes), as LND sends one on every open and
    /// close; nothing at the start.
    /// </summary>
    public override async Task SubscribeChannelBackups(ChannelBackupSubscription request,
                                                       IServerStreamWriter<ChanBackupSnapshot> responseStream,
                                                       ServerCallContext context)
    {
        var service = BackupService;
        var signal = new SemaphoreSlim(0, 1);
        void Changed(object? sender, ChannelUpdatedEventArgs args)
        {
            if (signal.CurrentCount == 0)
            {
                try
                {
                    signal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Already signalled
                }
            }
        }

        _channels.OnChannelAdded += Changed;
        _channels.OnChannelUpdated += Changed;
        _channels.OnChannelRemoved += Changed;
        try
        {
            // The set before the headers, so a change a client makes once subscribed is never in it
            var last = BackedUpPoints(await service.CreateSnapshotAsync(null, context.CancellationToken));
            await context.WriteResponseHeadersAsync(new Metadata());
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken);
                var snapshot = await ExportSnapshotAsync(service, context.CancellationToken);
                var points = snapshot.MultiChanBackup.ChanPoints.Select(PointKey).ToHashSet(StringComparer.Ordinal);
                if (points.SetEquals(last))
                    continue;

                last = points;
                await responseStream.WriteAsync(snapshot, context.CancellationToken);
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client left
        }
        finally
        {
            _channels.OnChannelAdded -= Changed;
            _channels.OnChannelUpdated -= Changed;
            _channels.OnChannelRemoved -= Changed;
            signal.Dispose();
        }
    }

    /// <summary>The node's backup service, or <c>UNAVAILABLE</c>.</summary>
    private IChannelBackupService BackupService
    {
        get
        {
            using var scope = _scopeFactory.CreateScope();
            return scope.ServiceProvider.GetService<IChannelBackupService>()
                ?? throw new RpcException(new Status(StatusCode.Unavailable,
                                                     "channel backups are not available on this node"));
        }
    }

    private async Task<ChanBackupSnapshot> ExportSnapshotAsync(IChannelBackupService service,
                                                               CancellationToken cancellationToken)
    {
        var all = await service.ExportAsync(null, cancellationToken);
        var multi = new MultiChanBackup { MultiChanBackup_ = ByteString.CopyFrom(all.Backup) };
        var singles = new ChannelBackups();
        foreach (var entry in all.Snapshot.Channels)
        {
            var point = ToChannelPoint(entry.FundingTxId, entry.FundingOutputIndex);
            multi.ChanPoints.Add(point);
            try
            {
                var single = await service.ExportAsync(entry.ChannelId, cancellationToken);
                singles.ChanBackups.Add(new ChannelBackup { ChanPoint = point, ChanBackup = ByteString.CopyFrom(single.Backup) });
            }
            catch (KeyNotFoundException)
            {
                // Closed between the two reads: the multi backup still holds it
            }
        }

        return new ChanBackupSnapshot { MultiChanBackup = multi, SingleChanBackups = singles };
    }

    private static HashSet<string> BackedUpPoints(ChannelBackupSnapshot snapshot) =>
        snapshot.Channels.Select(c => $"{c.FundingTxId}:{c.FundingOutputIndex}").ToHashSet(StringComparer.Ordinal);

    private static string PointKey(LndChannelPoint point) =>
        $"{new Domain.Bitcoin.ValueObjects.TxId(point.FundingTxidBytes.ToByteArray())}:{point.OutputIndex}";

    /// <summary>The blobs of a request (the multi backup, else the single ones), each checked to be ours.</summary>
    private static List<ReadOnlyMemory<byte>> BackupBlobs(ByteString? multi, IEnumerable<ByteString>? singles)
    {
        var blobs = new List<ReadOnlyMemory<byte>>();
        if (multi is { Length: > 0 })
            blobs.Add(multi.Memory);
        else if (singles is not null)
            blobs.AddRange(singles.Select(s => s.Memory));

        if (blobs.Count == 0)
            throw InvalidArgument("no channel backup given");
        foreach (var blob in blobs)
        {
            if (blob.Length > MaxBackupLength)
                throw InvalidArgument($"the backup is larger than {MaxBackupLength} bytes");
            if (!ChannelBackupCipher.LooksLikeBackup(blob.Span))
                throw InvalidArgument(ForeignBackupError);
        }

        return blobs;
    }
}