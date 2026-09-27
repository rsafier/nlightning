namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;

public interface IBitcoinWalletService
{
    /// <summary>
    /// The lowest-index address without a UTXO that is not reserved (a new batch when there is none). The same
    /// address is returned to every caller until funds arrive on it (NL-280).
    /// </summary>
    Task<WalletAddressModel> GetUnusedAddressAsync(AddressType addressType, bool isChange);

    /// <summary>
    /// Like <see cref="GetUnusedAddressAsync"/>, but the address is reserved and saved before it is returned, so no
    /// later call hands it out again (NL-045: a channel's <c>upfront_shutdown_script</c>).
    /// </summary>
    Task<WalletAddressModel> ReserveUnusedAddressAsync(AddressType addressType, bool isChange);
}