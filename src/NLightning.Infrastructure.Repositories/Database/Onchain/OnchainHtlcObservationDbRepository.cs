namespace NLightning.Infrastructure.Repositories.Database.Onchain;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Persistence.Contexts;
using Persistence.Entities.Onchain;

/// <summary>Stages checkpoints in the resolver's existing unit of work.</summary>
public sealed class OnchainHtlcObservationDbRepository(NLightningDbContext context) : IOnchainHtlcObservationDbRepository
{
    public async Task<bool> ContainsAsync(ChannelId channelId, HtlcDirection direction, ulong htlcId, bool settled) =>
        await context.OnchainHtlcObservations.FindAsync(channelId, direction, htlcId, settled) is not null;

    public void Add(OnchainHtlcObservationModel observation) => context.OnchainHtlcObservations.Add(new OnchainHtlcObservationEntity
    {
        ChannelId = observation.ChannelId,
        Direction = observation.Direction,
        HtlcId = observation.HtlcId,
        Settled = observation.Settled,
        ObservedAt = observation.ObservedAt
    });
}