using System.Collections.Concurrent;

namespace NLightning.Application.Onchain.Fees;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;

/// <summary>
/// An operator's fee bump of an on-chain resolution (LND's walletrpc <c>BumpFee</c>/<c>BumpForceCloseFee</c>, NL-1186):
/// the parameters LND's sweeper takes for an input.
/// </summary>
/// <param name="StartingFeeratePerKw">The fee rate to start from (sat/kw), when higher than the estimate.</param>
/// <param name="BudgetSat">The most the fee may be, replacing the node's own cap.</param>
/// <param name="DeadlineHeight">The height the input should be spent by (it never loosens a channel's own HTLC
/// deadline).</param>
/// <param name="ConfTarget">The confirmation target of the estimate.</param>
/// <param name="Immediate">Bump now instead of at the next block.</param>
public sealed record OperatorFeeBumpRequest(uint? StartingFeeratePerKw, ulong? BudgetSat, uint? DeadlineHeight,
                                            uint? ConfTarget, bool Immediate);

/// <summary>
/// The operator's fee bump requests (memory only, as LND's sweeper parameters): per channel for the anchor CPFP of its
/// pending commitment (<c>AnchorCpfpService</c>), per output for its unconfirmed sweep, claim or penalty
/// (<see cref="SweepScheduler"/>). A request is <em>fresh</em> until a round has applied it: a fresh request replaces
/// the pending transaction at once (no waiting for the RBF interval); afterwards its parameters keep applying to the
/// regular bumps.
/// </summary>
public sealed class OperatorFeeBumps
{
    private readonly ConcurrentDictionary<ChannelId, Entry> _anchors = new();
    private readonly ConcurrentDictionary<(TxId TxId, uint Index), Entry> _outputs = new();

    /// <summary>Records (or replaces) the request for the anchor CPFP of <paramref name="channelId"/>.</summary>
    public void SetAnchor(ChannelId channelId, OperatorFeeBumpRequest request) =>
        _anchors[channelId] = new Entry(request);

    /// <summary>The request for the anchor CPFP of <paramref name="channelId"/>, and whether no round applied it yet.
    /// </summary>
    public OperatorFeeBumpRequest? GetAnchor(ChannelId channelId, out bool fresh)
    {
        fresh = false;
        if (!_anchors.TryGetValue(channelId, out var entry))
            return null;

        fresh = entry.Fresh;
        return entry.Request;
    }

    /// <summary>A round applied the anchor request of <paramref name="channelId"/>.</summary>
    public void MarkAnchorApplied(ChannelId channelId)
    {
        if (_anchors.TryGetValue(channelId, out var entry))
            entry.Fresh = false;
    }

    /// <summary>Forgets the anchor request of <paramref name="channelId"/> (its commitment no longer needs one).</summary>
    public void ClearAnchor(ChannelId channelId) => _anchors.TryRemove(channelId, out _);

    /// <summary>Records (or replaces) the request for the output <paramref name="txId"/>:<paramref name="index"/>.
    /// </summary>
    public void SetOutput(TxId txId, uint index, OperatorFeeBumpRequest request) =>
        _outputs[(txId, index)] = new Entry(request);

    /// <summary>The request for the output, and whether no round applied it yet.</summary>
    public OperatorFeeBumpRequest? GetOutput(TxId txId, uint index, out bool fresh)
    {
        fresh = false;
        if (!_outputs.TryGetValue((txId, index), out var entry))
            return null;

        fresh = entry.Fresh;
        return entry.Request;
    }

    /// <summary>A round applied the request of the output.</summary>
    public void MarkOutputApplied(TxId txId, uint index)
    {
        if (_outputs.TryGetValue((txId, index), out var entry))
            entry.Fresh = false;
    }

    /// <summary>Forgets the request of the output (it is resolved).</summary>
    public void ClearOutput(TxId txId, uint index) => _outputs.TryRemove((txId, index), out _);

    private sealed class Entry(OperatorFeeBumpRequest request)
    {
        private int _fresh = 1;

        public OperatorFeeBumpRequest Request { get; } = request;

        public bool Fresh
        {
            get => Volatile.Read(ref _fresh) != 0;
            set => Volatile.Write(ref _fresh, value ? 1 : 0);
        }
    }
}