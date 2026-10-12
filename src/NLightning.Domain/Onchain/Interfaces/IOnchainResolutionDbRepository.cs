namespace NLightning.Domain.Onchain.Interfaces;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Models;

/// <summary>
/// Persistence port for the on-chain resolution of closed channels (BOLT 5 plan O1-T2): the funding spend of each
/// channel (<see cref="ChannelCloseModel"/>, table <c>ChannelCloses</c>) and the outputs to resolve
/// (<see cref="OutputResolutionModel"/>, table <c>OutputResolutions</c>). Writes are staged and committed by
/// <c>IUnitOfWork.SaveChangesAsync</c>, in the same save as the state change that decided them.
/// </summary>
public interface IOnchainResolutionDbRepository
{
    /// <summary>Stages the funding spend of a channel; replaces the one recorded before (a reorg can change it).</summary>
    Task UpsertCloseAsync(ChannelCloseModel close);

    Task<ChannelCloseModel?> GetCloseAsync(ChannelId channelId);

    /// <summary>Every recorded funding spend (startup).</summary>
    Task<IReadOnlyList<ChannelCloseModel>> GetClosesAsync();

    /// <summary>Stages removing the funding spend of a channel (its block was disconnected).</summary>
    Task DeleteCloseAsync(ChannelId channelId);

    /// <summary>Stages a new output or the new state of a known one.</summary>
    Task UpsertOutputAsync(OutputResolutionModel output);

    Task<OutputResolutionModel?> GetOutputAsync(TxId transactionId, uint outputIndex);

    /// <summary>Every output of the channel, by outpoint.</summary>
    Task<IReadOnlyList<OutputResolutionModel>> GetOutputsByChannelIdAsync(ChannelId channelId);

    /// <summary>Every output of every channel that is neither irrevocably resolved nor ignored.</summary>
    Task<IReadOnlyList<OutputResolutionModel>> GetUnresolvedOutputsAsync();
}