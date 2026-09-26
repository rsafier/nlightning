using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Onchain.Resolvers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
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
using Domain.Payments.Enums;
using Domain.Payments.ValueObjects;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Onchain.Interfaces;
using Remote;

/// <summary>
/// Resolves a <b>peer</b> commitment on chain (BOLT 5 §Unilateral Close Handling: Remote Commitment Transaction; plan
/// O4-T1..T3, rows B5-RMT-*): the peer's current commitment (<see cref="ChannelCloseKind.RemoteCommitment"/>), the one
/// we signed whose <c>revoke_and_ack</c> is outstanding (<see cref="ChannelCloseKind.RemoteNextCommitment"/>,
/// B5-RMT-01) and one newer than any we know (<see cref="ChannelCloseKind.FutureCommitment"/>, data loss, B5-RMT-03).
/// </summary>
/// <remarks>
/// <para>
/// Stateless (the <see cref="IOutputResolver"/> contract): every call reads the channel (with its commitment snapshot)
/// in a scope of its own, rebuilds the commitment on chain with <see cref="ICommitmentOutputMapper"/> at the peer's
/// per-commitment point of that commitment, reads the recorded spends from the watched outpoints and asks the pure
/// <see cref="OutputResolutionPlanner"/>. It returns actions only: sweeps and claims are built and signed here but
/// staged as <see cref="BroadcastAction"/>s next to the row that records them (persist before broadcast, D4), and an
/// output whose row names its resolving transaction is never built again (the chain monitor rebroadcasts, O6 bumps).
/// The executor marks spent outputs <see cref="OutputResolutionState.Resolved"/> and, 100 blocks on,
/// <see cref="OutputResolutionState.Irrevocable"/>; this resolver only moves rows between Pending, Waiting, Broadcast
/// and Ignored.
/// </para>
/// <para>
/// Outputs: <c>to_remote</c> (D5, B5-RMT-02) swept at once, also after data loss (static_remotekey: our
/// <c>payment_basepoint</c>, no point needed); HTLCs we offered (received outputs there, B5-RMT-LO-*) claimed with
/// <c>&lt;sig&gt; &lt;&gt;</c> and <c>nLockTime = cltv_expiry</c> once the tip reaches it; HTLCs the peer offered
/// (offered outputs there, B5-RMT-RO-*) claimed with <c>&lt;sig&gt; &lt;preimage&gt;</c> before <c>cltv_expiry</c>, only
/// when the peer is irrevocably committed to them and only with an allowed preimage: our own fulfill of that HTLC, the
/// preimage the HTLC switch persisted on it after accepting it as our final hop (NL-316; the switch is asked to decide
/// every round while the HTLC pays an <c>Open</c> invoice of ours, <see cref="FinalHopClaims"/>), or the preimage its
/// forward learnt downstream (off chain or on chain), never an invoice preimage alone (B5-LCL-RO-02). One transaction
/// per output.
/// </para>
/// <para>
/// Upstream: a preimage of our offered HTLC (from the peer's spend on chain, or known off chain) is staged into the
/// HTLC's record (<see cref="HtlcRecord.KnownPreimage"/>, BOLT2 I10) in the round that raises
/// <see cref="OutgoingHtlcFulfilled"/>, so the switch's own replays (link-up, startup) derive the fulfill too; our
/// timeout claim (or an HTLC without output, B5-RMT-LO-03) raises <see cref="OutgoingHtlcFailed"/> with
/// <see cref="RemoteHtlcSwitchEvents.OnchainTimeoutKind"/> once reasonably deep. Either event is raised again every
/// round until the upstream HTLC actually has its removal (or the payment is final), because the switch only logs a
/// removal it cannot send yet.
/// </para>
/// <para>
/// Data loss (no rebuild possible): every output of the commitment gets a row and a watch; <c>to_remote</c> is swept,
/// the others stay <see cref="OutputDescriptorKind.Unknown"/> and any spend of them is searched for the preimage of our
/// still-open offered HTLCs. They are ignored only once none of those HTLCs can matter any more. A funding spend that is
/// no known commitment (<see cref="ChannelCloseKind.Unknown"/>, B5-GEN-06) is handled the same way.
/// </para>
/// <para>
/// Upstream after data loss (NL-320): an offered HTLC of such a commitment has no output we can claim, so its upstream
/// HTLC is failed (<see cref="RemoteHtlcSwitchEvents.OnchainTimeout"/>) once its <c>cltv_expiry</c> plus the reasonable
/// depth has passed without a preimage (and the close is reasonably deep), every round until the upstream has its
/// removal; the watched outputs keep the channel from closing until then. Waiting for <c>Closed</c> (100 blocks, or
/// <c>cltv_expiry</c> + 100) could miss the upstream deadline and force-close the upstream channel too. A preimage
/// that shows up in a spend first fulfills instead.
/// </para>
/// </remarks>
public sealed class RemoteCommitResolver : IOutputResolver
{
    private readonly IChannelMemoryRepository? _channelMemoryRepository;
    private readonly IRemoteCommitmentSource _commitmentSource;
    private readonly IRemoteSweepDestination _destination;
    private readonly SweepFeePolicy _feePolicy;
    private readonly IFeeService _feeService;
    private readonly ILogger<RemoteCommitResolver> _logger;
    private readonly ICommitmentOutputMapper _mapper;
    private readonly RemoteResolutionOptions _options;
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILightningSigner _signer;
    private readonly ISweepTransactionBuilder _sweepBuilder;

    public RemoteCommitResolver(ICommitmentOutputMapper mapper, ISweepTransactionBuilder sweepBuilder,
                                ILightningSigner signer, IFeeService feeService, IRemoteSweepDestination destination,
                                IRemoteCommitmentSource commitmentSource, IServiceScopeFactory serviceScopeFactory,
                                IOptions<RemoteResolutionOptions>? options = null,
                                ILogger<RemoteCommitResolver>? logger = null, SweepFeePolicy? feePolicy = null,
                                IChannelMemoryRepository? channelMemoryRepository = null)
    {
        _mapper = mapper;
        _sweepBuilder = sweepBuilder;
        _signer = signer;
        _feeService = feeService;
        _destination = destination;
        _commitmentSource = commitmentSource;
        _serviceScopeFactory = serviceScopeFactory;
        _options = options?.Value ?? new RemoteResolutionOptions();
        _logger = logger ?? NullLogger<RemoteCommitResolver>.Instance;
        _feePolicy = feePolicy ?? new SweepFeePolicy();
        _channelMemoryRepository = channelMemoryRepository;
    }

