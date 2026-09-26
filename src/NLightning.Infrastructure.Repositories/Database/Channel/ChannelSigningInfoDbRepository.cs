using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Channel;

using Domain.Channels.Interfaces;
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
public class ChannelSigningInfoDbRepository : IChannelSigningInfoDbRepository
{
    private readonly NLightningDbContext _context;

    public ChannelSigningInfoDbRepository(NLightningDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <inheritdoc />
    public async Task<ChannelSigningInfo?> GetAsync(ChannelId channelId)
    {
        var channel = await _context.Channels.AsNoTracking()
                                    .Include(c => c.KeySets)
                                    .SingleOrDefaultAsync(c => c.ChannelId == channelId);
        if (channel is null)
            return null;

        var broadcastNumbers = await _context.BroadcastTransactions.AsNoTracking()
                                             .Where(b => b.ChannelId == channelId
                                                      && b.Purpose == (byte)BroadcastPurpose.LocalCommitment
                                                      && b.CommitmentNumber != null)
                                             .Select(b => b.CommitmentNumber!.Value)
                                             .ToListAsync();

        return Map(channel, broadcastNumbers);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<ChannelId, ChannelSigningInfo>> GetAllAsync()
    {
        var channels = await _context.Channels.AsNoTracking()
                                     .Include(c => c.KeySets)
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

        var result = new Dictionary<ChannelId, ChannelSigningInfo>();
        foreach (var channel in channels)
        {
            var numbers = numbersByChannel.GetValueOrDefault(channel.ChannelId) ?? [];
            if (Map(channel, numbers) is { } signingInfo)
                result[channel.ChannelId] = signingInfo;
        }

        return result;
    }

    /// <summary>
    /// Builds the signing info as <c>ChannelModel.GetSigningInfo</c> does, plus the S1 mark: the lowest commitment
    /// number signed for broadcast.
    /// </summary>
    private static ChannelSigningInfo? Map(ChannelEntity channel, IReadOnlyCollection<long> broadcastNumbers)
    {
        var local = channel.KeySets?.FirstOrDefault(k => k.IsLocal);
        var remote = channel.KeySets?.FirstOrDefault(k => !k.IsLocal);
        if (local is null || remote is null)
            return null;

        CompactPubKey localFundingPubKey = local.FundingPubKey;
        CompactPubKey remoteFundingPubKey = remote.FundingPubKey;
        CompactPubKey remoteHtlcBasepoint = remote.HtlcBasepoint;

        return new ChannelSigningInfo(channel.FundingTxId, channel.FundingOutputIndex,
                                      checked((ulong)channel.FundingAmountSatoshis * 1_000), localFundingPubKey,
                                      remoteFundingPubKey, local.KeyIndex, remoteHtlcBasepoint,
                                      channel.LocalCommitmentNumber, channel.DataLossDetected)
        {
            BroadcastSignedCommitmentNumber = broadcastNumbers.Count == 0 ? null : (ulong)broadcastNumbers.Min(),
            RemoteNodeId = channel.RemoteNodeId,
            ShortChannelId = channel.ShortChannelId
        };
    }
}