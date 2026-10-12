namespace NLightning.Domain.Bitcoin.SilentPayments.Interfaces;

using Persistence.Interfaces;
using Wallet.Models;

/// <summary>Seed-derived ordinary ownership catalogue for silent-payment transaction recovery.</summary>
public interface ISilentPaymentRecoveryAddressSource
{
    /// <summary>
    /// Stages missing receive and change addresses for P2WPKH and BIP86 indices below the explicit bound.
    /// Does not reserve addresses, save the unit of work, or move a scan cursor. The recovery transaction owner
    /// serializes catalogue writes and commits them with its recovery evidence.
    /// </summary>
    Task<IReadOnlyList<WalletAddressModel>> StageAddressesAsync(IUnitOfWork uow, uint addressCount = 30,
                                                              CancellationToken cancellationToken = default);
}