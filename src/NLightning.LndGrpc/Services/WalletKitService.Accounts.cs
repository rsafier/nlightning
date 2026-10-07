using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Walletrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using WalletAddressType = Walletrpc.AddressType;

public sealed partial class WalletKitService
{
    /// <summary>The wallet's account types, in LND's order (its default accounts per BIP 43 scope; no nested P2SH).</summary>
    private static readonly (AddressType Ours, WalletAddressType Lnd)[] s_accountTypes =
    [
        (AddressType.P2Wpkh, WalletAddressType.WitnessPubkeyHash),
        (AddressType.P2Tr, WalletAddressType.TaprootPubkey)
    ];

    /// <summary>
    /// <c>ListAccounts</c> (NL-1247): the wallet's <c>default</c> account per address type it holds, P2WPKH
    /// (<c>m/84'/0'/0'</c>) and P2TR (<c>m/86'/0'/0'</c>; the node derives under coin type 0 on every network), each
    /// with its extended public key in the plain BIP32 serialization for the network (<c>xpub</c>/<c>tpub</c>, where
    /// LND uses the SLIP-132 version of the scope), the master key fingerprint, the number of external and change
    /// addresses handed out or funded, never watch-only. Filters: <c>name</c> (only <c>default</c> exists; another
    /// name lists nothing) and <c>address_type</c>. LND's nested P2SH account does not exist here.
    /// </summary>
    public override async Task<ListAccountsResponse> ListAccounts(ListAccountsRequest request,
                                                                 ServerCallContext context)
    {
        var response = new ListAccountsResponse();
        if (request.Name.Length > 0 && request.Name != DefaultAccount)
            return response;

        var keys = KeyManager;
        var counts = await CountUsedAddressesAsync();
        foreach (var (ours, lnd) in s_accountTypes)
        {
            if (request.AddressType != WalletAddressType.Unknown && request.AddressType != lnd)
                continue;
            if (keys.GetDepositAccount(ours) is not { } account)
                continue;

            response.Accounts.Add(new Account
            {
                Name = DefaultAccount,
                AddressType = lnd,
                ExtendedPublicKey = account.ExtendedPublicKey,
                MasterKeyFingerprint = ByteString.CopyFrom(account.MasterFingerprint),
                DerivationPath = account.DerivationPath,
                ExternalKeyCount = counts.GetValueOrDefault((ours, false)),
                InternalKeyCount = counts.GetValueOrDefault((ours, true)),
                WatchOnly = false
            });
        }

        return response;
    }

    /// <summary>
    /// <c>ListAddresses</c> (NL-1247): per account type of the <c>default</c> account, every wallet address handed out
    /// or funded (the stored look-ahead addresses nobody was given are left out, as LND lists only the addresses its
    /// wallet derived), with <c>is_internal</c> for change, its balance from the wallet's unspent outputs, its full
    /// derivation path and its public key (the derived key; for P2TR the untweaked internal key). An
    /// <c>account_name</c> other than <c>default</c> lists nothing; <c>show_custom_accounts</c> adds nothing (the
    /// node's channel keys are not wallet addresses).
    /// </summary>
    public override async Task<ListAddressesResponse> ListAddresses(ListAddressesRequest request,
                                                                   ServerCallContext context)
    {
        var response = new ListAddressesResponse();
        if (request.AccountName.Length > 0 && request.AccountName != DefaultAccount)
            return response;

        var keys = KeyManager;
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var balances = new Dictionary<string, long>(StringComparer.Ordinal);
        var unspent = await unitOfWork.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true);
        foreach (var utxo in unspent)
        {
            if (utxo.WalletAddress?.Address is { } address)
                balances[address] = balances.GetValueOrDefault(address) + utxo.Amount.Satoshi;
        }

        var addresses = unitOfWork.WalletAddressesDbRepository.GetAllAddresses().ToList();
        foreach (var (ours, lnd) in s_accountTypes)
        {
            if (keys.GetDepositAccount(ours) is not { } account)
                continue;

            var xpub = ExtPubKey.Parse(account.ExtendedPublicKey, _network);
            var item = new AccountWithAddresses
            {
                Name = DefaultAccount,
                AddressType = lnd,
                DerivationPath = account.DerivationPath
            };
            foreach (var address in addresses.Where(a => a.AddressType == ours
                                                      && (a.IsReserved || balances.ContainsKey(a.Address)))
                                             .OrderBy(a => a.IsChange).ThenBy(a => a.Index))
            {
                var branch = address.IsChange ? 1u : 0u;
                item.Addresses.Add(new AddressProperty
                {
                    Address = address.Address,
                    IsInternal = address.IsChange,
                    Balance = balances.GetValueOrDefault(address.Address),
                    DerivationPath = $"{account.DerivationPath}/{branch}/{address.Index}",
                    PublicKey = ByteString.CopyFrom(xpub.Derive(branch).Derive(address.Index).PubKey.ToBytes())
                });
            }

            if (ours == AddressType.P2Tr)
                foreach (var group in unspent.Where(u => u.SilentPayment is not null)
                                             .GroupBy(u => Convert.ToHexStringLower(u.SilentPayment!.OutputKey))
                                             .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var output = group.First().SilentPayment!;
                    var script = new Script(new byte[] { 0x51, 0x20 }.Concat(output.OutputKey).ToArray());
                    item.Addresses.Add(new AddressProperty
                    {
                        Address = script.GetDestinationAddress(_network)!.ToString(),
                        IsInternal = output.Label == 0,
                        Balance = group.Sum(u => u.Amount.Satoshi),
                        // A BIP 352 output has no BIP32 derivation path. Expose its actual output key.
                        PublicKey = ByteString.CopyFrom(new byte[] { 2 }.Concat(output.OutputKey).ToArray())
                    });
                }

            response.AccountWithAddresses.Add(item);
        }

        return response;
    }

    /// <summary>The node's key manager (public account data only).</summary>
    private ISecureKeyManager KeyManager =>
        _serviceProvider.GetService<ISecureKeyManager>()
     ?? throw new RpcException(new Status(StatusCode.Unavailable, "the wallet keys are not available"));

    /// <summary>Per account type and branch, how many addresses were handed out or funded (highest index + 1).</summary>
    private async Task<Dictionary<(AddressType Type, bool IsChange), uint>> CountUsedAddressesAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var funded = (await unitOfWork.UtxoDbRepository.GetUnspentAsync(includeWalletAddress: true))
                    .Select(u => u.WalletAddress?.Address).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var counts = new Dictionary<(AddressType, bool), uint>();
        foreach (var address in unitOfWork.WalletAddressesDbRepository.GetAllAddresses())
        {
            if (!address.IsReserved && !funded.Contains(address.Address))
                continue;

            var key = (address.AddressType, address.IsChange);
            counts[key] = Math.Max(counts.GetValueOrDefault(key), address.Index + 1);
        }

        return counts;
    }
}