using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Networks;

/// <summary>Ordinary recovery address catalogue derived entirely through public wallet keys.</summary>
public sealed class SilentPaymentRecoveryAddressSource(ISecureKeyManager keys, IOptions<NodeOptions> nodeOptions)
    : ISilentPaymentRecoveryAddressSource
{
    private readonly Network _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    private readonly ConditionalWeakTable<IUnitOfWork, Dictionary<(AddressType, bool, uint), WalletAddressModel>> _staged = new();
    public const uint MaximumAddressCount = 100_000;

    public Task<IReadOnlyList<WalletAddressModel>> StageAddressesAsync(IUnitOfWork uow, uint addressCount = 30,
                                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uow);
        if (addressCount is 0 or > MaximumAddressCount)
            throw new ArgumentOutOfRangeException(nameof(addressCount));
        cancellationToken.ThrowIfCancellationRequested();
        var stored = uow.WalletAddressesDbRepository.GetAllAddresses().Where(address => address.AccountIndex == 0)
            .ToDictionary(a => (a.AddressType, a.IsChange, a.Index));
        var staged = _staged.GetOrCreateValue(uow);
        foreach (var entry in staged)
            stored.TryAdd(entry.Key, entry.Value);
        var missing = new List<WalletAddressModel>();
        var catalogue = new List<WalletAddressModel>();
        foreach (var type in new[] { AddressType.P2Wpkh, AddressType.P2Tr })
        {
            if ((keys.SupportedWalletAddressTypes & type) == 0) continue;
            foreach (var isChange in new[] { false, true })
                for (uint index = 0; index < addressCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var publicKey = new PubKey(keys.GetWalletPublicKey(index, isChange, type));
                    var scriptType = type == AddressType.P2Tr
                        ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit;
                    var address = publicKey.GetAddress(scriptType, _network).ToString();
                    if (stored.TryGetValue((type, isChange, index), out var existing))
                    {
                        if (!StringComparer.Ordinal.Equals(existing.Address, address))
                            throw new InvalidOperationException("Recovery address catalogue does not match the wallet seed or network.");
                        catalogue.Add(existing);
                    }
                    else
                    {
                        var model = new WalletAddressModel(type, index, isChange, address);
                        missing.Add(model);
                        catalogue.Add(model);
                    }
                }
        }
        if (missing.Count != 0)
        {
            uow.WalletAddressesDbRepository.AddRange(missing);
            foreach (var model in missing)
                staged[(model.AddressType, model.IsChange, model.Index)] = model;
        }
        return Task.FromResult<IReadOnlyList<WalletAddressModel>>(catalogue);
    }
}