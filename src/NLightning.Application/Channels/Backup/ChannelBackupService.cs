using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Channels.Backup;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Gossip.Addresses;
using Domain.Gossip.Graph;
using Domain.Node.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.Interfaces;
using Gossip.Graph.Interfaces;
using Interfaces;
using Models;

/// <summary>
/// Builds, encrypts, verifies and writes static channel backups (see <see cref="IChannelBackupService"/>).
/// </summary>
/// <remarks>
/// The file is only replaced when its channels changed, and never by an empty backup while the database holds no
/// channel row at all (a wiped or new database: the existing file is what <c>restorechanbackup</c> needs). A file
/// that does not decrypt with our key is moved aside, not overwritten. A file holding a channel the database has no
/// row for at all (lost, not closed through this database: e.g. a new channel opened on a wiped database before the
/// restore) is moved aside as <c>&lt;file&gt;.&lt;UTC time&gt;.superseded</c> before the new file is written, so no
/// lost channel's backup is ever dropped.
/// <para>Each channel carries the peer's stored address and the connectable addresses of its
/// <c>node_announcement</c> in the gossip graph (at most <see cref="MaxAddressesPerChannel"/>), so a restore can reach
/// a peer whose address changed (NL-431).</para>
/// <para>Splices and dual-funded opens (lane SP2-E, NL-478): an entry's funding is the channel's current one (the
/// outpoint, capacity and keys of <c>ChannelModel.FundingOutput</c>), with our funding key index and the pending splices
/// read from the stored fundings (<c>IChannelFundingDbRepository.GetFundingSetAsync</c>); the check re-derives the
/// rotated key through <see cref="IChannelFundingKeySource"/>. <see cref="ChannelBackupMonitor"/> rewrites the file when
/// a splice locks or a dual-funded open's funding moves.</para>
/// </remarks>
public sealed class ChannelBackupService : IChannelBackupService
{
    /// <summary>The most addresses a backed-up channel holds for its peer.</summary>
    public const int MaxAddressesPerChannel = 8;

    /// <summary>
    /// The highest funding key index searched when the recorded one does not derive a channel's funding key (one per
    /// splice of the channel).
    /// </summary>
    public const uint MaxFundingKeyIndexSearch = 256;

    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly ILightningSigner _signer;
    private readonly NodeOptions _nodeOptions;
    private readonly ChannelBackupOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelBackupService> _logger;
    private readonly IGraphStore? _graphStore;
    private readonly IChannelFundingKeySource _fundingKeySource;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private byte[]? _lastWrittenContent;
    private bool _keptExistingLogged;

    public ChannelBackupService(IServiceScopeFactory serviceScopeFactory, ISecureKeyManager secureKeyManager,
                                ILightningSigner signer, IOptions<NodeOptions> nodeOptions,
                                IOptions<ChannelBackupOptions>? options = null, TimeProvider? timeProvider = null,
                                ILogger<ChannelBackupService>? logger = null, IGraphStore? graphStore = null,
                                IChannelFundingKeySource? fundingKeySource = null)
    {
        _graphStore = graphStore;
        _fundingKeySource = fundingKeySource ?? new SignerChannelFundingKeySource(signer);
        _serviceScopeFactory = serviceScopeFactory;
        _secureKeyManager = secureKeyManager;
        _signer = signer;
        _nodeOptions = nodeOptions.Value;
        _options = options?.Value ?? new ChannelBackupOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ChannelBackupService>.Instance;
    }

    /// <summary>Whether <paramref name="channel"/> belongs in a backup: funding outpoint and the peer's keys known,
    /// not Closed or Stale.</summary>
    public static bool IsBackedUp(ChannelModel channel) =>
        channel.State is not (ChannelState.None or ChannelState.Closed or ChannelState.Stale)
     && channel.FundingOutput is { TransactionId: not null, Index: not null }
     && channel.RemoteKeySet is not null;

