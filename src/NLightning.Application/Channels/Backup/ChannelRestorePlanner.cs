namespace NLightning.Application.Channels.Backup;

using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;
using Models;

/// <summary>
/// Decides what <c>restorechanbackup</c> does with each channel of a decrypted backup (pure: the database lookups and
/// the key derivation are passed in).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The whole backup is refused when it is for another chain or another node (<see cref="Validate"/>).</item>
/// <item>A channel already in the database is never touched (<see cref="ChannelRestoreAction.AlreadyExists"/>): its
/// own state is better than the backup's, and a live channel must not become a recovery one.</item>
/// <item>A channel whose key index does not derive the funding key and payment basepoint it recorded is skipped
/// (<see cref="ChannelRestoreAction.KeysMismatch"/>): without our payment basepoint its <c>to_remote</c> can't be
/// found, and asking the peer to close would only lose the channel. The funding key checked is the one of the
/// entry's current funding, at <see cref="ChannelBackupEntry.LocalFundingKeyIndex"/> (a splice rotates it, NL-478); a
/// reader that predates that field reads index 0 and refuses a spliced channel here rather than restore it with the
/// wrong key. A pending splice whose key does not derive is left out of the entry (it can't be followed).</item>
/// <item>A repeated channel id is restored once (<see cref="ChannelRestoreAction.Duplicate"/>).</item>
/// <item>A channel whose key index another live channel of this database, or an open of this process, already uses is
/// refused (<see cref="FindKeyIndexConflict"/>, <see cref="ChannelRestoreAction.KeyIndexInUse"/>, NL-1392).</item>
/// </list>
/// </remarks>
public static class ChannelRestorePlanner
{
    /// <summary>Refuses a backup made for another chain or by another node.</summary>
    /// <exception cref="ChannelBackupException">It is not this node's backup of this chain.</exception>
    public static void Validate(ChannelBackupSnapshot snapshot, ChainHash chainHash, CompactPubKey nodeId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ChainHash != chainHash)
            throw new ChannelBackupException("The backup is for another chain.");

