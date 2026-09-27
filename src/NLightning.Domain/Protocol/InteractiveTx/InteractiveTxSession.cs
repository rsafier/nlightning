namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Exceptions;
using Interfaces;
using Messages;
using Models;
using Payloads;
using Protocol.Interfaces;
using Tlv;

/// <summary>
/// The pure interactive-tx negotiation engine of one channel (BOLT 2 "Interactive Transaction Construction"; splicing
/// plan §3.9, D4). No I/O: the driver (<c>InteractiveTxDriver</c>, lane IT-D) runs it under the channel's lock, sends
/// <see cref="InteractiveTxStepResult.Outbound"/>, and does the asynchronous work (the confirmed-input check, building,
/// signing, persistence) between steps through the ports.
/// </summary>
/// <remarks>
/// <para>Rules it owns (through <see cref="InteractiveTxRules"/> and <see cref="TxSignaturesOrder"/>): IT-S-01 (serial
/// id parity and uniqueness, sequence), IT-S-02 (turns, two consecutive <c>tx_complete</c>), IT-R-01..IT-R-04 (incl.
/// the 4096 <b>received</b> <c>tx_add_input</c>/<c>tx_add_output</c> per negotiation, NL-219, and 252 inputs/outputs at
/// <c>tx_complete</c>), the shared-funding checks of a splice (SP-TX-01/02/05), IT-SIG-01..03 (<c>tx_signatures</c>
/// order and checks) and IT-ABT-01 (<c>tx_abort</c>). A rule broken by the peer ends the negotiation with our
/// <c>tx_abort</c> in <see cref="InteractiveTxStepResult.Outbound"/>, never with a channel failure, with one exception:
/// a splice's <c>tx_signatures</c> without <c>shared_input_signature</c> throws <see cref="ChannelFailedException"/>
/// (BOLT 2 splicing: "If <c>shared_input_signature</c> is not set: MUST send an <c>error</c> and fail the channel").</para>
/// <para>After our <c>tx_signatures</c> nothing can abort the session any more (BOLT 2 tx_abort: "A sending node: MUST
/// NOT have already transmitted <c>tx_signatures</c>"): a bad message then leaves it unchanged, with no outbound and
/// <see cref="InteractiveTxStepResult.RequirementId"/> set, and the driver keeps it until an input of the transaction is
/// spent (<see cref="MustBeRemembered"/>).</para>
/// <para>Not yet applied (lane IT-A step 2): the feerate and weight checks of IT-R-04 (IT1-T2) and the RBF rules
/// IT-RBF-01 over <see cref="InteractiveTxSessionParameters.PreviousAttempts"/> (IT1-T4). Our <c>serial_id</c>s are
/// deterministic (initiator 0, 2, 4, ...; non-initiator 1, 3, 5, ...) in the order of
/// <see cref="InteractiveTxSessionParameters.LocalContribution"/>, the shared input and output first.</para>
/// <para>Immutable: every step returns the next session in <see cref="InteractiveTxStepResult.Next"/>.</para>
/// </remarks>
public sealed class InteractiveTxSession
{
    private const uint SharedInputSequence = InteractiveTxRules.MaxSequence;

    // What we still have to send, in order; _nextLocalItem indexes it.
    private readonly IReadOnlyList<LocalItem> _localItems;
    private int _nextLocalItem;
    private bool _started;
    private bool _lastSentWasComplete;
    private bool _lastReceivedWasComplete;

    /// <summary>The terms the session was created with.</summary>
    public InteractiveTxSessionParameters Parameters { get; }

    /// <summary>Where the negotiation is.</summary>
    public InteractiveTxSessionState State { get; private set; }

    /// <summary>The inputs currently added by both sides.</summary>
    public IReadOnlyList<InteractiveTxInput> Inputs { get; private set; }

    /// <summary>The outputs currently added by both sides.</summary>
    public IReadOnlyList<InteractiveTxOutput> Outputs { get; private set; }

    /// <summary>Whether the next message of the negotiation is ours (IT-S-02).</summary>
    public bool IsOurTurn { get; private set; }

    /// <summary>
    /// The number of <c>tx_add_input</c> received in this negotiation, removals not subtracted (the 4096 cap of IT-R-01,
    /// NL-219).
    /// </summary>
    public int ReceivedAddInputCount { get; private set; }

    /// <summary>The number of <c>tx_add_output</c> received in this negotiation (the 4096 cap of IT-R-02).</summary>
    public int ReceivedAddOutputCount { get; private set; }

