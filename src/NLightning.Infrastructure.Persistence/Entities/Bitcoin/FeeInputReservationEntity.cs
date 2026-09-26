// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

/// <summary>
/// Wallet outputs reserved to pay a fee (BOLT 5 plan O7-T1, migration <c>AddFeeInputReservations</c>): the fee inputs of
/// a CPFP child or of an anchor channel's HTLC transaction. Its outpoints are <see cref="FeeInputReservationInputEntity"/>
/// rows, deleted with it.
/// </summary>
public class FeeInputReservationEntity
{
    /// <summary>The reservation's id.</summary>
    /// <remarks>This is the primary key</remarks>
    public required Guid Id { get; set; }

    /// <summary>What the inputs pay for, at most 128 characters.</summary>
    public required string Purpose { get; set; }

    /// <summary>The fee the reservation was sized for (the wallet inputs' share), in satoshis.</summary>
    public required long FeeSats { get; set; }

    /// <summary>What goes back to the wallet through <see cref="ChangeScript"/>, in satoshis; 0 without change.</summary>
    public required long ChangeAmountSats { get; set; }

    /// <summary>The wallet's P2WPKH change script, or null without change.</summary>
    public byte[]? ChangeScript { get; set; }

    public required DateTimeOffset CreatedAt { get; set; }

    public virtual ICollection<FeeInputReservationInputEntity>? Inputs { get; set; }

    // Default constructor for EF Core
    internal FeeInputReservationEntity()
    {
    }
}