namespace NLightning.Domain.Bitcoin.Interfaces;

using Channels.ValueObjects;

/// <summary>
/// The local signer's durable safety state per channel (NL-1345, <see cref="ChannelSignerGuard"/>). The signer reads it
/// at registration and before every guarded operation, and raises it before a per-commitment secret, a commitment
/// signature for the peer or a broadcast signature leaves the signer. It is authoritative over the channel rows: a
/// process that starts later from the same database (a restart, a restore, a standby) never signs below it.
/// </summary>
/// <remarks>
/// The API is synchronous like the signer's. Both calls throw when the store cannot be read or written; the signer then
/// refuses the guarded operation (fail closed).
/// </remarks>
public interface IChannelSignerGuardStore
{
    /// <summary>The persisted guard of <paramref name="channelId"/>, or null when none was persisted yet.</summary>
    ChannelSignerGuard? Load(ChannelId channelId);

    /// <summary>
    /// Durably raises the persisted guard of <paramref name="channelId"/> to at least <paramref name="guard"/> (field by
    /// field as <see cref="ChannelSignerGuard.Merge"/>, atomically against other writers: a stale writer never lowers
    /// it) and returns once the write is committed.
    /// </summary>
    void Raise(ChannelId channelId, ChannelSignerGuard guard);
}