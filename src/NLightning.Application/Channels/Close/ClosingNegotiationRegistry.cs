using System.Collections.Concurrent;

namespace NLightning.Application.Channels.Close;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;

/// <summary>
/// The in-memory part of each channel's mutual close (BOLT2 plan N10-T3): what happened on the peer's current
/// connection and the running <c>closing_signed</c> negotiation, which BOLT 2 restarts on every reconnection
/// (B2-RE-29), plus the IPC caller's preferences and waiters.
/// </summary>
/// <remarks>
/// Singleton. Every mutation of a channel's entry happens under that channel's lock, except
/// <see cref="ResetConnection"/> (called before any message of the new connection) and the waiters.
/// </remarks>
public sealed class ClosingNegotiationRegistry
{
    private readonly ConcurrentDictionary<ChannelId, Entry> _entries = new();

    // The wallet shutdown scripts handed out by this process, per channel (NL-280: the wallet answers every caller
    // with the same first unused address, and two closes on different channels run under different locks)
    private readonly ConcurrentDictionary<BitcoinScript, ChannelId> _shutdownScripts = new();

    /// <summary>The close state of one channel.</summary>
    public sealed class Entry
    {
        private readonly List<TaskCompletionSource<TxId>> _waiters = [];

        /// <summary>Our <c>shutdown</c> went out on the peer's current connection (first send or re-send).</summary>
        public bool ShutdownSentOnConnection { get; set; }

        /// <summary>The peer's <c>shutdown</c> arrived on its current connection.</summary>
        public bool ShutdownReceivedOnConnection { get; set; }

        /// <summary>
        /// Our <c>closing_signed</c> for the agreed transaction went out on the current connection in answer to the
        /// same fee (a Closing channel answers that once per connection, so two Closing nodes never echo forever).
        /// </summary>
        public bool AgreedClosingSignedSentOnConnection { get; set; }

        /// <summary>The negotiation on the current connection, or null before its first <c>closing_signed</c>.</summary>
        public ClosingNegotiation? Negotiation { get; set; }

        /// <summary>The IPC caller's close request (feerate, fee_range use), or null for the defaults.</summary>
        public ChannelCloseRequest? Request { get; set; }

        /// <summary>
        /// Our fee estimate (sat/kw) for the negotiation on the current connection, read once when it starts, or null
        /// before. A new connection reads it again (fees may have moved, which is why BOLT 2 restarts the negotiation).
        /// </summary>
        public ulong? EstimateFeeratePerKw { get; set; }

        /// <summary>
        /// The negotiation on the current connection already asked <see cref="ClosingFeeEstimator"/> (and may have
        /// waited for it): later messages don't wait again.
        /// </summary>
        public bool EstimateAttempted { get; set; }

        /// <summary>
        /// When the peer must have answered our last <c>closing_signed</c> (B2-CLS-03), or null when no answer is
        /// awaited. Cleared by any <c>closing_signed</c> received and by a new connection.
        /// </summary>
        public DateTimeOffset? ReplyDueAt { get; set; }

        /// <summary>
        /// When the peer must have sent a <c>fee_range</c> that overlaps ours (B2-CLS-R04), or null when none is
        /// awaited. Kept across reconnections.
        /// </summary>
        public DateTimeOffset? FeeRangeDueAt { get; set; }

        /// <summary>The earliest deadline, or null for none.</summary>
        public DateTimeOffset? NextDeadline => GetNextDeadline(true);

        /// <summary>
        /// The earliest deadline, or null for none; without the reply deadline when <paramref name="includeReply"/> is
        /// false (the peer is not on the connection our <c>closing_signed</c> went out on).
        /// </summary>
        public DateTimeOffset? GetNextDeadline(bool includeReply) =>
            (includeReply ? ReplyDueAt : null, FeeRangeDueAt) switch
            {
                ({ } reply, { } range) => reply < range ? reply : range,
                ({ } reply, null) => reply,
                (null, { } range) => range,
                _ => null
            };

        /// <summary>
        /// The deadline that is past at <paramref name="now"/>, if any (the reply deadline first, and only when
        /// <paramref name="includeReply"/>).
        /// </summary>
        public ClosingDeadline? GetExpired(DateTimeOffset now, bool includeReply = true)
        {
            if (includeReply && ReplyDueAt is { } reply && reply <= now)
                return new ClosingDeadline("B2-CLS-03", "no closing_signed answered ours in time",
                                           ClosingTimeoutMonitor.NoReplyPeerMessage);
            if (FeeRangeDueAt is { } range && range <= now)
                return new ClosingDeadline("B2-CLS-R04", "no fee_range overlapping ours arrived in time",
                                           ClosingTimeoutMonitor.NoFeeRangePeerMessage);
            return null;
        }

        /// <summary>Waits until the closing transaction is agreed and broadcast.</summary>
        public Task<TxId> WaitForClosingTxAsync()
        {
            var waiter = new TaskCompletionSource<TxId>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
                _waiters.Add(waiter);
            return waiter.Task;
        }

        /// <summary>Completes every waiter with the closing txid.</summary>
        public void CompleteWaiters(TxId closingTxId)
        {
            TaskCompletionSource<TxId>[] waiters;
            lock (_waiters)
            {
                waiters = _waiters.ToArray();
                _waiters.Clear();
            }

            foreach (var waiter in waiters)
                waiter.TrySetResult(closingTxId);
        }

        internal void ResetConnection()
        {
            ShutdownSentOnConnection = false;
            ShutdownReceivedOnConnection = false;
            AgreedClosingSignedSentOnConnection = false;
            Negotiation = null;
            ReplyDueAt = null;
            EstimateFeeratePerKw = null;
            EstimateAttempted = false;
        }
    }

    /// <summary>The entry of <paramref name="channelId"/>, created on first use.</summary>
    public Entry Get(ChannelId channelId) => _entries.GetOrAdd(channelId, _ => new Entry());

    /// <summary>The entry of <paramref name="channelId"/> if there is one.</summary>
    public bool TryGet(ChannelId channelId, out Entry? entry) => _entries.TryGetValue(channelId, out entry);

    /// <summary>
    /// A new connection with the channel's peer: nothing was exchanged on it yet and the negotiation restarts.
    /// </summary>
    public void ResetConnection(ChannelId channelId)
    {
        if (_entries.TryGetValue(channelId, out var entry))
            entry.ResetConnection();
    }

    /// <summary>
    /// Claims <paramref name="script"/> as the shutdown script of <paramref name="channelId"/> for this process;
    /// false when another channel claimed it first. Atomic, so two concurrent closes never both get it.
    /// </summary>
    public bool TryReserveShutdownScript(ChannelId channelId, BitcoinScript script) =>
        _shutdownScripts.GetOrAdd(script, channelId) == channelId;

    /// <summary>Forgets a closed channel (and its shutdown script reservation).</summary>
    public void Remove(ChannelId channelId)
    {
        _entries.TryRemove(channelId, out _);
        foreach (var (script, owner) in _shutdownScripts)
            if (owner == channelId)
                _shutdownScripts.TryRemove(new KeyValuePair<BitcoinScript, ChannelId>(script, owner));
    }
}

/// <summary>A closing negotiation deadline that passed.</summary>
/// <param name="RequirementId">The BOLT 2 row that asks to fail the channel.</param>
/// <param name="Reason">The local log text.</param>
/// <param name="PeerMessage">The <c>error</c> text.</param>
public sealed record ClosingDeadline(string RequirementId, string Reason, string PeerMessage);