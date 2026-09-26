using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Closing;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Fees;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Onchain.Parsers;
using Domain.Onchain.Planners;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Infrastructure.Bitcoin.Outputs;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Local;

/// <summary>
/// Resolves the outputs of <b>our own</b> commitment on chain (BOLT 5 §Unilateral Close Handling: Local Commitment
/// Transaction; plan O3-T3/T4, rows B5-LCL-*): <c>to_local</c> swept after the peer's <c>to_self_delay</c>, our offered
/// HTLCs timed out with the pre-signed HTLC-timeout at <c>cltv_expiry</c>, the peer's offered HTLCs claimed with the
/// HTLC-success only with an allowed preimage, the outputs of our HTLC transactions swept after the CSV, and our
/// offered HTLCs resolved upstream (fulfilled at once from a preimage seen on chain, persisted first; failed with
/// <see cref="HtlcRemovalKind.OnchainTimeout"/> once the settling transaction is reasonably deep).
/// </summary>
/// <remarks>
/// <para>
/// Stateless (the <see cref="IOutputResolver"/> contract): every call reloads the channel (with its commitment
/// snapshot) and the output rows, rebuilds our commitment with <see cref="ICommitmentOutputMapper"/> (it must have the
/// recorded txid: the classifier recognizes our commitment by txid only), reads the recorded spends from the watched
/// outpoints, and asks the pure <see cref="OutputResolutionPlanner"/> what to do. Transactions are built and signed
/// here but only returned as <see cref="BroadcastAction"/>s next to the row that records them (persist before
/// broadcast, D4); an output whose row already names its resolving transaction is never built again (the chain monitor
/// rebroadcasts pending rows, O6 bumps fees).
/// </para>
/// <para>
/// Rows: one per output of ours on the commitment (<c>to_local</c>, each HTLC output), created here when the watcher did
/// not, plus one per confirmed HTLC-timeout/success output (vout 0 of our HTLC transaction, kind
/// <see cref="OutputDescriptorKind.DelayedToLocal"/> with the HTLC's id), created when that transaction confirms. The
/// second-level output is swept from the parent row's plan.
/// </para>
/// <para>
/// Upstream events are repeated every call until the row is irrevocable (the switch is idempotent; a refused upstream
/// removal is so retried each block). A preimage learned on chain is staged into the HTLC's record
/// (<see cref="HtlcRecord.KnownPreimage"/>, BOLT2 I10) in the same round as its fulfill, so the switch's own replays
/// derive the fulfill too. The preimage guard (B5-LCL-RO-02): an incoming HTLC is claimed only with our own persisted
/// fulfill of it, or with the preimage its forward learned downstream; never with an invoice preimage the switch did
/// not accept for that HTLC. Alerts come from the spending witness (<see cref="OnOutputSpentAsync"/>) only.
/// </para>
/// <para>
/// Anchor channels (O7) are not resolved beyond <c>to_local</c>: their HTLC transactions need fee inputs. Such HTLC
/// outputs are only logged.
/// </para>
/// </remarks>
public sealed class LocalCommitResolver : IOutputResolver
{
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IBitcoinChainService? _chainService;
    private readonly ConcurrentDictionary<(ChannelId, ulong), byte> _unreadableSpendAlerts = new();
    private readonly ISweepDestinationProvider _destinationProvider;
    private readonly SweepFeePolicy _feePolicy;
    private readonly IFeeService _feeService;
    private readonly IHtlcTransactionBuilder _htlcTransactionBuilder;
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<LocalCommitResolver> _logger;
    private readonly ICommitmentOutputMapper _outputMapper;
    private readonly LocalCommitResolverOptions _options;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ISweepTransactionBuilder _sweepTransactionBuilder;

    public LocalCommitResolver(ICommitmentOutputMapper outputMapper, IHtlcTransactionBuilder htlcTransactionBuilder,
                               ISweepTransactionBuilder sweepTransactionBuilder, ILightningSigner lightningSigner,
                               IFeeService feeService, ISweepDestinationProvider destinationProvider,
                               IServiceScopeFactory serviceScopeFactory, ILogger<LocalCommitResolver> logger,
                               IOptions<LocalCommitResolverOptions>? options = null, SweepFeePolicy? feePolicy = null,
                               IChannelMemoryRepository? channelMemoryRepository = null,
                               IBitcoinChainService? chainService = null)
    {
        _chainService = chainService;
        _outputMapper = outputMapper;
        _htlcTransactionBuilder = htlcTransactionBuilder;
        _sweepTransactionBuilder = sweepTransactionBuilder;
        _lightningSigner = lightningSigner;
        _feeService = feeService;
        _destinationProvider = destinationProvider;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        _options = options?.Value ?? new LocalCommitResolverOptions();
        _feePolicy = feePolicy ?? new SweepFeePolicy();
        _channelMemoryRepository = channelMemoryRepository;
    }

