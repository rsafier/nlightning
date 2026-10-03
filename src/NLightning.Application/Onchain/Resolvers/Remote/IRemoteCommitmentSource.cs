namespace NLightning.Application.Onchain.Resolvers.Remote;

using Domain.Onchain.Models;

/// <summary>
/// The peer commitment on chain, for the rounds that need its outputs rather than a rebuild: a commitment we cannot
/// rebuild (data loss, B5-RMT-03) and a rebuild whose txid differs from the one on chain (then mapped by script).
/// </summary>
public interface IRemoteCommitmentSource
{
    /// <summary>
    /// The funding spend <paramref name="close"/> records, or null when it cannot be read now (the resolver asks again
    /// on the next block).
    /// </summary>
    Task<ChainTx?> GetCommitmentAsync(ChannelCloseModel close, CancellationToken cancellationToken);
}