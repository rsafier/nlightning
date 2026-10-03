using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Persistence.Contexts;
using Persistence.Entities.Channel;

/// <summary>
/// The signer's view of a stored channel (NL-067), read from the channel row, its two key sets and its commitment
/// broadcast rows. Nothing secret is stored: the local key set holds the channel key index the signer derives every key
/// from.
/// </summary>
/// <remarks>
/// A <see cref="ChannelState.Closed"/> or <see cref="ChannelState.Stale"/> channel is not served: its funding output is
/// irrevocably spent (or was never confirmed), so the signer, which loads what this repository returns, keeps refusing
/// it after a restart as it did before NL-067. Every other state is served, also a channel that
/// <c>ChannelDbRepository.GetByIdAsync</c> refuses (legacy HTLC rows), because its commitment may still have to be
/// broadcast and resolved.
/// </remarks>
public class ChannelSigningInfoDbRepository : IChannelSigningInfoDbRepository
{
    private const byte ClosedState = (byte)ChannelState.Closed;
    private const byte StaleState = (byte)ChannelState.Stale;

    private readonly NLightningDbContext _context;

    public ChannelSigningInfoDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    /// <remarks>The channel row, its fundings and commitments are read from one snapshot of the database (NL-810): a
    /// splice lock saved in between moves the funding columns and the funding rows together.</remarks>
    public Task<ChannelSigningInfo?> GetAsync(ChannelId channelId) =>
        _context.ReadConsistentlyAsync(() => ReadAsync(channelId));

    /// <inheritdoc />
    /// <remarks>Every row is read from one snapshot of the database (NL-810), as by <see cref="GetAsync"/>.</remarks>
    public Task<IReadOnlyDictionary<ChannelId, ChannelSigningInfo>> GetAllAsync() =>
        _context.ReadConsistentlyAsync(ReadAllAsync);

    private async Task<ChannelSigningInfo?> ReadAsync(ChannelId channelId)
    {
        var channel = await _context.Channels.AsNoTracking()
                                    .Include(c => c.KeySets)
                                    .Include(c => c.Config)
                                    .Where(c => c.State != ClosedState && c.State != StaleState)
                                    .SingleOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel is null)
            return null;

        var broadcastNumbers = await _context.BroadcastTransactions.AsNoTracking()
                                             .Where(b => b.ChannelId == channelId
                                                      && b.Purpose == (byte)BroadcastPurpose.LocalCommitment
                                                      && b.CommitmentNumber != null)
                                             .Select(b => b.CommitmentNumber!.Value)
                                             .ToListAsync();
        var fundings = await _context.ChannelFundings.AsNoTracking()
                                     .Where(f => f.ChannelId == channelId)
                                     .ToListAsync();
        var localSlots = await GetLocalSlotsAsync(c => c.ChannelId == channelId);

