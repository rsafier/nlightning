using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Models;

/// <summary>
/// The durable signer guard (NL-1345): the revocation guard, the remote-commitment guard, the S1 broadcast mark and the
/// data-loss flag kept in an <see cref="IChannelSignerGuardStore"/>, so a process that starts later from the same
/// database (a restart, a restore, a standby) never signs below what an earlier one released or signed, even when the
/// channel rows it reads lag.
/// </summary>
/// <remarks>
/// Order: the guarded operation first reads the store (outside the commitment lock) and applies it to memory, then
/// checks and moves the in-memory guard under the lock (so concurrent operations of this process see it at once), and
/// raises the store (outside the lock) before the secret or signature is returned. A failed read or write refuses the
/// operation; the in-memory guard then stays at least as strict as what was persisted. Without a store (the remote
/// signer process, harnesses) only the in-memory guard applies.
/// </remarks>
public partial class LocalLightningSigner
{
    private readonly IChannelSignerGuardStore? _guardStore;

    // What the store is known to hold per channel (read from it or written to it): a raise it already covers is skipped
    private readonly ConcurrentDictionary<ChannelId, ChannelSignerGuard> _durableGuards = new();

    // The highest commitment number of the peer's commitment signed per channel, on any funding (NL-1345)
    private readonly ConcurrentDictionary<ChannelId, ulong> _remoteSignedNumbers = new();

    // Channels whose registration could not synchronize the durable guard: the next guarded operation does it first
    // and refuses while it cannot (NL-1375). The value is the registration's token, so a synchronization never clears
    // the mark of a later registration it did not persist
    private readonly ConcurrentDictionary<ChannelId, object> _unsyncedDurableGuards = new();

    /// <summary>
    /// At registration: applies the persisted guard and persists what the registration brought (the channel rows of a
    /// database from before the store, or a newer row). A failure is logged and leaves the channel unsynchronized:
    /// every guarded operation (each reads the store first, <see cref="RefreshDurableGuard"/>) retries the whole
    /// synchronization and refuses while it fails (NL-1375).
    /// </summary>
    private void SyncDurableGuardAtRegistration(ChannelId channelId)
    {
        if (_guardStore is null)
            return;

        _unsyncedDurableGuards[channelId] = new object();
        try
        {
            RefreshDurableGuard(channelId);
        }
        catch (SignerException e)
        {
            _logger.LogWarning(e, "Could not synchronize the durable signer guard of channel {ChannelId} at "
                                + "registration; its guarded operations are refused until a retry succeeds",
                               channelId);
        }
    }

    /// <summary>
    /// Reads the persisted guard of <paramref name="channelId"/> and applies it to memory; for a channel whose
    /// registration could not synchronize it, also persists what the registration brought. Called outside the
    /// commitment lock, before a guarded operation takes it.
    /// </summary>
    /// <exception cref="SignerException">The store could not be read, or an unsynchronized registration could not be
    /// persisted (fail closed).</exception>
    private void RefreshDurableGuard(ChannelId channelId)
    {
        if (_guardStore is null)
            return;

        ChannelSignerGuard? loaded;
        try
        {
            loaded = _guardStore.Load(channelId);
        }
        catch (Exception e)
        {
            throw new SignerException("The signer's durable guard could not be read", channelId, e,
                                      "Internal error");
        }

        if (loaded is { } guard)
            ApplyDurableGuard(channelId, guard);

        if (!_unsyncedDurableGuards.TryGetValue(channelId, out var registration))
            return;

        // What a registration brought (rows newer than the store, or a database from before it) is durable before the
        // channel's first guarded operation
        ChannelSignerGuard current;
        lock (GetCommitmentLock(channelId))
            current = GetMemoryGuard(channelId);
        PersistDurableGuard(channelId, current);
        _unsyncedDurableGuards.TryRemove(new KeyValuePair<ChannelId, object>(channelId, registration));
    }

    /// <summary>Moves the in-memory guard to at least <paramref name="guard"/>; it never moves back.</summary>
    private void ApplyDurableGuard(ChannelId channelId, ChannelSignerGuard guard)
    {
        // Normalized: the local number is at least one past the revoked one
        guard = guard.Merge(new ChannelSignerGuard(0));
        lock (GetCommitmentLock(channelId))
        {
            var local = guard.LocalCommitmentNumber;
            _localCommitmentNumbers.AddOrUpdate(channelId, local, (_, current) => Math.Max(current, local));
            if (guard.BroadcastSignedCommitmentNumber is { } broadcast)
                MarkBroadcastSignedInMemory(channelId, broadcast);
            if (guard.RemoteSignedCommitmentNumber is { } remote)
                _remoteSignedNumbers.AddOrUpdate(channelId, remote, (_, current) => Math.Max(current, remote));
            if (guard.DataLossDetected)
                _dataLossChannels[channelId] = true;
            _durableGuards.AddOrUpdate(channelId, guard, (_, current) => current.Merge(guard));
        }
    }

