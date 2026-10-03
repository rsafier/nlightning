namespace NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;

public interface IBitcoinWalletService
{
    /// <summary>
    /// Hands out a fresh address: the lowest-index address above every handed-out or funded one (a new batch when
    /// there is none), reserved and saved before it is returned, so no later call gets it again, also after its funds
    /// were spent (NL-280). Every call returns another address.
    /// </summary>
    /// <remarks>
    /// It saves the scope's unit of work: call it before staging anything else in that scope.
    /// </remarks>
    Task<WalletAddressModel> GetUnusedAddressAsync(AddressType addressType, bool isChange);

    /// <summary>
    /// The same as <see cref="GetUnusedAddressAsync"/> (every handed-out address is reserved since NL-280); kept as the
    /// explicit name for callers that own the address (NL-045: a channel's <c>upfront_shutdown_script</c>).
    /// </summary>
    Task<WalletAddressModel> ReserveUnusedAddressAsync(AddressType addressType, bool isChange);
}