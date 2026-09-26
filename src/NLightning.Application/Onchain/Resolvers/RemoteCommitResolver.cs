using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Onchain.Resolvers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Fees;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;
using Domain.Onchain.Planners;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Remote;

/// <summary>
/// BOLT 5 plan O4-T1..T3: resolves a peer commitment on chain (current, next or future) with the W4 pieces:
/// <see cref="ICommitmentOutputMapper"/> (descriptors, keyed by the peer's per-commitment point of that commitment),
/// <see cref="OutputResolutionPlanner"/> (the B5-RMT-* rows), <see cref="SweepInputFactory"/>,
/// <see cref="ISweepTransactionBuilder"/> and <see cref="ILightningSigner.SignSweepInput"/>. See
/// <see cref="IRemoteCommitResolver"/> for the contract with the watcher.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>to_remote</c> (ours, D5, B5-RMT-02): swept at once to a wallet address, also after data loss (B5-RMT-03,
/// static_remotekey: <c>payment_basepoint</c>, no point needed).</item>
/// <item>HTLCs we offered (received outputs there, B5-RMT-LO-*): claimed with <c>&lt;sig&gt; &lt;&gt;</c> and
/// <c>nLockTime = cltv_expiry</c> once the tip reaches it; the peer's HTLC-success reveals the preimage (fulfilled
/// upstream at once, saved first); our confirmed timeout claim fails the HTLC upstream once reasonably deep.</item>
/// <item>HTLCs the peer offered (offered outputs there, B5-RMT-RO-*): claimed with <c>&lt;sig&gt; &lt;preimage&gt;</c>
/// before <c>cltv_expiry</c>, only with our own fulfill's preimage for that HTLC (the switch accepted it as final hop or
/// the downstream fulfilled it; never an invoice preimage alone, B5-LCL-RO-02) and only when the peer is irrevocably
/// committed to it (B5-RMT-RO-02).</item>
/// <item>HTLCs we offered without an output in the commitment on chain (trimmed, not in it yet, or removed; B5-RMT-LO-03):
/// fulfilled upstream at once with a known preimage, else failed once the commitment is reasonably deep, or at once
/// when no valid commitment has an output for it.</item>
/// </list>
/// Each output gets its own transaction (timeout claims can never share one with other spends, and one transaction per
/// output keeps the persisted row, its resolving txid and the chain monitor's rebroadcast one to one). Fee bumps are
/// the sweep scheduler's (O6).
/// </remarks>
public sealed class RemoteCommitResolver : IRemoteCommitResolver
{
    private readonly IChainBroadcaster _broadcaster;
    private readonly IRemoteSweepDestination _destination;
    private readonly IFeeService _feeService;
    private readonly SweepFeePolicy _feePolicy;
    private readonly IHtlcSwitch _htlcSwitch;
    private readonly ILogger<RemoteCommitResolver> _logger;
    private readonly ICommitmentOutputMapper _mapper;
    private readonly RemoteResolutionMemory _memory;
    private readonly RemoteResolutionOptions _options;
    private readonly IOutpointWatcher _outpointWatcher;
    private readonly ILightningSigner _signer;
    private readonly ISweepTransactionBuilder _sweepBuilder;

    public RemoteCommitResolver(ICommitmentOutputMapper mapper, ISweepTransactionBuilder sweepBuilder,
                                ILightningSigner signer, IFeeService feeService, IRemoteSweepDestination destination,
                                IChainBroadcaster broadcaster, IOutpointWatcher outpointWatcher,
                                IHtlcSwitch htlcSwitch, RemoteResolutionMemory memory,
                                IOptions<RemoteResolutionOptions>? options = null,
                                ILogger<RemoteCommitResolver>? logger = null, SweepFeePolicy? feePolicy = null)
    {
        _mapper = mapper;
        _sweepBuilder = sweepBuilder;
        _signer = signer;
        _feeService = feeService;
        _destination = destination;
        _broadcaster = broadcaster;
        _outpointWatcher = outpointWatcher;
        _htlcSwitch = htlcSwitch;
        _memory = memory;
        _options = options?.Value ?? new RemoteResolutionOptions();
        _logger = logger ?? NullLogger<RemoteCommitResolver>.Instance;
        _feePolicy = feePolicy ?? new SweepFeePolicy();
    }

