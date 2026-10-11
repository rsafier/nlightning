using System.Data.Common;

namespace NLightning.Domain.Node.Fencing;

/// <summary>
/// An optional node write fence, for high-availability or enclave deployments: it guarantees that only the current
/// authorized instance of a node commits state or produces external effects (a paused or partitioned former primary
/// must not act). None is registered by default; then nothing is checked.
/// </summary>
/// <remarks>
/// <para>A check that refuses throws (preferably <see cref="NodeFencedException"/>); any exception counts as a refusal.
/// Both checks are also called from synchronous code (a synchronous save, the signer), which blocks on the returned
/// <see cref="ValueTask"/>: complete synchronously when possible and never depend on the caller's synchronization
/// context.</para>
/// <para>The guarantees assume durable commits: a save the fence allowed is committed before anything it records leaves
/// the node (a message, a broadcast, a signature or a released secret), and a later instance reads it. The host must
/// make the database commit durably, with synchronous commit on a single primary or a synchronous replication quorum;
/// a commit acknowledged before it is durable (asynchronous commit, an asynchronous replica promoted after a failover)
/// can lose a write whose effect already left, and no fence can make up for it (NL-1378).</para>
/// </remarks>
public interface INodeWriteFence
{
    /// <summary>
    /// Called inside the database transaction of every unit-of-work save, after the writes were sent to the database
    /// and before the commit. Throwing rolls the transaction back: nothing of the save is committed.
    /// </summary>
    /// <remarks>
    /// An implementation must take only shared locks here (for example a shared row lock or a shared advisory lock on
    /// the fence's lease, held to the commit), never an exclusive one: every save of the node runs this check, and an
    /// exclusive lock would serialize them all (NL-1378). Only the change of the authorized instance takes the
    /// exclusive lock, so it waits for the saves in flight and every later save sees it.
    /// </remarks>
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