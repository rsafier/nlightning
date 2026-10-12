using NLightning.Domain.Bitcoin.Wallet.Models;

namespace NLightning.Domain.Bitcoin.Interfaces;

using Enums;

public interface IWalletAddressesDbRepository
{
    /// <summary>
    /// The lowest-index address of that type and chain that is not reserved, has no UTXO and lies above every address
    /// of that type and chain that is reserved or holds a UTXO (NL-280: the lookup never goes back to an address that
    /// was handed out or funded, even after its UTXOs were spent), or null.
    /// </summary>
    Task<WalletAddressModel?> GetUnusedAddressAsync(AddressType type, bool isChange);

    /// <summary>
    /// Stages the reservation of a stored address (<see cref="WalletAddressModel.IsReserved"/>): from the next save on
    /// <see cref="GetUnusedAddressAsync"/> never returns it again.
    /// </summary>
    /// <exception cref="InvalidOperationException">The address is not stored.</exception>
    Task ReserveAsync(WalletAddressModel address);

    Task<uint> GetLastUsedAddressIndex(AddressType addressType, bool isChange);
    void AddRange(List<WalletAddressModel> addresses);
    void UpdateAsync(WalletAddressModel address);
    IEnumerable<WalletAddressModel> GetAllAddresses();
}