    /// <inheritdoc />
    public bool CanResolve(ChannelCloseKind kind) =>
        kind is ChannelCloseKind.RemoteCommitment or ChannelCloseKind.RemoteNextCommitment
            or ChannelCloseKind.FutureCommitment or ChannelCloseKind.Unknown;

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
        var context = await LoadAsync(unitOfWork, close, outputs, height, cancellationToken);
        if (context is null)
            return [];

        var actions = new List<OutputResolverAction>();
        if (context.IsDataLoss)
            await AddDataLossRowsAsync(context, actions, cancellationToken);
        else
            AddMissingRows(context, actions);

        foreach (var row in context.Rows.Where(r => r.TransactionId == context.CommitmentTxId)
                                   .OrderBy(r => r.OutputIndex)
                                   .ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ResolveRowAsync(context, row, actions, cancellationToken);
        }

        if (!context.IsDataLoss)
            await ResolveHtlcsWithoutOutputAsync(context, actions);
        else
            await ResolveUnrebuildableHtlcsAsync(context, actions);

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
        if (!CanResolve(close.Kind) || output.TransactionId != close.CommitmentTransactionId
                                    || output.ChannelId != close.ChannelId)
            return [];

        var inputIndex = spendingTransaction.IndexOfInputSpending(output.TransactionId, output.OutputIndex);
        if (inputIndex < 0)
            return [];

        using var scope = _serviceScopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rows = await unitOfWork.OnchainResolutionDbRepository.GetOutputsByChannelIdAsync(close.ChannelId);
        var merged = rows.Where(r => r.TransactionId != output.TransactionId || r.OutputIndex != output.OutputIndex)
                         .Append(output)
                         .ToList();
        var context = await LoadAsync(unitOfWork, close, merged, height, cancellationToken);
        if (context is null)
            return [];

        var actions = new List<OutputResolverAction>();
        var byUs = await IsOurTransactionAsync(context, output, spendingTransaction.TxId);
        var witness = spendingTransaction.Inputs[inputIndex].Witness;
        var htlc = OutputDescriptorData.TryDecode(output)?.Htlc;
        switch (output.Descriptor)
        {
            case OutputDescriptorKind.RemoteReceivedHtlc when htlc is { } offered:
                if (HtlcWitnessParser.TryExtractPreimage(witness, offered.PaymentHash, out var preimage))
                {
                    // B5-RMT-LO-01: the peer's HTLC-success revealed the preimage: persist it, fulfill upstream at once
                    _logger.LogInformation("Channel {ChannelId}: the peer claimed our HTLC {HtlcId} on chain with its "
                                         + "preimage in {TxId}", close.ChannelId, offered.Id,
                                           Display(spendingTransaction.TxId));
                    AddFulfill(context, offered, preimage, actions);
                }
                else if (!byUs)
                {
                    actions.Add(new AlertAction("B5-RMT-LO-02",
                                                $"Our offered HTLC {offered.Id} ({offered.AmountMsat} msat) on the peer's "
                                              + $"commitment of channel {close.ChannelId} was taken by "
                                              + $"{Display(spendingTransaction.TxId)} without its preimage"));
                }

                break;

            case OutputDescriptorKind.RemoteOfferedHtlc when htlc is { } received:
                // The peer's HTLC-timeout transaction takes back its own HTLC; any other spend is a loss
                if (!byUs && HtlcWitnessParser.Parse(witness).Path != HtlcSpendPath.HtlcTimeoutTransaction)
                    actions.Add(new AlertAction("B5-RMT-RO-02",
                                                $"The peer's HTLC {received.Id} ({received.AmountMsat} msat) on its "
                                              + $"commitment of channel {close.ChannelId} was taken by "
                                              + $"{Display(spendingTransaction.TxId)} by another path than its timeout"));
                break;

            case OutputDescriptorKind.PaymentToRemote when !byUs:
                actions.Add(new AlertAction("B5-RMT-02",
                                            $"Our to_remote {Display(output.TransactionId)}:{output.OutputIndex} of "
                                          + $"channel {close.ChannelId} was taken by {Display(spendingTransaction.TxId)}, "
                                          + "not by our sweep"));
                break;

            case OutputDescriptorKind.Unknown:
                // Data loss (B5-RMT-LO-01): the spend of an output we could not map may still reveal the preimage of
                // one of our offered HTLCs
                foreach (var record in OpenOutgoingHtlcs(context))
                {
                    if (!HtlcWitnessParser.TryExtractPreimage(witness, record.PaymentHash, out var found))
                        continue;

                    _logger.LogInformation("Channel {ChannelId}: {TxId} revealed the preimage of our HTLC {HtlcId} on a "
                                         + "commitment we cannot rebuild", close.ChannelId,
                                           Display(spendingTransaction.TxId), record.Id);
                    AddFulfill(context, ToSpec(record), found, actions);
                }

                break;
        }

