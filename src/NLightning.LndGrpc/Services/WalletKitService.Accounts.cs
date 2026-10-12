using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.Wallet.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet;
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
    /// Lists the default BIP84/BIP86 scopes and durable named owned or watch-only accounts, with public key origins
    /// and issued/discovered branch counts. Default paths retain coin type 0 on every network. Name/type filters apply
    /// to all accounts. Nested and hybrid P2SH scopes are unsupported.
    /// </summary>
    public override async Task<ListAccountsResponse> ListAccounts(ListAccountsRequest request,
                                                                 ServerCallContext context)
    {
        var response = new ListAccountsResponse();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var keys = KeyManager;
        var counts = await CountUsedAddressesAsync();
        foreach (var (ours, lnd) in s_accountTypes)
        {
            if (request.Name.Length > 0 && request.Name != DefaultAccount) continue;
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

        foreach (var account in await (uow.WalletAccountDbRepository ?? Domain.Bitcoin.Wallet.Interfaces.NullWalletAccountDbRepository.Instance).ListAsync(context.CancellationToken))
            if ((request.Name.Length == 0 || request.Name == account.Name)
                && (request.AddressType == WalletAddressType.Unknown || request.AddressType == ToAccountType(account.AddressType)))
                response.Accounts.Add(ToAccount(account));

        return response;
    }

    /// <summary>
    /// Lists handed-out or discovered addresses, actual BIP32 children and balances for the selected account.
    /// Without an explicit name, custom accounts are included when <c>show_custom_accounts</c> is set. Imported
    /// balances come from the isolated watch-only index; they do not count as spendable custody. Silent-payment
    /// outputs retain their actual P2TR output keys without an invented BIP32 path.
    /// </summary>
    public override async Task<ListAddressesResponse> ListAddresses(ListAddressesRequest request,
                                                                   ServerCallContext context)
    {
        var response = new ListAddressesResponse();

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

        if (_serviceProvider.GetService<Infrastructure.Bitcoin.Wallet.Imports.ImportedTapscriptTracker>() is { } imported)
            foreach (var output in (await Run(() => imported.SnapshotAsync(context.CancellationToken))).Outputs)
                if (output.Output.ScriptPubKey.GetDestinationAddress(_network) is { } destination)
                    balances[destination.ToString()] = balances.GetValueOrDefault(destination.ToString()) + output.Output.Value.Satoshi;

        var addresses = unitOfWork.WalletAddressesDbRepository.GetAllAddresses().ToList();
        foreach (var (ours, lnd) in s_accountTypes)
        {
            if (request.AccountName.Length > 0 && request.AccountName != DefaultAccount) continue;
            if (keys.GetDepositAccount(ours) is not { } account)
                continue;

            var xpub = ExtPubKey.Parse(account.ExtendedPublicKey, _network);
            var item = new AccountWithAddresses
            {
                Name = DefaultAccount,
                AddressType = lnd,
                DerivationPath = account.DerivationPath
            };
            foreach (var address in addresses.Where(a => a.AccountIndex == 0 && a.AddressType == ours
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

        foreach (var account in await (unitOfWork.WalletAccountDbRepository ?? Domain.Bitcoin.Wallet.Interfaces.NullWalletAccountDbRepository.Instance).ListAsync(context.CancellationToken))
        {
            if (request.AccountName.Length > 0 && request.AccountName != account.Name) continue;
            if (request.AccountName.Length == 0 && !request.ShowCustomAccounts) continue;
            var accountService = scope.ServiceProvider.GetRequiredService<WalletAccountService>();
            var item = new AccountWithAddresses
            {
                Name = account.Name,
                AddressType = ToAccountType(account.AddressType),
                DerivationPath = account.DerivationPath
            };
            var xpub = ExtPubKey.Parse(account.ExtendedPublicKey, _network);
            foreach (var change in new[] { false, true })
                for (uint index = 0; index < (change ? account.InternalKeyCount : account.ExternalKeyCount); index++)
                {
                    var address = accountService.Address(account, change, index);
                    item.Addresses.Add(new AddressProperty
                    {
                        Address = address,
                        IsInternal = change,
                        Balance = balances.GetValueOrDefault(address),
                        DerivationPath = $"{account.DerivationPath}/{(change ? 1 : 0)}/{index}",
                        PublicKey = ByteString.CopyFrom(xpub.Derive(change ? 1u : 0u).Derive(index).PubKey.ToBytes())
                    });
                }
            response.AccountWithAddresses.Add(item);
        }

        return response;
    }

    public override async Task<XCreateAccountResponse> XCreateAccount(XCreateAccountRequest request, ServerCallContext context)
    {
        if (!request.IKnowWhatIAmDoing)
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                "Named-account recovery requires recording the account scope and index; set i_know_what_i_am_doing."));
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WalletAccountService>();
        var account = await Run(() => service.CreateAsync(request.Name, ParseAccountType(request.AddressType), context.CancellationToken));
        return new XCreateAccountResponse { Account = ToAccount(account) };
    }

    public override async Task<ImportAccountResponse> ImportAccount(ImportAccountRequest request, ServerCallContext context)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WalletAccountService>();
        if (request.AddressType == WalletAddressType.Unknown)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Specify the account address type."));
        var account = await Run(() => service.ImportAsync(request.Name, request.ExtendedPublicKey,
            request.MasterKeyFingerprint.ToByteArray(), ParseAccountType(request.AddressType), request.DryRun, context.CancellationToken));
        var response = new ImportAccountResponse { Account = ToAccount(account) };
        if (request.DryRun)
            for (uint index = 0; index < BitcoinWalletService.GapLimit; index++)
            {
                response.DryRunExternalAddrs.Add(service.Address(account, false, index));
                response.DryRunInternalAddrs.Add(service.Address(account, true, index));
            }
        return response;
    }

    private static AddressType ParseAccountType(WalletAddressType type) => type switch
    {
        WalletAddressType.Unknown or WalletAddressType.TaprootPubkey => AddressType.P2Tr,
        WalletAddressType.WitnessPubkeyHash => AddressType.P2Wpkh,
        _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "Only BIP84 and BIP86 account scopes are supported."))
    };
    private static WalletAddressType ToAccountType(AddressType type) =>
        type == AddressType.P2Tr ? WalletAddressType.TaprootPubkey : WalletAddressType.WitnessPubkeyHash;
    private static Account ToAccount(WalletAccountModel account) => new()
    {
        Name = account.Name,
        AddressType = ToAccountType(account.AddressType),
        ExtendedPublicKey = account.ExtendedPublicKey,
        MasterKeyFingerprint = ByteString.CopyFrom(account.MasterFingerprint),
        DerivationPath = account.DerivationPath,
        WatchOnly = account.WatchOnly,
        ExternalKeyCount = account.ExternalKeyCount,
        InternalKeyCount = account.InternalKeyCount
    };

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
            if (address.AccountIndex != 0 || (!address.IsReserved && !funded.Contains(address.Address)))
                continue;

            var key = (address.AddressType, address.IsChange);
            counts[key] = Math.Max(counts.GetValueOrDefault(key), address.Index + 1);
        }

        return counts;
    }
}