    /// <inheritdoc />
    public async Task<ChannelBackupSnapshot> CreateSnapshotAsync(ChannelId? channelId,
                                                                 CancellationToken cancellationToken)
    {
        var (snapshot, _, unverified) = await LoadAsync(channelId, cancellationToken);
        if (unverified.Count > 0)
            _logger.LogError("The funding key of channel(s) {ChannelIds} does not derive from this node's key file at "
                           + "any funding key index: their backup can't be restored", string.Join(", ", unverified));
        if (channelId is { } only && snapshot.Channels.Count == 0)
            throw new KeyNotFoundException($"Channel {only} is not backed up: unknown, closed, or before its "
                                         + "funding outpoint.");

        return snapshot;
    }

    /// <inheritdoc />
    public async Task<ChannelBackupExport> ExportAsync(ChannelId? channelId, CancellationToken cancellationToken)
    {
        var snapshot = await CreateSnapshotAsync(channelId, cancellationToken);
        return new ChannelBackupExport(snapshot, Encrypt(snapshot));
    }

    /// <inheritdoc />
    public ChannelBackupSnapshot Decrypt(ReadOnlySpan<byte> backup)
    {
        var key = DeriveKey();
        try
        {
            return ChannelBackupCodec.Decode(ChannelBackupCipher.Decrypt(key, backup));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc />
    public async Task<ChannelBackupVerification> VerifyAsync(ReadOnlyMemory<byte> backup,
                                                             CancellationToken cancellationToken)
    {
        ChannelBackupSnapshot snapshot;
        try
        {
            snapshot = Decrypt(backup.Span);
        }
        catch (ChannelBackupException e)
        {
            return new ChannelBackupVerification(false, e.Message, null, []);
        }

        if (snapshot.ChainHash != _nodeOptions.BitcoinNetwork.ChainHash)
            return new ChannelBackupVerification(false, "The backup is for another chain.", snapshot, []);

        if (snapshot.NodeId != _secureKeyManager.GetNodePubKey())
            return new ChannelBackupVerification(false, "The backup was made by another node.", snapshot, []);

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var checks = new List<ChannelBackupEntryCheck>(snapshot.Channels.Count);
        foreach (var entry in snapshot.Channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The funding key of the current funding: a splice rotates it (index > 0), the basepoints keep index 0
            var basepoints = _signer.GetChannelBasepoints(entry.KeyIndex);
            var keysMatch = basepoints.PaymentBasepoint == entry.LocalPaymentBasepoint
                         && _fundingKeySource.GetFundingPubKey(entry.KeyIndex, entry.LocalFundingKeyIndex)
                                is { } fundingKey
                         && fundingKey == entry.LocalFundingPubKey;
            checks.Add(new ChannelBackupEntryCheck(entry, keysMatch,
                                                   await GetLocalStateAsync(unitOfWork, entry.ChannelId)));
        }

        var mismatches = checks.Where(c => !c.KeysMatch).ToList();
        var error = mismatches.Count == 0
                        ? null
                        : $"The keys of {mismatches.Count} channel(s) do not derive from this node's key file "
                        + $"({string.Join(", ", mismatches.Select(c => c.Entry.ChannelId))}).";
        return new ChannelBackupVerification(error is null, error, snapshot, checks);
    }

    /// <inheritdoc />
    public async Task<ChannelBackupWriteResult> WriteFileAsync(CancellationToken cancellationToken)
    {
        var path = _options.FilePath;
        if (!_options.Enabled || string.IsNullOrWhiteSpace(path))
            return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Disabled, 0, null);

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var (snapshot, channelIds, unverified) = await LoadAsync(null, cancellationToken);
            var channelRows = channelIds.Count;
            if (unverified.Count > 0)
                snapshot = await KeepPreviousEntriesAsync(path, snapshot, unverified, cancellationToken);
            IReadOnlyList<ChannelId>? unverifiedResult = unverified.Count == 0 ? null : unverified;
            var content = CanonicalContent(snapshot);
            if (_lastWrittenContent is not null && content.AsSpan().SequenceEqual(_lastWrittenContent))
                return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Unchanged, snapshot.Channels.Count,
                                                    path, UnverifiedChannels: unverifiedResult);

            string? movedAside = null;
            if (File.Exists(path))
            {
                ChannelBackupSnapshot? existing = null;
                try
                {
                    existing = Decrypt(await File.ReadAllBytesAsync(path, cancellationToken));
                }
                catch (ChannelBackupException e)
                {
                    movedAside = ChannelBackupFile.MoveAside(path, _timeProvider.GetUtcNow());
                    _logger.LogWarning("The channel backup {Path} does not decrypt with this node's key ({Reason}); "
                                     + "moved to {MovedTo}", path, e.Message, movedAside);
                }

                if (existing is not null)
                {
                    if (channelRows == 0 && existing.Channels.Count > 0)
                    {
                        if (!_keptExistingLogged)
                        {
                            _keptExistingLogged = true;
                            _logger.LogWarning("The database holds no channel but the channel backup {Path} holds {Count}"
                                             + ": the file is kept (restore it with restorechanbackup)", path,
                                               existing.Channels.Count);
                        }

                        return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.KeptExisting,
                                                            existing.Channels.Count, path);
                    }

                    if (CanonicalContent(existing).AsSpan().SequenceEqual(content))
                    {
                        _lastWrittenContent = content;
                        return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Unchanged,
                                                            snapshot.Channels.Count, path,
                                                            UnverifiedChannels: unverifiedResult);
                    }

                    // Channels the database never saw closed: this file is their only backup, keep it aside
                    var lost = existing.Channels.Where(c => !channelIds.Contains(c.ChannelId))
                                       .Select(c => c.ChannelId).Distinct().ToList();
                    if (lost.Count > 0)
                    {
                        movedAside = ChannelBackupFile.MoveAside(path, _timeProvider.GetUtcNow(), "superseded");
                        _logger.LogError("The channel backup {Path} holds {Count} channel(s) the database has no row for "
                                       + "({ChannelIds}); it is kept as {MovedTo} (restore it with restorechanbackup) "
                                       + "and a new file is written", path, lost.Count, string.Join(", ", lost),
                                         movedAside);
                    }
                }
            }

            await ChannelBackupFile.WriteAtomicallyAsync(path, Encrypt(snapshot), cancellationToken);
            _lastWrittenContent = content;
            _keptExistingLogged = false;
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation("Channel backup {Path} written with {Count} channel(s)", path,
                                       snapshot.Channels.Count);

            return new ChannelBackupWriteResult(ChannelBackupWriteOutcome.Written, snapshot.Channels.Count, path,
                                                movedAside, unverifiedResult);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// The snapshot of the backed-up channels, every channel id of the database, and the channels whose funding key
    /// no key index derives (their entries are in the snapshot as built; <see cref="WriteFileAsync"/> keeps the file's
    /// previous entry for them instead).
    /// </summary>
    private async Task<(ChannelBackupSnapshot Snapshot, HashSet<ChannelId> ChannelIds, List<ChannelId> Unverified)>
        LoadAsync(ChannelId? channelId, CancellationToken cancellationToken)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var channels = (await unitOfWork.ChannelDbRepository.GetAllAsync()).ToList();
        var unverified = new List<ChannelId>();
        var entries = new List<ChannelBackupEntry>();
        var peers = new Dictionary<Domain.Crypto.ValueObjects.CompactPubKey, PeerModel?>();
        foreach (var channel in channels.Where(IsBackedUp).OrderBy(c => c.ChannelId.ToString()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (channelId is { } only && channel.ChannelId != only)
                continue;

            if (!peers.TryGetValue(channel.RemoteNodeId, out var peer))
            {
                peer = await unitOfWork.PeerDbRepository.GetByNodeIdAsync(channel.RemoteNodeId);
                peers[channel.RemoteNodeId] = peer;
            }

            var entry = CreateEntry(channel, peer, GetGraphNode(channel.RemoteNodeId),
                                    await GetFundingSetAsync(unitOfWork, channel.ChannelId, _logger),
                                    await GetDualFundCandidatesAsync(unitOfWork, channel, _logger));
            if (CheckFundingKey(entry) is { } checkedEntry)
            {
                entries.Add(checkedEntry);
            }
            else
            {
                unverified.Add(channel.ChannelId);
                entries.Add(entry);
            }
        }

        var createdAt = DateTimeOffset.FromUnixTimeSeconds(_timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var snapshot = new ChannelBackupSnapshot(_nodeOptions.BitcoinNetwork.ChainHash,
                                                 _secureKeyManager.GetNodePubKey(), createdAt, entries);
        return (snapshot, channels.Select(c => c.ChannelId).ToHashSet(), unverified);
    }

    /// <summary>
    /// <paramref name="entry"/> with a funding key index that derives its funding key (lane SP2-E review): the one it
    /// has, else the first of 0 .. <see cref="MaxFundingKeyIndexSearch"/> that does (the index read from a stored row
    /// can be missing: a failed read, or a funding row that does not match); pending fundings whose key does not
    /// derive are left out. Null when no index derives the key: the restore would refuse the entry.
    /// </summary>
    internal ChannelBackupEntry? CheckFundingKey(ChannelBackupEntry entry)
    {
        var fixedEntry = entry;
        if (!Derives(entry.KeyIndex, entry.LocalFundingKeyIndex, entry.LocalFundingPubKey))
        {
            uint? found = null;
            for (var index = 0u; index <= MaxFundingKeyIndexSearch; index++)
                if (index != entry.LocalFundingKeyIndex && Derives(entry.KeyIndex, index, entry.LocalFundingPubKey))
                {
                    found = index;
                    break;
                }

            if (found is not { } fundingKeyIndex)
                return null;

            _logger.LogWarning("The funding key of channel {ChannelId} is at funding key index {Found}, not the "
                             + "recorded {Recorded}; the backup records {Found}", entry.ChannelId, fundingKeyIndex,
                               entry.LocalFundingKeyIndex, fundingKeyIndex);
            fixedEntry = entry with { LocalFundingKeyIndex = fundingKeyIndex };
        }

        var pending = entry.PendingFundings
                           .Where(f => Derives(entry.KeyIndex, f.LocalFundingKeyIndex, f.LocalFundingPubKey))
                           .ToList();
        return pending.Count == entry.PendingFundings.Count ? fixedEntry : fixedEntry with { PendingFundings = pending };
    }

    private bool Derives(uint channelKeyIndex, uint fundingKeyIndex, Domain.Crypto.ValueObjects.CompactPubKey key)
    {
        try
        {
            return _fundingKeySource.GetFundingPubKey(channelKeyIndex, fundingKeyIndex) is { } derived
                && derived == key;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not derive funding key {FundingKeyIndex} of channel key {KeyIndex}",
                             fundingKeyIndex, channelKeyIndex);
            return false;
        }
    }

    /// <summary>
    /// <paramref name="snapshot"/> with the entry of each channel of <paramref name="unverified"/> (its funding key
    /// derives at no index: the restore would refuse it) replaced by the one the file at <paramref name="path"/> holds
    /// when that one's key derives (an entry at an earlier funding still restores: the restore follows splices), else
    /// left out. Logged at error level: the channel's backup is not up to date.
    /// </summary>
    private async Task<ChannelBackupSnapshot> KeepPreviousEntriesAsync(string path, ChannelBackupSnapshot snapshot,
                                                                       IReadOnlyCollection<ChannelId> unverified,
                                                                       CancellationToken cancellationToken)
    {
        ChannelBackupSnapshot? existing = null;
        try
        {
            if (File.Exists(path))
                existing = Decrypt(await File.ReadAllBytesAsync(path, cancellationToken));
        }
        catch (Exception e) when (e is ChannelBackupException or IOException)
        {
            _logger.LogDebug(e, "The channel backup {Path} can't be read for the previous entries", path);
        }

        var entries = new List<ChannelBackupEntry>(snapshot.Channels.Count);
        foreach (var entry in snapshot.Channels)
        {
            if (!unverified.Contains(entry.ChannelId))
            {
                entries.Add(entry);
                continue;
            }

            var previous = existing?.Channels.FirstOrDefault(c => c.ChannelId == entry.ChannelId);
            if (previous is not null && CheckFundingKey(previous) is { } kept)
            {
                _logger.LogError("The funding key {FundingKey} of channel {ChannelId} derives at no funding key index "
                               + "of this node's key file; its previous backup (funding {FundingTxId}:{Index}) is kept",
                                 entry.LocalFundingPubKey, entry.ChannelId, kept.FundingTxId,
                                 kept.FundingOutputIndex);
                entries.Add(kept);
            }
            else
            {
                _logger.LogError("The funding key {FundingKey} of channel {ChannelId} derives at no funding key index "
                               + "of this node's key file and no earlier backup of it is kept: the channel is left out "
                               + "of the backup", entry.LocalFundingPubKey, entry.ChannelId);
            }
        }

        return snapshot with { Channels = entries };
    }

    /// <summary>
    /// The other signed candidates of an unconfirmed dual-funded open (lane SP2-E review): every stored negotiation of
    /// <paramref name="channel"/> (a v2 channel without a short channel id yet) for which we sent our
    /// <c>tx_signatures</c>, so the peer can broadcast it and any of them may be the one that confirms. They share the
    /// channel's funding keys (an RBF keeps them). Empty for any other channel, or when the negotiations can't be read.
    /// </summary>
    internal static async Task<IReadOnlyList<ChannelBackupFunding>> GetDualFundCandidatesAsync(
        IUnitOfWork unitOfWork, ChannelModel channel, ILogger logger)
    {
        if (channel.Version != ChannelVersion.V2 || HasShortChannelId(channel.ShortChannelId)
                                                 || channel.FundingOutput is not
                                                 { TransactionId: { } fundingTxId } funding)
            return [];

        IReadOnlyList<Domain.Protocol.InteractiveTx.InteractiveTxSessionModel> sessions;
        try
        {
            if (unitOfWork.InteractiveTxSessionDbRepository is not { } repository)
                return [];

            sessions = await repository.GetByChannelIdAsync(channel.ChannelId);
        }
        catch (NotSupportedException)
        {
            return [];
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the negotiations of channel {ChannelId} for its backup",
                              channel.ChannelId);
            return [];
        }

        var candidates = new List<ChannelBackupFunding>();
        foreach (var session in sessions)
        {
            if (session.Purpose is not (InteractiveTxPurpose.DualFund or InteractiveTxPurpose.DualFundRbf)
             || !session.TxSignaturesSent
             || session.ConstructedTx is not { SharedOutputIndex: { } index } constructed
             || index >= constructed.Outputs.Count || index > ushort.MaxValue
             || constructed.TxId == fundingTxId
             || candidates.Any(c => c.FundingTxId == constructed.TxId))
                continue;

            candidates.Add(new ChannelBackupFunding(constructed.TxId, (ushort)index,
                                                    (ulong)constructed.Outputs[(int)index].Amount.Satoshi, 0,
                                                    funding.LocalFundingPubKey, funding.RemoteFundingPubKey));
        }

        return candidates;
    }

    /// <summary>
    /// The addresses of <paramref name="node"/>'s announcement a node can connect to: IPv4, IPv6, Tor v3 (valid
    /// checksum; dialed through Tor) and DNS with a port, in the announcement's (ascending type) order. A backup keeps
    /// the onion service of a Tor-only peer even when this node runs without Tor: the restoring one may not.
    /// </summary>
    public static IEnumerable<AddressDescriptor> ConnectableAddresses(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Addresses.Where(a => a.Port > 0
                                      && (a.Type is AddressDescriptorType.IPv4 or AddressDescriptorType.IPv6
                                                    or AddressDescriptorType.Dns
                                       || (a.Type == AddressDescriptorType.TorV3 && OnionV3Address.IsValid(a.Address))));
    }

    private GraphNode? GetGraphNode(Domain.Crypto.ValueObjects.CompactPubKey nodeId)
    {
        try
        {
            return _graphStore is not null && _graphStore.TryGetNode(nodeId, out var node) ? node : null;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Could not read the graph node {Peer}", nodeId);
            return null;
        }
    }

    /// <summary>
    /// The stored fundings of a channel (splicing plan §3.3), or null when the unit of work stores none (a test double,
    /// or a build without the table) or they can't be read: the entry then describes the channel's funding output with
    /// key index 0 and no pending splice.
    /// </summary>
    internal static async Task<FundingSet?> GetFundingSetAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                               ILogger logger)
    {
        try
        {
            var repository = unitOfWork.ChannelFundingDbRepository;
            return repository is null ? null : await repository.GetFundingSetAsync(channelId);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the fundings of channel {ChannelId} for its backup", channelId);
            return null;
        }
    }

    /// <summary>
    /// The backup entry of <paramref name="channel"/>. Its funding fields are the channel's <b>current</b> funding
    /// (NL-478): the outpoint, capacity and both funding keys of <see cref="ChannelModel.FundingOutput"/>, which a
    /// locked splice replaces, with our key's index from <paramref name="fundings"/> (else the engine snapshot's current
    /// funding, else 0), and the pending splices of <paramref name="fundings"/> (else the snapshot's).
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="peer">Its peer row (its address).</param>
    /// <param name="graphNode">The peer's node in the gossip graph (its announced addresses).</param>
    /// <param name="fundings">The channel's stored fundings.</param>
    /// <param name="dualFundCandidates">The other signed candidates of an unconfirmed dual-funded open
    /// (<see cref="GetDualFundCandidatesAsync"/>), backed up as pending fundings.</param>
    internal static ChannelBackupEntry CreateEntry(ChannelModel channel, PeerModel? peer, GraphNode? graphNode = null,
                                                   FundingSet? fundings = null,
                                                   IReadOnlyList<ChannelBackupFunding>? dualFundCandidates = null)
    {
        var funding = channel.FundingOutput!;
        var remote = channel.RemoteKeySet!;
        var fundingTxId = funding.TransactionId!.Value;
        var fundingIndex = funding.Index!.Value;

        // Our key index of the current funding: its stored row, else the engine's view of it when it is a splice's (a
        // splice locked in this process; rebuilt after a restart the engine's funding always says index 0), else 0
        // (never spliced)
        var current = fundings?.Current is { } stored && stored.FundingTxId == fundingTxId
                                                      && stored.OutputIndex == fundingIndex
                          ? stored
                          : channel.Commitments?.Params.Funding is { Kind: not ChannelFundingKind.Initial } engine
                         && engine.FundingTxId == fundingTxId && engine.OutputIndex == fundingIndex
                              ? engine
                              : null;
        var pending = (fundings?.Pending ?? (IReadOnlyList<ChannelFunding>?)channel.Commitments?.PendingFundings ?? [])
                     .Where(f => f.Status == ChannelFundingStatus.Pending && f.FundingTxId != fundingTxId)
                     .Select(f => new ChannelBackupFunding(f.FundingTxId, f.OutputIndex, f.CapacitySatoshis,
                                                           f.LocalFundingKeyIndex, f.LocalFundingPubKey,
                                                           f.RemoteFundingPubKey))
                     .ToList();
        foreach (var candidate in dualFundCandidates ?? [])
            if (candidate.FundingTxId != fundingTxId && pending.All(p => p.FundingTxId != candidate.FundingTxId))
                pending.Add(candidate);
        var addresses = new List<ChannelBackupAddress>();
        // An inbound-only peer's row holds no address of it (it connected from a loopback address, NL-497)
        if (peer is { IsInboundOnly: false } && !string.IsNullOrWhiteSpace(peer.Host)
                                             && peer.Port is > 0 and <= ushort.MaxValue)
            addresses.Add(new ChannelBackupAddress(peer.Type, peer.Host, (ushort)peer.Port));

        // The peer's announced addresses: the peer row may hold only where it connected from (NL-431)
        if (graphNode is not null)
            foreach (var descriptor in ConnectableAddresses(graphNode))
            {
                if (addresses.Count >= MaxAddressesPerChannel)
                    break;

                var host = descriptor.Host;
                if (addresses.Any(a => a.Port == descriptor.Port
                                    && string.Equals(a.Host, host, StringComparison.OrdinalIgnoreCase)))
                    continue;

                addresses.Add(new ChannelBackupAddress(ToPeerType(descriptor.Type), host, descriptor.Port));
            }

        return new ChannelBackupEntry
        {
            ChannelId = channel.ChannelId,
            RemoteNodeId = channel.RemoteNodeId,
            Addresses = addresses,
            FundingTxId = fundingTxId,
            FundingOutputIndex = fundingIndex,
            CapacitySat = (ulong)funding.Amount.Satoshi,
            FundingHeight = channel.FundingCreatedAtBlockHeight,
            ShortChannelId = HasShortChannelId(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null,
            IsInitiator = channel.IsInitiator,
            OptionAnchorOutputs = channel.ChannelParams.OptionAnchorOutputs,
            OptionSimpleTaproot = channel.ChannelParams.OptionSimpleTaproot,
            AnnounceChannel = channel.ChannelParams.AnnounceChannel,
            HasInferredParams = channel.ChannelParams.HasInferredParams,
            Version = channel.Version,
            UseScidAlias = channel.ChannelParams.UseScidAlias,
            MinimumDepth = channel.ChannelParams.MinimumDepth,
            ChannelType = channel.ChannelParams.ToChannelType().GetWireBytes() ?? [],
            KeyIndex = channel.LocalKeySet.KeyIndex,
            LocalFundingPubKey = funding.LocalFundingPubKey,
            LocalPaymentBasepoint = channel.LocalKeySet.PaymentCompactBasepoint,
            RemoteFundingPubKey = funding.RemoteFundingPubKey,
            RemoteRevocationBasepoint = remote.RevocationCompactBasepoint,
            RemotePaymentBasepoint = remote.PaymentCompactBasepoint,
            RemoteDelayedPaymentBasepoint = remote.DelayedPaymentCompactBasepoint,
            RemoteHtlcBasepoint = remote.HtlcCompactBasepoint,
            Local = ChannelBackupParty.From(channel.ChannelParams.Local),
            Remote = ChannelBackupParty.From(channel.ChannelParams.Remote),
            LocalFundingKeyIndex = current?.LocalFundingKeyIndex ?? 0,
            PendingFundings = pending
        };
    }

    private static string ToPeerType(AddressDescriptorType type) => type switch
    {
        AddressDescriptorType.IPv4 => "IPv4",
        AddressDescriptorType.IPv6 => "IPv6",
        AddressDescriptorType.TorV3 => "TorV3",
        _ => "DNS"
    };

    private static bool HasShortChannelId(ShortChannelId shortChannelId)
    {
        var bytes = (byte[])shortChannelId;
        return bytes is not null && bytes.Any(b => b != 0);
    }

    private static async Task<ChannelState?> GetLocalStateAsync(IUnitOfWork unitOfWork, ChannelId channelId)
    {
        try
        {
            return (await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId))?.State;
        }
        catch (Exception)
        {
            // A row the repository refuses (legacy HTLC state) still exists
            return null;
        }
    }

    /// <summary>The plaintext with the creation time left out, to compare two backups' channels.</summary>
    private static byte[] CanonicalContent(ChannelBackupSnapshot snapshot) =>
        ChannelBackupCodec.Encode(snapshot with { CreatedAt = DateTimeOffset.UnixEpoch });

    private byte[] Encrypt(ChannelBackupSnapshot snapshot)
    {
        var key = DeriveKey();
        try
        {
            return ChannelBackupCipher.Encrypt(key, ChannelBackupCodec.Encode(snapshot));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private byte[] DeriveKey()
    {
        var keyPair = _secureKeyManager.GetNodeKeyPair();
        var privateKey = (byte[])keyPair.PrivKey;
        try
        {
            return ChannelBackupCipher.DeriveKey(privateKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }
}