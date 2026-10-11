namespace NLightning.Domain.Channels.ValueObjects;

/// <summary>
/// The local signer's durable safety state of one channel (NL-1345): what any process signing for the channel has
/// already released or signed, so a later process (a restart, a restore, a standby starting from the same database)
/// never signs below it even when the channel rows it reads lag. Every field only moves in its safe direction:
/// <see cref="Merge"/> keeps the higher numbers, the lower broadcast mark and a set data-loss flag.
/// </summary>
/// <param name="LocalCommitmentNumber">The highest local commitment number the signer advanced to: commitments below it
/// are revoked or about to be, and are never signed for broadcast (I4, NL-189).</param>
/// <param name="RevokedCommitmentNumber">The highest local commitment whose per-commitment secret left the signer, if
/// any.</param>
/// <param name="RemoteSignedCommitmentNumber">The highest commitment number of the peer's commitment the signer signed,
/// on any funding, if any: a lower one is never signed again (the peer could hold two unrevoked versions).</param>
/// <param name="BroadcastSignedCommitmentNumber">The lowest local commitment number signed for broadcast (S1), if any:
/// its secret is never released and nothing later is signed.</param>
/// <param name="DataLossDetected">Data loss was proven on the channel (I12): nothing is signed for it any more.</param>
public readonly record struct ChannelSignerGuard(ulong LocalCommitmentNumber,
                                                 ulong? RevokedCommitmentNumber = null,
                                                 ulong? RemoteSignedCommitmentNumber = null,
                                                 ulong? BroadcastSignedCommitmentNumber = null,
                                                 bool DataLossDetected = false)
{
    /// <summary>
    /// The guard that is at least as strict as both: the higher local, revoked and remote numbers (the local number at
    /// least one past the revoked one), the lower broadcast mark, data loss if either has it.
    /// </summary>
    public ChannelSignerGuard Merge(ChannelSignerGuard other)
    {
        var revoked = Max(RevokedCommitmentNumber, other.RevokedCommitmentNumber);
        var local = Math.Max(LocalCommitmentNumber, other.LocalCommitmentNumber);
        if (revoked is { } r)
            local = Math.Max(local, r + 1);

        return new ChannelSignerGuard(local, revoked,
                                      Max(RemoteSignedCommitmentNumber, other.RemoteSignedCommitmentNumber),
                                      Min(BroadcastSignedCommitmentNumber, other.BroadcastSignedCommitmentNumber),
                                      DataLossDetected || other.DataLossDetected);
    }

    /// <summary>True when this guard is already at least as strict as <paramref name="other"/> in every field.</summary>
    public bool Covers(ChannelSignerGuard other) => Merge(other) == this;

    private static ulong? Max(ulong? a, ulong? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    private static ulong? Min(ulong? a, ulong? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}