namespace NLightning.Domain.Onchain.Interfaces;

using Channels.Enums;
using Channels.ValueObjects;
using Models;

/// <summary>Checkpoint writes share the resolver's unit of work and its single commit.</summary>
public interface IOnchainHtlcObservationDbRepository
{
    Task<bool> ContainsAsync(ChannelId channelId, HtlcDirection direction, ulong htlcId, bool settled);

    /// <summary>
    /// Stages a unique checkpoint. The caller holds the channel lock across Contains, Add and Save; a composite
    /// database key rejects independent concurrent writers. Failed saves must roll back the checkpoint too.
    /// </summary>
    void Add(OnchainHtlcObservationModel observation);
}