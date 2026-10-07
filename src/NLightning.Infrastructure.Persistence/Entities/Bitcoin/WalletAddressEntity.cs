namespace NLightning.Infrastructure.Persistence.Entities.Bitcoin;

using Domain.Bitcoin.Enums;

public class WalletAddressEntity
{
    public uint Index { get; set; }
    public bool IsChange { get; set; }
    public required AddressType AddressType { get; set; }
    public required string Address { get; set; }
    public uint AccountIndex { get; set; }
    public uint? DerivationIndex { get; set; }
    public string AccountName { get; set; } = "default";

    /// <summary>
    /// Handed out for a use that owns the address until its funds arrive (a channel's <c>upfront_shutdown_script</c>,
    /// NL-045): never returned by the unused-address lookup again (migration
    /// <c>AddShutdownHtlcBoundaryAndAddressReservation</c>).
    /// </summary>
    public bool IsReserved { get; set; }

    public virtual IEnumerable<UtxoEntity>? Utxos { get; set; }

    // Default constructor for EF Core
    internal WalletAddressEntity() { }
}