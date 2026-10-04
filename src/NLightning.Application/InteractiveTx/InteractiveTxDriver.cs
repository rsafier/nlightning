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
using Domain.LiquidityAds.Models;
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

    /// <summary>
    /// How long our <c>tx_abort</c> waits for its echo before the driver stops waiting (a peer that already sent
    /// <c>tx_signatures</c> never echoes, and a lost echo must not block the channel's interactive-tx messages for
    /// the life of the connection).
    /// </summary>
    public static readonly TimeSpan AbortEchoTimeout = TimeSpan.FromMinutes(1);

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
        ThrowIfAwaitingAbortEcho(entry, terms.ChannelId);
        if (entry.Host is not null && !ReferenceEquals(entry.Host, host))
            // A new protocol run on the channel: the earlier completed attempts belong to the earlier host
            entry.Completed.Clear();

        entry.Host = host;
        entry.RemoteNodeId = terms.RemoteNodeId;
        entry.PendingRbf = null;
        entry.EchoedWithoutActivity = false;

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
        ChannelId channelId, IUnitOfWork unitOfWork, CancellationToken cancellationToken = default,
        CompactSignature? theirCommitmentSignature = null)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);

        if (!_channels.TryGetValue(channelId, out var entry) || entry.Current is not { Model: not null } attempt
         || attempt.Model.CommitmentSignedReceived)
            throw new InvalidOperationException(
                $"No constructed interactive-tx negotiation waits for commitment_signed on channel {channelId}");

        var checkpoint = attempt.Checkpoint();
        IReadOnlyList<IChannelMessage> outbound = [];
        try
        {
            attempt.Negotiation = attempt.Negotiation.OnCommitmentSignedReceived();
            attempt.Model = attempt.Model with
            {
                CommitmentSignedReceived = true,
                TheirCommitmentSignature = theirCommitmentSignature ?? attempt.Model.TheirCommitmentSignature,
                State = attempt.Negotiation.State
            };

            // IT-SIG-01/03: after a valid commitment_signed, we send first by the order rule, or once the peer's
            // arrived
            if (!attempt.Model.TxSignaturesSent
             && (attempt.Negotiation.RemoteWitnesses is not null || attempt.Negotiation.SendsTxSignaturesFirst()))
                outbound = await SendOurSignaturesAsync(entry, attempt, unitOfWork, cancellationToken);

            await SaveModelAsync(attempt, unitOfWork);
        }
        catch (SignaturesAbandonedException e)
        {
            // NL-867: nothing was signed or sent, and our tx_signatures did not go out, so the attempt is abandoned
            // with tx_abort (a failed connection would only bring the same commitment_signed back after reconnecting)
            attempt.Restore(checkpoint);
            return await AbortOurselvesAsync(entry, attempt, e.Message, unitOfWork, cancellationToken);
        }
        catch
        {
            // Nothing was saved or sent: memory goes back to what the database holds
            attempt.Restore(checkpoint);
            throw;
        }

        ApplyCompletion(entry, attempt);
        return outbound;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<IChannelMessage>> RequestRbfAsync(InteractiveTxTerms terms,
                                                                LightningMoney fundingOutputContribution,
                                                                CancellationToken cancellationToken = default,
                                                                RequestFunding? requestFunding = null)
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
        ThrowIfAwaitingAbortEcho(entry, terms.ChannelId);

        var minimum = GetMinimumRbfFeeratePerKw(entry.Completed[^1].FeeratePerKw);
        if (terms.FeeratePerKw < minimum)
            throw new InvalidOperationException(
                $"[IT-RBF-01] tx_init_rbf feerate {terms.FeeratePerKw} sat/kw is below the minimum {minimum} sat/kw");

        entry.PendingRbf = terms;
        entry.EchoedWithoutActivity = false;

        var message = new TxInitRbfMessage(new TxInitRbfPayload(terms.ChannelId, terms.FeeratePerKw, terms.Locktime),
                                           CreateContributionTlv(fundingOutputContribution),
                                           terms.LocalRequiresConfirmedInputs ? new RequireConfirmedInputsTlv() : null,
                                           requestFunding is null ? null : new RequestFundingTlv(requestFunding));
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

            // Our tx_init_rbf is withdrawn, and whoever waits for it learns why (NL-527, NL-867)
            entry.PendingRbf = null;
            MarkAbortSent(entry);
            await entry.Host!.OnRbfRequestEndedAsync(channelId, reason, cancellationToken);
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

        entry.AbortSentAt = null;
        entry.EchoedWithoutActivity = false;
        if (entry.PendingRbf is not null)
        {
            // NL-527: our tx_init_rbf is gone with the connection
            entry.PendingRbf = null;
            await entry.Host!.OnRbfRequestEndedAsync(channelId, "disconnected", cancellationToken);
        }

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
    public IReadOnlyList<IChannelMessage> AbortQuiescence(ChannelId channelId, CompactPubKey peerPubKey,
                                                          string reason)
    {
        var entry = _channels.GetOrAdd(channelId, _ => new ChannelEntry(peerPubKey));
        if (entry.Current is not null || entry.PendingRbf is not null || IsAwaitingAbortEcho(entry))
        {
            RemoveIfIdle(channelId, entry);
            return [];
        }

        // Our tx_abort ends the quiescence (SP-Q-01); its echo is recognised as such and never answered
        MarkAbortSent(entry);
        _logger.LogInformation("Ending the quiescence of channel {ChannelId} with tx_abort ({Reason})", channelId,
                               reason);
        _quiescenceService?.Terminate(channelId, QuiescenceEndReason.TxAbort);
        return [CreateTxAbort(channelId, reason)];
    }

    /// <inheritdoc />
    public async Task ResumeAsync(InteractiveTxSessionModel model, InteractiveTxTerms terms, IInteractiveTxHost host,
                                  CancellationToken cancellationToken = default,
                                  IReadOnlyList<InteractiveTxSessionModel>? completedAttempts = null)
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
        entry.EchoedWithoutActivity = false;

        // The channel's other fully signed attempts (RBF): the floor and the double-spend rule need them
        var signed = (completedAttempts ?? [])
                    .Append(model)
                    .Where(m => m.ChannelId == model.ChannelId && m.State == InteractiveTxSessionState.Signed
                             && m.ConstructedTx is not null)
                    .OrderBy(m => m.CreatedAt);
        foreach (var completed in signed)
        {
            if (entry.Completed.All(c => c.SessionId != completed.SessionId))
                entry.Completed.Add(new CompletedAttempt(completed.SessionId, completed.ConstructedTx!,
                                                         completed.FeeratePerKw, completed.LocalContribution)
                {
                    Model = completed
                });
        }

        if (model.State is InteractiveTxSessionState.Signed or InteractiveTxSessionState.Aborted)
            return;

        var parameters = await CreateParametersAsync(entry, terms, model.LocalContribution, cancellationToken);
        entry.Current = new Attempt(model.SessionId, terms, _engine.Restore(model, parameters),
                                    model.LocalContribution)
        { Model = model };
    }

    /// <inheritdoc />
    public TxSignaturesMessage? CreateTxSignaturesRetransmission(ChannelId channelId, TxId fundingTxId)
    {
        if (!_channels.TryGetValue(channelId, out var entry))
            return null;

        var model = entry.Current?.Model is { } current && Matches(current, fundingTxId)
                        ? current
                        : entry.Completed.Select(c => c.Model).LastOrDefault(m => m is not null && Matches(m, fundingTxId));
        if (model is not { TxSignaturesSent: true, ConstructedTx: { } transaction })
            return null;

        return new TxSignaturesMessage(
            new TxSignaturesPayload(channelId, transaction.TxId, (model.OurWitnesses ?? []).ToList()),
            model.OurSharedInputSignature is { } signature ? new SharedInputSignatureTlv(signature) : null);

        static bool Matches(InteractiveTxSessionModel m, TxId txId) =>
            m.ConstructedTx is { } tx && tx.TxId.Equals(txId);
    }

    /// <inheritdoc />
    public IReadOnlyList<ChannelId> GetChannels(CompactPubKey peerPubKey) =>
        _channels.Where(kv => kv.Value.RemoteNodeId == peerPubKey).Select(kv => kv.Key).ToList();

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
                                                attempt?.Model is not null, IsAwaitingAbortEcho(entry),
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
            bool isUnconfirmed;
            if (addInput.Payload.PrevTx is not { Length: > 0 } && addInput.PrevTxDetailsTlv is { } details)
            {
                // A taproot input described by prevtx_details (BOLTs PR #1324, NL-957): only its outpoint is known
                isUnconfirmed = !await _prevTxInspector.IsOutputConfirmedAsync(details.PrevTxId,
                                                                               addInput.Payload.PrevTxVout,
                                                                               cancellationToken);
            }
            else
            {
                var inspection = _prevTxInspector.Inspect(addInput.Payload.PrevTx, addInput.Payload.PrevTxVout);
                isUnconfirmed = inspection is { IsValid: true, TxId: { } prevTxId }
                             && !await _prevTxInspector.IsConfirmedAsync(prevTxId, cancellationToken);
            }

            if (isUnconfirmed)
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

        var checkpoint = attempt.Checkpoint();
        attempt.Negotiation = step.Next;

        // BOLTs PR #1324: a taproot session's tx_complete carries the sender's commit_nonces, new ones after every
        // change of the transaction; the peer's last ones are for the transaction both tx_complete close
        if (message is TxCompleteMessage txComplete)
            attempt.RemoteCommitNonces = txComplete.CommitNoncesTlv;

        if (step.Aborted)
        {
            // A rule the peer broke: our tx_abort goes out and waits for its echo (none comes from a peer that
            // already sent tx_signatures: it must keep the negotiation)
            LogAborted(attempt, step.AbortReason!, step.RequirementId, true);
            MarkAbortSent(entry, PeerSentSignatures(attempt) || message is TxSignaturesMessage);
            await FinishAbortAsync(entry, attempt, step.AbortReason!, unitOfWork, cancellationToken);
            return EnsureTxAbort(channelId, step.Outbound, step.AbortReason!);
        }

        try
        {
            var outbound = new List<IChannelMessage>(WithCommitNonces(entry, attempt, step.Outbound));
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
        catch (SignaturesAbandonedException e) when (ReferenceEquals(entry.Current, attempt))
        {
            // NL-867: we cannot sign our tx_signatures (an RBF sibling confirmed): tx_abort, which a peer that already
            // sent its tx_signatures never echoes
            attempt.Restore(checkpoint);
            return await AbortOurselvesAsync(entry, attempt, e.Message, unitOfWork, cancellationToken,
                                             message is TxSignaturesMessage);
        }
        catch when (ReferenceEquals(entry.Current, attempt))
        {
            // A save failed: nothing was sent, so memory goes back to what the database holds (persist, then
            // update memory, then send)
            attempt.Restore(checkpoint);
            throw;
        }
    }

    private async Task<IReadOnlyList<IChannelMessage>> ReceiveAbortAsync(
        ChannelId channelId, ChannelEntry? entry, TxAbortMessage abort, CompactPubKey peerPubKey,
        IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        var data = DescribeAbortData(abort.Payload.Data);
        if (entry?.Current is not { } attempt)
        {
            if (entry is not null && IsAwaitingAbortEcho(entry))
            {
                // The echo of our tx_abort: the peer has seen it, the negotiation is over on both sides. The
                // quiescence ends here too when our tx_abort went out without one to finish (NL-509: a refused
                // tx_init_rbf, or a negotiation-less tx_abort, sent it while the channel was quiescent)
                entry.AbortSentAt = null;
                _logger.LogDebug("tx_abort echoed by {Peer} on channel {ChannelId}", peerPubKey, channelId);
                _quiescenceService?.Terminate(channelId, QuiescenceEndReason.TxAbort);
                RemoveIfIdle(channelId, entry);
                return [];
            }

            if (entry is { EchoedWithoutActivity: true, PendingRbf: null }
             && _quiescenceService?.GetState(channelId) is not { BlocksNewLocalUpdates: true })
            {
                // We echoed a tx_abort and nothing (no negotiation, no RBF request, no new quiescence) started since:
                // this one can only be the echo of our echo, so echoing it again would bounce tx_abort forever
                _logger.LogInformation("Not echoing tx_abort from {Peer} on channel {ChannelId} ({Data}): we echoed "
                                     + "one and nothing started since", peerPubKey, channelId, data);
                return [];
            }

            // BOLT 2: the receiver MUST echo tx_abort if it has not sent one (also ends a quiescence without a
            // negotiation, e.g. a withdrawn tx_init_rbf of the peer's or a quiescence the peer gives up)
            entry ??= _channels.GetOrAdd(channelId, _ => new ChannelEntry(peerPubKey));
            var rbfRefused = entry.PendingRbf is not null;
            entry.PendingRbf = null;
            entry.EchoedWithoutActivity = true;

            _logger.LogInformation("tx_abort from {Peer} on channel {ChannelId} without a negotiation ({Data}); "
                                 + "echoing it", peerPubKey, channelId, data);
            _quiescenceService?.Terminate(channelId, QuiescenceEndReason.TxAbort);

            // NL-527: the peer refused our tx_init_rbf; whoever waits for the RBF gets the peer's reason now
            if (rbfRefused && entry.Host is not null)
                await entry.Host.OnRbfRequestEndedAsync(channelId, $"peer sent tx_abort: {data}", cancellationToken);
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
        if (entry is null || entry.Host is null || entry.Completed.Count == 0 || IsAwaitingAbortEcho(entry))
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
            await entry.Host.OnRbfRequestEndedAsync(channelId, "simultaneous tx_init_rbf", cancellationToken);
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
        catch (ArgumentException e)
        {
            // The engine refused our own terms (e.g. IT-RBF-01: our contribution does not double-spend an earlier
            // attempt); the reservation was released by CreateAttemptAsync
            return RejectRbf(channelId, entry, $"cannot build the rbf attempt: {e.Message}");
        }

        entry.Current = attempt;
        entry.EchoedWithoutActivity = false;
        LogStarted(attempt);
        return
        [
            new TxAckRbfMessage(new TxAckRbfPayload(channelId),
                                CreateContributionTlv(decision.FundingOutputContribution),
                                terms.LocalRequiresConfirmedInputs ? new RequireConfirmedInputsTlv() : null,
                                decision.WillFund is null ? null : new ProvideFundingTlv(decision.WillFund))
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

        // NL-521: the peer may change its funding_output_contribution in tx_ack_rbf; the host takes it (or refuses it)
        // before the attempt's shared funding is built
        if (await entry.Host!.OnRbfAcknowledgedAsync(ackRbf, cancellationToken) is { } refusal)
        {
            await entry.Host.OnAbortedAsync(channelId, refusal, cancellationToken);
            return RejectRbf(channelId, entry, refusal);
        }

        Attempt attempt;
        try
        {
            attempt = await CreateAttemptAsync(entry, terms, cancellationToken);
        }
        catch (InsufficientFundsException e)
        {
            return await EndRequestedRbfAsync(channelId, entry, $"cannot fund the rbf attempt: {e.Message}",
                                              cancellationToken);
        }
        catch (ArgumentException e)
        {
            return await EndRequestedRbfAsync(channelId, entry, $"cannot build the rbf attempt: {e.Message}",
                                              cancellationToken);
        }

        entry.Current = attempt;
        LogStarted(attempt);
        return await StartAttemptAsync(entry, attempt, cancellationToken);
    }

    /// <summary>
    /// <paramref name="outbound"/> with our <c>commit_nonces</c> on every <c>tx_complete</c> of a session whose host asks
    /// for them (BOLTs PR #1324, simple taproot channels): verification nonces for the transaction negotiated so far
    /// (<paramref name="attempt"/>'s inputs and outputs now), so a change of the transaction sends new ones with the next
    /// <c>tx_complete</c>. A transaction that cannot be built yet (no input or no output) gets none: the peer then refuses
    /// to sign it, and so would we.
    /// </summary>
    private IReadOnlyList<IChannelMessage> WithCommitNonces(ChannelEntry entry, Attempt attempt,
                                                           IReadOnlyList<IChannelMessage> outbound)
    {
        if (entry.Host is not { } host || !outbound.Any(m => m is TxCompleteMessage))
            return outbound;

        var decorated = new List<IChannelMessage>(outbound.Count);
        foreach (var message in outbound)
        {
            if (message is not TxCompleteMessage { CommitNoncesTlv: null } txComplete)
            {
                decorated.Add(message);
                continue;
            }

            ConstructedInteractiveTx transaction;
            try
            {
                transaction = _builder.Build(attempt.Terms.Locktime, attempt.Negotiation.Inputs,
                                             attempt.Negotiation.Outputs);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                decorated.Add(message);
                continue;
            }

            decorated.Add(host.GetLocalCommitNonces(transaction.TxId) is { } nonces
                              ? new TxCompleteMessage(txComplete.Payload, nonces, txComplete.FundingNonceTlv)
                              : message);
        }

        return decorated;
    }

    private async Task<IReadOnlyList<IChannelMessage>> StartAttemptAsync(ChannelEntry entry, Attempt attempt,
                                                                         CancellationToken cancellationToken)
    {
        var step = attempt.Negotiation.Start();
        attempt.Negotiation = step.Next;
        if (!step.Aborted)
            return WithCommitNonces(entry, attempt, step.Outbound);

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
        // IT-RBF-01: every attempt MUST double-spend all other attempts; checked here too, whatever the engine does,
        // so we never sign two funding transactions that could both confirm
        if (entry.Completed.FirstOrDefault(c => !DoubleSpends(attempt.Negotiation.Inputs, c.Transaction)) is
            { } notReplaced)
            return await RejectAsync(entry, attempt,
                                     $"[IT-RBF-01] the negotiated transaction does not double-spend the earlier "
                                   + $"attempt {notReplaced.Transaction.TxId}", unitOfWork, cancellationToken);

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

        // BOLTs PR #1324: a taproot commitment step needs the peer's commit_nonces of the constructed transaction
        if (entry.Host!.AcceptRemoteCommitNonces(attempt.Negotiation.ConstructedTx!, attempt.RemoteCommitNonces) is
            { } nonceRefusal)
            return await RejectAsync(entry, attempt, nonceRefusal, unitOfWork, cancellationToken);

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
        ApplyCompletion(entry, attempt);
        return outbound;
    }

    /// <summary>Signs and records our tx_signatures (not saved: the caller saves before sending).</summary>
    private async Task<IReadOnlyList<IChannelMessage>> SendOurSignaturesAsync(ChannelEntry entry, Attempt attempt,
                                                                              IUnitOfWork unitOfWork,
                                                                              CancellationToken cancellationToken)
    {
        var transaction = attempt.Negotiation.ConstructedTx
                       ?? throw new InvalidOperationException("The negotiated transaction is not constructed");

        // NL-867: the host may know the attempt can never confirm (an RBF sibling confirmed): abandoned, not signed
        if (await entry.Host!.GetTxSignaturesRefusalAsync(transaction, cancellationToken) is { } refusal)
            throw new SignaturesAbandonedException(refusal);

        IReadOnlyList<Witness> witnesses;
        try
        {
            witnesses = attempt.Contribution.Inputs.Count == 0
                            ? []
                            : await _contributor.SignAsync(transaction, attempt.Contribution,
                                                           GetOtherSpentOutputs(transaction), cancellationToken);
        }
        catch (InteractiveTxInputsSpentException e)
        {
            throw new SignaturesAbandonedException($"our inputs were spent on chain: {e.Message}");
        }

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

        // Applied to memory by ApplyCompletion, after the caller's save
        attempt.IsCompleted = true;
        return outbound;
    }

    /// <summary>
    /// After the save that stored a fully signed attempt: it becomes a completed attempt (RBF), the negotiation ends
    /// and so does the quiescence. Nothing for an attempt that is not complete.
    /// </summary>
    private void ApplyCompletion(ChannelEntry entry, Attempt attempt)
    {
        if (!attempt.IsCompleted || !ReferenceEquals(entry.Current, attempt))
            return;

        var transaction = attempt.Negotiation.ConstructedTx!;
        var completed = new CompletedAttempt(attempt.SessionId, transaction, attempt.Terms.FeeratePerKw,
                                             attempt.Contribution)
        { Model = attempt.Model };
        entry.Completed.Add(completed);
        entry.Current = null;
        _quiescenceService?.Terminate(attempt.Terms.ChannelId, QuiescenceEndReason.TxSignaturesExchanged);

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation("Interactive-tx negotiation {SessionId} on channel {ChannelId} fully signed: {TxId}",
                                   attempt.SessionId, attempt.Terms.ChannelId, transaction.TxId);
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
                                                                           CancellationToken cancellationToken,
                                                                           bool peerSentSignatures = false)
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
        MarkAbortSent(entry, peerSentSignatures || PeerSentSignatures(attempt));
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
        if (entry is not null && IsAwaitingAbortEcho(entry))
        {
            // Sent before the peer saw our tx_abort: stale, the echo will follow
            _logger.LogDebug("Ignoring {MessageType} on channel {ChannelId} while our tx_abort waits for its echo",
                             messageType, channelId);
            return [];
        }

        _logger.LogInformation("{MessageType} from {Peer} on channel {ChannelId} without an interactive-tx "
                             + "negotiation; answering tx_abort", messageType, peerPubKey, channelId);
        MarkAbortSent(_channels.GetOrAdd(channelId, _ => new ChannelEntry(peerPubKey)));
        return [CreateTxAbort(channelId, NoNegotiationText)];
    }

    /// <summary>
    /// Our own RBF attempt could not be built after the peer's <c>tx_ack_rbf</c> (NL-527): <c>tx_abort</c>, and the host
    /// learns that its RBF ended (nothing of the attempt exists, its contribution was released).
    /// </summary>
    private async Task<IReadOnlyList<IChannelMessage>> EndRequestedRbfAsync(ChannelId channelId, ChannelEntry entry,
                                                                            string reason,
                                                                            CancellationToken cancellationToken)
    {
        var messages = RejectRbf(channelId, entry, reason);
        await entry.Host!.OnRbfRequestEndedAsync(channelId, reason, cancellationToken);
        return messages;
    }

    private IReadOnlyList<IChannelMessage> RejectRbf(ChannelId channelId, ChannelEntry entry, string reason)
    {
        _logger.LogInformation("Rejecting the RBF on channel {ChannelId}: {Reason}", channelId, reason);
        MarkAbortSent(entry);
        // NL-509: the refusal's tx_abort ends the quiescence here, not only through the host (which the splice's is,
        // but any other RBF host runs behind a quiescence too and would stay quiescent until its timeout)
        _quiescenceService?.Terminate(channelId, QuiescenceEndReason.TxAbort);
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
                                                  terms.Locktime, terms.DustLimitSatoshis, contribution, sharedFunding,
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
            LocalFundingSatoshis = negotiation.Parameters.SharedFunding?.LocalOutputShare.Satoshi,
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

    /// <summary>
    /// Records our <c>tx_abort</c>: it waits for its echo unless the peer already sent <c>tx_signatures</c> (such a
    /// peer keeps the negotiation and never echoes, IT-ABT-01).
    /// </summary>
    private void MarkAbortSent(ChannelEntry entry, bool peerSentSignatures = false)
    {
        entry.AbortSentAt = peerSentSignatures ? null : _timeProvider.GetUtcNow();
        entry.EchoedWithoutActivity = false;
    }

    /// <summary>Whether our <c>tx_abort</c> still waits for its echo (at most <see cref="AbortEchoTimeout"/>).</summary>
    private bool IsAwaitingAbortEcho(ChannelEntry entry)
    {
        if (entry.AbortSentAt is not { } sentAt)
            return false;
        if (_timeProvider.GetUtcNow() - sentAt < AbortEchoTimeout)
            return true;

        _logger.LogInformation("No echo of our tx_abort on channel entry of {Peer} after {Timeout}; no longer waiting",
                               entry.RemoteNodeId, AbortEchoTimeout);
        entry.AbortSentAt = null;
        return false;
    }

    private void ThrowIfAwaitingAbortEcho(ChannelEntry entry, ChannelId channelId)
    {
        // A new attempt before the echo of our tx_abort would take that echo (and the peer's stale messages) as the
        // new attempt's: BOLT 2 echoes tx_abort so that the originating peer can end the process without stale ones
        if (IsAwaitingAbortEcho(entry))
            throw new InvalidOperationException(
                $"Our tx_abort on channel {channelId} waits for its echo; start a new attempt after it");
    }

    private static bool PeerSentSignatures(Attempt attempt) =>
        attempt.Negotiation.RemoteWitnesses is not null || attempt.Model?.TxSignaturesReceived == true;

    private static bool DoubleSpends(IReadOnlyList<InteractiveTxInput> inputs, ConstructedInteractiveTx earlier) =>
        earlier.Inputs.Any(e => inputs.Any(i => i.PrevTxId.Equals(e.PrevTxId) && i.PrevTxVout == e.PrevTxVout));

    private void RemoveIfIdle(ChannelId channelId, ChannelEntry entry)
    {
        if (entry is
            {
                Current: null, PendingRbf: null, AbortSentAt: null, EchoedWithoutActivity: false,
                Completed.Count: 0
            })
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

    /// <summary>
    /// The <c>funding_output_contribution</c> TLV of a <c>tx_init_rbf</c>/<c>tx_ack_rbf</c> from a signed contribution
    /// in satoshis (NL-481: an s64, negative for a splice-out RBF, BOLT 2 "Channel Splicing"), or null for none (0: the
    /// sender does not contribute).
    /// </summary>
    public static FundingOutputContributionTlv? CreateContributionTlv(long satoshis) =>
        satoshis == 0 ? null : new FundingOutputContributionTlv(satoshis);

    private static FundingOutputContributionTlv? CreateContributionTlv(LightningMoney amount) =>
        CreateContributionTlv(amount.Satoshi);

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

    /// <summary>
    /// Our <c>tx_signatures</c> cannot be made for the attempt, which can never confirm (NL-867): the caller restores
    /// the attempt and aborts it with <c>tx_abort</c> (nothing was signed or sent).
    /// </summary>
    private sealed class SignaturesAbandonedException(string message) : Exception(message);

    /// <summary>The driver's state for one channel.</summary>
    private sealed class ChannelEntry(CompactPubKey remoteNodeId)
    {
        public CompactPubKey RemoteNodeId { get; set; } = remoteNodeId;
        public IInteractiveTxHost? Host { get; set; }
        public Attempt? Current { get; set; }
        public List<CompletedAttempt> Completed { get; } = [];
        public InteractiveTxTerms? PendingRbf { get; set; }

        /// <summary>When our last <c>tx_abort</c> went out, while it waits for its echo; null otherwise.</summary>
        public DateTimeOffset? AbortSentAt { get; set; }

        /// <summary>
        /// We echoed a <c>tx_abort</c> and no negotiation or RBF request started since: a further <c>tx_abort</c> is
        /// the echo of our echo and is not echoed again.
        /// </summary>
        public bool EchoedWithoutActivity { get; set; }
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

        /// <summary>Both tx_signatures exchanged and handed to the host; applied to memory after the save.</summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// The <c>commit_nonces</c> of the peer's latest <c>tx_complete</c> (BOLTs PR #1324, simple taproot sessions);
        /// null when it sent none.
        /// </summary>
        public CommitNoncesTlv? RemoteCommitNonces { get; set; }

        public (IInteractiveTxNegotiation Negotiation, InteractiveTxSessionModel? Model, bool IsCompleted)
            Checkpoint() => (Negotiation, Model, IsCompleted);

        public void Restore((IInteractiveTxNegotiation Negotiation, InteractiveTxSessionModel? Model, bool IsCompleted)
                                checkpoint) =>
            (Negotiation, Model, IsCompleted) = checkpoint;
    }

    private sealed record CompletedAttempt(Guid SessionId, ConstructedInteractiveTx Transaction, uint FeeratePerKw,
                                           InteractiveTxContribution Contribution)
    {
        /// <summary>The stored row, for a <c>tx_signatures</c> retransmission.</summary>
        public InteractiveTxSessionModel? Model { get; init; }
    }
}