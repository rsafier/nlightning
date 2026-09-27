namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Interfaces;
using Models;
using Protocol.Interfaces;

/// <summary>
/// The pure interactive-tx negotiation engine of one channel (BOLT 2 "Interactive Transaction Construction"; splicing
/// plan §3.9, D4). No I/O: the driver (<c>InteractiveTxDriver</c>, lane IT-D) runs it under the channel's lock, sends
/// <see cref="InteractiveTxStepResult.Outbound"/>, and does the asynchronous work (the confirmed-input check, building,
/// signing, persistence) between steps through the ports.
/// </summary>
/// <remarks>
/// <para>Contracts only (IT-0): the behaviour is lane IT-A's (IT1-T1..T4), which moves the rules of the old
/// <c>Infrastructure/Protocol/Validators/Tx*Validator</c> here and deletes them with <c>InteractiveTransactionService</c>.
/// The member signatures are the seam with lanes IT-B, IT-C and IT-D; change them only through the integrator.</para>
/// <para>Rules it owns: IT-S-01 (serial id parity and uniqueness, sequence), IT-S-02 (turns, two consecutive
/// <c>tx_complete</c>), IT-R-01..IT-R-04 (incl. the 4096 <b>received</b> <c>tx_add_input</c>/<c>tx_add_output</c> per
/// negotiation, NL-219, and 252 inputs/outputs at <c>tx_complete</c>), IT-S-03 through <c>CollaborativeFeeCalculator</c>,
/// IT-SIG-01..03 (<c>tx_signatures</c> order and checks), IT-ABT-01 (<c>tx_abort</c>) and, for an RBF,
/// IT-RBF-01 (double-spend every <see cref="InteractiveTxSessionParameters.PreviousAttempts"/>). A rule broken by the
/// peer ends the negotiation with our <c>tx_abort</c> in <see cref="InteractiveTxStepResult.Outbound"/>, never with a
/// channel failure.</para>
/// <para>Immutable: every step returns the next session in <see cref="InteractiveTxStepResult.Next"/>.</para>
/// </remarks>
public sealed class InteractiveTxSession
{
    private const string NotImplementedMessage = "Interactive-tx engine: lane IT-A (IT1-T1..T4)";

    /// <summary>The terms the session was created with.</summary>
    public InteractiveTxSessionParameters Parameters { get; }

    /// <summary>Where the negotiation is.</summary>
    public InteractiveTxSessionState State { get; }

    /// <summary>The inputs currently added by both sides.</summary>
    public IReadOnlyList<InteractiveTxInput> Inputs { get; }

    /// <summary>The outputs currently added by both sides.</summary>
    public IReadOnlyList<InteractiveTxOutput> Outputs { get; }

    /// <summary>Whether the next message of the negotiation is ours (IT-S-02).</summary>
    public bool IsOurTurn { get; }

    /// <summary>
    /// The number of <c>tx_add_input</c> received in this negotiation, removals not subtracted (the 4096 cap of IT-R-01,
    /// NL-219).
    /// </summary>
    public int ReceivedAddInputCount { get; }

    /// <summary>The number of <c>tx_add_output</c> received in this negotiation (the 4096 cap of IT-R-02).</summary>
    public int ReceivedAddOutputCount { get; }

    /// <summary>The constructed transaction, once <see cref="WithConstructedTransaction"/> set it; null before.</summary>
    public ConstructedInteractiveTx? ConstructedTx { get; }

    /// <summary>The peer's witnesses from its valid <c>tx_signatures</c>, in its inputs' <c>serial_id</c> order; null
    /// before.</summary>
    public IReadOnlyList<Witness>? RemoteWitnesses { get; }

    /// <summary>The peer's <c>shared_input_signature</c>, when a splice's <c>tx_signatures</c> carried one.</summary>
    public CompactSignature? RemoteSharedInputSignature { get; }

    private InteractiveTxSession(InteractiveTxSessionParameters parameters)
    {
        Parameters = parameters;
        State = InteractiveTxSessionState.Negotiating;
        Inputs = [];
        Outputs = [];
        IsOurTurn = parameters.IsInitiator;
    }

