using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Fees;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;
using Domain.Onchain.Planners;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Infrastructure.Bitcoin.Outputs;
using Revoked;

/// <summary>
/// Resolves a revoked commitment of the peer on chain: the penalty (justice) side of BOLT 5 §Revoked Transaction Close
/// Handling (plan O5-T2, O5-T3; B5-REV-02..09, B5-REV-RES-01..03).
/// </summary>
/// <remarks>
/// <para>
/// Every round it rebuilds the revoked commitment <c>n</c> from the revocation log (<see cref="IRevokedCommitmentDbRepository"/>,
/// the spec of every revoked commitment with HTLCs) and the peer's secret <c>n</c> from our copy of its shachain, maps
/// each output (<see cref="ICommitmentOutputMapper"/>, by script when the txid differs) and runs
/// <see cref="OutputResolutionPlanner"/> on it with the chain facts: the recorded spends of the watched outputs, the
/// preimages the witnesses reveal, the second-level outputs. The planner's sweeps become penalties through
/// <see cref="PenaltyTransactionComposer"/> (batched, split at <c>security_delay</c>, replaced when the cheater's HTLC
/// transaction invalidates them); its upstream decisions become <see cref="OutgoingHtlcFulfilled"/> (a preimage on chain,
/// at once) and <see cref="OutgoingHtlcFailed"/> (our offered HTLC penalized, or committed without an output, at
/// reasonable depth) events for the switch.
/// </para>
/// <para>
/// A commitment without HTLCs has no log entry: its <c>to_local</c> and <c>to_remote</c> are found by script. A
/// commitment revoked before the log existed (<see cref="RevokedCommitContext.PredatesLog"/>) gets the same treatment,
/// and its HTLC outputs, which cannot be rebuilt, are reported (plan §8 risk 5).
/// </para>
/// <para>
/// Stateless apart from a cache of spending transactions seen through <see cref="OnOutputSpentAsync"/>: every decision
/// is taken again from the rows and the chain each round, and repeated until its effect is on chain (the executor
/// dedupes broadcasts by txid, watches by outpoint, and the switch is idempotent).
/// </para>
/// </remarks>
public sealed class RevokedCommitResolver : IOutputResolver
{
    private const int MaxCachedSpends = 1024;

    private readonly PenaltyTransactionComposer _composer;
    private readonly IRevokedCommitDataSource _dataSource;
    private readonly IKeyDerivationService _keyDerivationService;
    private readonly ILogger<RevokedCommitResolver> _logger;
    private readonly ICommitmentOutputMapper _mapper;
    private readonly RevokedCommitResolverOptions _options;
    private readonly ConcurrentDictionary<(ChannelId, ulong, bool), byte> _raised = new();
    private readonly ConcurrentDictionary<(TxId, uint), (ChainTx Transaction, uint Height)> _seenSpends = new();

    public RevokedCommitResolver(IRevokedCommitDataSource dataSource, ICommitmentOutputMapper mapper,
                                 IPenaltyTransactionBuilder penaltyTransactionBuilder,
                                 ISweepTransactionBuilder sweepTransactionBuilder, ILightningSigner signer,
                                 IKeyDerivationService keyDerivationService, ILogger<RevokedCommitResolver> logger,
                                 IOptions<RevokedCommitResolverOptions>? options = null)
    {
        _dataSource = dataSource;
        _mapper = mapper;
        _keyDerivationService = keyDerivationService;
        _logger = logger;
        _options = options?.Value ?? new RevokedCommitResolverOptions();
        _composer = new PenaltyTransactionComposer(penaltyTransactionBuilder, sweepTransactionBuilder, signer,
                                                   new SweepFeePolicy(_options.FeePolicy), logger);
    }

    /// <inheritdoc />
    public bool CanResolve(ChannelCloseKind kind) => kind == ChannelCloseKind.RevokedCommitment;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                                        IReadOnlyList<OutputResolutionModel> outputs,
                                                                        uint height,
                                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(outputs);

        var load = await _dataSource.LoadAsync(close, cancellationToken);
        if (load.Context is not { } context)
            return
            [
                new AlertAction("B5-REV-03",
                                $"Channel {close.ChannelId}: revoked commitment {close.CommitmentTransactionId} cannot "
                              + $"be penalized: {load.Problem}")
            ];

        var round = new Round(context, close, outputs, height);
        var map = Map(context);
        ReportUnmapped(round, map);