    /// <summary>The in-memory guard of <paramref name="channelId"/>; the caller holds its commitment lock.</summary>
    private ChannelSignerGuard GetMemoryGuard(ChannelId channelId) =>
        new(_localCommitmentNumbers.GetValueOrDefault(channelId), null,
            _remoteSignedNumbers.TryGetValue(channelId, out var remote) ? remote : null,
            _broadcastSignedNumbers.TryGetValue(channelId, out var broadcast) ? broadcast : null,
            _dataLossChannels.ContainsKey(channelId));

    /// <summary>
    /// Durably raises the persisted guard of <paramref name="channelId"/> to at least <paramref name="guard"/>, outside
    /// the commitment lock, before what it guards leaves the signer.
    /// </summary>
    /// <exception cref="SignerException">The store could not be written: the guarded secret or signature must not be
    /// returned.</exception>
    private void PersistDurableGuard(ChannelId channelId, ChannelSignerGuard guard)
    {
        if (_guardStore is null)
            return;

        if (_durableGuards.TryGetValue(channelId, out var known) && known.Covers(guard))
            return;

        try
        {
            _guardStore.Raise(channelId, guard);
        }
        catch (Exception e)
        {
            throw new SignerException("The signer's durable guard could not be written", channelId, e,
                                      "Internal error");
        }

        _durableGuards.AddOrUpdate(channelId, guard, (_, current) => current.Merge(guard));
    }

    /// <summary>
    /// The commitment number of <paramref name="unsignedTransaction"/> when it is a commitment transaction of the
    /// channel (BOLT 3 locktime and sequence form, decoded with the channel's obscuring factor); null for any other
    /// transaction (a closing transaction, whose locktime and sequence never have the commitment form).
    /// </summary>
    /// <exception cref="SignerException">The transaction does not parse, or it has the commitment form and the
    /// channel's obscuring factor is not known: its number cannot be checked against the remote-commitment guard, so
    /// it is not signed (fail closed, NL-1374).</exception>
    private ulong? GetCommitmentNumber(ChannelId channelId, ChannelSigningInfo signingInfo,
                                       SignedTransaction unsignedTransaction)
    {
        Transaction tx;
        try
        {
            tx = Transaction.Load(unsignedTransaction.RawTxBytes, _network);
        }
        catch (Exception e)
        {
            throw new SignerException("Failed to load the transaction to sign", channelId, e, "Internal error");
        }

        if (tx.Inputs.Count != 1
         || !CommitmentNumber.TryGetObscured(tx.LockTime.Value, tx.Inputs[0].Sequence.Value, out var obscured))
            return null;

        // Every source of signing data carries the factor (the channel rows, the channel model once the peer's
        // payment basepoint is known), so a commitment without it is a registration that cannot be guarded
        if (signingInfo.CommitmentObscuringFactor is not { } factor)
            throw new SignerException(
                "Refusing to sign a commitment transaction: the channel's commitment number obscuring factor is not "
              + "known, so the remote-commitment guard cannot check it", channelId, "Internal error");

        return obscured ^ factor;
    }

    /// <summary>
    /// The remote-commitment guard, under the commitment lock: the peer's commitment <paramref name="number"/> is never
    /// signed below the highest one already signed (the peer could then hold two unrevoked commitments); the same
    /// number again (a retransmission, another funding of a splice) is allowed. Moves the in-memory guard to it.
    /// </summary>
    private void CheckAndMarkRemoteCommitment(ChannelId channelId, ulong number)
    {
        if (_remoteSignedNumbers.TryGetValue(channelId, out var signed) && number < signed)
            throw new SignerException(
                $"Refusing to sign the peer's commitment {number}: commitment {signed} is already signed", channelId,
                "Internal error");

        _remoteSignedNumbers.AddOrUpdate(channelId, number, (_, current) => Math.Max(current, number));
    }

    /// <summary>
    /// Persists the remote-commitment guard of the peer's commitment <paramref name="number"/> (null: the signed
    /// transaction was not a commitment) before the signature is returned.
    /// </summary>
    private void PersistRemoteCommitmentGuard(ChannelId channelId, ulong? number)
    {
        if (number is not { } n)
            return;

        ChannelSignerGuard current;
        lock (GetCommitmentLock(channelId))
            current = GetMemoryGuard(channelId);
        PersistDurableGuard(channelId, new ChannelSignerGuard(current.LocalCommitmentNumber, null, n));
    }

    /// <summary>Persists the S1 mark of <paramref name="commitmentNumber"/> (already marked in memory).</summary>
    private void PersistBroadcastGuard(ChannelId channelId, ulong commitmentNumber)
    {
        ChannelSignerGuard current;
        lock (GetCommitmentLock(channelId))
            current = GetMemoryGuard(channelId);
        PersistDurableGuard(channelId,
                            new ChannelSignerGuard(current.LocalCommitmentNumber, null, null, commitmentNumber));
    }
}