        if (snapshot.NodeId != nodeId)
            throw new ChannelBackupException("The backup was made by another node.");
    }

    /// <summary>One plan item per channel of <paramref name="channels"/>, in order.</summary>
    /// <param name="channels">The backed-up channels.</param>
    /// <param name="existingState">The state of a channel already in the database: <c>(true, state)</c> when a row
    /// exists (state null when it can't be read), <c>(false, null)</c> when not.</param>
    /// <param name="deriveBasepoints">Our basepoints of a key index.</param>
    public static IReadOnlyList<ChannelRestorePlanItem> Plan(
        IReadOnlyList<ChannelBackupEntry> channels,
        Func<ChannelId, (bool Exists, ChannelState? State)> existingState,
        Func<uint, ChannelBasepoints> deriveBasepoints)
    {
        ArgumentNullException.ThrowIfNull(deriveBasepoints);
        return Plan(channels, existingState, deriveBasepoints,
                    (keyIndex, fundingKeyIndex) => fundingKeyIndex == 0
                                                       ? deriveBasepoints(keyIndex).FundingPubKey
                                                       : null);
    }

    /// <summary>One plan item per channel of <paramref name="channels"/>, in order.</summary>
    /// <param name="channels">The backed-up channels.</param>
    /// <param name="existingState">The state of a channel already in the database: <c>(true, state)</c> when a row
    /// exists (state null when it can't be read), <c>(false, null)</c> when not.</param>
    /// <param name="deriveBasepoints">Our basepoints of a key index.</param>
    /// <param name="deriveFundingKey">Our funding key of a channel key index and a funding key index (null when it
    /// can't be derived: the channel is refused).</param>
    public static IReadOnlyList<ChannelRestorePlanItem> Plan(
        IReadOnlyList<ChannelBackupEntry> channels,
        Func<ChannelId, (bool Exists, ChannelState? State)> existingState,
        Func<uint, ChannelBasepoints> deriveBasepoints,
        Func<uint, uint, CompactPubKey?> deriveFundingKey)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(existingState);
        ArgumentNullException.ThrowIfNull(deriveBasepoints);
        ArgumentNullException.ThrowIfNull(deriveFundingKey);

        var seen = new HashSet<ChannelId>();
        var plan = new List<ChannelRestorePlanItem>(channels.Count);
        foreach (var entry in channels)
        {
            if (!seen.Add(entry.ChannelId))
            {
                plan.Add(new ChannelRestorePlanItem(entry, ChannelRestoreAction.Duplicate));
                continue;
            }

            var (exists, state) = existingState(entry.ChannelId);
            if (exists)
            {
                plan.Add(new ChannelRestorePlanItem(entry, ChannelRestoreAction.AlreadyExists, state));
                continue;
            }

            var basepoints = deriveBasepoints(entry.KeyIndex);
            if (basepoints.PaymentBasepoint != entry.LocalPaymentBasepoint
             || deriveFundingKey(entry.KeyIndex, entry.LocalFundingKeyIndex) is not { } fundingKey
             || (!entry.FundingKeysUnknown && fundingKey != entry.LocalFundingPubKey))
            {
                plan.Add(new ChannelRestorePlanItem(entry, ChannelRestoreAction.KeysMismatch));
                continue;
            }

            // A pending splice is followed only with a funding key we derive
            var pending = entry.PendingFundings
                               .Where(f => deriveFundingKey(entry.KeyIndex, f.LocalFundingKeyIndex) is { } key
                                        && key == f.LocalFundingPubKey)
                               .ToList();
            var checkedEntry = pending.Count == entry.PendingFundings.Count
                                   ? entry
                                   : entry with { PendingFundings = pending };
            plan.Add(new ChannelRestorePlanItem(checkedEntry, ChannelRestoreAction.Restore,
                                                LocalBasepoints: basepoints, LocalFundingPubKey: fundingKey));
        }

        return plan;
    }

    /// <summary>
    /// Why <paramref name="entry"/> must not be restored because its key index is already in use (NL-1392), or null.
    /// </summary>
    /// <remarks>
    /// Two channels at one key index share their funding key, basepoints and per-commitment seed: a secret one of them
    /// reveals belongs to the other too. In use: a channel row of another channel id at that index in any state but
    /// <see cref="ChannelState.Closed"/> and <see cref="ChannelState.Stale"/> (a closed channel signs nothing more, a
    /// stale one never held funds), or an index this process handed out for a negotiation that has no row yet. The
    /// detail says what the operator must do.
    /// </remarks>
    /// <param name="entry">The backed-up channel to restore.</param>
    /// <param name="rows">Our key index of every channel row of the database, with its state.</param>
    /// <param name="isAllocatedByThisProcess">Whether this process handed out a key index (null: unknown).</param>
    public static string? FindKeyIndexConflict(
        ChannelBackupEntry entry, IEnumerable<(ChannelId ChannelId, uint KeyIndex, ChannelState State)> rows,
        Func<uint, bool>? isAllocatedByThisProcess)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(rows);

        foreach (var (channelId, keyIndex, state) in rows)
        {
            if (keyIndex != entry.KeyIndex || channelId == entry.ChannelId || state is ChannelState.Closed
                                                                                   or ChannelState.Stale)
                continue;

            return $"key index {entry.KeyIndex} is already used by channel {channelId} ({state}) of this database: "
                 + "two channels must never sign with the same keys. Close that channel first (closechannel, or "
                 + "forceclosechannel when its peer is away), wait until it is Closed, then run restorechanbackup "
                 + "again";
        }

        if (isAllocatedByThisProcess?.Invoke(entry.KeyIndex) == true)
            return $"key index {entry.KeyIndex} was handed out by this process to a channel negotiation not stored "
                 + "yet: two channels must never sign with the same keys. Wait until that open completes or fails; "
                 + "close the channel if it opened, then restart the node and run restorechanbackup again";

        return null;
    }
}