    /// <summary>
    /// Whether two consecutive <c>tx_complete</c> ended the negotiation (IT-S-02): <see cref="Inputs"/> and
    /// <see cref="Outputs"/> are final. Stays true after <see cref="WithConstructedTransaction"/>.
    /// </summary>
    public bool IsNegotiationComplete { get; private set; }

    /// <summary>The constructed transaction, once <see cref="WithConstructedTransaction"/> set it; null before.</summary>
    public ConstructedInteractiveTx? ConstructedTx { get; private set; }

    /// <summary>The peer's witnesses from its valid <c>tx_signatures</c>, in its inputs' <c>serial_id</c> order; null
    /// before.</summary>
    public IReadOnlyList<Witness>? RemoteWitnesses { get; private set; }

    /// <summary>The peer's <c>shared_input_signature</c>, when a splice's <c>tx_signatures</c> carried one.</summary>
    public CompactSignature? RemoteSharedInputSignature { get; private set; }

    /// <summary>Our witnesses as sent in our <c>tx_signatures</c>; null before.</summary>
    public IReadOnlyList<Witness>? LocalWitnesses { get; private set; }

    /// <summary>Our <c>shared_input_signature</c> as sent in our <c>tx_signatures</c>; null before or without a
    /// shared input.</summary>
    public CompactSignature? LocalSharedInputSignature { get; private set; }

    /// <summary>
    /// Whether our <c>tx_signatures</c> was sent: the negotiation can no longer be aborted and must be remembered until
    /// an input of the transaction is spent (IT-ABT-01).
    /// </summary>
    public bool MustBeRemembered => State is InteractiveTxSessionState.TxSignaturesSent
                                              or InteractiveTxSessionState.Signed;

    private InteractiveTxSession(InteractiveTxSessionParameters parameters, IReadOnlyList<LocalItem> localItems)
    {
        Parameters = parameters;
        State = InteractiveTxSessionState.Negotiating;
        Inputs = [];
        Outputs = [];
        IsOurTurn = parameters.IsInitiator;
        _localItems = localItems;
    }

    private InteractiveTxSession(InteractiveTxSession other)
    {
        Parameters = other.Parameters;
        State = other.State;
        Inputs = other.Inputs;
        Outputs = other.Outputs;
        IsOurTurn = other.IsOurTurn;
        ReceivedAddInputCount = other.ReceivedAddInputCount;
        ReceivedAddOutputCount = other.ReceivedAddOutputCount;
        IsNegotiationComplete = other.IsNegotiationComplete;
        ConstructedTx = other.ConstructedTx;
        RemoteWitnesses = other.RemoteWitnesses;
        RemoteSharedInputSignature = other.RemoteSharedInputSignature;
        LocalWitnesses = other.LocalWitnesses;
        LocalSharedInputSignature = other.LocalSharedInputSignature;
        _localItems = other._localItems;
        _nextLocalItem = other._nextLocalItem;
        _started = other._started;
        _lastSentWasComplete = other._lastSentWasComplete;
        _lastReceivedWasComplete = other._lastReceivedWasComplete;
    }

    /// <summary>
    /// A new negotiation in <see cref="InteractiveTxSessionState.Negotiating"/>; the initiator's turn first.
    /// </summary>
    /// <exception cref="ArgumentException">The parameters are inconsistent (for example a contribution with a sequence
    /// above 0xFFFFFFFD, or a shared input without us being the initiator of a negotiation that has one).</exception>
    public static InteractiveTxSession Create(InteractiveTxSessionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(parameters.LocalContribution);

        if (parameters.LocalNodeId == parameters.RemoteNodeId)
            throw new ArgumentException("Both sides have the same node id.", nameof(parameters));

        var local = InteractiveTxParty.Local;
        var serialId = parameters.IsInitiator ? 0UL : 1UL;
        var items = new List<LocalItem>();

        var shared = parameters.SharedFunding;
        if (parameters.IsInitiator && shared?.SharedInput is { } sharedInput)
        {
            items.Add(new LocalItem(new InteractiveTxInput(serialId, local, sharedInput.TxId, sharedInput.Vout,
                                                           SharedInputSequence, sharedInput.Amount,
                                                           sharedInput.ScriptPubKey, null, true), null));
            serialId += 2;
        }

        foreach (var input in parameters.LocalContribution.Inputs)
        {
            if (InteractiveTxRules.CheckSequence(input.Sequence) is not null)
                throw new ArgumentException($"Contributed input {input.PrevTxId}:{input.PrevTxVout} has sequence " +
                                            $"0x{input.Sequence:X8}, above 0x{InteractiveTxRules.MaxSequence:X8}.",
                                            nameof(parameters));

            if (input.PrevTx is null || input.PrevTx.Length == 0)
                throw new ArgumentException($"Contributed input {input.PrevTxId}:{input.PrevTxVout} has no prevtx.",
                                            nameof(parameters));

            items.Add(new LocalItem(new InteractiveTxInput(serialId, local, input.PrevTxId, input.PrevTxVout,
                                                           input.Sequence, input.Amount, input.ScriptPubKey,
                                                           input.PrevTx, false), null));
            serialId += 2;
        }

        if (parameters.IsInitiator && shared is not null)
        {
            items.Add(new LocalItem(null, new InteractiveTxOutput(serialId, local, shared.SharedOutputAmount,
                                                                  shared.SharedOutputScript, true)));
            serialId += 2;
        }

        foreach (var output in parameters.LocalContribution.Outputs)
        {
            items.Add(new LocalItem(null, new InteractiveTxOutput(serialId, local, output.Amount, output.ScriptPubKey,
                                                                  false)));
            serialId += 2;
        }

        return new InteractiveTxSession(parameters, items);
    }