        return actions;
    }

    #region Loading

    private async Task<RemoteCommitContext?> LoadAsync(IUnitOfWork unitOfWork, ChannelCloseModel close,
                                                       IReadOnlyList<OutputResolutionModel> outputs, uint height,
                                                       CancellationToken cancellationToken)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(close.ChannelId);
        if (channel is null)
        {
            _logger.LogError("Cannot resolve the peer's commitment of channel {ChannelId}: the channel is not stored",
                             close.ChannelId);
            return null;
        }

        var rows = outputs.Where(r => r.ChannelId == close.ChannelId).ToList();
        if (!TryGetRemoteCommit(channel, close, out var commit))
            return new RemoteCommitContext(unitOfWork, channel, close, null, null, rows, height);

        try
        {
            var spec = CommitmentTxSpec.FromCommitmentSpec(commit.Spec);
            var map = _mapper.Map(channel, spec, CommitmentCase.Remote, commit.Number, commit.PerCommitmentPoint);
            if (map.ExpectedTxId != close.CommitmentTransactionId)
            {
                // Our rebuild differs from the transaction on chain: map its outputs by script instead
                var onChain = await _commitmentSource.GetCommitmentAsync(close, cancellationToken);
                if (onChain is null)
                {
                    _logger.LogError("Channel {ChannelId}: the rebuilt commitment {Expected} is not {TxId} and the "
                                   + "transaction cannot be read; retrying on the next block", close.ChannelId,
                                     Display(map.ExpectedTxId), Display(close.CommitmentTransactionId));
                    return null;
                }

                map = _mapper.Map(channel, spec, CommitmentCase.Remote, commit.Number, commit.PerCommitmentPoint,
                                  onChain);
            }

            return new RemoteCommitContext(unitOfWork, channel, close, commit, map, rows, height);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _logger.LogError(e, "Channel {ChannelId}: cannot rebuild the peer's commitment {Number}", close.ChannelId,
                             commit.Number);
            return null;
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

    private static bool IsOurs(OutputDescriptorKind kind) =>
        kind is OutputDescriptorKind.PaymentToRemote or OutputDescriptorKind.RemoteReceivedHtlc
            or OutputDescriptorKind.RemoteOfferedHtlc;

    /// <summary>Rows (and watches) for our outputs of a rebuilt commitment that are not recorded yet.</summary>
    private static void AddMissingRows(RemoteCommitContext context, List<OutputResolverAction> actions)
    {
        var map = context.Map!;
        var created = false;
        foreach (var descriptor in map.Outputs.Where(o => IsOurs(o.Kind)))
        {
            if (context.GetRow(context.CommitmentTxId, descriptor.Vout) is not null)
                continue;

            AddRow(context, descriptor.Vout, descriptor.Kind,
                   OutputDescriptorData.FromDescriptor(descriptor, map.PerCommitmentPoint), descriptor.Htlc, actions);
            created = true;
        }

        if (created && map.UnmappedVouts.Count > 0)
            actions.Add(new AlertAction("B5-RMT-03",
                                        $"Outputs {string.Join(", ", map.UnmappedVouts)} of the peer's commitment "
                                      + $"{Display(context.CommitmentTxId)} of channel {context.Channel.ChannelId} "
                                      + "match no expected output"));
    }

    /// <summary>
    /// B5-RMT-03: a commitment we cannot rebuild. Once: a row and a watch for every output (our <c>to_remote</c> found
    /// by its script, every other one <see cref="OutputDescriptorKind.Unknown"/>) and a critical alert.
    /// </summary>
    private async Task AddDataLossRowsAsync(RemoteCommitContext context, List<OutputResolverAction> actions,
                                            CancellationToken cancellationToken)
    {
        if (context.Rows.Any(r => r.TransactionId == context.CommitmentTxId))
            return;

        var onChain = await _commitmentSource.GetCommitmentAsync(context.Close, cancellationToken);
        if (onChain is null)
        {
            _logger.LogCritical("Channel {ChannelId}: the peer's commitment {TxId} cannot be rebuilt (data loss) nor "
                              + "read; retrying on the next block", context.Channel.ChannelId,
                                Display(context.CommitmentTxId));
            return;
        }

        var toRemote = _mapper.FindPaymentToRemote(onChain, context.Channel.LocalKeySet.PaymentCompactBasepoint,
                                                   context.Channel.ChannelParams.OptionAnchorOutputs);
        for (var vout = 0U; vout < onChain.Outputs.Count; vout++)
        {
            var output = onChain.Outputs[(int)vout];
            var found = toRemote.FirstOrDefault(d => d.Vout == vout);
            var data = found is not null
                           ? OutputDescriptorData.FromDescriptor(found, null)
                           : new OutputDescriptorData(output.AmountSat, output.ScriptPubKey, null, 0,
                                                      context.Channel.ChannelParams.OptionAnchorOutputs, null, null);
            AddRow(context, vout, found?.Kind ?? OutputDescriptorKind.Unknown, data, null, actions);
        }

        var what = context.Close.Kind == ChannelCloseKind.Unknown
                       ? $"The funding spend {Display(context.CommitmentTxId)} of channel {context.Channel.ChannelId} "
                       + "is no known commitment"
                       : $"The peer's commitment {context.Close.CommitmentNumber} ({context.Close.Kind}, "
                       + $"{Display(context.CommitmentTxId)}) of channel {context.Channel.ChannelId} cannot be rebuilt";
        actions.Add(new AlertAction(context.Close.Kind == ChannelCloseKind.Unknown ? "B5-GEN-06" : "B5-RMT-03",
                                    $"{what}: only our to_remote ({toRemote.Count} output(s)) is swept; every other "
                                  + "output is watched for the preimages of our offered HTLCs, which are failed "
                                  + "upstream once expired and reasonably deep, and any HTLC in it may be lost"));
    }

    private static void AddRow(RemoteCommitContext context, uint vout, OutputDescriptorKind kind,
                               OutputDescriptorData data, SpecHtlc? htlc, List<OutputResolverAction> actions)
    {
        var row = new OutputResolutionModel
        {
            TransactionId = context.CommitmentTxId,
            OutputIndex = vout,
            ChannelId = context.Channel.ChannelId,
            Descriptor = kind,
            DescriptorData = data.Encode(),
            HtlcDirection = htlc?.Direction,
            HtlcId = htlc?.Id
        };
        context.Rows.Add(row);
        actions.Add(new UpsertOutputAction(row));
        actions.Add(new WatchOutpointAction(new WatchedOutpointModel(context.CommitmentTxId, vout,
                                                                     context.Channel.ChannelId,
                                                                     WatchedOutpointPurpose.ResolutionOutput)));
    }

    /// <summary>
    /// The confirmed spend of a row's outpoint: the watched outpoint's recorded spend, else (no watch row) the
    /// executor's resolution of the row, attributed to us when it names our resolving transaction.
    /// </summary>
    private static async Task<OutputSpend?> GetSpendAsync(RemoteCommitContext context, OutputResolutionModel row)
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
    private static async Task<bool> IsOurTransactionAsync(RemoteCommitContext context, OutputResolutionModel row,
                                                          TxId txId)
    {
        if (row.ResolvingTransactionId is { } resolving && resolving == txId)
            return true;

        var broadcast = await context.UnitOfWork.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId);
        return broadcast?.ChannelId is { } channelId && channelId == context.Channel.ChannelId;
    }

    #endregion

    #region Commitment outputs

    private async Task ResolveRowAsync(RemoteCommitContext context, OutputResolutionModel row,
                                       List<OutputResolverAction> actions, CancellationToken cancellationToken)
    {
        switch (row.Descriptor)
        {
            case OutputDescriptorKind.PeerOutput or OutputDescriptorKind.PeerAnchor or OutputDescriptorKind.OurAnchor:
                // Not ours (B5-RMT-02; our anchor waits for O7): nothing to resolve
                if (row.State is OutputResolutionState.Pending or OutputResolutionState.Waiting)
                    ReplaceRow(context, row with { State = OutputResolutionState.Ignored, WaitUntilHeight = null },
                               actions);
                return;

            case OutputDescriptorKind.Unknown:
                await ResolveUnknownRowAsync(context, row, actions);
                return;

            case OutputDescriptorKind.PaymentToRemote or OutputDescriptorKind.RemoteReceivedHtlc
                or OutputDescriptorKind.RemoteOfferedHtlc:
                break;

            default:
                return;
        }

        if (row.State == OutputResolutionState.Irrevocable)
            return;

        if (OutputDescriptorData.TryDecode(row) is not { } data)
        {
            _logger.LogError("Channel {ChannelId}: output {Vout} has unreadable resolution data", row.ChannelId,
                             row.OutputIndex);
            return;
        }

        var descriptor = new CommitmentOutputDescriptor(row.OutputIndex, data.AmountSat, row.Descriptor,
                                                        data.ScriptPubKey, data.WitnessScript, data.Htlc,
                                                        data.CsvDelay, data.HasAnchors);
        var htlc = data.Htlc;
        var record = htlc is { } h ? context.Commitments?.GetHtlc(h.Direction, h.Id) : null;
        var ours = htlc is { Direction: HtlcDirection.Outgoing };
        var spend = await GetSpendAsync(context, row);
        if (spend is { ByUs: false })
        {
            spend = row.Descriptor switch
            {
                // A preimage the peer revealed on chain was staged into the record when the spend was seen
                OutputDescriptorKind.RemoteReceivedHtlc => spend with { Preimage = OutgoingPreimage(record) },

                // Alerts come from the spending witness, when the spend is seen (OnOutputSpentAsync), not every block
                OutputDescriptorKind.RemoteOfferedHtlc => spend with { Path = HtlcSpendPath.HtlcTimeoutTransaction },
                _ => spend
            };
        }

        var allowedPreimage = ours ? OutgoingPreimage(record) : await GetIncomingPreimageAsync(context, record);
        var upstreamResolved = ours && await IsUpstreamResolvedAsync(context, htlc!.Value.Id);

        // A timeout claim abandoned as uneconomic (an Ignored row that is not spent): our HTLC is then as good as
        // trimmed, so its upstream is resolved as for an HTLC without output (B5-RMT-LO-03)
        if (row.State == OutputResolutionState.Ignored)
        {
            if (!ours)
                return;

            if (spend is null)
            {
                RaiseUpstream(context, htlc!.Value, record,
                              OutputResolutionPlanner.PlanHtlcWithoutOutput(
                                  htlc.Value, new HtlcWithoutOutputFacts(context.Height, context.Close.SpentAtHeight,
                                                                         allowedPreimage, true, upstreamResolved,
                                                                         _options.ReasonableDepth,
                                                                         _options.IrrevocableDepth)),
                              actions);
                return;
            }
        }

        // NL-316/NL-322: an HTLC of the peer that pays one of our invoices and has no preimage we may use yet: the switch
        // decides as final hop (it persists the preimage on the record, which the next round claims with)
        if (!ours && spend is null && await FinalHopClaims.GetFinalHopDecisionAsync(
                                          context.UnitOfWork, context.Channel.ChannelId, record, context.Height) is
            { } finalHopDecision)
            actions.Add(new RaiseChannelEventAction(finalHopDecision));

        OutputResolutionPlan plan;
        try
        {
            var facts = new OutputResolutionFacts(context.Height, context.Close.SpentAtHeight, spend, null,
                                                  allowedPreimage,
                                                  ours || (record is not null
                                                        && HtlcStateTable.IsAddIrrevocablyCommitted(record.State)),
                                                  upstreamResolved, 0, _options.ReasonableDepth,
                                                  _options.IrrevocableDepth);
            plan = OutputResolutionPlanner.Plan(descriptor, facts);
        }
        catch (ArgumentException e)
        {
            _logger.LogError(e, "Channel {ChannelId}: cannot plan output {Vout}", context.Channel.ChannelId,
                             row.OutputIndex);
            return;
        }

        if (row.State == OutputResolutionState.Ignored)
        {
            // Spent after all (e.g. the peer's preimage claim of an abandoned HTLC): only the upstream part is left
            RaiseUpstream(context, htlc!.Value, record, plan, actions);
            return;
        }

        uint? waitUntil = null;
        var updated = row;
        foreach (var action in plan.Actions)
        {
            switch (action.Kind)
            {
                case ResolutionActionKind.Wait:
                    waitUntil = waitUntil is { } earlier
                                    ? Math.Min(earlier, action.WaitUntilHeight!.Value)
                                    : action.WaitUntilHeight;
                    break;

                case ResolutionActionKind.Sweep when spend is null && updated.ResolvingTransactionId is null:
                    updated = await SweepAsync(context, updated, descriptor, data, action, actions, cancellationToken);
                    break;
            }
        }

        if (htlc is { } upstreamHtlc)
            RaiseUpstream(context, upstreamHtlc, record, plan, actions);

        UpdateRowState(context, row, updated, plan, spend, waitUntil, actions);
    }

    /// <summary>The upstream part of a plan: fulfill (preimage staged first) or fail our offered HTLC.</summary>
    private void RaiseUpstream(RemoteCommitContext context, SpecHtlc htlc, HtlcRecord? record,
                               OutputResolutionPlan plan, List<OutputResolverAction> actions)
    {
        if (htlc.Direction != HtlcDirection.Outgoing)
            return;

        foreach (var action in plan.Actions)
        {
            if (action is { Kind: ResolutionActionKind.RaiseFulfilled, Preimage: { } preimage })
                AddFulfill(context, htlc, new Secret(preimage), actions);
            else if (action.Kind == ResolutionActionKind.RaiseFailed)
                AddFail(context, htlc, record, actions);
        }
    }

    /// <summary>
    /// Builds, signs and stages one sweep or claim of the output (D4: saved with the row, published after), and
    /// records it on the row; an output that does not pay its own fee is <see cref="OutputResolutionState.Ignored"/>.
    /// </summary>
    private async Task<OutputResolutionModel> SweepAsync(RemoteCommitContext context, OutputResolutionModel row,
                                                         CommitmentOutputDescriptor descriptor,
                                                         OutputDescriptorData data, ResolutionAction action,
                                                         List<OutputResolverAction> actions,
                                                         CancellationToken cancellationToken)
    {
        try
        {
            var commitmentTxId = context.CommitmentTxId;
            var input = action.SpendKind switch
            {
                SweepSpendKind.PaymentToRemote => SweepInputFactory.ToRemote(
                    descriptor, commitmentTxId, context.Channel.LocalKeySet.PaymentCompactBasepoint),
                SweepSpendKind.HtlcTimeoutClaim => SweepInputFactory.HtlcTimeoutClaim(
                    descriptor, commitmentTxId, RequirePoint(data)),
                SweepSpendKind.HtlcPreimageClaim => SweepInputFactory.HtlcPreimageClaim(
                    descriptor, commitmentTxId, RequirePoint(data),
                    action.Preimage ?? throw new InvalidOperationException("A preimage claim without its preimage")),
                _ => throw new InvalidOperationException($"A {action.SpendKind} spend is not a remote-commitment spend")
            };

            context.Destination ??= await _destination.GetScriptAsync(context.Channel.ChannelId, cancellationToken);
            var destination = context.Destination;
            var weight = SweepWeights.EstimateTransactionWeight([input], [destination.Length]);
            var target = _feePolicy.GetConfirmationTarget(context.Height, action.DeadlineHeight);
            var estimate = await Fees.FeeEstimates.GetForTargetAsync(_feeService, target, _logger, cancellationToken);
            var decision = _feePolicy.Decide(input.AmountSat, weight, estimate, false, context.Height,
                                             action.DeadlineHeight);
            if (decision.Abandon)
            {
                _logger.LogWarning("Channel {ChannelId}: output {Vout} ({Amount} sat) does not pay its own sweep fee; "
                                 + "abandoned", context.Channel.ChannelId, row.OutputIndex, input.AmountSat);
                return row with { State = OutputResolutionState.Ignored, WaitUntilHeight = null };
            }

            var unsigned = _sweepBuilder.BuildWithFee([input], destination, decision.FeeSat);
            var signed = _sweepBuilder.Sign(unsigned, _signer, context.Channel.ChannelId);
            var purpose = input.SpendKind == SweepSpendKind.PaymentToRemote
                              ? BroadcastPurpose.Sweep
                              : BroadcastPurpose.HtlcClaim;
            actions.Add(new BroadcastAction(new BroadcastTransactionModel(signed, purpose, context.Channel.ChannelId,
                                                                          context.Height, decision.FeeratePerKw)));
            _logger.LogInformation("Channel {ChannelId}: {Kind} of output {Vout} ({Requirement}) in {TxId}, fee {Fee} sat",
                                   context.Channel.ChannelId, input.SpendKind, row.OutputIndex, action.RequirementId,
                                   Display(signed.TxId), decision.FeeSat);
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
            _logger.LogError(e, "Channel {ChannelId}: cannot build the {Kind} of output {Vout}",
                             context.Channel.ChannelId, action.SpendKind, row.OutputIndex);
            return row;
        }
    }

    /// <summary>
    /// Records what the plan says about an output that is not spent yet (<see cref="OutputResolutionState.Waiting"/>,
    /// <see cref="OutputResolutionState.Broadcast"/>, or <see cref="OutputResolutionState.Ignored"/> for an expired
    /// HTLC of the peer that we may not claim). A spent output's state belongs to the executor.
    /// </summary>
    private static void UpdateRowState(RemoteCommitContext context, OutputResolutionModel original,
                                       OutputResolutionModel updated, OutputResolutionPlan plan, OutputSpend? spend,
                                       uint? waitUntil, List<OutputResolverAction> actions)
    {
        var next = updated;
        if (spend is null && next.State is OutputResolutionState.Pending or OutputResolutionState.Waiting
                                               or OutputResolutionState.Broadcast)
        {
            if (plan.State == PlannedResolutionState.IrrevocablyResolved)
                next = next with { State = OutputResolutionState.Ignored, WaitUntilHeight = null };
            else if (next.ResolvingTransactionId is not null)
                next = next with { State = OutputResolutionState.Broadcast, WaitUntilHeight = null };
            else if (waitUntil is not null)
                next = next with { State = OutputResolutionState.Waiting, WaitUntilHeight = waitUntil };
        }

        if (!SameRow(original, next))
            ReplaceRow(context, next, actions);
    }

    /// <summary>
    /// An output of a commitment we could not rebuild (data loss): it is only watched (a spend may reveal a preimage).
    /// It is ignored once the commitment is irrevocable and every open HTLC we offered is resolved upstream or long
    /// expired (<c>cltv_expiry</c> + the irrevocable depth), so no preimage can still matter.
    /// </summary>
    private async Task ResolveUnknownRowAsync(RemoteCommitContext context, OutputResolutionModel row,
                                              List<OutputResolverAction> actions)
    {
        if (row.State is not (OutputResolutionState.Pending or OutputResolutionState.Waiting)
         || Depth(context.Height, context.Close.SpentAtHeight) < _options.IrrevocableDepth)
            return;

        foreach (var record in OpenOutgoingHtlcs(context))
        {
            if (context.Height < record.CltvExpiry + _options.IrrevocableDepth
             && !await IsUpstreamResolvedAsync(context, record.Id))
                return;
        }

        ReplaceRow(context, row with { State = OutputResolutionState.Ignored, WaitUntilHeight = null }, actions);
    }

    #endregion

    #region HTLCs without an output

    /// <summary>
    /// NL-320: our offered HTLCs when the commitment on chain cannot be rebuilt (<see cref="ChannelCloseKind.FutureCommitment"/>
    /// after data loss, or <see cref="ChannelCloseKind.Unknown"/>). With a known preimage (off chain, or found in a spend
    /// and staged on the record) the upstream is fulfilled; without one, it is failed once the tip is
    /// <c>cltv_expiry</c> + the reasonable depth and the close itself is reasonably deep: we cannot time the HTLC out on
    /// chain, and holding the upstream HTLC longer only makes the upstream channel fail too (BOLT 5: fail the incoming
    /// HTLC once the outgoing one timed out reasonably deep). Repeated every round until the upstream HTLC has its
    /// removal (or the payment is final).
    /// </summary>
    private async Task ResolveUnrebuildableHtlcsAsync(RemoteCommitContext context, List<OutputResolverAction> actions)
    {
        var closeDeep = Depth(context.Height, context.Close.SpentAtHeight) >= _options.ReasonableDepth;
        foreach (var record in OpenOutgoingHtlcs(context).ToList())
        {
            var preimage = OutgoingPreimage(record);
            var expired = closeDeep && (ulong)context.Height >= (ulong)record.CltvExpiry + _options.ReasonableDepth;
            if ((preimage is null && !expired) || await IsUpstreamResolvedAsync(context, record.Id))
                continue;

            var htlc = ToSpec(record);
            if (preimage is not null && Hashes(preimage, record.PaymentHash))
            {
                AddFulfill(context, htlc, new Secret(preimage), actions);
                continue;
            }

            if (!expired)
                continue;

            if (_logger.IsEnabled(LogLevel.Warning))
                _logger.LogWarning("Channel {ChannelId}: our HTLC {HtlcId} (cltv_expiry {CltvExpiry}) on a commitment we "
                                 + "cannot rebuild expired without a preimage on chain; failing it upstream at height "
                                 + "{Height}", context.Channel.ChannelId, record.Id, record.CltvExpiry, context.Height);
            AddFail(context, htlc, record, actions);
        }
    }

    /// <summary>
    /// B5-RMT-LO-03: our offered HTLCs the engine still tracks that have no output in the commitment on chain
    /// (trimmed, not in it yet, or removed from it): fulfilled upstream at once with a known preimage, else failed once
    /// the commitment is reasonably deep, or at once when no other valid commitment has an output for it. Repeated
    /// every round until the upstream HTLC has its removal, and never once the commitment is irrevocable.
    /// </summary>
    private async Task ResolveHtlcsWithoutOutputAsync(RemoteCommitContext context, List<OutputResolverAction> actions)
    {
        var depth = Depth(context.Height, context.Close.SpentAtHeight);
        if (context.Commitments is null || depth >= _options.IrrevocableDepth)
            return;

        var mapped = context.Map!.Outputs.Where(o => o.Htlc is { Direction: HtlcDirection.Outgoing })
                            .Select(o => o.Htlc!.Value.Id)
                            .ToHashSet();
        foreach (var record in OpenOutgoingHtlcs(context).Where(r => !mapped.Contains(r.Id)))
        {
            if (await IsUpstreamResolvedAsync(context, record.Id))
                continue;

            var htlc = ToSpec(record);
            var preimage = OutgoingPreimage(record);
            var elsewhere = preimage is null && depth < _options.ReasonableDepth
                                             && HasOutputInAnotherCommitment(context, record.Id);
            var plan = OutputResolutionPlanner.PlanHtlcWithoutOutput(
                htlc, new HtlcWithoutOutputFacts(context.Height, context.Close.SpentAtHeight, preimage, elsewhere,
                                                 false, _options.ReasonableDepth, _options.IrrevocableDepth));
            RaiseUpstream(context, htlc, record, plan, actions);
        }
    }

    /// <summary>
    /// Whether our current commitment or the peer's current or next one (other than the one on chain) has an output for
    /// our HTLC <paramref name="htlcId"/>. A rebuild that fails counts as "yes" (wait for reasonable depth).
    /// </summary>
    private bool HasOutputInAnotherCommitment(RemoteCommitContext context, ulong htlcId)
    {
        var commitments = context.Commitments!;
        var candidates = new List<(CommitmentSpec Spec, CommitmentCase Case, ulong Number, CompactPubKey? Point)>
        {
            (commitments.LocalCommit.Spec, CommitmentCase.Local, commitments.LocalCommit.Number, null)
        };
        if (context.Close.Kind != ChannelCloseKind.RemoteCommitment)
            candidates.Add((commitments.RemoteCommit.Spec, CommitmentCase.Remote, commitments.RemoteCommit.Number,
                            commitments.RemoteCommit.PerCommitmentPoint));
        if (context.Close.Kind != ChannelCloseKind.RemoteNextCommitment && commitments.RemoteNextCommit is { } next)
            candidates.Add((next.Commit.Spec, CommitmentCase.Remote, next.Commit.Number,
                            next.Commit.PerCommitmentPoint));

        foreach (var (spec, commitmentCase, number, point) in candidates)
        {
            if (spec.Htlcs.All(h => h.Direction != HtlcDirection.Outgoing || h.Id != htlcId))
                continue;

            try
            {
                var map = _mapper.Map(context.Channel, CommitmentTxSpec.FromCommitmentSpec(spec), commitmentCase,
                                      number, point);
                if (map.Outputs.Any(o => o.Htlc is { Direction: HtlcDirection.Outgoing } h && h.Id == htlcId))
                    return true;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning(e, "Channel {ChannelId}: cannot rebuild the {Case} commitment {Number}",
                                   context.Channel.ChannelId, commitmentCase, number);
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Preimages and upstream

    /// <summary>
    /// The preimage we may use to claim the peer's HTLC (B5-RMT-RO-01, B5-LCL-RO-02): our own persisted fulfill of it,
    /// the preimage the switch persisted on it when it accepted it as our final hop (<see cref="FinalHopClaims"/>),
    /// or the preimage the forward of it learnt downstream (the outgoing HTLC's <see cref="HtlcRecord.KnownPreimage"/>
    /// or fulfill, live or archived; a preimage seen on the downstream chain is staged there too). The switch cannot
    /// write the upstream fulfill once this channel is closed, so the downstream record is the only place it is left.
    /// Never an invoice preimage alone.
    /// </summary>
    private async Task<byte[]?> GetIncomingPreimageAsync(RemoteCommitContext context, HtlcRecord? record)
    {
        if (record is null)
            return null;

        if (record.Removal is { IsFulfill: true, PaymentPreimage: { } fulfilled })
            return fulfilled;

        // Accepted as our final hop by the switch (NL-316/NL-322): the preimage it persisted on this HTLC's record
        var unitOfWork = context.UnitOfWork;
        if (await FinalHopClaims.GetAcceptedPreimageAsync(unitOfWork, record) is { } accepted)
            return accepted;

        var channelId = context.Channel.ChannelId;
        var outgoing = (await unitOfWork.ChannelStateDbRepository.FindHtlcsByOriginAsync(
                            HtlcOrigin.Forwarded(channelId, record.Id))).ToList();
        var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(channelId, record.Id);
        if (circuit is { OutgoingChannelId: { } circuitChannel, OutgoingHtlcId: { } circuitHtlc })
            outgoing.Add((circuitChannel, new HtlcKey(HtlcDirection.Outgoing, circuitHtlc)));

        foreach (var (outgoingChannelId, key) in outgoing.Distinct())
        {
            var outgoingRecord = await FindHtlcRecordAsync(unitOfWork, outgoingChannelId, key);
            if (OutgoingPreimage(outgoingRecord) is { } preimage && Hashes(preimage, record.PaymentHash))
                return preimage;
        }

        if (circuit is { Status: ForwardCircuitStatus.Fulfilled })
            _logger.LogError("Channel {ChannelId}: the forward of HTLC {HtlcId} was fulfilled downstream but its "
                           + "preimage is not stored; the HTLC cannot be claimed on chain", channelId, record.Id);
        return null;
    }

    /// <summary>
    /// Whether the upstream of our offered HTLC <paramref name="htlcId"/> no longer needs its event: for a forward,
    /// the incoming HTLC has its removal (or is gone), or its channel is not open any more and the circuit is resolved
    /// (that channel's own resolver takes the preimage from here); for our payment, the payment is final. Until then
    /// (and for an HTLC without a stored origin) the event is raised every round.
    /// </summary>
    private async Task<bool> IsUpstreamResolvedAsync(RemoteCommitContext context, ulong htlcId)
    {
        if (context.UpstreamResolved.TryGetValue(htlcId, out var known))
            return known;

        var resolved = await ComputeUpstreamResolvedAsync(context, htlcId);
        context.UpstreamResolved[htlcId] = resolved;
        return resolved;
    }

    private async Task<bool> ComputeUpstreamResolvedAsync(RemoteCommitContext context, ulong htlcId)
    {
        var unitOfWork = context.UnitOfWork;
        var origin = await unitOfWork.ChannelStateDbRepository.GetHtlcOriginAsync(
                         context.Channel.ChannelId, new HtlcKey(HtlcDirection.Outgoing, htlcId));
        switch (origin)
        {
            case
            {
                Kind: HtlcOriginKind.Forwarded, IncomingChannelId: { } incomingChannelId,
                IncomingHtlcId: { } incomingHtlcId
            }:
                {
                    var incomingChannel = await unitOfWork.ChannelDbRepository.GetByIdAsync(incomingChannelId);
                    var incoming = incomingChannel?.Commitments?.GetHtlc(HtlcDirection.Incoming, incomingHtlcId);
                    if (incoming is null || incoming.Removal is not null || HtlcStateTable.IsFinal(incoming.State))
                        return true;

                    if (incomingChannel!.State == ChannelState.Open)
                        return false;

                    var circuit = await unitOfWork.ForwardCircuitDbRepository.GetByIncomingAsync(incomingChannelId,
                                      incomingHtlcId);
                    return circuit is { Status: ForwardCircuitStatus.Fulfilled or ForwardCircuitStatus.Failed };
                }

            case { Kind: HtlcOriginKind.Local, PaymentHash: { } paymentHash }:
                {
                    var payment = await unitOfWork.PaymentDbRepository.GetByPaymentHashAsync(paymentHash);
                    return payment is null || payment.Status != PaymentStatus.InFlight;
                }

            default:
                // No origin stored (an HTLC offered before NL-250): the switch decides, so keep raising
                return false;
        }
    }

    /// <summary>An HTLC record of a channel: the live one, else its archived (settled, unpruned) row.</summary>
    private static async Task<HtlcRecord?> FindHtlcRecordAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                               HtlcKey key)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        if (channel?.Commitments is not { } commitments)
            return null;

        if (commitments.GetHtlc(key.Direction, key.Id) is { } live)
            return live;

        var persisted = await unitOfWork.ChannelStateDbRepository.LoadAsync(channelId, commitments.Params);
        return persisted?.SettledHtlcs.FirstOrDefault(h => h.Key == key);
    }

    /// <summary>
    /// Fulfills our offered HTLC upstream (B5-RMT-LO-01/03): the preimage is staged into the HTLC's record first when
    /// it is not stored there yet (BOLT2 I10), in the same save that precedes the event.
    /// </summary>
    private void AddFulfill(RemoteCommitContext context, SpecHtlc htlc, Secret preimage,
                            List<OutputResolverAction> actions)
    {
        var channelId = context.Channel.ChannelId;
        var record = context.Commitments?.GetHtlc(HtlcDirection.Outgoing, htlc.Id);
        if (record is not null && record.KnownPreimage != preimage && context.StagedPreimages.Add(htlc.Id))
        {
            var memory = _channelMemoryRepository;
            actions.Add(new StageWriteAction($"preimage of HTLC {htlc.Id} of channel {channelId}",
                                             (unitOfWork, _) => StageKnownPreimageAsync(unitOfWork, memory, channelId,
                                                                                        htlc.Id, preimage)));
        }

        actions.Add(new RaiseChannelEventAction(RemoteHtlcSwitchEvents.Fulfilled(channelId, htlc.Id, htlc.PaymentHash,
                                                                                 preimage)));
    }

    /// <summary>
    /// Fails our offered HTLC upstream (B5-RMT-LO-02/03): a failure the peer had sent (not irrevocable yet when the
    /// channel closed) keeps its reason; otherwise it timed out on chain.
    /// </summary>
    private static void AddFail(RemoteCommitContext context, SpecHtlc htlc, HtlcRecord? record,
                                List<OutputResolverAction> actions)
    {
        var channelId = context.Channel.ChannelId;
        IChannelDomainEvent failed = record?.Removal is
        {
            Kind: HtlcRemovalKind.Fail or HtlcRemovalKind.FailMalformed
        } failure
                                         ? new OutgoingHtlcFailed(channelId, htlc.Id, htlc.PaymentHash, failure)
                                         : RemoteHtlcSwitchEvents.OnchainTimeout(channelId, htlc.Id,
                                                                                 htlc.PaymentHash);
        actions.Add(new RaiseChannelEventAction(failed));
    }

    /// <summary>
    /// Stages <see cref="HtlcRecord.KnownPreimage"/> of our offered HTLC in the round's unit of work, and puts it into
    /// the loaded channel's snapshot so the switch's replays see it.
    /// </summary>
    private static async Task StageKnownPreimageAsync(IUnitOfWork unitOfWork, IChannelMemoryRepository? memory,
                                                      ChannelId channelId, ulong htlcId, Secret preimage)
    {
        var channel = await unitOfWork.ChannelDbRepository.GetByIdAsync(channelId);
        if (channel?.Commitments is not { } commitments
         || commitments.GetHtlc(HtlcDirection.Outgoing, htlcId) is not { } record
         || record.KnownPreimage == preimage)
            return;

        var updated = record with { KnownPreimage = preimage };
        await unitOfWork.ChannelStateDbRepository.ApplyAsync(WithRecord(commitments, updated),
                                                             new ChannelTransition([updated], [], [], false, false,
                                                                                   false, false));

        if (memory is not null && memory.TryGetChannel(channelId, out var loaded)
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

    /// <summary>Our offered HTLCs that are not final (their resolution may still be pending upstream).</summary>
    private static IEnumerable<HtlcRecord> OpenOutgoingHtlcs(RemoteCommitContext context) =>
        context.Commitments?.Htlcs.Values.Where(h => h.Direction == HtlcDirection.Outgoing
                                                  && !HtlcStateTable.IsFinal(h.State))
     ?? [];

    private static SpecHtlc ToSpec(HtlcRecord record) =>
        new(record.Direction, record.Id, record.AmountMsat, record.PaymentHash, record.CltvExpiry);

    /// <summary>The preimage the peer revealed for our offered HTLC (off chain, or on chain once staged).</summary>
    private static byte[]? OutgoingPreimage(HtlcRecord? record) =>
        record?.KnownPreimage is { } known
            ? (byte[])known
            : record?.Removal is { IsFulfill: true, PaymentPreimage: { } fulfilled }
                ? (byte[])fulfilled
                : null;

    private static CompactPubKey RequirePoint(OutputDescriptorData data) =>
        data.PerCommitmentPoint
     ?? throw new InvalidOperationException("An HTLC claim needs the peer's per-commitment point");

    private static void ReplaceRow(RemoteCommitContext context, OutputResolutionModel row,
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

    private static bool Hashes(byte[] preimage, Hash paymentHash) =>
        System.Security.Cryptography.SHA256.HashData(preimage).AsSpan().SequenceEqual((byte[])paymentHash);

    private static uint Depth(uint tip, uint height) => tip >= height ? tip - height + 1 : 0;

    /// <summary>A txid in the display (RPC) byte order, for logs (NL-275).</summary>
    private static string Display(TxId txId) => new uint256((byte[])txId).ToString();

    private sealed class RemoteCommitContext(
        IUnitOfWork unitOfWork,
        ChannelModel channel,
        ChannelCloseModel close,
        RemoteCommit? commit,
        CommitmentOutputMap? map,
        List<OutputResolutionModel> rows,
        uint height)
    {
        public IUnitOfWork UnitOfWork { get; } = unitOfWork;
        public ChannelModel Channel { get; } = channel;
        public ChannelCommitments? Commitments => Channel.Commitments;
        public ChannelCloseModel Close { get; } = close;
        public RemoteCommit? Commit { get; } = commit;
        public CommitmentOutputMap? Map { get; } = map;
        public List<OutputResolutionModel> Rows { get; } = rows;
        public uint Height { get; } = height;
        public TxId CommitmentTxId => Close.CommitmentTransactionId;
        public bool IsDataLoss => Commit is null;
        public byte[]? Destination { get; set; }
        public Dictionary<ulong, bool> UpstreamResolved { get; } = [];
        public HashSet<ulong> StagedPreimages { get; } = [];

        public OutputResolutionModel? GetRow(TxId txId, uint vout) =>
            Rows.FirstOrDefault(r => r.TransactionId == txId && r.OutputIndex == vout);
    }

    #endregion
}