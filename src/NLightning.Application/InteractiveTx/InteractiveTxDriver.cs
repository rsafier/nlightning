using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace NLightning.Application.InteractiveTx;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Quiescence;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Payloads;
using Domain.Protocol.Tlv;
using Interfaces;
using Models;

/// <summary>
/// The interactive-tx driver (splicing plan §3.9, IT4-T1): runs one <see cref="IInteractiveTxNegotiation"/> per channel
/// under the channel's lock, does the asynchronous work between the engine's steps (the <c>require_confirmed_inputs</c>
/// check, building, signing, persistence) and calls the negotiation's <see cref="IInteractiveTxHost"/> for the shared
/// funding spec, the commitment step, the completion and the abort.
/// </summary>
/// <remarks>
/// <para>BOLT 2 "Interactive Transaction Construction" rules the driver enforces itself (the engine owns the rest):
/// no negotiation in progress → <c>tx_abort</c> for types 66-73; <c>tx_abort</c> is echoed unless it echoes ours
/// (stale messages that arrive while our <c>tx_abort</c> waits for its echo are ignored); never <c>tx_abort</c> after
/// our <c>tx_signatures</c> (the negotiation is kept); a negotiation is stored in the save that precedes sending our
/// <c>commitment_signed</c> for it; the <c>tx_init_rbf</c> feerate rule (IT-RBF-01) before the host is asked.</para>
/// <para>State is in memory, keyed by channel id, and only touched under the channel's lock; different channels run
/// concurrently.</para>
/// </remarks>
public sealed class InteractiveTxDriver : IInteractiveTxDriver
{
    private const string NoNegotiationText = "no interactive-tx negotiation in progress";

    private readonly ConcurrentDictionary<ChannelId, ChannelEntry> _channels = new();
    private readonly IInteractiveTxEngine _engine;
    private readonly IInteractiveTxBuilder _builder;
    private readonly IInteractiveTxContributor _contributor;
    private readonly IPrevTxInspector _prevTxInspector;
    private readonly ILogger<InteractiveTxDriver> _logger;
    private readonly IQuiescenceService? _quiescenceService;
    private readonly TimeProvider _timeProvider;