    /// <summary>
    /// Resumes a stored negotiation (from <see cref="InteractiveTxSessionModel.State"/>
    /// <see cref="InteractiveTxSessionState.AwaitingCommitmentSigned"/> on): after a restart or on
    /// <c>channel_reestablish</c> with <c>next_funding</c>, to finish the signature exchange.
    /// </summary>
    /// <exception cref="ArgumentException">The row is not resumable (still negotiating, aborted, or without its
    /// constructed transaction), or it does not match <paramref name="parameters"/>.</exception>
    public static InteractiveTxSession Restore(InteractiveTxSessionModel model,
                                               InteractiveTxSessionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(parameters);

        if (model.State is InteractiveTxSessionState.Negotiating or InteractiveTxSessionState.Aborted)
            throw new ArgumentException($"A session in state {model.State} cannot be restored.", nameof(model));

        if (model.ConstructedTx is null)
            throw new ArgumentException("A stored session has no constructed transaction.", nameof(model));

        if (model.ChannelId != parameters.ChannelId || model.IsInitiator != parameters.IsInitiator
                                                    || model.FeeratePerKw != parameters.FeeratePerKw
                                                    || model.Locktime != parameters.Locktime)
            throw new ArgumentException("The stored session does not match the parameters.", nameof(parameters));

        var sent = model.State is InteractiveTxSessionState.TxSignaturesSent or InteractiveTxSessionState.Signed;
        if (sent && model.OurWitnesses is null)
            throw new ArgumentException("A session past our tx_signatures has no witnesses of ours.", nameof(model));

        return new InteractiveTxSession(parameters, [])
        {
            State = model.State,
            Inputs = [.. model.Inputs.OrderBy(i => i.SerialId)],
            Outputs = [.. model.Outputs.OrderBy(o => o.SerialId)],
            IsOurTurn = false,
            IsNegotiationComplete = true,
            _started = true,
            ConstructedTx = model.ConstructedTx,
            RemoteWitnesses = model.TheirWitnesses,
            RemoteSharedInputSignature = model.TheirSharedInputSignature,
            LocalWitnesses = sent ? model.OurWitnesses : null,
            LocalSharedInputSignature = sent ? model.OurSharedInputSignature : null
        };
    }

    /// <summary>
    /// The initiator's first message (IT-S-02: the initiator starts; its first <c>tx_add_input</c>, or
    /// <c>tx_add_output</c>, or <c>tx_complete</c> when it adds nothing).
    /// </summary>
    /// <exception cref="InvalidOperationException">We are not the initiator, or the negotiation already started.</exception>
    public InteractiveTxStepResult Start()
    {
        if (!Parameters.IsInitiator)
            throw new InvalidOperationException("Only the initiator starts an interactive-tx negotiation.");

        if (_started || State != InteractiveTxSessionState.Negotiating)
            throw new InvalidOperationException("The negotiation already started.");

        var next = new InteractiveTxSession(this) { _started = true };
        return next.SendNext();
    }