    /// <inheritdoc />
    public bool CanResolve(ChannelCloseKind kind) => kind == ChannelCloseKind.LocalCommitment;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolverAction>> ResolveAsync(ChannelCloseModel close,
                                                                        IReadOnlyList<OutputResolutionModel> outputs,
                                                                        uint height,
                                                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(outputs);
        if (!CanResolve(close.Kind))
            return [];

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var context = await LoadAsync(unitOfWork, close, outputs, height);
        if (context is null)
            return [];

        var actions = new List<OutputResolverAction>();
        AddMissingRows(context, actions);
        foreach (var descriptor in context.Map.Outputs.Where(IsResolvedHere))
            await ResolveOutputAsync(context, descriptor, actions, cancellationToken);

        ResolveHtlcsWithoutOutput(context, actions);
        return actions;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutputResolverAction>> OnOutputSpentAsync(ChannelCloseModel close,
                                                                              OutputResolutionModel output,
                                                                              ChainTx spendingTransaction,
                                                                              uint height,
                                                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(close);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(spendingTransaction);
        if (!CanResolve(close.Kind))
            return [];

        var inputIndex = spendingTransaction.IndexOfInputSpending(output.TransactionId, output.OutputIndex);
        if (inputIndex < 0)
            return [];

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rows = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(close.ChannelId);
        var context = await LoadAsync(unitOfWork, close, rows, height);
        if (context is null)
            return [];

        var actions = new List<OutputResolverAction>();
        var byUs = await IsOurTransactionAsync(context, output, spendingTransaction.TxId);
        var descriptor = output.TransactionId == close.CommitmentTransactionId
                             ? context.Map.GetOutput(output.OutputIndex)
                             : null;
        var witness = spendingTransaction.Inputs[inputIndex].Witness;
        switch (descriptor)
        {
            case { Kind: OutputDescriptorKind.LocalOfferedHtlc or OutputDescriptorKind.LocalReceivedHtlc } when byUs:
                // Our HTLC-timeout/success confirmed: its output is ours after the CSV
                EnsureSecondLevelRow(context, output, descriptor, spendingTransaction, actions);
                break;

            case { Kind: OutputDescriptorKind.LocalOfferedHtlc, Htlc: { } offered }:
                if (HtlcWitnessParser.TryExtractPreimage(witness, offered.PaymentHash, out var preimage))
                {
                    // B5-LCL-LO-01: the peer claimed it with the preimage: persist it, then fulfill upstream at once
                    _logger.LogInformation("Peer claimed our HTLC {HtlcId} of channel {ChannelId} on chain with its "
                                         + "preimage in {TxId}", offered.Id, close.ChannelId,
                                           Display(spendingTransaction.TxId));
                    AddFulfill(context, offered, preimage, actions);
                }
                else
                {
                    actions.Add(new AlertAction("B5-LCL-LO-03",
                                                $"Our offered HTLC {offered.Id} ({offered.AmountMsat} msat) of channel "
                                              + $"{close.ChannelId} was taken by {Display(spendingTransaction.TxId)} "
                                              + "without a preimage (a revocation of our own commitment)"));
                }

                break;

            case { Kind: OutputDescriptorKind.LocalReceivedHtlc, Htlc: { } received }:
                // B5-LCL-RO-04: the peer timing out its HTLC is final and nothing for us; any other path is a loss
                if (HtlcWitnessParser.Parse(witness).Path != HtlcSpendPath.TimeoutClaim)
                    actions.Add(new AlertAction("B5-LCL-RO-04",
                                                $"The peer's HTLC {received.Id} ({received.AmountMsat} msat) on our "
                                              + $"commitment of channel {close.ChannelId} was taken by "
                                              + $"{Display(spendingTransaction.TxId)} by another path than its timeout"));
                break;

            default:
                // to_local or the output of one of our HTLC transactions: only our own sweep may spend it
                if (!byUs && output.Descriptor == OutputDescriptorKind.DelayedToLocal)
                    actions.Add(new AlertAction("B5-LCL-01",
                                                $"Our delayed output {Display(output.TransactionId)}:{output.OutputIndex}"
                                              + $" of channel {close.ChannelId} was taken by "
                                              + $"{Display(spendingTransaction.TxId)}, not by our sweep (a revocation "
                                              + "of our own commitment)"));
                break;
        }

        return actions;
    }

    #region Loading

    private async Task<LocalCommitContext?> LoadAsync(IUnitOfWork unitOfWork, ChannelCloseModel close,
                                                      IReadOnlyList<OutputResolutionModel> outputs, uint height)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(close.ChannelId);
        if (channel is null)
        {
            _logger.LogError("Cannot resolve our commitment of channel {ChannelId}: the channel is not stored",
                             close.ChannelId);
            return null;
        }

        var commitments = channel.Commitments;
        var number = commitments?.LocalCommit.Number ?? channel.LocalCommitmentNumber;
        var spec = commitments is not null
                       ? CommitmentTxSpec.FromCommitmentSpec(commitments.LocalCommit.Spec)
                       : CommitmentTxSpec.FromChannel(channel);
        if (close.CommitmentNumber is { } closeNumber && closeNumber != number)
        {
            _logger.LogError("Cannot resolve our commitment {Number} of channel {ChannelId}: our latest local "
                           + "commitment is {Latest}", closeNumber, close.ChannelId, number);
            return null;
        }

        var map = _outputMapper.Map(channel, spec, CommitmentCase.Local, number, null);
        if (map.ExpectedTxId != close.CommitmentTransactionId)
        {
            _logger.LogError("Cannot resolve channel {ChannelId}: its closing transaction {TxId} is not our rebuilt "
                           + "commitment {Expected}", close.ChannelId, Display(close.CommitmentTransactionId),
                             Display(map.ExpectedTxId));
            return null;
        }

        return new LocalCommitContext(unitOfWork, channel, commitments, close, map, outputs.ToList(), height);
    }

    private static bool IsResolvedHere(CommitmentOutputDescriptor descriptor) =>
        descriptor.Kind is OutputDescriptorKind.DelayedToLocal or OutputDescriptorKind.LocalOfferedHtlc
                        or OutputDescriptorKind.LocalReceivedHtlc;