    /// <summary>
    /// A new negotiation in <see cref="InteractiveTxSessionState.Negotiating"/>; the initiator's turn first.
    /// </summary>
    /// <exception cref="ArgumentException">The parameters are inconsistent (for example a contribution with a sequence
    /// above 0xFFFFFFFD).</exception>
    public static InteractiveTxSession Create(InteractiveTxSessionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new InteractiveTxSession(parameters);
    }

    /// <summary>
    /// Resumes a stored negotiation (from <see cref="InteractiveTxSessionModel.State"/>
    /// <see cref="InteractiveTxSessionState.AwaitingCommitmentSigned"/> on): after a restart or on
    /// <c>channel_reestablish</c> with <c>next_funding</c>, to finish the signature exchange.
    /// </summary>
    public static InteractiveTxSession Restore(InteractiveTxSessionModel model,
                                               InteractiveTxSessionParameters parameters)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    /// <summary>
    /// The initiator's first message (IT-S-02: the initiator starts; its first <c>tx_add_input</c>, or
    /// <c>tx_add_output</c>, or <c>tx_complete</c> when it adds nothing).
    /// </summary>
    /// <exception cref="InvalidOperationException">We are not the initiator, or the negotiation already started.</exception>
    public InteractiveTxStepResult Start()
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    /// <summary>
    /// Applies a message received from the peer: <c>tx_add_input</c>, <c>tx_add_output</c>, <c>tx_remove_input</c>,
    /// <c>tx_remove_output</c>, <c>tx_complete</c>, <c>tx_signatures</c> or <c>tx_abort</c>. During the negotiation the
    /// result carries our next message (our turn); on the second consecutive <c>tx_complete</c> it sets
    /// <see cref="InteractiveTxStepResult.NegotiationComplete"/>. A rule broken by the peer yields our <c>tx_abort</c>.
    /// A received <c>tx_abort</c> is echoed unless we already sent one (IT-ABT-01).
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="prevTxInspector">Reads the <c>prevtx</c> of a <c>tx_add_input</c> (IT-R-01).</param>
    /// <exception cref="ArgumentException">The message is not an interactive-tx message.</exception>
    public InteractiveTxStepResult Receive(IChannelMessage message, IPrevTxInspector prevTxInspector)
    {
        throw new NotImplementedException(NotImplementedMessage);
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
        throw new NotImplementedException(NotImplementedMessage);
    }

    /// <summary>
    /// The peer's valid <c>commitment_signed</c> for the new funding was received: <c>tx_signatures</c> may now be sent
    /// (IT-SIG-03). Moves to <see cref="InteractiveTxSessionState.AwaitingTxSignatures"/>.
    /// </summary>
    public InteractiveTxSession OnCommitmentSignedReceived()
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    /// <summary>
    /// Whether we send <c>tx_signatures</c> first (IT-SIG-01): the side whose <c>tx_add_input</c>s total less sends
    /// first, the lower node id on a tie; a splice's shared input counts for the initiator. Valid once the transaction
    /// is constructed.
    /// </summary>
    public bool SendsTxSignaturesFirst()
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    /// <summary>
    /// Our <c>tx_signatures</c> (IT-SIG-01): witnesses in ascending <c>serial_id</c> order of our inputs, plus the
    /// <c>shared_input_signature</c> of a splice. Allowed once <see cref="OnCommitmentSignedReceived"/> ran and either
    /// we go first or the peer's <c>tx_signatures</c> was received. Moves to
    /// <see cref="InteractiveTxSessionState.TxSignaturesSent"/> (or <see cref="InteractiveTxSessionState.Signed"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Not allowed in the current state or order.</exception>
    public InteractiveTxStepResult SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                    CompactSignature? sharedInputSignature)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }

    /// <summary>
    /// Aborts the negotiation ourselves with <c>tx_abort</c> (for example the dependent protocol gave up).
    /// </summary>
    /// <exception cref="InvalidOperationException">Our <c>tx_signatures</c> was already sent (IT-ABT-01: MUST NOT
    /// send <c>tx_abort</c> after it).</exception>
    public InteractiveTxStepResult Abort(string reason)
    {
        throw new NotImplementedException(NotImplementedMessage);
    }
}