    /// <summary>
    /// Applies a message received from the peer: <c>tx_add_input</c>, <c>tx_add_output</c>, <c>tx_remove_input</c>,
    /// <c>tx_remove_output</c>, <c>tx_complete</c>, <c>tx_signatures</c> or <c>tx_abort</c>. During the negotiation the
    /// result carries our next message (our turn); on the second consecutive <c>tx_complete</c> it sets
    /// <see cref="InteractiveTxStepResult.NegotiationComplete"/>. A rule broken by the peer yields our <c>tx_abort</c>.
    /// A received <c>tx_abort</c> is echoed unless we already sent one (IT-ABT-01).
    /// </summary>
    /// <remarks>
    /// Messages received after the negotiation was aborted are ignored (no outbound). A valid <c>tx_signatures</c> never
    /// yields an outbound message: when ours is still to be sent, the driver signs and calls
    /// <see cref="SendTxSignatures"/> (BOLT 2: "MUST reply with their <c>tx_signatures</c> if not already
    /// transmitted").
    /// </remarks>
    /// <param name="message">The received message.</param>
    /// <param name="prevTxInspector">Reads the <c>prevtx</c> of a <c>tx_add_input</c> (IT-R-01).</param>
    /// <exception cref="ArgumentException">The message is not an interactive-tx message of this negotiation, or it
    /// belongs to another channel.</exception>
    /// <exception cref="ChannelFailedException">A splice's <c>tx_signatures</c> without
    /// <c>shared_input_signature</c> (SP-SIG-01).</exception>
    public InteractiveTxStepResult Receive(IChannelMessage message, IPrevTxInspector prevTxInspector)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(prevTxInspector);

        if (message.Payload.ChannelId != Parameters.ChannelId)
            throw new ArgumentException("The message belongs to another channel.", nameof(message));

        switch (message)
        {
            case TxAbortMessage abort:
                return ReceiveAbort(abort);
            case TxAddInputMessage or TxAddOutputMessage or TxRemoveInputMessage or TxRemoveOutputMessage
                or TxCompleteMessage or TxSignaturesMessage:
                break;
            default:
                throw new ArgumentException($"{message.Type} is not handled by the interactive-tx session.",
                                            nameof(message));
        }

        if (State == InteractiveTxSessionState.Aborted)
            return Unchanged();

        if (message is TxSignaturesMessage txSignatures)
            return ReceiveTxSignatures(txSignatures);

        if (State != InteractiveTxSessionState.Negotiating || IsNegotiationComplete)
            return Fail(new InteractiveTxRuleViolation("IT-S-02", $"{message.Type} after the negotiation completed"));

        if (IsOurTurn)
            return Fail(new InteractiveTxRuleViolation("IT-S-02", $"{message.Type} received out of turn"));