    /// <inheritdoc />
    public bool CanResolve(ChannelCloseKind kind) =>
        kind is ChannelCloseKind.RemoteCommitment or ChannelCloseKind.RemoteNextCommitment
            or ChannelCloseKind.FutureCommitment;

    /// <inheritdoc />
    public async Task<RemoteResolutionRound> BeginAsync(ChannelModel channel, ChannelCloseModel close,
                                                        ChainTx commitmentTransaction, uint tipHeight,
                                                        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(commitmentTransaction);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        RequireRemoteClose(channel, close);
        if (commitmentTransaction.TxId != close.CommitmentTransactionId)
            throw new ArgumentException("The commitment transaction is not the recorded funding spend",
                                        nameof(commitmentTransaction));

        var round = new RemoteResolutionRound();
        var (descriptors, point) = MapOurOutputs(channel, close, commitmentTransaction, round);

        var rows = new Dictionary<uint, OutputResolutionModel>();
        foreach (var row in await LoadRowsAsync(channel, close, unitOfWork))
            rows[row.OutputIndex] = row;

        var repository = unitOfWork.OnchainResolutionDbRepository;
        foreach (var descriptor in descriptors)
        {
            if (!rows.ContainsKey(descriptor.Vout))
            {
                var row = new OutputResolutionModel
                {
                    TransactionId = close.CommitmentTransactionId,
                    OutputIndex = descriptor.Vout,
                    ChannelId = channel.ChannelId,
                    Descriptor = descriptor.Kind,
                    DescriptorData = RemoteOutputData.FromDescriptor(descriptor, point).Encode(),
                    HtlcDirection = descriptor.Htlc?.Direction,
                    HtlcId = descriptor.Htlc?.Id,
                    State = OutputResolutionState.Pending
                };
                await repository.UpsertOutputAsync(row);
                rows[descriptor.Vout] = row;
            }

            var watches = unitOfWork.WatchedOutpointDbRepository;
            if (await watches.GetAsync(close.CommitmentTransactionId, descriptor.Vout) is null)
            {
                var watch = new WatchedOutpointModel(close.CommitmentTransactionId, descriptor.Vout, channel.ChannelId,
                                                     WatchedOutpointPurpose.ResolutionOutput);
                watches.Add(watch);
                round.Watches.Add(watch);
            }
        }

        _logger.LogInformation("Channel {ChannelId}: the peer's commitment {Kind} (number {Number}) confirmed at {Height}; "
                             + "{Count} outputs to resolve", channel.ChannelId, close.Kind, close.CommitmentNumber,
                               close.SpentAtHeight, descriptors.Count);

        await ResolveRowsAsync(channel, close, rows.Values.OrderBy(r => r.OutputIndex).ToList(), tipHeight,
                               unitOfWork, round, cancellationToken);
        return round;
    }

    /// <inheritdoc />
    public async Task<RemoteResolutionRound> OnOutputSpentAsync(ChannelModel channel, ChannelCloseModel close,
                                                                TxId spentTxId, uint spentOutputIndex,
                                                                ChainTx spender, uint spendHeight, uint tipHeight,
                                                                IUnitOfWork unitOfWork,
                                                                CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spender);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        RequireRemoteClose(channel, close);

        var round = new RemoteResolutionRound();
        if (spentTxId != close.CommitmentTransactionId)
            return round;

        var row = await unitOfWork.OnchainResolutionDbRepository.GetOutputAsync(spentTxId, spentOutputIndex);
        if (row is null || row.ChannelId != channel.ChannelId || spender.IndexOfInputSpending(spentTxId,
                spentOutputIndex) < 0)
            return round;

        if (!TryDecode(row, out var data))
            return round;

