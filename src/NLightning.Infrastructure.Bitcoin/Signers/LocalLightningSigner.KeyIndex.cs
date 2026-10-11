using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Exceptions;

/// <summary>
/// The channel key index claim (NL-1388): a new channel's key index is claimed in the database's high-water mark
/// (<see cref="IChannelKeyIndexStore"/>) before <see cref="CreateNewChannel"/> derives anything from it, so a second
/// process over the same database (a replacement host whose key file is older, an active/standby pair) never hands out
/// an index the first one reserved, even for a negotiation that never saved a channel row.
/// </summary>
/// <remarks>
/// The key manager reserves the next index first (its own file or callback write, SECURITY_REVIEW SR-19), then the
/// signer claims it; an index someone else already claimed is skipped (the key manager is raised past the stored mark)
/// and the next one is tried. A store that cannot be read or written refuses the channel (fail closed); an index the
/// key manager reserved but the store refused is never used. Without a store (the remote signer process, harnesses)
/// only the key manager's own record applies.
/// </remarks>
public partial class LocalLightningSigner
{
    // A bound on skips: each one raises the key manager past the stored mark, so a second skip needs another writer
    private const int MaxKeyIndexClaims = 8;

    private readonly IChannelKeyIndexStore? _keyIndexStore;

    /// <summary>Reserves a channel key index the database has not seen yet and claims it there.</summary>
    /// <exception cref="SignerException">The store could not be written, or kept refusing.</exception>
    private uint ReserveClaimedChannelKeyIndex()
    {
        if (_keyIndexStore is null)
            throw new InvalidOperationException("No channel key index store");

        for (var attempt = 0; attempt < MaxKeyIndexClaims; attempt++)
        {
            var index = _secureKeyManager.ReserveChannelKeyIndex();
            bool claimed;
            uint? stored;
            try
            {
                claimed = _keyIndexStore.TryClaim(index);
                stored = claimed ? null : _keyIndexStore.GetHighWater();
            }
            catch (Exception e)
            {
                throw new SignerException("Could not claim a channel key index in the database", e,
                                          "Internal signer error");
            }

            if (claimed)
                return index;

            // Another process over this database (or an older key file of this node) handed it out already
            _logger.LogWarning("Channel key index {Index} was already handed out by a process over this database "
                             + "(high-water mark {Stored}); skipping past it", index, stored);
            if (stored is { } mark && mark > index)
            {
                try
                {
                    _secureKeyManager.EnsureLastUsedChannelIndexAtLeast(mark);
                }
                catch (NotSupportedException)
                {
                    // The next reservations advance one by one
                }
            }
        }

        throw new SignerException("Could not claim a channel key index in the database after "
                                + $"{MaxKeyIndexClaims} attempts", "Internal signer error");
    }
}