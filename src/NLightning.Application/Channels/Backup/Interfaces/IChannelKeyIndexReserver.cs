namespace NLightning.Application.Channels.Backup.Interfaces;

/// <summary>
/// Makes sure the node never hands out a channel key index that a restored channel uses: the last used index lives in
/// the key file, and an older copy of it (or a lost write) would let the next channel reuse a restored channel's
/// funding key, basepoints and per-commitment secrets.
/// </summary>
public interface IChannelKeyIndexReserver
{
    /// <summary>
    /// Advances the key manager's last used channel index to at least <paramref name="highestUsedIndex"/> and persists
    /// it before returning.
    /// </summary>
    /// <returns>The last used index afterwards (at least <paramref name="highestUsedIndex"/>).</returns>
    Task<uint> ReserveThroughAsync(uint highestUsedIndex, CancellationToken cancellationToken);
}