namespace NLightning.Domain.Channels.KeyIndexes;

/// <summary>
/// Channel key indexes handed out to an opening that has no channel row yet and that a restart may resume (NL-1392):
/// the native remote signer's unconsumed <c>ChannelKeyAllocation</c> workflows, whose saved signer receipt replays the
/// same index when the peer sends its open again. A channel restore counts them as in use.
/// </summary>
public interface IPendingChannelKeyIndexSource
{
    /// <summary>The key index of every unconsumed allocation whose signer receipt is saved.</summary>
    Task<IReadOnlyCollection<uint>> GetPendingKeyIndexesAsync(CancellationToken cancellationToken = default);
}