    /// <summary>Rows (and watches) for our outputs that the watcher has not recorded.</summary>
    private static void AddMissingRows(LocalCommitContext context, List<OutputResolverAction> actions)
    {
        foreach (var descriptor in context.Map.Outputs.Where(IsResolvedHere))
        {
            if (context.GetRow(context.CommitmentTxId, descriptor.Vout) is not null)
                continue;

            var row = new OutputResolutionModel
            {
                TransactionId = context.CommitmentTxId,
                OutputIndex = descriptor.Vout,
                ChannelId = context.Channel.ChannelId,
                Descriptor = descriptor.Kind,
                DescriptorData = OutputDescriptorData.FromDescriptor(descriptor, context.Map.PerCommitmentPoint)
                                                     .Encode(),
                HtlcDirection = descriptor.Htlc?.Direction,
                HtlcId = descriptor.Htlc?.Id
            };
            context.Rows.Add(row);
            actions.Add(new UpsertOutputAction(row));
            actions.Add(new WatchOutpointAction(new WatchedOutpointModel(context.CommitmentTxId, descriptor.Vout,
                                                                         context.Channel.ChannelId,
                                                                         WatchedOutpointPurpose.ResolutionOutput)));
        }
    }

    /// <summary>
    /// The confirmed spend of a row's outpoint: the watched outpoint's recorded spend, else (no watch row) the row's
    /// own resolution, attributed to us when we recorded a resolving transaction.
    /// </summary>
    private static async Task<OutputSpend?> GetSpendAsync(LocalCommitContext context, OutputResolutionModel row)
    {
        var watch = await context.UnitOfWork.WatchedOutpointDbRepository.GetAsync(row.TransactionId,
                                                                                  row.OutputIndex);
        if (watch is { SpentByTransactionId: { } spender, SpentAtHeight: { } spentAt })
            return new OutputSpend(spender, spentAt, await IsOurTransactionAsync(context, row, spender));

        if (row is
            {
                State: OutputResolutionState.Resolved or OutputResolutionState.Irrevocable,
                ResolvedHeight: { } resolvedAt
            })
            return new OutputSpend(row.ResolvingTransactionId ?? default, resolvedAt,
                                   row.ResolvingTransactionId is not null);

        return null;
    }

