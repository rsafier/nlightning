using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Interfaces;
using Networks;

public class BitcoinWalletService : IBitcoinWalletService
{
    /// <summary>
    /// Serializes address lookup and batch generation across scopes (NL-283): two scopes that both find no unused
    /// address would otherwise compute the same first index and the second save would hit the
    /// (Index, IsChange, AddressType) key. Process-wide, so nodes sharing a process (tests) only wait on each other.
    /// </summary>
    private static readonly SemaphoreSlim s_addressGenerationLock = new(1, 1);

    /// <summary>
    /// How many addresses past each batch are derived, stored (unreserved) and watched, BIP 32/44 discovery style
    /// (NL-463): a wallet restored from seed onto a fresh database finds deposits on this window past the highest
    /// address it knows, and every deposit on a window address extends the window with the next batch, so restored
    /// funds stay discoverable up to the gap. BIP 44's default gap.
    /// </summary>
    public const int GapLimit = 20;

    /// <summary>How many hand-out addresses one batch carries (the gap window comes on top).</summary>
    private const int BatchSize = 10;

    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly ILogger<BitcoinWalletService> _logger;
    private readonly Network _network;
    private readonly ISecureKeyManager _secureKeyManager;
    private readonly IUnitOfWork _uow;

    public BitcoinWalletService(IBlockchainMonitor blockchainMonitor, ILogger<BitcoinWalletService> logger,
                                IOptions<NodeOptions> nodeOptions, ISecureKeyManager secureKeyManager,
                                IUnitOfWork uow)
    {
        _blockchainMonitor = blockchainMonitor;
        _logger = logger;
        _secureKeyManager = secureKeyManager;
        _uow = uow;

        // Fails on an unknown network: never derive addresses for a network we are not on
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
        _logger.LogInformation("BitcoinWalletService network: {Network} (config: {ConfigNetwork})", _network,
                               nodeOptions.Value.BitcoinNetwork);
    }

    /// <inheritdoc />
    public Task<WalletAddressModel> GetUnusedAddressAsync(AddressType addressType, bool isChange) =>
        ReserveUnusedAddressAsync(addressType, isChange);

    public async Task<WalletAddressModel> ReserveUnusedAddressAsync(AddressType addressType, bool isChange)
    {
        if (addressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
            throw new InvalidOperationException(
                "You cannot use flags for this method. Please select only one address type.");

        // Under the same lock as the lookup: no other caller can get this address between the lookup and the save
        await s_addressGenerationLock.WaitAsync();
        try
        {
            var address = await GetOrGenerateUnusedAddressAsync(addressType, isChange);
            await _uow.WalletAddressesDbRepository.ReserveAsync(address);
            await _uow.SaveChangesAsync();
            _logger.LogDebug("Handed out and reserved wallet address {Address}", address.Address);
            return new WalletAddressModel(address.AddressType, address.Index, address.IsChange, address.Address)
            {
                IsReserved = true
            };
        }
        finally
        {
            s_addressGenerationLock.Release();
        }
    }

    private async Task<WalletAddressModel> GetOrGenerateUnusedAddressAsync(AddressType addressType, bool isChange)
    {
        // Find an unused address in the DB
        var addressModel = await _uow.WalletAddressesDbRepository.GetUnusedAddressAsync(addressType, isChange);

        if (addressModel is not null)
            return addressModel;

        // If there's none, continue after the highest index we generated (NL-283)
        var firstIndex = await GetNextAddressIndexAsync(addressType, isChange);

        _logger.LogInformation("Generating {AddressCount} new {addressType} {change}addresses plus a {GapLimit} "
                               + "address discovery window and saving them to the database.",
                               BatchSize, Enum.GetName(addressType), isChange ? "change " : string.Empty, GapLimit);

        // Generate the hand-out batch and the discovery window past it (NL-463): the window addresses are stored
        // unreserved, so they are handed out before any new batch, and watched, so deposits on them are found
        var addressList = new List<WalletAddressModel>(BatchSize + GapLimit);
        for (var i = firstIndex; i < firstIndex + BatchSize + GapLimit; i++)
        {
            var pubKey = new PubKey(_secureKeyManager.GetWalletPublicKey(i, isChange, addressType));
            var scriptType = addressType == AddressType.P2Tr ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit;
            var address = pubKey.GetAddress(scriptType, _network);
            addressList.Add(new WalletAddressModel(addressType, i, isChange, address.ToString()));
        }

        _uow.WalletAddressesDbRepository.AddRange(addressList);
        await _uow.SaveChangesAsync();

        // Register all newly generated addresses with blockchain monitor
        foreach (var address in addressList)
        {
            _blockchainMonitor.WatchBitcoinAddress(address);
        }

        return addressList[0];
    }

    /// <summary>
    /// The first index of a new batch: one past the highest stored index, or 0 when this address type and chain have no
    /// address yet. The repository reports 0 both for "no address" and for "only index 0", so the empty case is checked
    /// separately.
    /// </summary>
    private async Task<uint> GetNextAddressIndexAsync(AddressType addressType, bool isChange)
    {
        var highestIndex = await _uow.WalletAddressesDbRepository.GetLastUsedAddressIndex(addressType, isChange);
        if (highestIndex > 0)
            return highestIndex + 1;

        var hasAny = _uow.WalletAddressesDbRepository.GetAllAddresses()
                         .Any(a => a.AccountIndex == 0 && a.AddressType == addressType && a.IsChange == isChange);
        return hasAny ? 1u : 0u;
    }
}