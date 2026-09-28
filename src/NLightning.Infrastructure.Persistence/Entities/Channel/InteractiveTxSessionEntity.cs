namespace NLightning.Infrastructure.Persistence.Entities.Channel;

using Domain.Channels.ValueObjects;

/// <summary>
/// A persisted interactive-tx negotiation (BOLT 2 "Interactive Transaction Construction", splicing plan §3.8, migration
/// <c>AddInteractiveTxSessions</c>, lane IT-C). One row per negotiation from the moment our <c>commitment_signed</c> for
/// it is sent; an RBF attempt is a new row.
/// </summary>
/// <remarks>
/// No FK to <c>Channels</c>: a dual-funded open (wave DF) may store its negotiation before or in the same save as its
/// channel row. The list columns are version-prefixed binary blobs written and read only by
/// <c>Infrastructure.Repositories/Database/Channel/InteractiveTxSessionEncoding</c>.
/// </remarks>
public class InteractiveTxSessionEntity
{
    public required ChannelId ChannelId { get; set; }
    public required Guid SessionId { get; set; }

    /// <summary><c>InteractiveTxPurpose</c>.</summary>
    public required byte Purpose { get; set; }

    public required bool IsInitiator { get; set; }
    public required uint FeeratePerKw { get; set; }
    public required uint Locktime { get; set; }

    /// <summary>Every input of both sides, ascending <c>serial_id</c> (blob).</summary>
    public required byte[] Inputs { get; set; }

    /// <summary>Every output of both sides, ascending <c>serial_id</c> (blob).</summary>
    public required byte[] Outputs { get; set; }

    /// <summary>Our contributed inputs and outputs (blob), without the reservation id.</summary>
    public required byte[] LocalContribution { get; set; }

    /// <summary>The wallet reservation of our contribution (<c>FeeInputReservations.Id</c>), if any.</summary>
    public Guid? LocalReservationId { get; set; }

    /// <summary>The constructed unsigned transaction (blob), once built.</summary>
    public byte[]? ConstructedTx { get; set; }

    /// <summary>Our witnesses (blob), once signed.</summary>
    public byte[]? OurWitnesses { get; set; }

    /// <summary>The peer's witnesses (blob), once received.</summary>
    public byte[]? TheirWitnesses { get; set; }

    /// <summary>Our 64-byte <c>shared_input_signature</c>.</summary>
    public byte[]? OurSharedInputSignature { get; set; }

    /// <summary>The peer's 64-byte <c>shared_input_signature</c>.</summary>
    public byte[]? TheirSharedInputSignature { get; set; }

    /// <summary>Our share of the funding output in satoshis (migration <c>AddDualFundAttempts</c>).</summary>
    public long? LocalFundingSatoshis { get; set; }

    /// <summary>
    /// The peer's 64-byte signature of our first commitment for the new funding (a dual-funded open; migration
    /// <c>AddDualFundAttempts</c>).
    /// </summary>
    public byte[]? TheirCommitmentSignature { get; set; }

    public required bool CommitmentSignedSent { get; set; }
    public required bool CommitmentSignedReceived { get; set; }
    public required bool TxSignaturesSent { get; set; }
    public required bool TxSignaturesReceived { get; set; }

    /// <summary><c>InteractiveTxSessionState</c>.</summary>
    public required byte State { get; set; }

    /// <summary>UTC ticks (<c>UtcTicksConverter</c>).</summary>
    public required DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC ticks (<c>UtcTicksConverter</c>); null while unresolved.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    internal InteractiveTxSessionEntity()
    {
    }
}