    /// <summary>
    /// True when <paramref name="txId"/> is the row's resolving transaction or a transaction we broadcast for the
    /// channel (a fee-bumped replacement).
    /// </summary>
    private static async Task<bool> IsOurTransactionAsync(LocalCommitContext context, OutputResolutionModel row,
                                                          TxId txId)
    {
        if (row.ResolvingTransactionId is { } resolving && resolving == txId)
            return true;

        var broadcast = await context.UnitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId);
        return broadcast?.ChannelId is { } channelId && channelId == context.Channel.ChannelId;
    }

    #endregion

    #region Commitment outputs

    private async Task ResolveOutputAsync(LocalCommitContext context, CommitmentOutputDescriptor descriptor,
                                          List<OutputResolverAction> actions, CancellationToken cancellationToken)
    {
        var row = context.GetRow(context.CommitmentTxId, descriptor.Vout)!;
        var record = descriptor.Htlc is { } htlc ? context.Commitments?.GetHtlc(htlc.Direction, htlc.Id) : null;
        if (descriptor.Htlc is not null && descriptor.HasAnchors)
        {
            // O7: an anchor channel's HTLC transactions carry no fee and need wallet inputs
            _logger.LogWarning("HTLC output {Vout} of our commitment of anchor channel {ChannelId} cannot be resolved "
                             + "yet (O7)", descriptor.Vout, context.Channel.ChannelId);
            return;
        }

        var spend = await GetSpendAsync(context, row);
        OutputResolutionModel? child = null;
        OutputSpend? secondLevelSpend = null;
        if (spend is { ByUs: true } && descriptor.Htlc is not null)
        {
            child = context.GetRow(spend.SpendingTxId, 0);
            if (child is not null)
                secondLevelSpend = await GetSpendAsync(context, child);
        }

        var lossUnproven = false;
        if (spend is { ByUs: false } && descriptor is
            {
                Kind: OutputDescriptorKind.LocalOfferedHtlc, Htlc: { } spentOffered
            })
        {
            if (record?.KnownPreimage is { } known)
            {
                spend = spend with { Preimage = (byte[])known };
            }
            else
            {
                // The preimage staged at spend time is missing (the stage found no record, its save failed, or the
                // spend was never reported): read the witness again. Only a witness that proves a path without the
                // preimage (our HTLC-timeout, a revocation) may lead to the upstream fail (BOLT 5: fail upstream only
                // when the output was resolved by a timeout); an unreadable one is alerted, never failed
                var (path, preimage) = await ReadSpendingWitnessAsync(context, row, spend.SpendingTxId,
                                                                      spend.Height, spentOffered.PaymentHash);
                spend = spend with { Path = path, Preimage = preimage };
                lossUnproven = preimage is null
                            && path is not (HtlcSpendPath.HtlcTimeoutTransaction or HtlcSpendPath.Revocation);
            }
        }
        else if (spend is { ByUs: false } && descriptor.Kind == OutputDescriptorKind.LocalReceivedHtlc)
            spend = spend with { Path = HtlcSpendPath.TimeoutClaim }; // alerts come from the witness, at spend time

        // NL-316/NL-322: an HTLC of the peer that pays one of our invoices and has no preimage we may use yet: the switch
        // decides as final hop (it persists the preimage on the record, which the next round claims with)
        if (spend is null && descriptor.Kind == OutputDescriptorKind.LocalReceivedHtlc
                          && await FinalHopClaims.GetFinalHopDecisionAsync(context.UnitOfWork, context.Channel.ChannelId,
                                                                          record, context.Height) is { } decision)
            actions.Add(new RaiseChannelEventAction(decision));

        var facts = new OutputResolutionFacts(context.Height, context.Close.SpentAtHeight, spend, secondLevelSpend,
                                              await GetAllowedPreimageAsync(context, descriptor, record),
                                              record is { Direction: HtlcDirection.Incoming, State: var state }
                                              && state >= HtlcState.RcvdAddAckRevocation,
                                              false,
                                              descriptor.SecondLevel?.ToSelfDelay ?? descriptor.CsvDelay,
                                              _options.ReasonableDepth, _options.IrrevocableDepth);
        var plan = OutputResolutionPlanner.Plan(descriptor, facts);

        uint? waitUntil = null;
        var updated = row;
        var raiseUpstream = row.State != OutputResolutionState.Irrevocable;
        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case ResolutionActionKind.Wait:
                    waitUntil = waitUntil is { } earlier ? Math.Min(earlier, action.WaitUntilHeight!.Value)
                                                         : action.WaitUntilHeight;
                    break;

                case ResolutionActionKind.BroadcastHtlcTimeoutTx or ResolutionActionKind.BroadcastHtlcSuccessTx
                    when updated.ResolvingTransactionId is null:
                    updated = AddHtlcTransaction(context, descriptor, updated, action, actions);
                    break;

                case ResolutionActionKind.Sweep when action is { SpendKind: SweepSpendKind.DelayedOutput }:
                    if (!action.OnSecondLevel)
                    {
                        if (updated.ResolvingTransactionId is null)
                            updated = await AddSweepAsync(context, updated,
                                                          SweepInputFactory.ToLocal(descriptor, context.CommitmentTxId,
                                                                                    context.Map.PerCommitmentPoint),
                                                          actions, cancellationToken);
                    }
                    else if (spend is not null)
                    {
                        child ??= EnsureSecondLevelRow(context, row, descriptor, spend.SpendingTxId, null, actions);
                        if (child is { ResolvingTransactionId: null } && ToSecondLevelInput(context, child) is { } input)
                            await AddSweepAsync(context, child, input, actions, cancellationToken);
                    }

                    break;

                case ResolutionActionKind.RaiseFulfilled when raiseUpstream && descriptor.Htlc is { } offered:
                    AddFulfill(context, offered, new Secret(action.Preimage!), actions);
                    break;

                case ResolutionActionKind.RaiseFailed when raiseUpstream && lossUnproven
                                                        && descriptor.Htlc is { } offered:
                    // The peer may have been paid on chain with the preimage: failing upstream could lose the amount
                    _logger.LogError("Not failing HTLC {HtlcId} of channel {ChannelId} upstream: its output was taken "
                                   + "by {TxId}, whose witness could not be read", offered.Id,
                                     context.Channel.ChannelId, Display(spend!.SpendingTxId));
                    // NL-315: alerted once per process, from the first round at or past the reasonable depth, so a
                    // node that was offline over that block is alerted too
                    if (Depth(context.Height, spend.Height) >= _options.ReasonableDepth
                     && _unreadableSpendAlerts.TryAdd((context.Channel.ChannelId, offered.Id), 0))
                        actions.Add(new AlertAction("B5-LCL-LO-03",
                                                    $"Our offered HTLC {offered.Id} ({offered.AmountMsat} msat) of "
                                                  + $"channel {context.Channel.ChannelId} was taken by "
                                                  + $"{Display(spend.SpendingTxId)} and its witness cannot be read: "
                                                  + "the upstream HTLC is neither fulfilled nor failed; check the "
                                                  + "transaction for the preimage"));
                    break;

                case ResolutionActionKind.RaiseFailed when raiseUpstream && descriptor.Htlc is { } offered:
                    AddFail(context, offered, record, actions);
                    break;
            }
        }

        UpdateRowState(context, updated, plan, spend, waitUntil, actions);
    }

    /// <summary>
    /// The spend path and the preimage (checked against <paramref name="paymentHash"/>) of the input of
    /// <paramref name="spenderTxId"/> that spends <paramref name="row"/>'s outpoint, read from the chain: from the block
    /// at <paramref name="spentAt"/> first (no <c>txindex</c> needed, NL-315), else <c>getrawtransaction</c>;
    /// <see cref="HtlcSpendPath.Unknown"/> when the transaction cannot be fetched.
    /// </summary>
    private async Task<(HtlcSpendPath Path, byte[]? Preimage)> ReadSpendingWitnessAsync(
        LocalCommitContext context, OutputResolutionModel row, TxId spenderTxId, uint spentAt, Hash paymentHash)
    {
        if (_chainService is null)
            return (HtlcSpendPath.Unknown, null);

        try
        {
            var hash = new uint256((byte[])spenderTxId);
            var block = await _chainService.GetBlockAsync(spentAt);
            var transaction = block?.Transactions.FirstOrDefault(t => t.GetHash() == hash)
                           ?? await _chainService.GetTransactionAsync(hash);
            if (transaction is null)
            {
                _logger.LogWarning("The spender {TxId} of {Output}:{Vout} of channel {ChannelId} is not found",
                                   Display(spenderTxId), Display(row.TransactionId), row.OutputIndex,
                                   context.Channel.ChannelId);
                return (HtlcSpendPath.Unknown, null);
            }

            var spender = ChainTxMapper.FromTransaction(transaction);
            var index = spender.IndexOfInputSpending(row.TransactionId, row.OutputIndex);
            if (index < 0)
                return (HtlcSpendPath.Unknown, null);

            var witness = spender.Inputs[index].Witness;
            return HtlcWitnessParser.TryExtractPreimage(witness, paymentHash, out var preimage)
                       ? (HtlcSpendPath.PreimageClaim, (byte[])preimage)
                       : (HtlcWitnessParser.Parse(witness).Path, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning("Cannot fetch the spender {TxId} of {Output}:{Vout} of channel {ChannelId}: {Reason}",
                               Display(spenderTxId), Display(row.TransactionId), row.OutputIndex,
                               context.Channel.ChannelId, e.Message);
            return (HtlcSpendPath.Unknown, null);
        }
    }

    /// <summary>
    /// The preimage we may use for an HTLC (B5-LCL-RO-02): for ours, the one the peer revealed (off chain or on chain,
    /// both persisted in the record); for the peer's, our own persisted fulfill, the preimage the switch persisted on it
    /// when it accepted it as our final hop (<see cref="FinalHopClaims"/>), or the preimage the forward of it learnt
    /// downstream (the switch's upstream fulfill is refused once the channel is closed).
    /// </summary>
    private async Task<byte[]?> GetAllowedPreimageAsync(LocalCommitContext context,
                                                        CommitmentOutputDescriptor descriptor, HtlcRecord? record)
    {
        if (record is null)
            return null;

        if (record.Direction == HtlcDirection.Outgoing)
            return ToBytes(record.KnownPreimage);

        if (record.Removal is { Kind: HtlcRemovalKind.Fulfill, PaymentPreimage: { } fulfilled })
            return fulfilled;

        if (descriptor.Kind != OutputDescriptorKind.LocalReceivedHtlc)
            return null;

        // Accepted as our final hop by the switch (NL-316/NL-322): the preimage it persisted on this HTLC's record
        if (await FinalHopClaims.GetAcceptedPreimageAsync(context.UnitOfWork, record) is { } accepted)
            return accepted;

        var forwards = await context.UnitOfWork.ChannelStateDbRepository.FindHtlcsByOriginAsync(
                           HtlcOrigin.Forwarded(context.Channel.ChannelId, record.Id));
        foreach (var (outgoingChannelId, key) in forwards)
        {
            var outgoing = await context.UnitOfWork.ChannelDbRepository.GetByIdAsync(outgoingChannelId);
            if (outgoing?.Commitments is not { } outgoingCommitments)
                continue;

            var outgoingRecord = outgoingCommitments.GetHtlc(key.Direction, key.Id);
            if (outgoingRecord is null)
            {
                var persisted = await context.UnitOfWork.ChannelStateDbRepository.LoadAsync(outgoingChannelId,
                                    outgoingCommitments.Params);
                outgoingRecord = persisted?.SettledHtlcs.FirstOrDefault(h => h.Key == key);
            }

            if (outgoingRecord?.KnownPreimage is { } preimage && Hashes(preimage, record.PaymentHash))
                return preimage;
        }

        return null;
    }

    /// <summary>
    /// Builds and signs our HTLC-timeout (B5-LCL-LO-02) or HTLC-success (B5-LCL-RO-01) transaction from the peer's
    /// stored HTLC signature, and records it on the row in the same round as its broadcast.
    /// </summary>
    private OutputResolutionModel AddHtlcTransaction(LocalCommitContext context,
                                                     CommitmentOutputDescriptor descriptor, OutputResolutionModel row,
                                                     ResolutionAction action, List<OutputResolverAction> actions)
    {
        var model = descriptor.SecondLevel;
        var signatures = context.Commitments?.LocalCommit.RemoteSignatures;
        var index = context.Map.Outputs.Where(o => o.Htlc is not null).OrderBy(o => o.Vout).ToList()
                           .IndexOf(descriptor);
        if (model is null || signatures is null || index < 0 || index >= signatures.HtlcSignatures.Count)
        {
            _logger.LogError("No HTLC transaction or peer signature for output {Vout} of our commitment of channel "
                           + "{ChannelId}", descriptor.Vout, context.Channel.ChannelId);
            return row;
        }

        var built = _htlcTransactionBuilder.Build(model);
        var localSignature = _lightningSigner.SignLocalHtlcTransaction(
            context.Channel.ChannelId, new HtlcSigningContext(built, context.Map.PerCommitmentPoint, model.HasAnchors));
        var preimage = model.Type == HtlcTransactionType.Success ? action.Preimage : null;
        var signed = _htlcTransactionBuilder.AddWitness(model, built, signatures.HtlcSignatures[index], localSignature,
                                                        preimage);

        _logger.LogInformation("Broadcasting our HTLC-{Type} {TxId} for HTLC {HtlcId} of channel {ChannelId}",
                               model.Type, Display(signed.TxId), descriptor.Htlc?.Id, context.Channel.ChannelId);
        actions.Add(new BroadcastAction(new BroadcastTransactionModel(signed, BroadcastPurpose.HtlcTransaction,
                                                                      context.Channel.ChannelId, context.Height,
                                                                      context.Commitments!.LocalCommit.Spec
                                                                                         .FeeratePerKw)));
        return row with
        {
            ResolvingTransactionId = signed.TxId,
            DeadlineHeight = action.DeadlineHeight ?? row.DeadlineHeight
        };
    }

    /// <summary>
    /// Builds and signs a sweep of one delayed output into the wallet at the current estimate (floored and capped by
    /// <see cref="SweepFeePolicy"/>), and records it on <paramref name="row"/>; an output that does not pay its own fee
    /// is recorded <see cref="OutputResolutionState.Ignored"/>.
    /// </summary>
    private async Task<OutputResolutionModel> AddSweepAsync(LocalCommitContext context, OutputResolutionModel row,
                                                            SweepInput input, List<OutputResolverAction> actions,
                                                            CancellationToken cancellationToken)
    {
        var destination = await _destinationProvider.GetDestinationScriptAsync(cancellationToken);
        var weight = SweepWeights.EstimateTransactionWeight([input], [destination.Length]);
        var estimate = await Fees.FeeEstimates.GetForTargetAsync(_feeService,
                                                            _feePolicy.GetConfirmationTarget(context.Height, null),
                                                            _logger, cancellationToken);
        var decision = _feePolicy.Decide(input.AmountSat, weight, estimate, false, context.Height, null);
        var dust = ShutdownScriptValidator.GetDustThresholdSat(destination);
        var floorFee = SweepWeights.FeeSat(_feePolicy.Options.MinFeeratePerKw, weight);
        if (decision.Abandon || input.AmountSat < floorFee + dust)
        {
            // Worth no more than its own fee plus a dust output even at the floor rate: it can never be swept
            _logger.LogWarning("Not sweeping {TxId}:{Vout} ({AmountSat} sat) of channel {ChannelId}: it does not pay "
                             + "its own sweep fee ({FloorFeeSat} sat at the floor rate) and a {DustSat} sat output",
                               Display(input.TxId), input.Vout, input.AmountSat, context.Channel.ChannelId, floorFee,
                               dust);
            var ignored = row with { State = OutputResolutionState.Ignored };
            ReplaceRow(context, ignored, actions);
            return ignored;
        }

        // The capped fee (half the value) of a small output can leave less than dust: pay what leaves exactly dust,
        // which is still at least the floor fee
        var feeSat = Math.Min(decision.FeeSat, input.AmountSat - dust);
        SignedTransaction signed;
        try
        {
            var unsigned = _sweepTransactionBuilder.BuildWithFee([input], destination, feeSat);
            signed = _sweepTransactionBuilder.Sign(unsigned, _lightningSigner, context.Channel.ChannelId);
        }
        catch (ArgumentException e)
        {
            // Not a property of the output (checked above): leave the row as it is, so the next block tries again
            _logger.LogError(e, "Cannot build the sweep of {TxId}:{Vout} ({AmountSat} sat, fee {FeeSat} sat) of "
                              + "channel {ChannelId}; retrying next block", Display(input.TxId), input.Vout,
                             input.AmountSat, feeSat, context.Channel.ChannelId);
            return row;
        }

        var feeratePerKw = feeSat == decision.FeeSat ? decision.FeeratePerKw : SweepFeePolicy.FeeratePerKw(feeSat, weight);
        _logger.LogInformation("Sweeping {TxId}:{Vout} ({AmountSat} sat, fee {FeeSat} sat) of channel {ChannelId} "
                             + "with {SweepTxId}", Display(input.TxId), input.Vout, input.AmountSat, feeSat,
                               context.Channel.ChannelId, Display(signed.TxId));
        actions.Add(new BroadcastAction(new BroadcastTransactionModel(signed, BroadcastPurpose.Sweep,
                                                                      context.Channel.ChannelId, context.Height,
                                                                      feeratePerKw)));
        var swept = row with
        {
            ResolvingTransactionId = signed.TxId,
            State = row.State is OutputResolutionState.Pending or OutputResolutionState.Waiting
                        ? OutputResolutionState.Broadcast
                        : row.State,
            WaitUntilHeight = null
        };
        ReplaceRow(context, swept, actions);
        return swept;
    }

    /// <summary>
    /// The row of the output of our confirmed HTLC-timeout/success transaction (vout 0), with its watch; created once.
    /// </summary>
    private OutputResolutionModel? EnsureSecondLevelRow(LocalCommitContext context, OutputResolutionModel parent,
                                                        CommitmentOutputDescriptor descriptor, ChainTx spender,
                                                        List<OutputResolverAction> actions)
    {
        var row = EnsureSecondLevelRow(context, parent, descriptor, spender.TxId, spender, actions);
        if (row is not null && OutputDescriptorData.TryDecode(row) is { } data
                            && (spender.Outputs.Count == 0
                             || !spender.Outputs[0].ScriptPubKey.AsSpan().SequenceEqual(data.ScriptPubKey)))
            actions.Add(new AlertAction("B5-LCL-LO-03",
                                        $"Our HTLC transaction {Display(spender.TxId)} of channel "
                                      + $"{context.Channel.ChannelId} does not pay the expected second-level script"));
        return row;
    }

    private OutputResolutionModel? EnsureSecondLevelRow(LocalCommitContext context, OutputResolutionModel parent,
                                                        CommitmentOutputDescriptor descriptor, TxId htlcTxId,
                                                        ChainTx? spender, List<OutputResolverAction> actions)
    {
        if (context.GetRow(htlcTxId, 0) is { } existing)
            return existing;

        if (descriptor.SecondLevel is not { } model)
            return null;

        var output = new HtlcResolutionOutput(model.OutputAmount, new PubKey(model.LocalDelayedPubKey),
                                              new PubKey(model.RevocationPubKey), model.ToSelfDelay);
        var amountSat = spender is { Outputs.Count: > 0 }
                            ? spender.Outputs[0].AmountSat
                            : (ulong)model.OutputAmount.Satoshi;
        var data = new OutputDescriptorData(amountSat, (byte[])output.BitcoinScriptPubKey, (byte[])output.RedeemBitcoinScript,
                                            model.ToSelfDelay, model.HasAnchors, context.Map.PerCommitmentPoint,
                                            descriptor.Htlc);
        var row = new OutputResolutionModel
        {
            TransactionId = htlcTxId,
            OutputIndex = 0,
            ChannelId = context.Channel.ChannelId,
            Descriptor = OutputDescriptorKind.DelayedToLocal,
            DescriptorData = data.Encode(),
            HtlcDirection = parent.HtlcDirection,
            HtlcId = parent.HtlcId
        };
        context.Rows.Add(row);
        actions.Add(new UpsertOutputAction(row));
        actions.Add(new WatchOutpointAction(new WatchedOutpointModel(htlcTxId, 0, context.Channel.ChannelId,
                                                                     WatchedOutpointPurpose.ResolutionOutput)));
        return row;
    }

    private static SweepInput? ToSecondLevelInput(LocalCommitContext context, OutputResolutionModel row) =>
        OutputDescriptorData.TryDecode(row) is { WitnessScript: { } witnessScript } data
            ? SweepInputFactory.SecondLevelOutput(row.TransactionId, data.AmountSat, witnessScript, data.CsvDelay,
                                                  data.PerCommitmentPoint ?? context.Map.PerCommitmentPoint)
            : null;

    /// <summary>
    /// Records what the plan says about an output that is not spent yet (<see cref="OutputResolutionState.Waiting"/>,
    /// <see cref="OutputResolutionState.Broadcast"/>, or <see cref="OutputResolutionState.Ignored"/> for an expired HTLC
    /// of the peer that we may not claim). A spent output's state belongs to the executor.
    /// </summary>
    private static void UpdateRowState(LocalCommitContext context, OutputResolutionModel row,
                                       OutputResolutionPlan plan, OutputSpend? spend, uint? waitUntil,
                                       List<OutputResolverAction> actions)
    {
        var current = context.GetRow(row.TransactionId, row.OutputIndex) ?? row;
        var next = row with { State = current.State };
        if (spend is null && current.State is OutputResolutionState.Pending or OutputResolutionState.Waiting
                                                   or OutputResolutionState.Broadcast)
        {
            if (plan.State == PlannedResolutionState.IrrevocablyResolved)
                next = next with { State = OutputResolutionState.Ignored, WaitUntilHeight = null };
            else if (next.ResolvingTransactionId is not null)
                next = next with { State = OutputResolutionState.Broadcast, WaitUntilHeight = null };
            else if (waitUntil is not null)
                next = next with { State = OutputResolutionState.Waiting, WaitUntilHeight = waitUntil };
        }

        if (!SameRow(current, next))
            ReplaceRow(context, next, actions);
    }

    #endregion

    #region HTLCs without an output

    /// <summary>
    /// Our offered HTLCs that have no output in our commitment on chain (trimmed, not in it yet, or removed from it;
    /// B5-LCL-LO-04): fulfilled upstream at once with a known preimage, else failed once the commitment is reasonably
    /// deep, or at once when neither the peer's current nor its next commitment has an output for it.
    /// </summary>
    private void ResolveHtlcsWithoutOutput(LocalCommitContext context, List<OutputResolverAction> actions)
    {
        if (context.Commitments is not { } commitments
         || Depth(context.Height, context.Close.SpentAtHeight) >= _options.IrrevocableDepth)
            return;

        var mapped = context.Map.Outputs.Where(o => o.Htlc is not null)
                            .Select(o => new HtlcKey(o.Htlc!.Value.Direction, o.Htlc.Value.Id))
                            .ToHashSet();
        var withoutOutput = commitments.Htlcs.Values
                                       .Where(h => h.Direction == HtlcDirection.Outgoing && !mapped.Contains(h.Key))
                                       .ToList();
        if (withoutOutput.Count == 0)
            return;

        var elsewhere = MapRemoteHtlcOutputs(context.Channel, commitments);
        foreach (var record in withoutOutput)
        {
            var htlc = new SpecHtlc(record.Direction, record.Id, record.AmountMsat, record.PaymentHash,
                                    record.CltvExpiry);
            var facts = new HtlcWithoutOutputFacts(context.Height, context.Close.SpentAtHeight,
                                                   ToBytes(record.KnownPreimage),
                                                   elsewhere?.Contains(record.Key) ?? true, false,
                                                   _options.ReasonableDepth, _options.IrrevocableDepth);
            foreach (var action in OutputResolutionPlanner.PlanHtlcWithoutOutput(htlc, facts).Actions)
            {
                if (action.Kind == ResolutionActionKind.RaiseFulfilled)
                    AddFulfill(context, htlc, new Secret(action.Preimage!), actions);
                else if (action.Kind == ResolutionActionKind.RaiseFailed)
                    AddFail(context, htlc, record, actions);
            }
        }
    }

    /// <summary>
    /// The HTLCs that have an output in the peer's current or next commitment, or null when they cannot be rebuilt
    /// (then every HTLC is assumed to have one, so none is failed before the commitment is reasonably deep).
    /// </summary>
    private HashSet<HtlcKey>? MapRemoteHtlcOutputs(ChannelModel channel, ChannelCommitments commitments)
    {
        try
        {
            var remote = new List<RemoteCommit> { commitments.RemoteCommit };
            if (commitments.RemoteNextCommit is { } next)
                remote.Add(next.Commit);

            return remote.SelectMany(r => _outputMapper.Map(channel, CommitmentTxSpec.FromCommitmentSpec(r.Spec),
                                                            CommitmentCase.Remote, r.Number, r.PerCommitmentPoint)
                                                       .Outputs)
                         .Where(o => o.Htlc is not null)
                         .Select(o => new HtlcKey(o.Htlc!.Value.Direction, o.Htlc.Value.Id))
                         .ToHashSet();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _logger.LogWarning("Cannot rebuild the peer's commitments of channel {ChannelId}: {Reason}",
                               channel.ChannelId, e.Message);
            return null;
        }
    }

    #endregion

    #region Upstream events

    /// <summary>
    /// Fulfills our offered HTLC upstream (B5-LCL-LO-01/04): the preimage is staged into the HTLC's record first when
    /// it is not stored there yet (BOLT2 I10), in the same save that precedes the event.
    /// </summary>
    private void AddFulfill(LocalCommitContext context, SpecHtlc htlc, Secret preimage,
                            List<OutputResolverAction> actions)
    {
        var channelId = context.Channel.ChannelId;
        var record = context.Commitments?.GetHtlc(HtlcDirection.Outgoing, htlc.Id);
        if (record is not null && record.KnownPreimage != preimage)
            actions.Add(new StageWriteAction($"preimage of HTLC {htlc.Id} of channel {channelId}",
                                             (unitOfWork, _) => StageKnownPreimageAsync(unitOfWork, channelId, htlc.Id,
                                                                                        preimage)));

        actions.Add(new RaiseChannelEventAction(new OutgoingHtlcFulfilled(channelId, htlc.Id, htlc.PaymentHash,
                                                                          preimage)));
    }

    /// <summary>
    /// Fails our offered HTLC upstream (B5-LCL-LO-03/04): a failure the peer had sent (not yet irrevocable when the
    /// channel closed) keeps its reason; otherwise it timed out on chain.
    /// </summary>
    private static void AddFail(LocalCommitContext context, SpecHtlc htlc, HtlcRecord? record,
                                List<OutputResolverAction> actions)
    {
        var removal = record?.Removal is { Kind: HtlcRemovalKind.Fail or HtlcRemovalKind.FailMalformed } failure
                          ? failure
                          : HtlcRemoval.OnchainTimeout();
        actions.Add(new RaiseChannelEventAction(new OutgoingHtlcFailed(context.Channel.ChannelId, htlc.Id,
                                                                       htlc.PaymentHash, removal)));
    }

    /// <summary>
    /// Stages <see cref="HtlcRecord.KnownPreimage"/> of our offered HTLC in the round's unit of work, and puts it into
    /// the loaded channel's snapshot so the switch's replays see it.
    /// </summary>
    private async Task StageKnownPreimageAsync(IUnitOfWork unitOfWork, ChannelId channelId, ulong htlcId,
                                               Secret preimage)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        if (channel?.Commitments is not { } commitments
         || commitments.GetHtlc(HtlcDirection.Outgoing, htlcId) is not { } record)
        {
            // The fulfill is still raised; the per-block round reads the preimage from the chain again
            _logger.LogError("Cannot persist the preimage of our HTLC {HtlcId} of channel {ChannelId} seen on chain: "
                           + "the channel or its HTLC record is not stored", htlcId, channelId);
            return;
        }

        if (record.KnownPreimage == preimage)
            return;

        var updated = record with { KnownPreimage = preimage };
        await unitOfWork.ChannelStateDbRepository.ApplyAsync(WithRecord(commitments, updated),
                                                             new ChannelTransition([updated], [], [], false, false,
                                                                                   false, false));

        if (_channelMemoryRepository is not null
         && _channelMemoryRepository.TryGetChannel(channelId, out var loaded)
         && loaded.Commitments is { } loadedCommitments
         && loadedCommitments.GetHtlc(HtlcDirection.Outgoing, htlcId) is { } loadedRecord)
            loaded.UpdateCommitments(WithRecord(loadedCommitments, loadedRecord with { KnownPreimage = preimage }));
    }

    private static ChannelCommitments WithRecord(ChannelCommitments commitments, HtlcRecord record) =>
        ChannelCommitments.Restore(commitments.ChannelId, commitments.Params, commitments.LocalBalanceMsat,
                                   commitments.RemoteBalanceMsat, commitments.Htlcs.SetItem(record.Key, record).Values,
                                   commitments.FeeUpdates, commitments.LocalNextHtlcId,
                                   commitments.RemoteNextHtlcId, commitments.LocalCommit, commitments.RemoteCommit,
                                   commitments.RemoteNextCommit, commitments.RemoteNextPerCommitmentPoint);

    #endregion

    #region Helpers

    private static void ReplaceRow(LocalCommitContext context, OutputResolutionModel row,
                                   List<OutputResolverAction> actions)
    {
        var index = context.Rows.FindIndex(r => r.TransactionId == row.TransactionId
                                             && r.OutputIndex == row.OutputIndex);
        if (index >= 0)
            context.Rows[index] = row;
        else
            context.Rows.Add(row);

        actions.RemoveAll(a => a is UpsertOutputAction u && u.Output.TransactionId == row.TransactionId
                                                     && u.Output.OutputIndex == row.OutputIndex);
        actions.Add(new UpsertOutputAction(row));
    }

    private static bool SameRow(OutputResolutionModel a, OutputResolutionModel b) =>
        a.State == b.State && a.ResolvingTransactionId == b.ResolvingTransactionId
                           && a.WaitUntilHeight == b.WaitUntilHeight && a.DeadlineHeight == b.DeadlineHeight
                           && a.ResolvedHeight == b.ResolvedHeight;

    private static bool Hashes(Secret preimage, Hash paymentHash) =>
        System.Security.Cryptography.SHA256.HashData((byte[])preimage).AsSpan().SequenceEqual((byte[])paymentHash);

    private static byte[]? ToBytes(Secret? secret) => secret is { } value ? (byte[])value : null;

    private static uint Depth(uint tip, uint height) => tip >= height ? tip - height + 1 : 0;

    /// <summary>A txid in the display (RPC) byte order, for logs (NL-275).</summary>
    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    private sealed record LocalCommitContext(
        IUnitOfWork UnitOfWork,
        ChannelModel Channel,
        ChannelCommitments? Commitments,
        ChannelCloseModel Close,
        CommitmentOutputMap Map,
        List<OutputResolutionModel> Rows,
        uint Height)
    {
        public TxId CommitmentTxId => Close.CommitmentTransactionId;

        public OutputResolutionModel? GetRow(TxId txId, uint vout) =>
            Rows.FirstOrDefault(r => r.TransactionId == txId && r.OutputIndex == vout);
    }

    #endregion
}