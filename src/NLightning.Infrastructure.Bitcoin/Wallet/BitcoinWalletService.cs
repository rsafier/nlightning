using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
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

    public async Task<WalletAddressModel> GetUnusedAddressAsync(AddressType addressType, bool isChange)
    {
        if (addressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
            throw new InvalidOperationException(
                "You cannot use flags for this method. Please select only one address type.");

        await s_addressGenerationLock.WaitAsync();
        try
        {
            return await GetOrGenerateUnusedAddressAsync(addressType, isChange);
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

        _logger.LogInformation("Generating 10 new {addressType} {change}addresses and saving to the database.",
                               Enum.GetName(addressType), isChange ? "change " : string.Empty);

        // Generate 10 new addresses
        var addressList = new List<WalletAddressModel>(10);
        for (var i = firstIndex; i < firstIndex + 10; i++)
        {
            ExtPrivKey extPrivKey;
            if (addressType == AddressType.P2Tr)
            {
                extPrivKey = _secureKeyManager.GetDepositP2TrKeyAtIndex(i, isChange);
                var extKey = ExtKey.CreateFromBytes(extPrivKey);
                var address = extKey.Neuter().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, _network);

                addressList.Add(new WalletAddressModel(addressType, i, isChange, address.ToString()));
            }
            else
            {
                extPrivKey = _secureKeyManager.GetDepositP2WpkhKeyAtIndex(i, isChange);
                var extKey = ExtKey.CreateFromBytes(extPrivKey);
                var address = extKey.Neuter().PubKey.GetAddress(ScriptPubKeyType.Segwit, _network);

                addressList.Add(new WalletAddressModel(addressType, i, isChange, address.ToString()));
            }
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
                         .Any(a => a.AddressType == addressType && a.IsChange == isChange);
        return hasAny ? 1u : 0u;
    }
}