        var needs = new List<PenaltyNeed>();
        var spentBy = new Dictionary<(TxId, uint), TxId>();
        var mappedHtlcs = new HashSet<HtlcKey>();

        foreach (var descriptor in map.Outputs)
        {
            if (descriptor.Kind is not (OutputDescriptorKind.RevokedToLocal or OutputDescriptorKind.RevokedHtlc
                                        or OutputDescriptorKind.PaymentToRemote))
                continue;

            if (descriptor.Htlc is { } mapped)
                mappedHtlcs.Add(new HtlcKey(mapped.Direction, mapped.Id));

            await PlanOutputAsync(round, map, descriptor, needs, spentBy, cancellationToken);
        }

        PlanHtlcsWithoutOutput(round, mappedHtlcs);

        if (needs.Count > 0)
        {
            var destination = await _dataSource.GetDestinationScriptAsync(close.ChannelId, cancellationToken);
            var estimate = await _dataSource.GetFeeratePerKwAsync(cancellationToken);
            var amounts = CollectAmounts(round, map);
            round.Actions.AddRange(await _composer.ComposeAsync(close.ChannelId, needs, round.AllRows, spentBy, height,
                                                                estimate, destination,
                                                                txId => GetFeeAsync(txId, amounts)));
        }

        return round.Actions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(ChannelCloseModel close,
                                                                              OutputResolutionModel output,
                                                                              ChainTx spendingTransaction, uint height,
                                                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(spendingTransaction);

        Remember(output.TransactionId, output.OutputIndex, spendingTransaction, height);

        // Only a commitment output spent by the cheater needs anything now: its second-level output, and a preimage
        if (output.TransactionId != close.CommitmentTransactionId || output.Descriptor != OutputDescriptorKind.RevokedHtlc
         || spendingTransaction.TxId == output.ResolvingTransactionId
         || await _dataSource.IsOurTransactionAsync(spendingTransaction.TxId))
            return [];

        var load = await _dataSource.LoadAsync(close, cancellationToken);
        if (load.Context is not { } context)
            return [new AlertAction("B5-REV-06", $"Channel {close.ChannelId}: {load.Problem}")];

        var descriptor = Map(context).GetOutput(output.OutputIndex);
        if (descriptor?.Htlc is not { } htlc)
            return [];

        var actions = new List<OutputResolverAction>();
        var inputIndex = spendingTransaction.IndexOfInputSpending(output.TransactionId, output.OutputIndex);
        var parsed = HtlcWitnessParser.Parse(inputIndex >= 0 ? spendingTransaction.Inputs[inputIndex].Witness : null);
        if (parsed.Path is HtlcSpendPath.HtlcSuccessTransaction or HtlcSpendPath.HtlcTimeoutTransaction)
        {
            var secondLevel = CreateSecondLevelRow(context, close, output, htlc, spendingTransaction, (uint)inputIndex,
                                                   height, out var alert);
            if (secondLevel is not null)
            {
                actions.Add(new UpsertOutputAction(secondLevel));
                actions.Add(new WatchOutpointAction(new WatchedOutpointModel(
                                secondLevel.TransactionId, secondLevel.OutputIndex, close.ChannelId,
                                WatchedOutpointPurpose.ResolutionOutput)));
            }

            if (alert is not null)
                actions.Add(alert);
        }

        // B5-REV-07, B5-REV-RES-01: a preimage of our offered HTLC fulfills upstream at once
        if (htlc.Direction == HtlcDirection.Outgoing
         && HtlcWitnessParser.TryExtractPreimage(spendingTransaction, output.TransactionId, output.OutputIndex,
                                                 htlc.PaymentHash, out var preimage)
         && !IsUpstreamResolved(context.Channel, htlc))
            Raise(actions, new OutgoingHtlcFulfilled(close.ChannelId, htlc.Id, htlc.PaymentHash, preimage));

        return actions;
    }