    public InteractiveTxDriver(IInteractiveTxEngine engine, IInteractiveTxBuilder builder,
                               IInteractiveTxContributor contributor, IPrevTxInspector prevTxInspector,
                               ILogger<InteractiveTxDriver> logger, IQuiescenceService? quiescenceService = null,
                               TimeProvider? timeProvider = null)
    {
        _engine = engine;
        _builder = builder;
        _contributor = contributor;
        _prevTxInspector = prevTxInspector;
        _logger = logger;
        _quiescenceService = quiescenceService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The <c>tx_init_rbf</c> feerate floor (BOLT 2, IT-RBF-01 with the additive rule of bolts#1327): the maximum of
    /// ⌊25/24 × previous⌋ and previous + 25 sat/kw.
    /// </summary>
    public static ulong GetMinimumRbfFeeratePerKw(uint previousFeeratePerKw) =>
        Math.Max((ulong)previousFeeratePerKw * 25 / 24, (ulong)previousFeeratePerKw + 25);

    /// <summary>
    /// Our <c>tx_abort</c> for <paramref name="channelId"/> with a printable ASCII reason.
    /// </summary>
    public static TxAbortMessage CreateTxAbort(ChannelId channelId, string reason) =>
        new(new TxAbortPayload(channelId, Encoding.ASCII.GetBytes(ToPrintableAscii(reason))));

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> StartAsync(InteractiveTxTerms terms, IInteractiveTxHost host,
                                                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(host);

        var entry = _channels.GetOrAdd(terms.ChannelId, _ => new ChannelEntry(terms.RemoteNodeId));
        if (entry.Current is not null)
            throw new InvalidOperationException(
                $"An interactive-tx negotiation is already in progress on channel {terms.ChannelId}");
        if (entry.Host is not null && !ReferenceEquals(entry.Host, host))
            // A new protocol run on the channel: the earlier completed attempts belong to the earlier host
            entry.Completed.Clear();

        entry.Host = host;
        entry.RemoteNodeId = terms.RemoteNodeId;
        entry.AwaitingAbortEcho = false;
        entry.PendingRbf = null;

        var attempt = await CreateAttemptAsync(entry, terms, cancellationToken);
        entry.Current = attempt;
        LogStarted(attempt);

        return terms.IsInitiator ? await StartAttemptAsync(entry, attempt, cancellationToken) : [];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> ReceiveAsync(IChannelMessage message, CompactPubKey peerPubKey,
                                                                   IUnitOfWork unitOfWork,
                                                                   CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(unitOfWork);

        var channelId = message.Payload.ChannelId;
        _channels.TryGetValue(channelId, out var entry);
        if (entry is not null && entry.RemoteNodeId != peerPubKey)
        {
            // Not this peer's negotiation: answer as if there were none, and leave ours alone
            _logger.LogWarning("Interactive-tx message {MessageType} for channel {ChannelId} from {Peer}, which is "
                             + "not the channel's peer", message.Type, channelId, peerPubKey);
            return message is TxAbortMessage ? [] : [CreateTxAbort(channelId, NoNegotiationText)];
        }

        return message switch
        {
            TxAbortMessage abort => await ReceiveAbortAsync(channelId, entry, abort, peerPubKey, unitOfWork,
                                                            cancellationToken),
            TxInitRbfMessage initRbf => await ReceiveInitRbfAsync(channelId, entry, initRbf, peerPubKey,
                                                                  unitOfWork, cancellationToken),
            TxAckRbfMessage ackRbf => await ReceiveAckRbfAsync(channelId, entry, ackRbf, peerPubKey, unitOfWork,
                                                               cancellationToken),
            TxAddInputMessage or TxAddOutputMessage or TxRemoveInputMessage or TxRemoveOutputMessage
                or TxCompleteMessage or TxSignaturesMessage =>
                await ReceiveNegotiationMessageAsync(channelId, entry, message, peerPubKey, unitOfWork,
                                                     cancellationToken),
            _ => throw new ArgumentException($"{message.Type} is not an interactive-tx message", nameof(message))
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> OnCommitmentSignedReceivedAsync(
        ChannelId channelId, IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        if (!_channels.TryGetValue(channelId, out var entry) || entry.Current is not { Model: not null } attempt
         || attempt.Model.CommitmentSignedReceived)
            throw new InvalidOperationException(
                $"No constructed interactive-tx negotiation waits for commitment_signed on channel {channelId}");

        attempt.Negotiation = attempt.Negotiation.OnCommitmentSignedReceived();
        attempt.Model = attempt.Model with
        {
            CommitmentSignedReceived = true,
            State = attempt.Negotiation.State
        };

        // IT-SIG-01/03: after a valid commitment_signed, we send first by the order rule, or once the peer's arrived
        IReadOnlyList<IChannelMessage> outbound = [];
        if (!attempt.Model.TxSignaturesSent
         && (attempt.Negotiation.RemoteWitnesses is not null || attempt.Negotiation.SendsTxSignaturesFirst()))
            outbound = await SendOurSignaturesAsync(entry, attempt, unitOfWork, cancellationToken);

        await SaveModelAsync(attempt, unitOfWork);
        return outbound;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> RequestRbfAsync(InteractiveTxTerms terms,
                                                                LightningMoney fundingOutputContribution,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (!terms.IsInitiator)
            throw new InvalidOperationException("The sender of tx_init_rbf is the initiator of the new attempt");
        if (!_channels.TryGetValue(terms.ChannelId, out var entry) || entry.Host is null || entry.Completed.Count == 0)
            throw new InvalidOperationException(
                $"No completed interactive-tx negotiation to replace on channel {terms.ChannelId}");
        if (entry.Current is not null || entry.PendingRbf is not null)
            throw new InvalidOperationException(
                $"An interactive-tx negotiation is already in progress on channel {terms.ChannelId}");

        var minimum = GetMinimumRbfFeeratePerKw(entry.Completed[^1].FeeratePerKw);
        if (terms.FeeratePerKw < minimum)
            throw new InvalidOperationException(
                $"[IT-RBF-01] tx_init_rbf feerate {terms.FeeratePerKw} sat/kw is below the minimum {minimum} sat/kw");

        entry.PendingRbf = terms;
        entry.AwaitingAbortEcho = false;

        var message = new TxInitRbfMessage(new TxInitRbfPayload(terms.ChannelId, terms.FeeratePerKw, terms.Locktime),
                                           CreateContributionTlv(fundingOutputContribution),
                                           terms.LocalRequiresConfirmedInputs ? new RequireConfirmedInputsTlv() : null);
        return Task.FromResult<IReadOnlyList<IChannelMessage>>([message]);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IChannelMessage>> AbortAsync(ChannelId channelId, string reason,
                                                                 IUnitOfWork unitOfWork,
                                                                 CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        if (!_channels.TryGetValue(channelId, out var entry))
            return [];

        if (entry.Current is not { } attempt)
        {
            if (entry.PendingRbf is null)
                return [];

            // Our tx_init_rbf is withdrawn
            entry.PendingRbf = null;
            entry.AwaitingAbortEcho = true;
            return [CreateTxAbort(channelId, reason)];
        }

        if (OurSignaturesSent(attempt))
            throw new InvalidOperationException(
                $"[IT-ABT-01] tx_abort after our tx_signatures on channel {channelId}");

        return await AbortOurselvesAsync(entry, attempt, reason, unitOfWork, cancellationToken);
    }

    /// <inheritdoc />
    public async Task OnDisconnectedAsync(ChannelId channelId, CancellationToken cancellationToken = default)
    {
        if (!_channels.TryGetValue(channelId, out var entry))
            return;

        entry.AwaitingAbortEcho = false;
        entry.PendingRbf = null;
        if (entry.Current is { Model: null } attempt)
        {
            // BOLT 2: a negotiation without our commitment_signed is not remembered across a disconnection
            entry.Current = null;
            await ReleaseContributionAsync(entry, attempt.Contribution, cancellationToken);
            await entry.Host!.OnAbortedAsync(channelId, "disconnected", cancellationToken);
            _logger.LogInformation("Interactive-tx negotiation {SessionId} on channel {ChannelId} forgotten on "
                                 + "disconnection", attempt.SessionId, channelId);
        }

        RemoveIfIdle(channelId, entry);
    }

    /// <inheritdoc />
    public async Task ResumeAsync(InteractiveTxSessionModel model, InteractiveTxTerms terms, IInteractiveTxHost host,
                                  CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(host);

        var entry = _channels.GetOrAdd(model.ChannelId, _ => new ChannelEntry(terms.RemoteNodeId));
        if (entry.Current is not null)
            throw new InvalidOperationException(
                $"An interactive-tx negotiation is already in progress on channel {model.ChannelId}");

        entry.Host = host;
        entry.RemoteNodeId = terms.RemoteNodeId;
        var parameters = await CreateParametersAsync(entry, terms, model.LocalContribution, cancellationToken);
        entry.Current = new Attempt(model.SessionId, terms, _engine.Restore(model, parameters),
                                    model.LocalContribution)
        { Model = model };
    }

    /// <inheritdoc />
    public bool IsNegotiating(ChannelId channelId) =>
        _channels.TryGetValue(channelId, out var entry) && entry.Current is not null;

    /// <inheritdoc />
    public InteractiveTxNegotiationInfo? GetInfo(ChannelId channelId)
    {
        if (!_channels.TryGetValue(channelId, out var entry))
            return null;

        var attempt = entry.Current;
        return new InteractiveTxNegotiationInfo(channelId, attempt?.SessionId, attempt?.Negotiation.State,
                                                attempt?.Model is not null, entry.AwaitingAbortEcho,
                                                entry.PendingRbf is not null,
                                                entry.Completed.Select(c => c.Transaction).ToList(),
                                                attempt?.Negotiation.Inputs ?? [], attempt?.Negotiation.Outputs ?? []);
    }

    private async Task<IReadOnlyList<IChannelMessage>> ReceiveNegotiationMessageAsync(
        ChannelId channelId, ChannelEntry? entry, IChannelMessage message, CompactPubKey peerPubKey,
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        if (entry?.Current is not { } attempt)
        {
            if (message is TxSignaturesMessage signatures && entry is not null
             && entry.Completed.Any(c => ((byte[])c.Transaction.TxId).AsSpan().SequenceEqual(signatures.Payload.TxId)))
            {
                // A retransmission for an attempt that is already fully signed
                _logger.LogDebug("Ignoring tx_signatures for the completed negotiation on channel {ChannelId}",
                                 channelId);
                return [];
            }

            return NoNegotiation(channelId, entry, peerPubKey, message.Type);
        }

        // IT-SIG-03: tx_signatures only after our commitment_signed was sent (the negotiation is stored from then on)
        if (message is TxSignaturesMessage && attempt.Model is null)
            return await RejectAsync(entry, attempt, "tx_signatures before commitment_signed", unitOfWork,
                                     cancellationToken);

        // BOLT 2: with require_confirmed_inputs sent, every input the peer adds must be confirmed
        if (message is TxAddInputMessage { SharedInputTxIdTlv: null } addInput
         && attempt.Terms.LocalRequiresConfirmedInputs)
        {
            var inspection = _prevTxInspector.Inspect(addInput.Payload.PrevTx, addInput.Payload.PrevTxVout);
            if (inspection is { IsValid: true, TxId: { } prevTxId }
             && !await _prevTxInspector.IsConfirmedAsync(prevTxId, cancellationToken))
                return await RejectAsync(entry, attempt,
                                         $"input {addInput.Payload.SerialId} is unconfirmed but "
                                       + "require_confirmed_inputs was sent", unitOfWork, cancellationToken);
        }

        InteractiveTxNegotiationStep step;
        try
        {
            step = attempt.Negotiation.Receive(message, _prevTxInspector);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException
                                      or OverflowException)
        {
            // A message the negotiation cannot use: tx_abort, never a channel failure
            return await RejectAsync(entry, attempt, $"invalid {Enum.GetName(message.Type)}: {e.Message}",
                                     unitOfWork, cancellationToken);
        }

        attempt.Negotiation = step.Next;
        if (step.Aborted)
        {
            // A rule the peer broke: our tx_abort goes out and waits for its echo
            LogAborted(attempt, step.AbortReason!, step.RequirementId, true);
            entry.AwaitingAbortEcho = true;
            await FinishAbortAsync(entry, attempt, step.AbortReason!, unitOfWork, cancellationToken);
            return EnsureTxAbort(channelId, step.Outbound, step.AbortReason!);
        }

        var outbound = new List<IChannelMessage>(step.Outbound);
        if (step.NegotiationComplete)
        {
            outbound.AddRange(await ConstructAsync(entry, attempt, unitOfWork, cancellationToken));
        }
        else if (message is TxSignaturesMessage)
        {
            outbound.AddRange(await AfterRemoteSignaturesAsync(entry, attempt, unitOfWork, cancellationToken));
        }

        return outbound;
    }

    private async Task<IReadOnlyList<IChannelMessage>> ReceiveAbortAsync(
        ChannelId channelId, ChannelEntry? entry, TxAbortMessage abort, CompactPubKey peerPubKey,
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var data = DescribeAbortData(abort.Payload.Data);
        if (entry?.Current is not { } attempt)
        {
            if (entry is { AwaitingAbortEcho: true })
            {
                // The echo of our tx_abort: the peer has seen it, the negotiation is over on both sides
                entry.AwaitingAbortEcho = false;
                _logger.LogDebug("tx_abort echoed by {Peer} on channel {ChannelId}", peerPubKey, channelId);
                RemoveIfIdle(channelId, entry);
                return [];
            }

            // BOLT 2: the receiver MUST echo tx_abort if it has not sent one (also ends a quiescence without a
            // negotiation, e.g. a withdrawn tx_init_rbf of the peer's or a quiescence the peer gives up)
            if (entry is not null)
            {
                entry.PendingRbf = null;
                RemoveIfIdle(channelId, entry);
            }

            _logger.LogInformation("tx_abort from {Peer} on channel {ChannelId} without a negotiation ({Data}); "
                                 + "echoing it", peerPubKey, channelId, data);
            _quiescenceService?.Terminate(channelId, QuiescenceEndReason.TxAbort);
            return [CreateTxAbort(channelId, "tx_abort acknowledged")];
        }

        if (OurSignaturesSent(attempt))
        {
            // IT-ABT-01: we MUST NOT forget a negotiation after our tx_signatures (the peer may still broadcast
            // it), and a sending node MUST NOT send tx_abort after tx_signatures, so there is no echo either
            _logger.LogWarning("tx_abort from {Peer} on channel {ChannelId} after our tx_signatures ({Data}); keeping "
                             + "negotiation {SessionId} until an input of {TxId} is spent", peerPubKey, channelId,
                               data, attempt.SessionId, attempt.Negotiation.ConstructedTx?.TxId);
            return [];
        }

        IReadOnlyList<IChannelMessage> outbound;
        try
        {
            var step = attempt.Negotiation.Receive(abort, _prevTxInspector);
            attempt.Negotiation = step.Next;
            outbound = step.Outbound;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _logger.LogDebug(e, "Engine refused tx_abort on channel {ChannelId}; echoing it", channelId);
            outbound = [CreateTxAbort(channelId, "tx_abort acknowledged")];
        }

        var reason = $"peer sent tx_abort: {data}";
        LogAborted(attempt, reason, "IT-ABT-01", false);
        await FinishAbortAsync(entry, attempt, reason, unitOfWork, cancellationToken);
        return outbound;
    }

    private async Task<IReadOnlyList<IChannelMessage>> ReceiveInitRbfAsync(
        ChannelId channelId, ChannelEntry? entry, TxInitRbfMessage initRbf, CompactPubKey peerPubKey,
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        if (entry is null || entry.Host is null || entry.Completed.Count == 0 || entry.AwaitingAbortEcho)
            return NoNegotiation(channelId, entry, peerPubKey, initRbf.Type);

        if (entry.Current is { } running)
        {
            if (OurSignaturesSent(running))
            {
                _logger.LogWarning("Ignoring tx_init_rbf on channel {ChannelId}: negotiation {SessionId} waits for "
                                 + "the peer's tx_signatures", channelId, running.SessionId);
                return [];
            }

            return await RejectAsync(entry, running, "tx_init_rbf during a negotiation", unitOfWork,
                                     cancellationToken);
        }

        if (entry.PendingRbf is not null)
        {
            // Both sides asked for an RBF at once: ours is withdrawn and theirs rejected, either may retry
            entry.PendingRbf = null;
            return RejectRbf(channelId, entry, "simultaneous tx_init_rbf");
        }

        var minimum = GetMinimumRbfFeeratePerKw(entry.Completed[^1].FeeratePerKw);
        if (initRbf.Payload.Feerate < minimum)
            return RejectRbf(channelId, entry,
                             $"[IT-RBF-01] feerate {initRbf.Payload.Feerate} sat/kw is below {minimum} sat/kw");

        var decision = await entry.Host.OnRbfRequestedAsync(initRbf, entry.Completed.Select(c => c.Transaction).ToList(),
                                                            cancellationToken);
        if (decision.Terms is null)
            return RejectRbf(channelId, entry, decision.RejectReason ?? "rbf rejected");

        var terms = decision.Terms with
        {
            ChannelId = channelId,
            IsInitiator = false,
            FeeratePerKw = initRbf.Payload.Feerate,
            Locktime = initRbf.Payload.Locktime,
            RemoteRequiresConfirmedInputs = initRbf.RequireConfirmedInputsTlv is not null
        };

        Attempt attempt;
        try
        {
            attempt = await CreateAttemptAsync(entry, terms, cancellationToken);
        }
        catch (InsufficientFundsException e)
        {
            // BOLT 2: fail the negotiation when we cannot provide (confirmed) inputs
            return RejectRbf(channelId, entry, $"cannot fund the rbf attempt: {e.Message}");
        }

        entry.Current = attempt;
        LogStarted(attempt);
        return
        [
            new TxAckRbfMessage(new TxAckRbfPayload(channelId),
                                CreateContributionTlv(decision.FundingOutputContribution),
                                terms.LocalRequiresConfirmedInputs ? new RequireConfirmedInputsTlv() : null)
        ];
    }

    private async Task<IReadOnlyList<IChannelMessage>> ReceiveAckRbfAsync(
        ChannelId channelId, ChannelEntry? entry, TxAckRbfMessage ackRbf, CompactPubKey peerPubKey,
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        if (entry?.PendingRbf is not { } pending)
        {
            if (entry?.Current is { } running && !OurSignaturesSent(running))
                return await RejectAsync(entry, running, "unexpected tx_ack_rbf", unitOfWork, cancellationToken);

            return NoNegotiation(channelId, entry, peerPubKey, ackRbf.Type);
        }

        entry.PendingRbf = null;
        var terms = pending with { RemoteRequiresConfirmedInputs = ackRbf.RequireConfirmedInputsTlv is not null };

        Attempt attempt;
        try
        {
            attempt = await CreateAttemptAsync(entry, terms, cancellationToken);
        }
        catch (InsufficientFundsException e)
        {
            return RejectRbf(channelId, entry, $"cannot fund the rbf attempt: {e.Message}");
        }

        entry.Current = attempt;
        LogStarted(attempt);
        return await StartAttemptAsync(entry, attempt, cancellationToken);
    }

    private async Task<IReadOnlyList<IChannelMessage>> StartAttemptAsync(ChannelEntry entry, Attempt attempt,
                                                                         CancellationToken cancellationToken)
    {
        var step = attempt.Negotiation.Start();
        attempt.Negotiation = step.Next;
        if (!step.Aborted)
            return step.Outbound;

        // Our own contribution broke a rule (a bug in the host or the contributor): nothing was sent yet
        LogAborted(attempt, step.AbortReason!, step.RequirementId, true);
        entry.Current = null;
        await ReleaseContributionAsync(entry, attempt.Contribution, cancellationToken);
        await entry.Host!.OnAbortedAsync(attempt.Terms.ChannelId, step.AbortReason!, cancellationToken);
        throw new InvalidOperationException(
            $"The interactive-tx negotiation could not start on channel {attempt.Terms.ChannelId}: {step.AbortReason}");
    }

    /// <summary>
    /// After the second consecutive <c>tx_complete</c>: build the transaction, run the host's commitment step and store
    /// the negotiation in the save that precedes our <c>commitment_signed</c> (BOLT 2: remember the negotiation).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> ConstructAsync(ChannelEntry entry, Attempt attempt,
                                                                      IUnitOfWork unitOfWork,
                                                                      CancellationToken cancellationToken)
    {
        try
        {
            var transaction = _builder.Build(attempt.Terms.Locktime, attempt.Negotiation.Inputs,
                                             attempt.Negotiation.Outputs);
            attempt.Negotiation = attempt.Negotiation.WithConstructedTransaction(transaction);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return await RejectAsync(entry, attempt, $"the negotiated transaction is invalid: {e.Message}",
                                     unitOfWork, cancellationToken);
        }

        var model = CreateModel(entry, attempt) with { CommitmentSignedSent = true };
        IReadOnlyList<IChannelMessage> commitment;
        try
        {
            commitment = await entry.Host!.CreateCommitmentSignedAsync(model, unitOfWork, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Commitment step of interactive-tx negotiation {SessionId} on channel {ChannelId} "
                                + "failed", attempt.SessionId, attempt.Terms.ChannelId);
            return await RejectAsync(entry, attempt, "cannot sign the commitment for the negotiated funding",
                                     unitOfWork, cancellationToken);
        }

        unitOfWork.InteractiveTxSessionDbRepository.Add(model);
        await unitOfWork.SaveChangesAsync();
        attempt.Model = model;

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Interactive-tx negotiation {SessionId} on channel {ChannelId} constructed {TxId}",
                                   attempt.SessionId, attempt.Terms.ChannelId,
                                   attempt.Negotiation.ConstructedTx!.TxId);

        return commitment;
    }

    /// <summary>The peer's valid tx_signatures was applied: store it, answer with ours, or complete.</summary>
    private async Task<IReadOnlyList<IChannelMessage>> AfterRemoteSignaturesAsync(ChannelEntry entry, Attempt attempt,
                                                                                  IUnitOfWork unitOfWork,
                                                                                  CancellationToken cancellationToken)
    {
        var negotiation = attempt.Negotiation;
        attempt.Model = attempt.Model! with
        {
            TxSignaturesReceived = true,
            TheirWitnesses = negotiation.RemoteWitnesses,
            TheirSharedInputSignature = negotiation.RemoteSharedInputSignature,
            State = negotiation.State
        };

        IReadOnlyList<IChannelMessage> outbound = [];
        if (negotiation.State == InteractiveTxSessionState.Signed)
            outbound = await CompleteAsync(entry, attempt, unitOfWork, cancellationToken);
        else if (!attempt.Model.TxSignaturesSent && attempt.Model.CommitmentSignedReceived)
            // BOLT 2: MUST reply with our tx_signatures if not already transmitted
            outbound = await SendOurSignaturesAsync(entry, attempt, unitOfWork, cancellationToken);

        await SaveModelAsync(attempt, unitOfWork);
        return outbound;
    }

    /// <summary>Signs and records our tx_signatures (not saved: the caller saves before sending).</summary>
    private async Task<IReadOnlyList<IChannelMessage>> SendOurSignaturesAsync(ChannelEntry entry, Attempt attempt,
                                                                              IUnitOfWork unitOfWork,
                                                                              CancellationToken cancellationToken)
    {
        var transaction = attempt.Negotiation.ConstructedTx
                       ?? throw new InvalidOperationException("The negotiated transaction is not constructed");

        var witnesses = attempt.Contribution.Inputs.Count == 0
                                               ? []
                                               : await _contributor.SignAsync(transaction, attempt.Contribution,
                                                                              GetOtherSpentOutputs(transaction),
                                                                              cancellationToken);
        var sharedInputSignature = transaction.Inputs.Any(i => i.IsShared)
                                       ? await entry.Host!.SignSharedInputAsync(transaction, cancellationToken)
                                       : null;

        var step = attempt.Negotiation.SendTxSignatures(witnesses, sharedInputSignature);
        attempt.Negotiation = step.Next;
        attempt.Model = attempt.Model! with
        {
            TxSignaturesSent = true,
            OurWitnesses = witnesses,
            OurSharedInputSignature = sharedInputSignature,
            State = step.Next.State
        };

        var outbound = new List<IChannelMessage>(step.Outbound);
        if (step.Next.State == InteractiveTxSessionState.Signed)
            outbound.AddRange(await CompleteAsync(entry, attempt, unitOfWork, cancellationToken));

        return outbound;
    }

    /// <summary>Both tx_signatures exchanged: finalize the transaction and hand it to the host.</summary>
    private async Task<IReadOnlyList<IChannelMessage>> CompleteAsync(ChannelEntry entry, Attempt attempt,
                                                                     IUnitOfWork unitOfWork,
                                                                     CancellationToken cancellationToken)
    {
        var transaction = attempt.Negotiation.ConstructedTx!;
        var model = attempt.Model!;
        var witnesses = new Dictionary<ulong, Witness>();
        AddWitnesses(witnesses, transaction, InteractiveTxParty.Local, model.OurWitnesses ?? []);
        AddWitnesses(witnesses, transaction, InteractiveTxParty.Remote, attempt.Negotiation.RemoteWitnesses ?? []);
        if (transaction.Inputs.FirstOrDefault(i => i.IsShared) is { } sharedInput)
            witnesses[sharedInput.SerialId] = entry.Host!.BuildSharedInputWitness(
                transaction,
                model.OurSharedInputSignature
             ?? throw new InvalidOperationException("Our shared_input_signature is missing"),
                attempt.Negotiation.RemoteSharedInputSignature
             ?? throw new InvalidOperationException("The peer's shared_input_signature is missing"));

        var signedTransaction = _builder.Finalize(transaction, witnesses);
        var completion = new InteractiveTxCompletion(model, transaction, signedTransaction, attempt.Terms.FeeratePerKw);
        var outbound = await entry.Host!.OnCompletedAsync(completion, unitOfWork, cancellationToken);

        entry.Completed.Add(new CompletedAttempt(attempt.SessionId, transaction, attempt.Terms.FeeratePerKw,
                                                 attempt.Contribution));
        entry.Current = null;
        _quiescenceService?.Terminate(attempt.Terms.ChannelId, QuiescenceEndReason.TxSignaturesExchanged);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Interactive-tx negotiation {SessionId} on channel {ChannelId} fully signed: {TxId}",
                                   attempt.SessionId, attempt.Terms.ChannelId, transaction.TxId);

        return outbound;
    }

    private async Task<IReadOnlyList<IChannelMessage>> RejectAsync(ChannelEntry entry, Attempt attempt, string reason,
                                                                   IUnitOfWork unitOfWork,
                                                                   CancellationToken cancellationToken)
    {
        if (!OurSignaturesSent(attempt))
            return await AbortOurselvesAsync(entry, attempt, reason, unitOfWork, cancellationToken);

        // IT-ABT-01: no tx_abort after our tx_signatures; the negotiation stays until an input is spent
        _logger.LogWarning("Ignoring an invalid message on channel {ChannelId} after our tx_signatures: {Reason}",
                           attempt.Terms.ChannelId, reason);
        return [];
    }

    private async Task<IReadOnlyList<IChannelMessage>> AbortOurselvesAsync(ChannelEntry entry, Attempt attempt,
                                                                           string reason, IUnitOfWork unitOfWork,
                                                                           CancellationToken cancellationToken)
    {
        IReadOnlyList<IChannelMessage> outbound;
        try
        {
            var step = attempt.Negotiation.Abort(reason);
            attempt.Negotiation = step.Next;
            outbound = step.Outbound;
        }
        catch (InvalidOperationException e)
        {
            _logger.LogDebug(e, "Engine refused to abort on channel {ChannelId}", attempt.Terms.ChannelId);
            outbound = [];
        }

        LogAborted(attempt, reason, null, true);
        entry.AwaitingAbortEcho = true;
        await FinishAbortAsync(entry, attempt, reason, unitOfWork, cancellationToken);
        return EnsureTxAbort(attempt.Terms.ChannelId, outbound, reason);
    }

    private async Task FinishAbortAsync(ChannelEntry entry, Attempt attempt, string reason, IUnitOfWork unitOfWork,
                                        CancellationToken cancellationToken)
    {
        entry.Current = null;
        await ReleaseContributionAsync(entry, attempt.Contribution, cancellationToken);
        if (attempt.Model is not null)
        {
            attempt.Model = attempt.Model with { State = InteractiveTxSessionState.Aborted };
            await SaveModelAsync(attempt, unitOfWork);
        }

        await entry.Host!.OnAbortedAsync(attempt.Terms.ChannelId, reason, cancellationToken);
        _quiescenceService?.Terminate(attempt.Terms.ChannelId, QuiescenceEndReason.TxAbort);
    }

    private IReadOnlyList<IChannelMessage> NoNegotiation(ChannelId channelId, ChannelEntry? entry,
                                                         CompactPubKey peerPubKey, MessageTypes messageType)
    {
        if (entry is { AwaitingAbortEcho: true })
        {
            // Sent before the peer saw our tx_abort: stale, the echo will follow
            _logger.LogDebug("Ignoring {MessageType} on channel {ChannelId} while our tx_abort waits for its echo",
                             messageType, channelId);
            return [];
        }

        _logger.LogInformation("{MessageType} from {Peer} on channel {ChannelId} without an interactive-tx "
                             + "negotiation; answering tx_abort", messageType, peerPubKey, channelId);
        _channels.GetOrAdd(channelId, _ => new ChannelEntry(peerPubKey)).AwaitingAbortEcho = true;
        return [CreateTxAbort(channelId, NoNegotiationText)];
    }

    private IReadOnlyList<IChannelMessage> RejectRbf(ChannelId channelId, ChannelEntry entry, string reason)
    {
        _logger.LogInformation("Rejecting the RBF on channel {ChannelId}: {Reason}", channelId, reason);
        entry.AwaitingAbortEcho = true;
        return [CreateTxAbort(channelId, reason)];
    }

    private async Task<Attempt> CreateAttemptAsync(ChannelEntry entry, InteractiveTxTerms terms,
                                                   CancellationToken cancellationToken)
    {
        var contribution = terms.Contribution
                        ?? (terms.ContributionRequest is { } request
                                ? await _contributor.ContributeAsync(request, cancellationToken)
                                : InteractiveTxContribution.Empty);
        try
        {
            var parameters = await CreateParametersAsync(entry, terms, contribution, cancellationToken);
            return new Attempt(Guid.NewGuid(), terms, _engine.Create(parameters), contribution);
        }
        catch
        {
            await ReleaseContributionAsync(entry, contribution, cancellationToken);
            throw;
        }
    }

    private async Task<InteractiveTxSessionParameters> CreateParametersAsync(
        ChannelEntry entry, InteractiveTxTerms terms, InteractiveTxContribution contribution,
        CancellationToken cancellationToken)
    {
        var sharedFunding = await entry.Host!.GetSharedFundingAsync(terms, cancellationToken);
        return new InteractiveTxSessionParameters(terms.ChannelId, terms.IsInitiator, terms.FeeratePerKw,
                                                  terms.Locktime, contribution, sharedFunding,
                                                  terms.LocalRequiresConfirmedInputs,
                                                  terms.RemoteRequiresConfirmedInputs, terms.LocalNodeId,
                                                  terms.RemoteNodeId,
                                                  entry.Completed.Select(c => c.Transaction).ToList());
    }

    private InteractiveTxSessionModel CreateModel(ChannelEntry entry, Attempt attempt)
    {
        var negotiation = attempt.Negotiation;
        return new InteractiveTxSessionModel
        {
            ChannelId = attempt.Terms.ChannelId,
            SessionId = attempt.SessionId,
            Purpose = entry.Host!.Purpose,
            IsInitiator = attempt.Terms.IsInitiator,
            FeeratePerKw = attempt.Terms.FeeratePerKw,
            Locktime = attempt.Terms.Locktime,
            Inputs = negotiation.Inputs.OrderBy(i => i.SerialId).ToList(),
            Outputs = negotiation.Outputs.OrderBy(o => o.SerialId).ToList(),
            LocalContribution = attempt.Contribution,
            ConstructedTx = negotiation.ConstructedTx,
            State = negotiation.State,
            CreatedAt = _timeProvider.GetUtcNow()
        };
    }

    private static async Task SaveModelAsync(Attempt attempt, IUnitOfWork unitOfWork)
    {
        await unitOfWork.InteractiveTxSessionDbRepository.UpdateAsync(attempt.Model!);
        await unitOfWork.SaveChangesAsync();
    }

    /// <summary>
    /// Releases a contribution's wallet reservation, unless a completed attempt still holds it (an RBF that re-adds
    /// the previous attempt's inputs keeps them reserved, IT-ABT-01).
    /// </summary>
    private async Task ReleaseContributionAsync(ChannelEntry entry, InteractiveTxContribution contribution,
                                                CancellationToken cancellationToken)
    {
        if (contribution.ReservationId is not { } reservationId
         || entry.Completed.Any(c => c.Contribution.ReservationId == reservationId))
            return;

        try
        {
            await _contributor.ReleaseAsync(contribution, cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The reservation is persisted; the orphaned-reservation sweep frees it at the next start
            _logger.LogError(e, "Could not release wallet reservation {ReservationId}", reservationId);
        }
    }

    private void RemoveIfIdle(ChannelId channelId, ChannelEntry entry)
    {
        if (entry is { Current: null, PendingRbf: null, AwaitingAbortEcho: false, Completed.Count: 0 })
            _channels.TryRemove(new KeyValuePair<ChannelId, ChannelEntry>(channelId, entry));
    }

    private static bool OurSignaturesSent(Attempt attempt) =>
        attempt.Model?.TxSignaturesSent == true
     || attempt.Negotiation.State is InteractiveTxSessionState.TxSignaturesSent or InteractiveTxSessionState.Signed;

    private static IReadOnlyList<SpentOutput> GetOtherSpentOutputs(ConstructedInteractiveTx transaction) =>
        transaction.Inputs
                   .Where(i => i.AddedBy != InteractiveTxParty.Local || i.IsShared)
                   .Select(i => new SpentOutput(i.PrevTxId, i.PrevTxVout, i.Amount, i.ScriptPubKey))
                   .ToList();

    private static void AddWitnesses(Dictionary<ulong, Witness> witnesses, ConstructedInteractiveTx transaction,
                                     InteractiveTxParty party, IReadOnlyList<Witness> partyWitnesses)
    {
        var inputs = transaction.Inputs.Where(i => i.AddedBy == party && !i.IsShared).OrderBy(i => i.SerialId)
                                .ToList();
        if (inputs.Count != partyWitnesses.Count)
            throw new InvalidOperationException(
                $"{inputs.Count} {party} inputs but {partyWitnesses.Count} witnesses");

        for (var i = 0; i < inputs.Count; i++)
            witnesses[inputs[i].SerialId] = partyWitnesses[i];
    }

    private static IReadOnlyList<IChannelMessage> EnsureTxAbort(ChannelId channelId,
                                                                IReadOnlyList<IChannelMessage> outbound, string reason)
    {
        return outbound.Any(m => m is TxAbortMessage) ? outbound : [.. outbound, CreateTxAbort(channelId, reason)];
    }

    private static FundingOutputContributionTlv? CreateContributionTlv(LightningMoney amount) =>
        amount > LightningMoney.Zero ? new FundingOutputContributionTlv(amount) : null;

    /// <summary>BOLT 2: a tx_abort's data SHOULD NOT be printed verbatim unless it is printable ASCII.</summary>
    internal static string DescribeAbortData(byte[] data)
    {
        if (data.Length == 0)
            return "(no data)";

        return data.All(b => b is >= 32 and <= 126)
                   ? Encoding.ASCII.GetString(data)
                   : $"0x{Convert.ToHexString(data).ToLowerInvariant()}";
    }

    private static string ToPrintableAscii(string text) =>
        new(text.Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());

    private void LogStarted(Attempt attempt)
    {
        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Interactive-tx negotiation {SessionId} started on channel {ChannelId} as "
                                 + "{Role} at {FeeratePerKw} sat/kw", attempt.SessionId, attempt.Terms.ChannelId,
                                   attempt.Terms.IsInitiator ? "initiator" : "non-initiator",
                                   attempt.Terms.FeeratePerKw);
    }

    private void LogAborted(Attempt attempt, string reason, string? requirementId, bool byUs)
    {
        _logger.LogInformation("Interactive-tx negotiation {SessionId} on channel {ChannelId} aborted by {Side}: "
                             + "{Reason} {RequirementId}", attempt.SessionId, attempt.Terms.ChannelId,
                               byUs ? "us" : "the peer", reason, requirementId);
    }

    /// <summary>The driver's state for one channel.</summary>
    private sealed class ChannelEntry(CompactPubKey remoteNodeId)
    {
        public CompactPubKey RemoteNodeId { get; set; } = remoteNodeId;
        public IInteractiveTxHost? Host { get; set; }
        public Attempt? Current { get; set; }
        public List<CompletedAttempt> Completed { get; } = [];
        public InteractiveTxTerms? PendingRbf { get; set; }
        public bool AwaitingAbortEcho { get; set; }
    }

    /// <summary>The attempt in progress.</summary>
    private sealed class Attempt(Guid sessionId, InteractiveTxTerms terms, IInteractiveTxNegotiation negotiation,
                                 InteractiveTxContribution contribution)
    {
        public Guid SessionId { get; } = sessionId;
        public InteractiveTxTerms Terms { get; } = terms;
        public IInteractiveTxNegotiation Negotiation { get; set; } = negotiation;
        public InteractiveTxContribution Contribution { get; } = contribution;

        /// <summary>The stored row: null until our commitment_signed is sent.</summary>
        public InteractiveTxSessionModel? Model { get; set; }
    }

    private sealed record CompletedAttempt(Guid SessionId, ConstructedInteractiveTx Transaction, uint FeeratePerKw,
                                           InteractiveTxContribution Contribution);
}