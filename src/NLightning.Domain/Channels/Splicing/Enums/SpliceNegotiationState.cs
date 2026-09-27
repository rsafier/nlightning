namespace NLightning.Domain.Channels.Splicing.Enums;

/// <summary>
/// The steps of one splice negotiation (splicing plan §3.5). The interactive-tx steps themselves (inputs, outputs,
/// <c>tx_complete</c>, signatures) are tracked by the interactive-tx session (<c>InteractiveTxSessionState</c>).
/// </summary>
/// <remarks>Persisted if lane SP1-C stores negotiations (SP-I7): never renumber.</remarks>
public enum SpliceNegotiationState : byte
{
    /// <summary>We asked for quiescence to splice (<c>IQuiescenceService.RequestAsync</c>); nothing sent yet.</summary>
    AwaitingQuiescence = 1,

    /// <summary>We sent <c>splice_init</c> and wait for <c>splice_ack</c> or <c>tx_abort</c>.</summary>
    InitSent = 2,

    /// <summary>The interactive-tx session is building the splice transaction (after <c>splice_ack</c> sent or
    /// received).</summary>
    Negotiating = 3,

    /// <summary>The transaction is constructed and our <c>commitment_signed</c> for the new funding is sent (the
    /// negotiation must be remembered from here, SP-CS-01).</summary>
    CommitmentSigned = 4,

    /// <summary>Both <c>tx_signatures</c> were exchanged: the splice is a pending funding; quiescence ended
    /// (SP-Q-01).</summary>
    Signed = 5,

    /// <summary>Ended by <c>tx_abort</c> or a disconnection before our <c>tx_signatures</c>.</summary>
    Aborted = 6
}