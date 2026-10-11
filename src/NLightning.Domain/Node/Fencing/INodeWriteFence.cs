using System.Data.Common;

namespace NLightning.Domain.Node.Fencing;

/// <summary>
/// An optional node write fence, for high-availability or enclave deployments: it guarantees that only the current
/// authorized instance of a node commits state or produces external effects (a paused or partitioned former primary
/// must not act). None is registered by default; then nothing is checked.
/// </summary>
/// <remarks>
/// A check that refuses throws (preferably <see cref="NodeFencedException"/>); any exception counts as a refusal. Both
/// checks are also called from synchronous code (a synchronous save, the signer), which blocks on the returned
/// <see cref="ValueTask"/>: complete synchronously when possible and never depend on the caller's synchronization
/// context.
/// </remarks>
public interface INodeWriteFence
{
    /// <summary>
    /// Called inside the database transaction of every unit-of-work save, after the writes were sent to the database
    /// and before the commit. Throwing rolls the transaction back: nothing of the save is committed.
    /// </summary>
    /// <param name="connection">The connection that commits the save.</param>
    /// <param name="transaction">The transaction that commits the save; queries the fence runs on
    /// <paramref name="connection"/> must enlist in it.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    ValueTask CheckSaveAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Called immediately before an external effect. Throwing stops it: a peer message is not sent (and the
    /// connection is closed), a broadcast is skipped (a stored one is sent again later), a signature is not made.
    /// </summary>
    ValueTask CheckEffectAsync(NodeEffect effect, CancellationToken cancellationToken);
}