        return Map(channel, broadcastNumbers, fundings, localSlots);
    }

    private async Task<IReadOnlyDictionary<ChannelId, ChannelSigningInfo>> ReadAllAsync()
    {
        var channels = await _context.Channels.AsNoTracking()
                                     .Include(c => c.KeySets)
                                     .Include(c => c.Config)
                                     .Where(c => c.State != ClosedState && c.State != StaleState)
                                     .ToListAsync();
        var broadcasts = await _context.BroadcastTransactions.AsNoTracking()
                                       .Where(b => b.ChannelId != null
                                                && b.Purpose == (byte)BroadcastPurpose.LocalCommitment
                                                && b.CommitmentNumber != null)
                                       .Select(b => new { b.ChannelId, b.CommitmentNumber })
                                       .ToListAsync();
        var numbersByChannel = broadcasts.GroupBy(b => b.ChannelId!.Value)
                                         .ToDictionary(g => g.Key,
                                                       g => g.Select(b => b.CommitmentNumber!.Value).ToList());
        var fundingsByChannel = (await _context.ChannelFundings.AsNoTracking().ToListAsync())
                               .GroupBy(f => f.ChannelId)
                               .ToDictionary(g => g.Key, g => g.ToList());
        var slotsByChannel = (await GetLocalSlotsAsync(_ => true)).GroupBy(c => c.ChannelId)
                                                                  .ToDictionary(g => g.Key, g => g.ToList());

        var result = new Dictionary<ChannelId, ChannelSigningInfo>();
        foreach (var channel in channels)
        {
            var numbers = numbersByChannel.GetValueOrDefault(channel.ChannelId) ?? [];
            if (Map(channel, numbers, fundingsByChannel.GetValueOrDefault(channel.ChannelId) ?? [],
                    slotsByChannel.GetValueOrDefault(channel.ChannelId) ?? []) is { } signingInfo)
                result[channel.ChannelId] = signingInfo;
        }

        return result;
    }

    /// <summary>Our local commitment slots that carry the peer's signatures (SP-I1 source), without tracking.</summary>
    private Task<List<CommitmentEntity>> GetLocalSlotsAsync(
        System.Linq.Expressions.Expression<Func<CommitmentEntity, bool>> filter) =>
        _context.Commitments.AsNoTracking()
                .Where(c => c.Slot == CommitmentEntity.LocalCurrentSlot && c.Signature != null)
                .Where(filter)
                .ToListAsync();

    /// <summary>
    /// Builds the signing info as <c>ChannelModel.GetSigningInfo</c> does, plus the S1 mark (the lowest commitment
    /// number signed for broadcast) and the splice data (splicing plan SP1-C): the current funding's keys and key index
    /// from its funding row, the other fundings, and per pending funding the number of its persisted local commitment
    /// with the peer's signatures (SP-I1).
    /// </summary>
    private static ChannelSigningInfo? Map(ChannelEntity channel, IReadOnlyCollection<long> broadcastNumbers,
                                           IReadOnlyCollection<ChannelFundingEntity> fundings,
                                           IReadOnlyCollection<CommitmentEntity> localSlots)
    {
        var local = channel.KeySets?.FirstOrDefault(k => k.IsLocal);
        var remote = channel.KeySets?.FirstOrDefault(k => !k.IsLocal);
        if (local is null || remote is null)
            return null;

        // After a splice the current funding's keys are its row's; the initial funding's are the key sets'
        var current = fundings.FirstOrDefault(f => f.FundingTxId == channel.FundingTxId
                                                && f.Kind != (byte)ChannelFundingKind.Initial);
        var localFundingPubKey = current?.LocalFundingPubKey ?? local.FundingPubKey;
        var remoteFundingPubKey = current?.RemoteFundingPubKey ?? remote.FundingPubKey;
        CompactPubKey remoteHtlcBasepoint = remote.HtlcBasepoint;

        var others = fundings.Where(f => f.FundingTxId != channel.FundingTxId)
                             .OrderBy(f => f.Sequence)
                             .Select(ChannelFundingDbRepository.MapToDomain)
                             .ToList();
        var persisted = new Dictionary<TxId, ulong>();
        foreach (var funding in others.Where(f => f.Status == ChannelFundingStatus.Pending))
        {
            var slot = localSlots.FirstOrDefault(c => c.FundingTxId == funding.FundingTxId);
            if (slot is not null)
                persisted[funding.FundingTxId] = slot.Number;
        }

        return new ChannelSigningInfo(channel.FundingTxId, channel.FundingOutputIndex,
                                      checked((ulong)channel.FundingAmountSatoshis * 1_000), localFundingPubKey,
                                      remoteFundingPubKey, local.KeyIndex, remoteHtlcBasepoint,
                                      channel.LocalCommitmentNumber, channel.DataLossDetected)
        {
            BroadcastSignedCommitmentNumber = broadcastNumbers.Count == 0 ? null : (ulong)broadcastNumbers.Min(),
            RemoteNodeId = channel.RemoteNodeId,
            ShortChannelId = channel.ShortChannelId,
            AnnounceChannel = channel.Config?.AnnounceChannel ?? false,
            IsSimpleTaproot = channel.Config?.OptionSimpleTaproot ?? false,
            LocalFundingKeyIndex = current?.LocalFundingKeyIndex ?? 0,
            Fundings = others.Count == 0 ? null : others,
            PersistedSpliceCommitments = persisted.Count == 0 ? null : persisted
        };
    }
}