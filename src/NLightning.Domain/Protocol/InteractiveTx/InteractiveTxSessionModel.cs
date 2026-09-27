namespace NLightning.Domain.Protocol.InteractiveTx;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Enums;
using Models;

/// <summary>
/// A persisted interactive-tx negotiation (splicing plan §3.8, row of <c>InteractiveTxSessions</c>; lane IT-C maps it,
/// lane IT-D writes it). Stored from the moment our <c>commitment_signed</c> for the negotiated funding is sent (BOLT 2:
/// remember the negotiation), so a reconnection can resume the signature exchange (<c>next_funding</c>) and a restart
/// keeps the wallet reservation of a transaction that may still confirm (IT-ABT-01).
/// </summary>
/// <remarks>
/// Immutable: the driver stores a new value with <c>with</c> and <see cref="Interfaces.IInteractiveTxSessionDbRepository.UpdateAsync"/>.
/// The primary key is (<see cref="ChannelId"/>, <see cref="SessionId"/>); an RBF attempt is a new session.
/// </remarks>
public sealed record InteractiveTxSessionModel
{
    /// <summary>The channel.</summary>
    public required ChannelId ChannelId { get; init; }

    /// <summary>The negotiation's id (ours; not on the wire).</summary>
    public required Guid SessionId { get; init; }

    /// <summary>The protocol the negotiation serves.</summary>
    public required InteractiveTxPurpose Purpose { get; init; }

    /// <summary>Whether we were the initiator.</summary>
    public required bool IsInitiator { get; init; }

    /// <summary>The agreed feerate (sat/kw).</summary>
    public required uint FeeratePerKw { get; init; }

    /// <summary>The agreed <c>nLockTime</c>.</summary>
    public required uint Locktime { get; init; }

    /// <summary>Every input of both sides, in ascending <c>serial_id</c> order.</summary>
    public required IReadOnlyList<InteractiveTxInput> Inputs { get; init; }

    /// <summary>Every output of both sides, in ascending <c>serial_id</c> order.</summary>
    public required IReadOnlyList<InteractiveTxOutput> Outputs { get; init; }

    /// <summary>What we contributed (for the wallet reservation: kept after our <c>tx_signatures</c>, IT-ABT-01).</summary>
    public required InteractiveTxContribution LocalContribution { get; init; }

    /// <summary>The unsigned transaction, once constructed; null before.</summary>
    public ConstructedInteractiveTx? ConstructedTx { get; init; }

    /// <summary>Our witnesses in ascending <c>serial_id</c> order of our inputs, once signed; null before.</summary>
    public IReadOnlyList<Witness>? OurWitnesses { get; init; }

    /// <summary>The peer's witnesses from its <c>tx_signatures</c>, once received; null before.</summary>
    public IReadOnlyList<Witness>? TheirWitnesses { get; init; }

    /// <summary>Our <c>shared_input_signature</c> (a splice), once made; null otherwise.</summary>
    public CompactSignature? OurSharedInputSignature { get; init; }

    /// <summary>The peer's <c>shared_input_signature</c> (a splice), once received; null otherwise.</summary>
    public CompactSignature? TheirSharedInputSignature { get; init; }

    /// <summary>Whether our <c>commitment_signed</c> for the new funding was sent.</summary>
    public bool CommitmentSignedSent { get; init; }

    /// <summary>Whether the peer's valid <c>commitment_signed</c> for the new funding was received (IT-SIG-03).</summary>
    public bool CommitmentSignedReceived { get; init; }

    /// <summary>Whether our <c>tx_signatures</c> was sent (from then on no <c>tx_abort</c>, IT-ABT-01).</summary>
    public bool TxSignaturesSent { get; init; }

    /// <summary>Whether the peer's <c>tx_signatures</c> was received.</summary>
    public bool TxSignaturesReceived { get; init; }

    /// <summary>Where the negotiation is.</summary>
    public required InteractiveTxSessionState State { get; init; }

    /// <summary>When the row was first stored.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the negotiation was settled: the transaction (or one that double-spends it) confirmed and the wallet
    /// reservation was settled. Null while unresolved (<see cref="Interfaces.IInteractiveTxSessionDbRepository.GetUnresolvedAsync"/>).
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; init; }
}