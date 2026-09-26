using Microsoft.Extensions.Logging;

namespace NLightning.Application.Onchain.Resolvers.Revoked;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Turns the outputs a revoked commitment's planner rows want spent (<see cref="PenaltyNeed"/>) into signed penalty
/// transactions and the row updates that go with them (BOLT 5 plan O5-T2/T3, B5-REV-08, B5-REV-09, D10).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>An output without a live transaction gets one: every output whose deadline is still more than
/// <c>security_delay</c> (18) blocks away shares one batched penalty (with our <c>to_remote</c> of the same commitment,
/// +272 weight); an output closer to its deadline gets its own (B5-REV-08), and so does, from the start, an output
/// marked <see cref="PenaltyNeed.Isolate"/> (anchors: an HTLC output the cheater can already spend, O7-T3).</item>
/// <item>A batched penalty that is still unconfirmed when one of its outputs comes within <c>security_delay</c> of its
/// deadline is split: every output still unspent gets its own penalty, the most urgent one paying at least the BIP 125
/// replacement fee of the batch (O5-T3), and published first, so the others no longer conflict once it replaced the
/// batch. The batch is kept (with an alert) when its fee is unknown or the most urgent output cannot outbid it: the
/// singles would all be refused as replacements, and the rows would point at them with nothing left to retry.</item>
/// <item>A penalty invalidated by the cheater (one of its inputs was spent by its HTLC-timeout/success transaction) is
/// replaced by a new one for the inputs it still has (B5-REV-09); the second-level outputs are penalized separately.</item>
/// <item>Fees follow <see cref="SweepFeePolicy"/>: the estimate floored at 253 sat/kw, capped at half the value, and at
/// the whole value once a deadline is within <c>security_delay</c>; an output that cannot pay its own fee at the floor
/// is recorded <see cref="OutputResolutionState.Ignored"/> (dust) with an alert. A transaction the builder refuses for
/// any other reason is alerted and the row left as it is, so the next block tries again.</item>
/// </list>
/// Pure apart from signing: nothing is saved or published here (the executor saves, then publishes).
/// </remarks>
public sealed class PenaltyTransactionComposer
{
    private readonly ILogger _logger;
    private readonly IPenaltyTransactionBuilder _penaltyTransactionBuilder;
    private readonly SweepFeePolicy _policy;

    /// <summary>The fee rules it applies.</summary>
    public SweepFeePolicy Policy => _policy;
    private readonly ILightningSigner _signer;
    private readonly ISweepTransactionBuilder _sweepTransactionBuilder;