        return message switch
        {
            TxAddInputMessage addInput => ReceiveAddInput(addInput, prevTxInspector),
            TxAddOutputMessage addOutput => ReceiveAddOutput(addOutput),
            TxRemoveInputMessage removeInput => ReceiveRemoveInput(removeInput),
            TxRemoveOutputMessage removeOutput => ReceiveRemoveOutput(removeOutput),
            _ => ReceiveComplete()
        };
    }

    /// <summary>
    /// Records the constructed transaction after <see cref="InteractiveTxStepResult.NegotiationComplete"/> (built by
    /// <see cref="IInteractiveTxBuilder.Build"/> from <see cref="Inputs"/> and <see cref="Outputs"/>), and moves to
    /// <see cref="InteractiveTxSessionState.AwaitingCommitmentSigned"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The negotiation is not complete, or the transaction does not match
    /// the negotiated inputs and outputs.</exception>
    public InteractiveTxSession WithConstructedTransaction(ConstructedInteractiveTx transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (State != InteractiveTxSessionState.Negotiating || !IsNegotiationComplete)
            throw new InvalidOperationException($"No completed negotiation to construct (state {State}).");

        if (transaction.Locktime != Parameters.Locktime)
            throw new InvalidOperationException("The transaction's locktime is not the negotiated one.");

        if (!transaction.Inputs.Select(i => i.SerialId).SequenceEqual(Inputs.Select(i => i.SerialId))
            || !transaction.Inputs.Zip(Inputs).All(p => SameInput(p.First, p.Second)))
            throw new InvalidOperationException("The transaction's inputs are not the negotiated ones in serial_id order.");

        if (!transaction.Outputs.Select(o => o.SerialId).SequenceEqual(Outputs.Select(o => o.SerialId))
            || !transaction.Outputs.Zip(Outputs).All(p => SameOutput(p.First, p.Second)))
            throw new InvalidOperationException("The transaction's outputs are not the negotiated ones in serial_id order.");

        return new InteractiveTxSession(this)
        {
            ConstructedTx = transaction,
            State = InteractiveTxSessionState.AwaitingCommitmentSigned
        };
    }

    /// <summary>
    /// The peer's valid <c>commitment_signed</c> for the new funding was received: <c>tx_signatures</c> may now be sent
    /// (IT-SIG-03). Moves to <see cref="InteractiveTxSessionState.AwaitingTxSignatures"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The session is not in
    /// <see cref="InteractiveTxSessionState.AwaitingCommitmentSigned"/>.</exception>
    public InteractiveTxSession OnCommitmentSignedReceived()
    {
        if (State != InteractiveTxSessionState.AwaitingCommitmentSigned)
            throw new InvalidOperationException($"commitment_signed is not expected in state {State}.");

        return new InteractiveTxSession(this) { State = InteractiveTxSessionState.AwaitingTxSignatures };
    }

    /// <summary>
    /// Whether we send <c>tx_signatures</c> first (IT-SIG-01): the side whose <c>tx_add_input</c>s total less sends
    /// first, the lower node id on a tie; a splice's shared input counts for the initiator. Valid once the transaction
    /// is constructed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The transaction is not constructed yet.</exception>
    public bool SendsTxSignaturesFirst()
    {
        if (ConstructedTx is null)
            throw new InvalidOperationException("The transaction is not constructed yet.");

        return TxSignaturesOrder.LocalSendsFirst(Inputs, Parameters.LocalNodeId, Parameters.RemoteNodeId);
    }

    /// <summary>
    /// Our <c>tx_signatures</c> (IT-SIG-01): witnesses in ascending <c>serial_id</c> order of our inputs, plus the
    /// <c>shared_input_signature</c> of a splice. Allowed once <see cref="OnCommitmentSignedReceived"/> ran and either
    /// we go first or the peer's <c>tx_signatures</c> was received. Moves to
    /// <see cref="InteractiveTxSessionState.TxSignaturesSent"/> (or <see cref="InteractiveTxSessionState.Signed"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not allowed in the current state or order.</exception>
    /// <exception cref="ArgumentException">The witnesses do not match our inputs (count, empty witness), or the
    /// shared input signature is missing for a splice or given without one.</exception>
    public InteractiveTxStepResult SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                    CompactSignature? sharedInputSignature)
    {
        ArgumentNullException.ThrowIfNull(localWitnesses);

        if (State != InteractiveTxSessionState.AwaitingTxSignatures || ConstructedTx is null)
            throw new InvalidOperationException(
                $"tx_signatures cannot be sent in state {State} (IT-SIG-03: only after the peer's commitment_signed).");

        if (RemoteWitnesses is null && !SendsTxSignaturesFirst())
            throw new InvalidOperationException("The peer sends tx_signatures first (IT-SIG-01).");

        var ourInputs = Inputs.Count(i => i.AddedBy == InteractiveTxParty.Local && !i.IsShared);
        if (localWitnesses.Count != ourInputs)
            throw new ArgumentException($"{localWitnesses.Count} witnesses for our {ourInputs} inputs.",
                                        nameof(localWitnesses));

        if (localWitnesses.Any(w => ((byte[])w) is null || w.Length == 0))
            throw new ArgumentException("An empty witness.", nameof(localWitnesses));

        var hasSharedInput = Inputs.Any(i => i.IsShared);
        if (hasSharedInput && sharedInputSignature is null)
            throw new ArgumentException("A splice's tx_signatures needs shared_input_signature (SP-SIG-01).",
                                        nameof(sharedInputSignature));

        if (!hasSharedInput && sharedInputSignature is not null)
            throw new ArgumentException("shared_input_signature without a shared input.",
                                        nameof(sharedInputSignature));

        var payload = new TxSignaturesPayload(Parameters.ChannelId, [.. (byte[])ConstructedTx.TxId],
                                              [.. localWitnesses]);
        var message = new TxSignaturesMessage(payload,
                                              sharedInputSignature is null
                                                  ? null
                                                  : new SharedInputSignatureTlv(sharedInputSignature));

        var next = new InteractiveTxSession(this)
        {
            LocalWitnesses = [.. localWitnesses],
            LocalSharedInputSignature = sharedInputSignature,
            State = RemoteWitnesses is null
                        ? InteractiveTxSessionState.TxSignaturesSent
                        : InteractiveTxSessionState.Signed
        };

        return new InteractiveTxStepResult(next, [message], false);
    }

    /// <summary>
    /// Aborts the negotiation ourselves with <c>tx_abort</c> (for example the dependent protocol gave up). Aborting an
    /// already aborted session sends nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Our <c>tx_signatures</c> was already sent (IT-ABT-01: MUST NOT
    /// send <c>tx_abort</c> after it).</exception>
    public InteractiveTxStepResult Abort(string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);

        if (MustBeRemembered)
            throw new InvalidOperationException("tx_abort cannot be sent after our tx_signatures (IT-ABT-01).");

        if (State == InteractiveTxSessionState.Aborted)
            return new InteractiveTxStepResult(this, [], false, reason, "IT-ABT-01");

        return Fail(new InteractiveTxRuleViolation("IT-ABT-01", reason));
    }

    #region Receive

    private InteractiveTxStepResult ReceiveAddInput(TxAddInputMessage message, IPrevTxInspector prevTxInspector)
    {
        var payload = message.Payload;
        var isSenderInitiator = !Parameters.IsInitiator;

        var violation = InteractiveTxRules.CheckReceivedAddInputCount(ReceivedAddInputCount)
                        ?? InteractiveTxRules.CheckAddedSerialId(payload.SerialId, isSenderInitiator,
                                                                 Inputs.Any(i => i.SerialId == payload.SerialId))
                        ?? InteractiveTxRules.CheckSequence(payload.Sequence);
        if (violation is not null)
            return Fail(violation);

        InteractiveTxInput input;
        var prevTx = payload.PrevTx;
        if (prevTx is null || prevTx.Length == 0)
        {
            var expected = Parameters.SharedFunding?.SharedInput;
            violation = InteractiveTxRules.CheckSharedInputAdd(message.SharedInputTxIdTlv?.FundingTxId,
                                                               payload.PrevTxVout, isSenderInitiator, expected,
                                                               Inputs.Any(i => i.IsShared));
            if (violation is not null || expected is null)
                return Fail(violation ?? new InteractiveTxRuleViolation("IT-R-01", "no shared input expected"));

            input = new InteractiveTxInput(payload.SerialId, InteractiveTxParty.Remote, expected.TxId, expected.Vout,
                                           payload.Sequence, expected.Amount, expected.ScriptPubKey, null, true);
        }
        else
        {
            if (message.SharedInputTxIdTlv is not null)
                return Fail(new InteractiveTxRuleViolation("IT-R-01", "shared_input_txid with a prevtx"));

            var inspection = prevTxInspector.Inspect(prevTx, payload.PrevTxVout);
            violation = InteractiveTxRules.CheckPrevTx(inspection, payload.PrevTxVout, IsOutpointAdded);
            if (violation is not null)
                return Fail(violation);

            input = new InteractiveTxInput(payload.SerialId, InteractiveTxParty.Remote, inspection.TxId!.Value,
                                           payload.PrevTxVout, payload.Sequence, inspection.Amount!,
                                           inspection.ScriptPubKey!.Value, [.. prevTx], false);
        }

        var next = new InteractiveTxSession(this)
        {
            ReceivedAddInputCount = ReceivedAddInputCount + 1,
            Inputs = Insert(Inputs, input, i => i.SerialId),
            _lastReceivedWasComplete = false
        };
        return next.SendNext();
    }

    private InteractiveTxStepResult ReceiveAddOutput(TxAddOutputMessage message)
    {
        var payload = message.Payload;
        var isSenderInitiator = !Parameters.IsInitiator;

        var violation = InteractiveTxRules.CheckReceivedAddOutputCount(ReceivedAddOutputCount)
                        ?? InteractiveTxRules.CheckAddedSerialId(payload.SerialId, isSenderInitiator,
                                                                 Outputs.Any(o => o.SerialId == payload.SerialId))
                        ?? InteractiveTxRules.CheckOutput(payload.Amount, payload.Script);
        if (violation is not null)
            return Fail(violation);

        var isShared = isSenderInitiator && Parameters.SharedFunding is { } shared
                                         && payload.Script == shared.SharedOutputScript;
        var output = new InteractiveTxOutput(payload.SerialId, InteractiveTxParty.Remote, payload.Amount,
                                             payload.Script, isShared);

        var next = new InteractiveTxSession(this)
        {
            ReceivedAddOutputCount = ReceivedAddOutputCount + 1,
            Outputs = Insert(Outputs, output, o => o.SerialId),
            _lastReceivedWasComplete = false
        };
        return next.SendNext();
    }

    private InteractiveTxStepResult ReceiveRemoveInput(TxRemoveInputMessage message)
    {
        var serialId = message.Payload.SerialId;
        var existing = Inputs.FirstOrDefault(i => i.SerialId == serialId);
        var violation = InteractiveTxRules.CheckRemove(serialId, existing?.AddedBy, true);
        if (violation is not null)
            return Fail(violation);

        var next = new InteractiveTxSession(this)
        {
            Inputs = [.. Inputs.Where(i => i.SerialId != serialId)],
            _lastReceivedWasComplete = false
        };
        return next.SendNext();
    }

    private InteractiveTxStepResult ReceiveRemoveOutput(TxRemoveOutputMessage message)
    {
        var serialId = message.Payload.SerialId;
        var existing = Outputs.FirstOrDefault(o => o.SerialId == serialId);
        var violation = InteractiveTxRules.CheckRemove(serialId, existing?.AddedBy, false);
        if (violation is not null)
            return Fail(violation);

        var next = new InteractiveTxSession(this)
        {
            Outputs = [.. Outputs.Where(o => o.SerialId != serialId)],
            _lastReceivedWasComplete = false
        };
        return next.SendNext();
    }

    private InteractiveTxStepResult ReceiveComplete()
    {
        // BOLT 2: "until both nodes have sent and received a consecutive tx_complete".
        if (_lastSentWasComplete)
            return Complete(new InteractiveTxSession(this) { _lastReceivedWasComplete = true }, []);

        var next = new InteractiveTxSession(this) { _lastReceivedWasComplete = true };
        return next.SendNext();
    }

    private InteractiveTxStepResult ReceiveTxSignatures(TxSignaturesMessage message)
    {
        if (State is not (InteractiveTxSessionState.AwaitingTxSignatures
                          or InteractiveTxSessionState.TxSignaturesSent))
        {
            var unexpected = new InteractiveTxRuleViolation("IT-SIG-03", $"tx_signatures received in state {State}");
            return MustBeRemembered ? Unchanged(unexpected.RequirementId) : Fail(unexpected);
        }

        var constructed = ConstructedTx!;
        var remoteInputs = Inputs.Where(i => i.AddedBy == InteractiveTxParty.Remote && !i.IsShared).ToList();
        var violation = InteractiveTxRules.CheckTxSignatures(message.Payload.TxId, message.Payload.Witnesses,
                                                             constructed.TxId, remoteInputs);
        if (violation is not null)
            return MustBeRemembered ? Unchanged(violation.RequirementId) : Fail(violation);

        var hasSharedInput = Inputs.Any(i => i.IsShared);
        if (hasSharedInput && message.SharedInputSignatureTlv is null)
            throw new ChannelFailedException(Parameters.ChannelId,
                                             "tx_signatures without shared_input_signature for a splice",
                                             "missing shared_input_signature")
            {
                RequirementId = "SP-SIG-01"
            };

        var next = new InteractiveTxSession(this)
        {
            RemoteWitnesses = [.. message.Payload.Witnesses],
            RemoteSharedInputSignature = hasSharedInput ? message.SharedInputSignatureTlv!.Signature : null,
            State = State == InteractiveTxSessionState.TxSignaturesSent
                        ? InteractiveTxSessionState.Signed
                        : InteractiveTxSessionState.AwaitingTxSignatures
        };
        return new InteractiveTxStepResult(next, [], false);
    }

    private InteractiveTxStepResult ReceiveAbort(TxAbortMessage message)
    {
        var reason = "peer sent tx_abort: " + InteractiveTxRules.DescribeAbortData(message.Payload.Data ?? []);

        // BOLT 2 (tx_abort receiver): "if they have already sent tx_signatures to the peer: MUST NOT forget the channel
        // until any inputs to the negotiated tx have been spent", and a sending node "MUST NOT have already transmitted
        // tx_signatures", so no echo either.
        if (MustBeRemembered)
            return Unchanged("IT-ABT-01");

        // "if they have not sent tx_abort: MUST echo back tx_abort" (an aborted session already sent or echoed one).
        if (State == InteractiveTxSessionState.Aborted)
            return new InteractiveTxStepResult(this, [], false, reason, "IT-ABT-01");

        var next = new InteractiveTxSession(this) { State = InteractiveTxSessionState.Aborted, IsOurTurn = false };
        return new InteractiveTxStepResult(next, [CreateAbort(reason)], false, reason, "IT-ABT-01");
    }

    #endregion

    #region Send

    private InteractiveTxStepResult SendNext()
    {
        if (_nextLocalItem < _localItems.Count)
        {
            var item = _localItems[_nextLocalItem];
            var next = new InteractiveTxSession(this)
            {
                _nextLocalItem = _nextLocalItem + 1,
                _lastSentWasComplete = false,
                IsOurTurn = false
            };

            IChannelMessage message;
            if (item.Input is { } input)
            {
                next.Inputs = Insert(Inputs, input, i => i.SerialId);
                message = CreateAddInput(input);
            }
            else
            {
                var output = item.Output!;
                next.Outputs = Insert(Outputs, output, o => o.SerialId);
                message = new TxAddOutputMessage(new TxAddOutputPayload(output.Amount, Parameters.ChannelId,
                                                                        output.ScriptPubKey, output.SerialId));
            }

            return new InteractiveTxStepResult(next, [message], false);
        }

        var complete = new TxCompleteMessage(new TxCompletePayload(Parameters.ChannelId));
        if (_lastReceivedWasComplete)
            return Complete(this, [complete]);

        var afterComplete = new InteractiveTxSession(this) { _lastSentWasComplete = true, IsOurTurn = false };
        return new InteractiveTxStepResult(afterComplete, [complete], false);
    }

    private InteractiveTxStepResult Complete(InteractiveTxSession session, IReadOnlyList<IChannelMessage> outbound)
    {
        var violation = InteractiveTxRules.CheckTxComplete(session.Inputs, session.Outputs, Parameters.SharedFunding);
        if (violation is not null)
            return session.Fail(violation);

        var completed = new InteractiveTxSession(session)
        {
            IsNegotiationComplete = true,
            IsOurTurn = false,
            _lastSentWasComplete = true,
            _lastReceivedWasComplete = true
        };
        return new InteractiveTxStepResult(completed, outbound, true);
    }

    private TxAddInputMessage CreateAddInput(InteractiveTxInput input)
    {
        var payload = new TxAddInputPayload(Parameters.ChannelId, input.SerialId, input.PrevTx ?? [], input.PrevTxVout,
                                            input.Sequence);
        return input.IsShared
                   ? new TxAddInputMessage(payload, new SharedInputTxIdTlv(input.PrevTxId))
                   : new TxAddInputMessage(payload);
    }

    private TxAbortMessage CreateAbort(string reason) =>
        new(new TxAbortPayload(Parameters.ChannelId, System.Text.Encoding.ASCII.GetBytes(Printable(reason))));

    private InteractiveTxStepResult Fail(InteractiveTxRuleViolation violation)
    {
        var next = new InteractiveTxSession(this) { State = InteractiveTxSessionState.Aborted, IsOurTurn = false };
        return new InteractiveTxStepResult(next, [CreateAbort(violation.Reason)], false, violation.Reason,
                                           violation.RequirementId);
    }

    private InteractiveTxStepResult Unchanged(string? requirementId = null) => new(this, [], false, null, requirementId);

    #endregion

    private bool IsOutpointAdded(TxId txId, uint vout) =>
        Inputs.Any(i => i.PrevTxId == txId && i.PrevTxVout == vout);

    private static IReadOnlyList<T> Insert<T>(IReadOnlyList<T> items, T item, Func<T, ulong> serialId) =>
        [.. items.Append(item).OrderBy(serialId)];

    private static bool SameInput(InteractiveTxInput a, InteractiveTxInput b) =>
        a.SerialId == b.SerialId && a.AddedBy == b.AddedBy && a.PrevTxId == b.PrevTxId && a.PrevTxVout == b.PrevTxVout
        && a.Sequence == b.Sequence && a.Amount.MilliSatoshi == b.Amount.MilliSatoshi && a.ScriptPubKey == b.ScriptPubKey
        && a.IsShared == b.IsShared;

    private static bool SameOutput(InteractiveTxOutput a, InteractiveTxOutput b) =>
        a.SerialId == b.SerialId && a.AddedBy == b.AddedBy && a.Amount.MilliSatoshi == b.Amount.MilliSatoshi
        && a.ScriptPubKey == b.ScriptPubKey && a.IsShared == b.IsShared;

    private static string Printable(string text) =>
        new([.. text.Select(c => c is >= ' ' and <= '~' ? c : '?')]);

    private sealed record LocalItem(InteractiveTxInput? Input, InteractiveTxOutput? Output);
}