    private async Task PlanOutputAsync(Round round, CommitmentOutputMap map, CommitmentOutputDescriptor descriptor,
                                       List<PenaltyNeed> needs, Dictionary<(TxId, uint), TxId> spentBy,
                                       CancellationToken cancellationToken)
    {
        var context = round.Context;
        var commitmentTxId = round.Close.CommitmentTransactionId;
        var row = round.GetOrCreateRow(commitmentTxId, descriptor.Vout,
                                       () => CreateCommitmentRow(round, descriptor, map.PerCommitmentPoint))!;

        var spend = await GetSpendAsync(row, descriptor.Htlc, cancellationToken);
        if (spend is not null)
            spentBy[(row.TransactionId, row.OutputIndex)] = spend.Value.Spend.SpendingTxId;

        // The cheater's HTLC-timeout/success took the output: its second-level output is the one to penalize now
        OutputResolutionModel? secondLevelRow = null;
        OutputSpend? secondLevelSpend = null;
        if (spend is { Spend: { ByUs: false, Path: HtlcSpendPath.HtlcSuccessTransaction or HtlcSpendPath.HtlcTimeoutTransaction } }
                    theirs && descriptor.Htlc is { } htlc)
        {
            var inputIndex = theirs.Transaction.IndexOfInputSpending(commitmentTxId, descriptor.Vout);
            secondLevelRow = round.GetOrCreateRow(theirs.Transaction.TxId, (uint)Math.Max(0, inputIndex), () =>
            {
                var created = CreateSecondLevelRow(context, round.Close, row, htlc, theirs.Transaction,
                                                   (uint)Math.Max(0, inputIndex), theirs.Spend.Height, out var alert);
                if (alert is not null)
                    round.Actions.Add(alert);
                return created;
            });

            if (secondLevelRow is not null)
            {
                var second = await GetSpendAsync(secondLevelRow, null, cancellationToken);
                if (second is not null)
                {
                    secondLevelSpend = second.Value.Spend;
                    spentBy[(secondLevelRow.TransactionId, secondLevelRow.OutputIndex)] = second.Value.Spend.SpendingTxId;
                }
            }
        }

        if (IsFinished(row) && (secondLevelRow is null || IsFinished(secondLevelRow)))
            return;

        var record = descriptor.Htlc is { } spec ? context.Channel.Commitments?.GetHtlc(spec.Direction, spec.Id) : null;
        var facts = new OutputResolutionFacts(round.Height, round.Close.SpentAtHeight, spend?.Spend, secondLevelSpend,
                                              record?.KnownPreimage is { } known ? (byte[])known : null,
                                              UpstreamResolved: descriptor.Htlc is { } h
                                                             && IsUpstreamResolved(context.Channel, h),
                                              SecondLevelCsvDelay: context.Channel.ChannelParams.Local.ToSelfDelay,
                                              ReasonableDepth: _options.ReasonableDepth,
                                              IrrevocableDepth: _options.IrrevocableDepth);

        OutputResolutionPlan plan;
        try
        {
            plan = OutputResolutionPlanner.Plan(descriptor, facts);
        }
        catch (ArgumentException e)
        {
            _logger.LogError(e, "Output {Vout} of revoked commitment {TxId} of channel {ChannelId} cannot be planned",
                             descriptor.Vout, commitmentTxId, round.Close.ChannelId);
            return;
        }

        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case ResolutionActionKind.Sweep when action.OnSecondLevel:
                    if (secondLevelRow is not null && !IsFinished(secondLevelRow)
                     && CreateSecondLevelInput(context, secondLevelRow) is { } secondLevelInput)
                        needs.Add(new PenaltyNeed(secondLevelRow, secondLevelInput,
                                                  secondLevelRow.DeadlineHeight ?? action.DeadlineHeight));
                    break;

                case ResolutionActionKind.Sweep:
                    if (spend is null && !IsFinished(row))
                        needs.Add(new PenaltyNeed(row, CreateInput(context, descriptor),
                                                  GetDeadline(round.Close, descriptor)));
                    break;

                case ResolutionActionKind.RaiseFulfilled when descriptor.Htlc is { } fulfilled
                                                            && action.Preimage is { } preimage:
                    Raise(round.Actions, new OutgoingHtlcFulfilled(round.Close.ChannelId, fulfilled.Id,
                                                                    fulfilled.PaymentHash, new Secret(preimage)));
                    break;

                case ResolutionActionKind.RaiseFailed when descriptor.Htlc is { } failed:
                    Raise(round.Actions, new OutgoingHtlcFailed(round.Close.ChannelId, failed.Id, failed.PaymentHash,
                                                                 OnchainHtlcRemovals.OnchainTimeout()));
                    break;

                case ResolutionActionKind.AlertLostFunds:
                    round.Actions.Add(new AlertAction(action.RequirementId,
                                                      $"Output {descriptor.Vout} of revoked commitment {commitmentTxId} "
                                                    + $"of channel {round.Close.ChannelId} ({descriptor.AmountSat} sat) "
                                                    + "went to the peer"));
                    break;
            }
        }
    }

    /// <summary>
    /// B5-REV-RES-03: our offered HTLCs that are committed but have no output in the revoked commitment.
    /// </summary>
    private void PlanHtlcsWithoutOutput(Round round, HashSet<HtlcKey> mappedHtlcs)
    {
        if (round.Context.Channel.Commitments is not { } commitments)
            return;

        var remoteNext = commitments.RemoteNextCommit?.Commit.Spec.Htlcs;
        foreach (var record in commitments.Htlcs.Values)
        {
            if (record.Direction != HtlcDirection.Outgoing || mappedHtlcs.Contains(record.Key))
                continue;

            var spec = new SpecHtlc(record.Direction, record.Id, record.AmountMsat, record.PaymentHash,
                                    record.CltvExpiry);
            if (IsUpstreamResolved(round.Context.Channel, spec))
                continue;

            var inAnyValidCommitment = record.IsInCommit(CommitmentSide.Local) || record.IsInCommit(CommitmentSide.Remote)
                                    || remoteNext?.Any(h => h.Direction == record.Direction && h.Id == record.Id) == true;
            var plan = OutputResolutionPlanner.PlanHtlcWithoutOutput(
                spec, new HtlcWithoutOutputFacts(round.Height, round.Close.SpentAtHeight,
                                                 record.KnownPreimage is { } known ? (byte[])known : null,
                                                 inAnyValidCommitment, false, _options.ReasonableDepth,
                                                 _options.IrrevocableDepth));
            foreach (var action in plan.Actions)
            {
                if (action is { Kind: ResolutionActionKind.RaiseFulfilled, Preimage: { } preimage })
                    Raise(round.Actions, new OutgoingHtlcFulfilled(round.Close.ChannelId, spec.Id, spec.PaymentHash,
                                                                    new Secret(preimage)));
                else if (action.Kind == ResolutionActionKind.RaiseFailed)
                    Raise(round.Actions, new OutgoingHtlcFailed(round.Close.ChannelId, spec.Id, spec.PaymentHash,
                                                                 OnchainHtlcRemovals.OnchainTimeout()));
            }
        }
    }

    private CommitmentOutputMap Map(RevokedCommitContext context)
    {
        var channel = context.Channel;
        var spec = context.LogEntry is { } entry
                       ? CommitmentTxSpec.FromCommitmentSpec(entry.Spec)
                       : WithoutHtlcs(channel);
        return _mapper.Map(channel, spec, CommitmentCase.Revoked, context.Number, context.PerCommitmentPoint,
                           context.CommitmentTransaction);
    }

    /// <summary>
    /// A spec to find the <c>to_local</c> and <c>to_remote</c> of a commitment without a log entry by script: their
    /// scripts do not depend on the amounts, so both balances are set high enough that neither output is trimmed.
    /// </summary>
    private static CommitmentTxSpec WithoutHtlcs(ChannelModel channel)
    {
        var halfMsat = channel.FundingOutput?.Amount.MilliSatoshi / 2 ?? 0;
        return new CommitmentTxSpec(halfMsat, halfMsat, (ulong)channel.ChannelParams.FeeRateAmountPerKw.Satoshi);
    }

    private void ReportUnmapped(Round round, CommitmentOutputMap map)
    {
        if (map.UnmappedVouts.Count == 0)
            return;

        var vouts = string.Join(", ", map.UnmappedVouts);
        var why = round.Context.PredatesLog
                      ? $"commitment {round.Context.Number} was revoked before the revocation log started at "
                      + $"{round.Context.LogStart}; its HTLC outputs cannot be rebuilt (pre-O1 state)"
                      : "they match no output of the rebuilt commitment";
        round.Actions.Add(new AlertAction("B5-REV-04",
                                          $"Revoked commitment {round.Close.CommitmentTransactionId} of channel "
                                        + $"{round.Close.ChannelId}: outputs {vouts} are not penalized: {why}"));
    }

    private SweepInput CreateInput(RevokedCommitContext context, CommitmentOutputDescriptor descriptor)
    {
        var txId = context.CommitmentTransaction.TxId;
        if (descriptor.Kind == OutputDescriptorKind.PaymentToRemote)
            return SweepInputFactory.ToRemote(descriptor, txId,
                                              context.Channel.LocalKeySet.PaymentCompactBasepoint);

        return SweepInputFactory.Penalty(descriptor, txId, context.PerCommitmentSecret, RevocationPubKey(context));
    }

    private SweepInput? CreateSecondLevelInput(RevokedCommitContext context, OutputResolutionModel row)
    {
        var data = OutputDescriptorData.TryDecode(row);
        if (data?.WitnessScript is not { } witnessScript)
            return null;

        return SweepInputFactory.SecondLevelPenalty(row.TransactionId, data.AmountSat, witnessScript,
                                                    context.PerCommitmentSecret) with
        { Vout = row.OutputIndex };
    }

    /// <summary>
    /// The row of the cheater's second-level output: its script is the <c>to_local</c> form with the revocation key and
    /// the cheater's delayed key at the revoked point, delayed by the <c>to_self_delay</c> we imposed
    /// (<c>ChannelParams.Local.ToSelfDelay</c>), so the penalty is <c>&lt;revocation_sig&gt; 1</c> (B5-REV-06).
    /// </summary>
    private OutputResolutionModel? CreateSecondLevelRow(RevokedCommitContext context, ChannelCloseModel close,
                                                        OutputResolutionModel parent, SpecHtlc htlc,
                                                        ChainTx htlcTransaction, uint vout, uint height,
                                                        out AlertAction? alert)
    {
        alert = null;
        if (vout >= htlcTransaction.Outputs.Count)
        {
            alert = new AlertAction("B5-REV-06",
                                    $"HTLC transaction {htlcTransaction.TxId} of channel {close.ChannelId} has no "
                                  + $"output {vout}; its second level cannot be penalized");
            return null;
        }

        var output = htlcTransaction.Outputs[(int)vout];
        var toSelfDelay = context.Channel.ChannelParams.Local.ToSelfDelay;
        var theirDelayedKey = _keyDerivationService.DerivePublicKey(
            context.Channel.RemoteKeySet!.DelayedPaymentCompactBasepoint, context.PerCommitmentPoint);
        var script = new HtlcResolutionOutput(LightningMoney.Satoshis(output.AmountSat), new PubKey(theirDelayedKey),
                                              new PubKey(RevocationPubKey(context)), toSelfDelay);
        var scriptPubKey = (byte[])script.BitcoinScriptPubKey;
        if (!scriptPubKey.AsSpan().SequenceEqual(output.ScriptPubKey))
        {
            alert = new AlertAction("B5-REV-06",
                                    $"Output {vout} of HTLC transaction {htlcTransaction.TxId} of channel "
                                  + $"{close.ChannelId} is not the expected second-level script; not penalized");
            return null;
        }

        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "Channel {ChannelId}: the cheater's HTLC transaction {TxId} spent revoked output {Vout}; penalizing its "
              + "output before block {Deadline} (B5-REV-06)", close.ChannelId, htlcTransaction.TxId,
                parent.OutputIndex, height + toSelfDelay);

        var data = new OutputDescriptorData(output.AmountSat, scriptPubKey, (byte[])script.RedeemBitcoinScript, toSelfDelay,
                                            context.Channel.ChannelParams.OptionAnchorOutputs,
                                            context.PerCommitmentPoint, htlc);
        return new OutputResolutionModel
        {
            TransactionId = htlcTransaction.TxId,
            OutputIndex = vout,
            ChannelId = close.ChannelId,
            Descriptor = OutputDescriptorKind.RevokedSecondLevel,
            DescriptorData = data.Encode(),
            HtlcDirection = htlc.Direction,
            HtlcId = htlc.Id,
            State = OutputResolutionState.Pending,
            DeadlineHeight = height + toSelfDelay
        };
    }

    private OutputResolutionModel CreateCommitmentRow(Round round, CommitmentOutputDescriptor descriptor,
                                                      CompactPubKey point) =>
        new()
        {
            TransactionId = round.Close.CommitmentTransactionId,
            OutputIndex = descriptor.Vout,
            ChannelId = round.Close.ChannelId,
            Descriptor = descriptor.Kind,
            DescriptorData = OutputDescriptorData.FromDescriptor(descriptor, point).Encode(),
            HtlcDirection = descriptor.Htlc?.Direction,
            HtlcId = descriptor.Htlc?.Id,
            State = OutputResolutionState.Pending,
            DeadlineHeight = GetDeadline(round.Close, descriptor)
        };

    /// <summary>
    /// The height from which the cheater can take a revoked output (BOLT 5 "expiry", the split trigger of B5-REV-08):
    /// its <c>to_local</c> once its CSV expires, an HTLC output at its <c>cltv_expiry</c>. The cheater can also race our
    /// offered HTLC with its HTLC-success at any time, but that race only moves the funds to a second-level output that
    /// is penalized in turn before <c>to_self_delay</c> (plan §3.7), so the batch is split on the expiry only.
    /// </summary>
    private static uint? GetDeadline(ChannelCloseModel close, CommitmentOutputDescriptor descriptor) =>
        descriptor.Kind switch
        {
            OutputDescriptorKind.RevokedToLocal => close.SpentAtHeight + descriptor.CsvDelay,
            OutputDescriptorKind.RevokedHtlc => descriptor.Htlc?.CltvExpiry,
            _ => null
        };

    private CompactPubKey RevocationPubKey(RevokedCommitContext context) =>
        _keyDerivationService.DeriveRevocationPubKey(context.Channel.LocalKeySet.RevocationCompactBasepoint,
                                                     context.PerCommitmentPoint);

    /// <summary>The recorded spend of an output, with the path and preimage its witness shows for an HTLC.</summary>
    private async Task<(OutputSpend Spend, ChainTx Transaction)?> GetSpendAsync(OutputResolutionModel? row,
                                                                               SpecHtlc? htlc,
                                                                               CancellationToken cancellationToken)
    {
        if (row is null)
            return null;

        ChainTx transaction;
        uint height;
        bool byUs;
        if (_seenSpends.TryGetValue((row.TransactionId, row.OutputIndex), out var seen))
        {
            (transaction, height) = seen;
            byUs = transaction.TxId == row.ResolvingTransactionId
                || await _dataSource.IsOurTransactionAsync(transaction.TxId);
        }
        else if (await _dataSource.GetSpendAsync(row.TransactionId, row.OutputIndex, cancellationToken) is { } recorded)
        {
            transaction = recorded.SpendingTransaction;
            height = recorded.Height;
            byUs = recorded.ByUs || transaction.TxId == row.ResolvingTransactionId;
        }
        else if (row is
        {
            State: OutputResolutionState.Resolved or OutputResolutionState.Irrevocable,
            ResolvedHeight: { } resolvedHeight
        })
        {
            // The executor saw a spend we cannot look up: ours if we had a transaction for it
            var spender = row.ResolvingTransactionId ?? new TxId(new byte[32]);
            return (new OutputSpend(spender, resolvedHeight, row.ResolvingTransactionId is not null,
                                    row.ResolvingTransactionId is not null
                                        ? HtlcSpendPath.Revocation
                                        : HtlcSpendPath.Unknown), new ChainTx(spender, 2, 0, [], []));
        }
        else
        {
            return null;
        }

        var path = HtlcSpendPath.Unknown;
        byte[]? preimage = null;
        if (htlc is { } spec)
        {
            var index = transaction.IndexOfInputSpending(row.TransactionId, row.OutputIndex);
            path = HtlcWitnessParser.Parse(index >= 0 ? transaction.Inputs[index].Witness : null).Path;
            if (HtlcWitnessParser.TryExtractPreimage(transaction, row.TransactionId, row.OutputIndex, spec.PaymentHash,
                                                     out var found))
                preimage = found;
        }

        return (new OutputSpend(transaction.TxId, height, byUs, path, preimage), transaction);
    }

    /// <summary>
    /// The fee a stored transaction of ours pays: its inputs' amounts (known from the rows and the mapped commitment)
    /// minus its outputs.
    /// </summary>
    private async Task<ulong?> GetFeeAsync(TxId transactionId, IReadOnlyDictionary<(TxId, uint), ulong> amounts)
    {
        var stored = await _dataSource.GetBroadcastAsync(transactionId);
        if (stored is null)
            return null;

        try
        {
            var transaction = Transaction.Load(stored.RawTransaction, Network.Main);
            var outputs = (ulong)transaction.Outputs.Sum(o => o.Value.Satoshi);
            ulong inputs = 0;
            foreach (var input in transaction.Inputs)
            {
                if (!amounts.TryGetValue((new TxId(input.PrevOut.Hash.ToBytes()), input.PrevOut.N), out var amount))
                    return null;
                inputs += amount;
            }

            return inputs > outputs ? inputs - outputs : 0;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>The amount of every output of the round: the mapped commitment's and the rows' (second level).</summary>
    private static Dictionary<(TxId, uint), ulong> CollectAmounts(Round round, CommitmentOutputMap map)
    {
        var amounts = new Dictionary<(TxId, uint), ulong>();
        foreach (var row in round.AllRows)
        {
            if (OutputDescriptorData.TryDecode(row) is { } data)
                amounts[(row.TransactionId, row.OutputIndex)] = data.AmountSat;
        }

        foreach (var output in map.Outputs)
            amounts[(round.Close.CommitmentTransactionId, output.Vout)] = output.AmountSat;

        return amounts;
    }

    /// <summary>
    /// Asks for an upstream event once per process (the switch is idempotent, and a restart asks again): the planner
    /// repeats its decision every block until the output is irrevocably resolved.
    /// </summary>
    private void Raise(List<OutputResolverAction> actions, IChannelDomainEvent channelEvent)
    {
        var key = channelEvent switch
        {
            OutgoingHtlcFulfilled fulfilled => (fulfilled.ChannelId, fulfilled.HtlcId, true),
            OutgoingHtlcFailed failed => (failed.ChannelId, failed.HtlcId, false),
            _ => throw new ArgumentOutOfRangeException(nameof(channelEvent), channelEvent, null)
        };

        // A fulfill wins over a failure asked for earlier in this process (a preimage is final knowledge)
        if (_raised.ContainsKey((key.ChannelId, key.HtlcId, true)) || !_raised.TryAdd(key, 0))
            return;

        actions.Add(new RaiseChannelEventAction(channelEvent));
    }

    private void Remember(TxId txId, uint vout, ChainTx spendingTransaction, uint height)
    {
        if (_seenSpends.Count >= MaxCachedSpends)
            _seenSpends.Clear();
        _seenSpends[(txId, vout)] = (spendingTransaction, height);
    }

    private static bool IsFinished(OutputResolutionModel row) =>
        row.State is OutputResolutionState.Irrevocable or OutputResolutionState.Ignored;

    /// <summary>
    /// True when the upstream side of our offered HTLC was already settled off chain: the HTLC left the channel's
    /// state (its removal is irrevocable, the engine raised the event) or its failure is irrevocably committed.
    /// </summary>
    private static bool IsUpstreamResolved(ChannelModel channel, SpecHtlc htlc)
    {
        if (htlc.Direction != HtlcDirection.Outgoing)
            return true;

        var record = channel.Commitments?.GetHtlc(htlc.Direction, htlc.Id);
        return record is null
            || record is { State: HtlcState.RcvdRemoveAckRevocation, Removal.IsFulfill: false };
    }

    /// <summary>The state of one round: the rows (with the ones created this round) and the actions.</summary>
    private sealed class Round
    {
        private readonly List<OutputResolutionModel> _rows;

        public RevokedCommitContext Context { get; }
        public ChannelCloseModel Close { get; }
        public uint Height { get; }
        public List<OutputResolverAction> Actions { get; } = [];
        public IReadOnlyList<OutputResolutionModel> AllRows => _rows;

        public Round(RevokedCommitContext context, ChannelCloseModel close,
                     IReadOnlyList<OutputResolutionModel> rows, uint height)
        {
            Context = context;
            Close = close;
            Height = height;
            _rows = rows.ToList();
        }

        /// <summary>The row of an outpoint; a missing one is created (staged with a watch).</summary>
        public OutputResolutionModel? GetOrCreateRow(TxId txId, uint vout, Func<OutputResolutionModel?> create)
        {
            var row = _rows.FirstOrDefault(r => r.TransactionId == txId && r.OutputIndex == vout);
            if (row is not null)
                return row;

            row = create();
            if (row is null)
                return null;

            _rows.Add(row);
            Actions.Add(new UpsertOutputAction(row));
            Actions.Add(new WatchOutpointAction(new WatchedOutpointModel(row.TransactionId, row.OutputIndex,
                                                                         row.ChannelId,
                                                                         WatchedOutpointPurpose.ResolutionOutput)));
            return row;
        }
    }
}