        var byUs = row.ResolvingTransactionId is { } resolving && resolving == spender.TxId;
        if (!byUs)
        {
            var ours = await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(spender.TxId);
            byUs = ours is not null && ours.ChannelId == channel.ChannelId;
        }

        var path = HtlcSpendPath.Unknown;
        byte[]? preimage = null;
        if (data.Htlc is { } htlc)
        {
            var input = spender.Inputs[spender.IndexOfInputSpending(spentTxId, spentOutputIndex)];
            path = HtlcWitnessParser.Parse(input.Witness).Path;
            if (HtlcWitnessParser.TryExtractPreimage(input.Witness, htlc.PaymentHash, out var revealed))
                preimage = revealed;
        }

        var spend = new RemoteRecordedSpend(spender.TxId, spendHeight, byUs, path, preimage);
        if (!SameSpend(data.Spend, spend))
        {
            row = row with { DescriptorData = (data with { Spend = spend }).Encode() };
            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(row);
            round.Outputs.Add(row);
            if (preimage is not null)
                _logger.LogInformation("Channel {ChannelId}: the peer's spend of output {Vout} revealed the preimage of "
                                     + "HTLC {HtlcId}", channel.ChannelId, spentOutputIndex, data.Htlc?.Id);
        }

        var rows = await LoadRowsAsync(channel, close, unitOfWork);
        var merged = rows.Select(r => r.OutputIndex == row.OutputIndex ? row : r).ToList();
        await ResolveRowsAsync(channel, close, merged, tipHeight, unitOfWork, round, cancellationToken);
        return round;
    }

    /// <inheritdoc />
    public async Task<RemoteResolutionRound> ResolveAsync(ChannelModel channel, ChannelCloseModel close,
                                                          uint tipHeight, IUnitOfWork unitOfWork,
                                                          CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        RequireRemoteClose(channel, close);

        var round = new RemoteResolutionRound();
        if (!CanRebuild(channel, close))
            FlagDataLoss(channel, close, round);

        var rows = await LoadRowsAsync(channel, close, unitOfWork);
        await ResolveRowsAsync(channel, close, rows, tipHeight, unitOfWork, round, cancellationToken);
        return round;
    }

    /// <inheritdoc />
    public async Task CompleteAsync(RemoteResolutionRound round, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(round);

        foreach (var watch in round.Watches)
            _outpointWatcher.TrackWatchedOutpoint(watch);

        foreach (var broadcast in round.Broadcasts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await _broadcaster.PublishAsync(broadcast))
                    _logger.LogWarning("Sweep {TxId} of channel {ChannelId} was refused; it is rebroadcast after the next "
                                     + "block", broadcast.TransactionId, broadcast.ChannelId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "Could not publish sweep {TxId} of channel {ChannelId}; it stays pending",
                                 broadcast.TransactionId, broadcast.ChannelId);
            }
        }

        foreach (var channelEvent in round.SwitchEvents)
        {
            try
            {
                await _htlcSwitch.HandleAsync(channelEvent, cancellationToken);
                _memory.MarkRaised(channelEvent.ChannelId, channelEvent.HtlcId);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogError(e, "The switch failed on {Event} of HTLC {HtlcId} (channel {ChannelId}); it is raised "
                                  + "again on the next block", channelEvent.GetType().Name, channelEvent.HtlcId,
                                 channelEvent.ChannelId);
            }
        }
    }

    /// <summary>
    /// The outputs of the commitment on chain that are ours to resolve (to_remote, both kinds of HTLC outputs), and the
    /// peer's per-commitment point of that commitment (null when it cannot be rebuilt).
    /// </summary>
    private (IReadOnlyList<CommitmentOutputDescriptor> Descriptors, CompactPubKey? Point) MapOurOutputs(
        ChannelModel channel, ChannelCloseModel close, ChainTx commitmentTransaction, RemoteResolutionRound round)
    {
        if (!TryGetRemoteCommit(channel, close, out var commit))
        {
            FlagDataLoss(channel, close, round);
            var toRemote = _mapper.FindPaymentToRemote(commitmentTransaction,
                                                       channel.LocalKeySet.PaymentCompactBasepoint,
                                                       channel.ChannelParams.OptionAnchorOutputs);
            return (toRemote, null);
        }

        var map = _mapper.Map(channel, CommitmentTxSpec.FromCommitmentSpec(commit.Spec), CommitmentCase.Remote,
                              commit.Number, commit.PerCommitmentPoint, commitmentTransaction);
        if (map.UnmappedVouts.Count > 0)
        {
            var alert = $"B5-RMT-03: outputs {string.Join(", ", map.UnmappedVouts)} of the peer's commitment "
                      + $"{close.CommitmentNumber} match no expected output";
            round.Alerts.Add(alert);
            _logger.LogCritical("Channel {ChannelId}: {Alert}", channel.ChannelId, alert);
        }

        var ours = map.Outputs.Where(o => o.Kind is OutputDescriptorKind.PaymentToRemote
                                              or OutputDescriptorKind.RemoteReceivedHtlc
                                              or OutputDescriptorKind.RemoteOfferedHtlc)
                      .ToList();
        return (ours, commit.PerCommitmentPoint);
    }

    private async Task ResolveRowsAsync(ChannelModel channel, ChannelCloseModel close,
                                        IReadOnlyList<OutputResolutionModel> rows, uint tipHeight,
                                        IUnitOfWork unitOfWork, RemoteResolutionRound round,
                                        CancellationToken cancellationToken)
    {
        var allDone = true;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.State is OutputResolutionState.Irrevocable or OutputResolutionState.Ignored)
                continue;

            if (!TryDecode(row, out var data))
            {
                allDone = false;
                continue;
            }

            var descriptor = data.ToDescriptor(row.OutputIndex, row.Descriptor);
            OutputResolutionPlan plan;
            try
            {
                plan = OutputResolutionPlanner.Plan(descriptor, BuildFacts(channel, close, data, tipHeight));
            }
            catch (ArgumentException e)
            {
                _logger.LogError(e, "Channel {ChannelId}: cannot plan output {Vout}", channel.ChannelId,
                                 row.OutputIndex);
                allDone = false;
                continue;
            }

            var updated = await ApplyAsync(channel, close, row, data, descriptor, plan, tipHeight, unitOfWork, round,
                                           cancellationToken);
            if (updated.State is not (OutputResolutionState.Irrevocable or OutputResolutionState.Ignored))
                allDone = false;
        }

        if (!ResolveHtlcsWithoutOutput(channel, close, rows, tipHeight, round))
            allDone = false;

        round.AllIrrevocablyResolved = allDone;
    }

    /// <summary>Acts on the planner's answer for one output and writes the row when it changed.</summary>
    private async Task<OutputResolutionModel> ApplyAsync(ChannelModel channel, ChannelCloseModel close,
                                                         OutputResolutionModel row, RemoteOutputData data,
                                                         CommitmentOutputDescriptor descriptor,
                                                         OutputResolutionPlan plan, uint tipHeight,
                                                         IUnitOfWork unitOfWork, RemoteResolutionRound round,
                                                         CancellationToken cancellationToken)
    {
        var updated = row;
        var updatedData = data;
        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case ResolutionActionKind.Wait:
                    if (updated.State is OutputResolutionState.Pending or OutputResolutionState.Waiting)
                        updated = updated with
                        {
                            State = OutputResolutionState.Waiting,
                            WaitUntilHeight = action.WaitUntilHeight
                        };
                    break;

                case ResolutionActionKind.Sweep:
                    // Already saved for broadcast and not spent yet: the chain monitor rebroadcasts it every block
                    if (updated is { State: OutputResolutionState.Broadcast, ResolvingTransactionId: not null }
                     && data.Spend is null)
                        break;

                    updated = await SweepAsync(channel, close, updated, descriptor, updatedData, action, tipHeight,
                                               unitOfWork, round, cancellationToken);
                    break;

                case ResolutionActionKind.RaiseFulfilled when descriptor.Htlc is { } htlc && action.Preimage is { } p:
                    round.SwitchEvents.Add(RemoteHtlcSwitchEvents.Fulfilled(channel.ChannelId, htlc.Id,
                                                                            htlc.PaymentHash, new Secret(p)));
                    updatedData = updatedData with { UpstreamRaised = true };
                    _logger.LogInformation("Channel {ChannelId}: fulfilling upstream of HTLC {HtlcId} ({Requirement})",
                                           channel.ChannelId, htlc.Id, action.RequirementId);
                    break;

                case ResolutionActionKind.RaiseFailed when descriptor.Htlc is { } htlc:
                    round.SwitchEvents.Add(RemoteHtlcSwitchEvents.OnchainTimeout(channel.ChannelId, htlc.Id,
                                                                                 htlc.PaymentHash));
                    updatedData = updatedData with { UpstreamRaised = true };
                    _logger.LogInformation("Channel {ChannelId}: failing upstream of HTLC {HtlcId} ({Requirement})",
                                           channel.ChannelId, htlc.Id, action.RequirementId);
                    break;

                case ResolutionActionKind.AlertLostFunds:
                    var alert = $"{action.RequirementId}: output {row.OutputIndex} ({row.Descriptor}, "
                              + $"{descriptor.AmountSat} sat) was taken by the peer";
                    round.Alerts.Add(alert);
                    _logger.LogCritical("Channel {ChannelId}: {Alert}", channel.ChannelId, alert);
                    break;
            }
        }

        if (updated.State != OutputResolutionState.Ignored)
        {
            var resolvedHeight = updatedData.Spend?.Height ?? updated.ResolvedHeight;
            updated = plan.State switch
            {
                PlannedResolutionState.Resolved => updated with
                {
                    State = OutputResolutionState.Resolved,
                    ResolvedHeight = resolvedHeight,
                    WaitUntilHeight = null
                },
                PlannedResolutionState.IrrevocablyResolved => updated with
                {
                    State = OutputResolutionState.Irrevocable,
                    ResolvedHeight = resolvedHeight,
                    WaitUntilHeight = null
                },
                _ => updated
            };
        }

        updated = updated with { DescriptorData = updatedData.Encode() };
        if (!SameRow(row, updated))
        {
            await unitOfWork.OnchainResolutionDbRepository.UpsertOutputAsync(updated);
            round.Outputs.Add(updated);
        }

        return updated;
    }

    /// <summary>Builds, signs and stages one sweep or claim of the output (D4: saved with the row, published after).
    /// </summary>
    private async Task<OutputResolutionModel> SweepAsync(ChannelModel channel, ChannelCloseModel close,
                                                         OutputResolutionModel row,
                                                         CommitmentOutputDescriptor descriptor, RemoteOutputData data,
                                                         ResolutionAction action, uint tipHeight,
                                                         IUnitOfWork unitOfWork, RemoteResolutionRound round,
                                                         CancellationToken cancellationToken)
    {
        try
        {
            var commitmentTxId = close.CommitmentTransactionId;
            var input = action.SpendKind switch
            {
                SweepSpendKind.PaymentToRemote => SweepInputFactory.ToRemote(
                    descriptor, commitmentTxId, channel.LocalKeySet.PaymentCompactBasepoint),
                SweepSpendKind.HtlcTimeoutClaim => SweepInputFactory.HtlcTimeoutClaim(
                    descriptor, commitmentTxId, RequirePoint(data)),
                SweepSpendKind.HtlcPreimageClaim => SweepInputFactory.HtlcPreimageClaim(
                    descriptor, commitmentTxId, RequirePoint(data),
                    action.Preimage ?? throw new InvalidOperationException("A preimage claim without its preimage")),
                _ => throw new InvalidOperationException($"A {action.SpendKind} spend is not a remote-commitment spend")
            };

            var destination = await _destination.GetScriptAsync(channel.ChannelId, cancellationToken);
            var weight = SweepWeights.EstimateTransactionWeight([input], [destination.Length]);
            var estimate = await _feeService.GetFeeRatePerKwAsync(cancellationToken);
            var decision = _feePolicy.Decide(input.AmountSat, weight, (uint)Math.Clamp(estimate.Satoshi, 0, uint.MaxValue),
                                             false, tipHeight, action.DeadlineHeight);
            if (decision.Abandon)
            {
                _logger.LogWarning("Channel {ChannelId}: output {Vout} ({Amount} sat) does not pay its own sweep fee; "
                                 + "abandoned", channel.ChannelId, row.OutputIndex, input.AmountSat);
                return row with { State = OutputResolutionState.Ignored, WaitUntilHeight = null };
            }

            var unsigned = _sweepBuilder.BuildWithFee([input], destination, decision.FeeSat);
            var signed = _sweepBuilder.Sign(unsigned, _signer, channel.ChannelId);
            var purpose = input.SpendKind == SweepSpendKind.PaymentToRemote
                              ? BroadcastPurpose.Sweep
                              : BroadcastPurpose.HtlcClaim;
            var broadcast = new BroadcastTransactionModel(signed, purpose, channel.ChannelId, tipHeight,
                                                          decision.FeeratePerKw);
            if (await unitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(signed.TxId) is null)
                unitOfWork.BroadcastTransactionDbRepository.Add(broadcast);
            round.Broadcasts.Add(broadcast);

            _logger.LogInformation("Channel {ChannelId}: {Kind} of output {Vout} ({Requirement}) in {TxId}, fee {Fee} sat",
                                   channel.ChannelId, input.SpendKind, row.OutputIndex, action.RequirementId,
                                   signed.TxId, decision.FeeSat);
            return row with
            {
                State = OutputResolutionState.Broadcast,
                ResolvingTransactionId = signed.TxId,
                DeadlineHeight = action.DeadlineHeight,
                WaitUntilHeight = null
            };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException
                                      or Domain.Exceptions.SignerException)
        {
            // Nothing staged: the planner asks again on the next block
            _logger.LogError(e, "Channel {ChannelId}: cannot build the {Kind} of output {Vout}", channel.ChannelId,
                             action.SpendKind, row.OutputIndex);
            return row;
        }
    }

    /// <summary>
    /// B5-RMT-LO-03: our offered HTLCs the engine still tracks that have no output in the commitment on chain. Returns
    /// true when all of them are irrevocably resolved.
    /// </summary>
    private bool ResolveHtlcsWithoutOutput(ChannelModel channel, ChannelCloseModel close,
                                           IReadOnlyList<OutputResolutionModel> rows, uint tipHeight,
                                           RemoteResolutionRound round)
    {
        if (channel.Commitments is not { } commitments || !CanRebuild(channel, close))
            return true;

        var withOutput = rows.Where(r => r.HtlcDirection == HtlcDirection.Outgoing && r.HtlcId.HasValue)
                             .Select(r => r.HtlcId!.Value)
                             .ToHashSet();
        var allDone = true;
        foreach (var record in commitments.Htlcs.Values)
        {
            if (record.Direction != HtlcDirection.Outgoing || withOutput.Contains(record.Id)
             || HtlcStateTable.IsFinal(record.State))
                continue;

            var htlc = new SpecHtlc(record.Direction, record.Id, record.AmountMsat, record.PaymentHash,
                                    record.CltvExpiry);
            var preimage = OutgoingPreimage(record);
            var upstreamResolved = _memory.WasRaised(channel.ChannelId, record.Id);
            var depth = tipHeight >= close.SpentAtHeight ? tipHeight - close.SpentAtHeight + 1 : 0;
            var elsewhere = preimage is null && !upstreamResolved && depth < _options.ReasonableDepth
                         && HasOutputInAnotherCommitment(channel, close, record.Id);
            var plan = OutputResolutionPlanner.PlanHtlcWithoutOutput(
                htlc, new HtlcWithoutOutputFacts(tipHeight, close.SpentAtHeight, preimage, elsewhere, upstreamResolved,
                                                 _options.ReasonableDepth, _options.IrrevocableDepth));
            if (plan.State != PlannedResolutionState.IrrevocablyResolved)
                allDone = false;

            foreach (var action in plan.Actions)
            {
                if (action.Kind == ResolutionActionKind.RaiseFulfilled && action.Preimage is { } p)
                    round.SwitchEvents.Add(RemoteHtlcSwitchEvents.Fulfilled(channel.ChannelId, record.Id,
                                                                            record.PaymentHash, new Secret(p)));
                else if (action.Kind == ResolutionActionKind.RaiseFailed)
                    round.SwitchEvents.Add(RemoteHtlcSwitchEvents.OnchainTimeout(channel.ChannelId, record.Id,
                                                                                 record.PaymentHash));
            }
        }

        return allDone;
    }

    /// <summary>
    /// Whether our current commitment or the peer's current or next one (other than the one on chain) has an output for
    /// our HTLC <paramref name="htlcId"/>. A rebuild that fails counts as "yes" (wait for reasonable depth).
    /// </summary>
    private bool HasOutputInAnotherCommitment(ChannelModel channel, ChannelCloseModel close, ulong htlcId)
    {
        var commitments = channel.Commitments!;
        var candidates = new List<(CommitmentSpec Spec, CommitmentCase Case, ulong Number, CompactPubKey? Point)>
        {
            (commitments.LocalCommit.Spec, CommitmentCase.Local, commitments.LocalCommit.Number, null)
        };
        if (close.Kind != ChannelCloseKind.RemoteCommitment)
            candidates.Add((commitments.RemoteCommit.Spec, CommitmentCase.Remote, commitments.RemoteCommit.Number,
                            commitments.RemoteCommit.PerCommitmentPoint));
        if (close.Kind != ChannelCloseKind.RemoteNextCommitment && commitments.RemoteNextCommit is { } next)
            candidates.Add((next.Commit.Spec, CommitmentCase.Remote, next.Commit.Number,
                            next.Commit.PerCommitmentPoint));

        foreach (var (spec, commitmentCase, number, point) in candidates)
        {
            if (spec.Htlcs.All(h => h.Direction != HtlcDirection.Outgoing || h.Id != htlcId))
                continue;

            try
            {
                var map = _mapper.Map(channel, CommitmentTxSpec.FromCommitmentSpec(spec), commitmentCase, number,
                                      point);
                if (map.Outputs.Any(o => o.Htlc is { Direction: HtlcDirection.Outgoing } h && h.Id == htlcId))
                    return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Channel {ChannelId}: cannot rebuild the {Case} commitment {Number}",
                                   channel.ChannelId, commitmentCase, number);
                return true;
            }
        }

        return false;
    }

    private OutputResolutionFacts BuildFacts(ChannelModel channel, ChannelCloseModel close, RemoteOutputData data,
                                             uint tipHeight)
    {
        byte[]? allowedPreimage = null;
        var remoteIrrevocablyCommitted = true;
        var upstreamResolved = false;
        if (data.Htlc is { } htlc)
        {
            var record = channel.Commitments?.GetHtlc(htlc.Direction, htlc.Id);
            if (htlc.Direction == HtlcDirection.Outgoing)
            {
                allowedPreimage = record is null ? null : OutgoingPreimage(record);
                upstreamResolved = data.UpstreamRaised && _memory.WasRaised(channel.ChannelId, htlc.Id);
            }
            else
            {
                // B5-LCL-RO-02: only the preimage of our own fulfill of this very HTLC (the switch accepted it)
                allowedPreimage = record?.Removal is { IsFulfill: true, PaymentPreimage: { } fulfilled }
                                      ? (byte[])fulfilled
                                      : null;
                remoteIrrevocablyCommitted = record is not null
                                          && HtlcStateTable.IsAddIrrevocablyCommitted(record.State);
            }
        }

        return new OutputResolutionFacts(tipHeight, close.SpentAtHeight, data.Spend?.ToOutputSpend(), null,
                                         allowedPreimage, remoteIrrevocablyCommitted, upstreamResolved, 0,
                                         _options.ReasonableDepth, _options.IrrevocableDepth);
    }

    private static byte[]? OutgoingPreimage(HtlcRecord record) =>
        record.KnownPreimage is { } known
            ? (byte[])known
            : record.Removal is { IsFulfill: true, PaymentPreimage: { } fulfilled }
                ? (byte[])fulfilled
                : null;

    private async Task<IReadOnlyList<OutputResolutionModel>> LoadRowsAsync(ChannelModel channel,
                                                                          ChannelCloseModel close,
                                                                          IUnitOfWork unitOfWork)
    {
        var rows = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(channel.ChannelId);
        return rows.Where(r => r.TransactionId == close.CommitmentTransactionId)
                   .OrderBy(r => r.OutputIndex)
                   .ToList();
    }

    private bool TryDecode(OutputResolutionModel row, out RemoteOutputData data)
    {
        try
        {
            data = RemoteOutputData.Decode(row.DescriptorData);
            return true;
        }
        catch (FormatException e)
        {
            _logger.LogError(e, "Channel {ChannelId}: output {Vout} has unreadable resolution data", row.ChannelId,
                             row.OutputIndex);
            data = null!;
            return false;
        }
    }

    /// <summary>The peer commitment the close names, from the snapshot; false when we cannot rebuild it.</summary>
    private static bool TryGetRemoteCommit(ChannelModel channel, ChannelCloseModel close, out RemoteCommit commit)
    {
        commit = null!;
        if (channel.Commitments is not { } commitments)
            return false;

        switch (close.Kind)
        {
            case ChannelCloseKind.RemoteCommitment:
                commit = commitments.RemoteCommit;
                return true;
            case ChannelCloseKind.RemoteNextCommitment when commitments.RemoteNextCommit is { } next:
                commit = next.Commit;
                return true;
            default:
                return false;
        }
    }

    private static bool CanRebuild(ChannelModel channel, ChannelCloseModel close) =>
        TryGetRemoteCommit(channel, close, out _);

    private void FlagDataLoss(ChannelModel channel, ChannelCloseModel close, RemoteResolutionRound round)
    {
        round.CriticalDataLoss = true;
        var message = $"B5-RMT-03: the peer's commitment {close.CommitmentNumber} ({close.Kind}) cannot be rebuilt; only "
                    + "our to_remote is swept, any HTLC in it may be lost";
        if (!_memory.CriticalAlerts.TryGetValue(channel.ChannelId, out var known) || known != message)
            _logger.LogCritical("Channel {ChannelId}: {Alert}", channel.ChannelId, message);
        _memory.RaiseCriticalAlert(channel.ChannelId, message);
        round.Alerts.Add(message);
    }

    private static CompactPubKey RequirePoint(RemoteOutputData data) =>
        data.RemotePerCommitmentPoint
     ?? throw new InvalidOperationException("An HTLC claim needs the peer's per-commitment point");

    private static void RequireRemoteClose(ChannelModel channel, ChannelCloseModel close)
    {
        ArgumentNullException.ThrowIfNull(close);
        if (close.ChannelId != channel.ChannelId)
            throw new ArgumentException("The close record belongs to another channel", nameof(close));
        if (close.Kind is not (ChannelCloseKind.RemoteCommitment or ChannelCloseKind.RemoteNextCommitment
                            or ChannelCloseKind.FutureCommitment))
            throw new ArgumentException($"A {close.Kind} close is not a peer commitment", nameof(close));
    }

    private static bool SameSpend(RemoteRecordedSpend? a, RemoteRecordedSpend b) =>
        a is not null && a.SpendingTxId == b.SpendingTxId && a.Height == b.Height && a.ByUs == b.ByUs
     && a.Path == b.Path && (a.Preimage ?? []).AsSpan().SequenceEqual(b.Preimage ?? []);

    private static bool SameRow(OutputResolutionModel a, OutputResolutionModel b) =>
        a.State == b.State && a.ResolvingTransactionId == b.ResolvingTransactionId
     && a.WaitUntilHeight == b.WaitUntilHeight && a.DeadlineHeight == b.DeadlineHeight
     && a.ResolvedHeight == b.ResolvedHeight && a.DescriptorData.AsSpan().SequenceEqual(b.DescriptorData);
}