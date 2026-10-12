namespace NLightning.Domain.Bitcoin.Interfaces;

/// <summary>
/// The local signer's view of the database's channel key index high-water mark (NL-1388): the highest channel key
/// index any process over this database handed out. The signer claims every new index here before it returns it, so a
/// second process over the same database (a replacement host with an older key file, an active/standby pair) never
/// hands out an index the first one reserved, even for a negotiation whose channel was never saved.
/// </summary>
/// <remarks>
/// The API is synchronous like the signer's. Every call throws when the store cannot be read or written; the signer then
/// refuses the new channel (fail closed). A store behind a node write fence (NL-1342) refuses a fenced process's claim.
/// </remarks>
public interface IChannelKeyIndexStore
{
    /// <summary>The stored high-water mark, or null when none was stored yet.</summary>
    uint? GetHighWater();

    /// <summary>
    /// Claims <paramref name="index"/> atomically against other writers and returns once the claim is committed: true
    /// when the high-water mark was below it and now is it, false when it was already at or above it (the index must
    /// not be used; <see cref="GetHighWater"/> tells how far to skip).
    /// </summary>
    bool TryClaim(uint index);

    /// <summary>Durably raises the high-water mark to at least <paramref name="index"/>; never lowers it.</summary>
    void Raise(uint index);
}