    public PenaltyTransactionComposer(IPenaltyTransactionBuilder penaltyTransactionBuilder,
                                      ISweepTransactionBuilder sweepTransactionBuilder, ILightningSigner signer,
                                      SweepFeePolicy policy, ILogger logger)
    {
        _penaltyTransactionBuilder = penaltyTransactionBuilder;
        _sweepTransactionBuilder = sweepTransactionBuilder;
        _signer = signer;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// The actions that give every need a live transaction.
    /// </summary>
    /// <param name="channelId">The channel (the signer's key set).</param>
    /// <param name="needs">Every output the planner wants spent now.</param>
    /// <param name="rows">Every row of the channel.</param>
    /// <param name="spentBy">The transaction that spent each spent output (by outpoint).</param>
    /// <param name="height">The tip.</param>
    /// <param name="estimatePerKw">The feerate estimate.</param>
    /// <param name="destinationScript">Where the penalties pay.</param>
    /// <param name="getFeeAsync">The fee a stored transaction of ours pays, if known (for a replacement).</param>
    public async Task<IReadOnlyList<OutputResolverAction>> ComposeAsync(
        ChannelId channelId, IReadOnlyList<PenaltyNeed> needs, IReadOnlyList<OutputResolutionModel> rows,
        IReadOnlyDictionary<(TxId, uint), TxId> spentBy, uint height, uint estimatePerKw, byte[] destinationScript,
        Func<TxId, Task<ulong?>> getFeeAsync)
    {
        ArgumentNullException.ThrowIfNull(needs);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(spentBy);
        ArgumentNullException.ThrowIfNull(destinationScript);
        ArgumentNullException.ThrowIfNull(getFeeAsync);

        var actions = new List<OutputResolverAction>();
        var fresh = new List<PenaltyNeed>();
        var splitCandidates = new Dictionary<TxId, List<PenaltyNeed>>();

        foreach (var need in needs)
        {
            if (need.Row.State != OutputResolutionState.Broadcast
             || need.Row.ResolvingTransactionId is not { } resolvingTxId)
            {
                fresh.Add(need);
                continue;
            }

            var group = rows.Where(r => r.ResolvingTransactionId == resolvingTxId).ToList();
            var invalidated = group.Any(r => spentBy.TryGetValue((r.TransactionId, r.OutputIndex), out var spender)
                                          && spender != resolvingTxId);
            if (invalidated)
            {
                // B5-REV-09: the cheater spent one of this transaction's inputs; it can never confirm
                if (_logger.IsEnabled(LogLevel.Information))
                    _logger.LogInformation(
                        "Penalty {TxId} of channel {ChannelId} was invalidated by the cheater's HTLC transaction; "
                      + "replacing it for output {Vout} of {OutputTxId}", resolvingTxId, channelId,
                        need.Row.OutputIndex, need.Row.TransactionId);
                fresh.Add(need);
                continue;
            }

            if (group.Count > 1)
            {
                if (!splitCandidates.TryGetValue(resolvingTxId, out var list))
                    splitCandidates[resolvingTxId] = list = [];
                list.Add(need);
            }
        }

        // O5-T3: split a batch that is still unconfirmed once one of its outputs is within security_delay
        foreach (var (batchTxId, members) in splitCandidates)
        {
            if (!members.Any(m => IsUrgent(m, height)))
                continue;

            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning(
                    "Batched penalty {TxId} of channel {ChannelId} is unconfirmed within {Delay} blocks of a deadline; "
                  + "splitting it into {Count} penalties (B5-REV-08)", batchTxId, channelId,
                    _policy.Options.SecurityDelay, members.Count);

            var ordered = members.OrderBy(m => m.DeadlineHeight ?? uint.MaxValue).ToList();
            if (await getFeeAsync(batchTxId) is not { } oldFee)
            {
                actions.Add(new AlertAction("B5-REV-08",
                                            $"Batched penalty {batchTxId} of channel {channelId} is unconfirmed near a "
                                          + "deadline but its fee is unknown, so no replacement can outbid it; it is "
                                          + "kept as it is"));
                continue;
            }

            if (!CanOutbid(ordered[0], oldFee, height, estimatePerKw, destinationScript))
            {
                actions.Add(new AlertAction("B5-REV-08",
                                            $"Batched penalty {batchTxId} of channel {channelId} is unconfirmed near a "
                                          + $"deadline, but output {ordered[0].Input.Vout} of {ordered[0].Input.TxId} "
                                          + $"cannot pay more than its {oldFee} sat fee; the batch is kept"));
                continue;
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                ulong? replacementOf = i == 0 ? oldFee : null;
                actions.AddRange(BuildSingle(channelId, ordered[i], height, estimatePerKw, destinationScript,
                                             batchTxId, replacementOf));
            }
        }

        if (fresh.Count == 0)
            return actions;

        // B5-REV-08: batch what is not urgent, one transaction each for the urgent ones
        var urgent = fresh.Where(n => IsUrgent(n, height)).ToList();
        var batchable = fresh.Where(n => !IsUrgent(n, height)).ToList();
        foreach (var need in urgent)
            actions.AddRange(BuildSingle(channelId, need, height, estimatePerKw, destinationScript, null, null));

        if (batchable.Count == 1)
            actions.AddRange(BuildSingle(channelId, batchable[0], height, estimatePerKw, destinationScript, null, null));
        else if (batchable.Count > 1)
            actions.AddRange(BuildBatch(channelId, batchable, height, estimatePerKw, destinationScript));

        return actions;
    }

    /// <summary>
    /// True when <paramref name="need"/> alone can replace a transaction paying <paramref name="oldFee"/> (BIP 125
    /// rules 3 and 4) and keep an output above dust.
    /// </summary>
    private bool CanOutbid(PenaltyNeed need, ulong oldFee, uint height, uint estimatePerKw, byte[] destinationScript)
    {
        var inputs = new List<SweepInput> { need.Input };
        var decision = Decide(inputs, destinationScript, need.IsPenalty, height, need.DeadlineHeight, estimatePerKw);
        if (decision.Abandon)
            return false;

        var weight = SweepWeights.EstimateTransactionWeight(inputs, [destinationScript.Length]);
        var wanted = Math.Max(decision.FeeSat, _policy.GetReplacementFee(oldFee, SweepWeights.VirtualSize(weight)));
        var dust = ShutdownScriptValidator.GetDustThresholdSat(destinationScript);
        return need.Input.AmountSat > dust && wanted <= need.Input.AmountSat - dust;
    }

    /// <summary>
    /// The largest fee that keeps the output at the dust threshold, when <paramref name="feeSat"/> would leave less;
    /// null when the fee leaves enough (or nothing above dust is left at all).
    /// </summary>
    private static ulong? LeavesDust(ulong amountSat, ulong feeSat, byte[] destinationScript)
    {
        var dust = ShutdownScriptValidator.GetDustThresholdSat(destinationScript);
        return amountSat > dust && feeSat > amountSat - dust ? amountSat - dust : null;
    }

    private bool IsUrgent(PenaltyNeed need, uint height) =>
        need is { IsPenalty: true, Isolate: true }
     || (need is { IsPenalty: true, DeadlineHeight: { } deadline } && _policy.ShouldSplitPenalty(height, deadline));

    private IEnumerable<OutputResolverAction> BuildBatch(ChannelId channelId, IReadOnlyList<PenaltyNeed> needs,
                                                         uint height, uint estimatePerKw, byte[] destinationScript)
    {
        // A batch needs a penalty input; our to_remote alone is a plain sweep
        if (!needs.Any(n => n.IsPenalty))
            return needs.SelectMany(n => BuildSingle(channelId, n, height, estimatePerKw, destinationScript, null,
                                                     null)).ToList();

        var inputs = needs.Select(n => n.Input).ToList();
        var decision = Decide(inputs, destinationScript, true, height, MinDeadline(needs), estimatePerKw);
        if (!decision.Abandon)
        {
            try
            {
                var unsigned = _penaltyTransactionBuilder.BuildBatched(inputs, destinationScript,
                                                                        decision.FeeratePerKw);
                return Finish(channelId, needs, unsigned, height, decision.FeeratePerKw, null);
            }
            catch (ArgumentException e)
            {
                _logger.LogWarning(e, "Batched penalty of channel {ChannelId} could not be built; one per output",
                                   channelId);
            }
        }

        return needs.SelectMany(n => BuildSingle(channelId, n, height, estimatePerKw, destinationScript, null, null))
                    .ToList();
    }

    private IEnumerable<OutputResolverAction> BuildSingle(ChannelId channelId, PenaltyNeed need, uint height,
                                                          uint estimatePerKw, byte[] destinationScript,
                                                          TxId? replaces, ulong? replacedFee)
    {
        var inputs = new List<SweepInput> { need.Input };
        var decision = Decide(inputs, destinationScript, need.IsPenalty, height, need.DeadlineHeight, estimatePerKw);
        if (decision.Abandon)
            return Abandon(need, $"output worth {need.Input.AmountSat} sat does not pay its own fee");

        try
        {
            UnsignedSweepTransaction unsigned;
            var feeratePerKw = decision.FeeratePerKw;
            if (replacedFee is { } oldFee)
            {
                // BIP 125 rules 3 and 4: pay more than the batch did, plus the relay fee of this transaction
                var weight = SweepWeights.EstimateTransactionWeight(inputs, [destinationScript.Length]);
                var wanted = Math.Max(decision.FeeSat,
                                      _policy.GetReplacementFee(oldFee, SweepWeights.VirtualSize(weight)));
                var dust = ShutdownScriptValidator.GetDustThresholdSat(destinationScript);
                var most = need.Input.AmountSat > dust ? need.Input.AmountSat - dust : 0;
                if (wanted > most)
                {
                    _logger.LogWarning(
                        "Split penalty for {Vout} of {OutputTxId} (channel {ChannelId}) cannot outbid batch {TxId} "
                      + "({Fee} sat); broadcasting it at {Most} sat", need.Input.Vout, need.Input.TxId, channelId,
                        replaces, oldFee, most);
                    wanted = most;
                }

                unsigned = _sweepTransactionBuilder.BuildWithFee(inputs, destinationScript, wanted);
                feeratePerKw = SweepFeePolicy.FeeratePerKw(unsigned.FeeSat, unsigned.EstimatedWeight);
            }
            else if (need.IsPenalty && LeavesDust(need.Input.AmountSat, decision.FeeSat, destinationScript) is { } fee)
            {
                // Near a deadline the policy may spend the whole value: pay all but the dust threshold rather than
                // leave the output to the cheater (the builder refuses an output below dust)
                unsigned = _sweepTransactionBuilder.BuildWithFee(inputs, destinationScript, fee);
                feeratePerKw = SweepFeePolicy.FeeratePerKw(unsigned.FeeSat, unsigned.EstimatedWeight);
            }
            else
            {
                unsigned = need.IsPenalty
                               ? _penaltyTransactionBuilder.BuildSingle(need.Input, destinationScript, feeratePerKw)
                               : _sweepTransactionBuilder.Build(inputs, destinationScript, feeratePerKw);
            }

            return Finish(channelId, [need], unsigned, height, feeratePerKw, replaces);
        }
        catch (ArgumentException e)
        {
            // Not the dust decision: never give the output up for it, the next block tries again
            _logger.LogError(e, "Penalty for {Vout} of {OutputTxId} (channel {ChannelId}) could not be built",
                             need.Input.Vout, need.Input.TxId, channelId);
            return
            [
                new AlertAction(need.IsPenalty ? "B5-REV-03" : "B5-REV-02",
                                $"Output {need.Input.Vout} of {need.Input.TxId} ({need.Input.AmountSat} sat) of channel "
                              + $"{need.Row.ChannelId}: no transaction could be built ({e.Message}); retrying every "
                              + "block")
            ];
        }
    }

    private List<OutputResolverAction> Finish(ChannelId channelId, IReadOnlyList<PenaltyNeed> needs,
                                              UnsignedSweepTransaction unsigned, uint height, uint feeratePerKw,
                                              TxId? replaces)
    {
        var signed = _sweepTransactionBuilder.Sign(unsigned, _signer, channelId);
        var purpose = needs.Any(n => n.IsPenalty) ? BroadcastPurpose.Penalty : BroadcastPurpose.Sweep;
        var actions = new List<OutputResolverAction>();
        foreach (var need in needs)
            actions.Add(new UpsertOutputAction(need.Row with
            {
                State = OutputResolutionState.Broadcast,
                ResolvingTransactionId = signed.TxId,
                DeadlineHeight = need.DeadlineHeight ?? need.Row.DeadlineHeight
            }));

        actions.Add(new BroadcastAction(new BroadcastTransactionModel(signed, purpose, channelId, height, feeratePerKw,
                                                                      replaces)));

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "{Purpose} {TxId} of channel {ChannelId}: {Inputs} input(s), fee {Fee} sat{Replaces}", purpose,
                signed.TxId, channelId, needs.Count, unsigned.FeeSat,
                replaces is { } replaced ? $", replaces {replaced}" : string.Empty);

        return actions;
    }

    private static IEnumerable<OutputResolverAction> Abandon(PenaltyNeed need, string why) =>
    [
        new UpsertOutputAction(need.Row with { State = OutputResolutionState.Ignored }),
        new AlertAction(need.IsPenalty ? "B5-REV-03" : "B5-REV-02",
                        $"Output {need.Input.Vout} of {need.Input.TxId} ({need.Input.AmountSat} sat) of channel "
                      + $"{need.Row.ChannelId} is abandoned: {why}")
    ];

    private SweepFeeDecision Decide(IReadOnlyCollection<SweepInput> inputs, byte[] destinationScript, bool isPenalty,
                                    uint height, uint? deadline, uint estimatePerKw)
    {
        var weight = SweepWeights.EstimateTransactionWeight(inputs, [destinationScript.Length]);
        var total = inputs.Aggregate(0UL, (sum, i) => checked(sum + i.AmountSat));
        return _policy.Decide(total, weight, estimatePerKw, isPenalty, height, deadline);
    }

    private static uint? MinDeadline(IEnumerable<PenaltyNeed> needs)
    {
        uint? min = null;
        foreach (var need in needs)
        {
            if (need.DeadlineHeight is { } deadline && (min is null || deadline < min))
                min = deadline;
        }

        return min;
    }
}