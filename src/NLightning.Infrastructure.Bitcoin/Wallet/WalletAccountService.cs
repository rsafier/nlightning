using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using NBitcoin;
using Bip32Encoders = NBitcoin.DataEncoders.Encoders;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Interfaces;
using Networks;

/// <summary>Named BIP84/BIP86 accounts with durable public discovery metadata and isolated watch-only scripts.</summary>
public sealed class WalletAccountService(IUnitOfWork uow, ISecureKeyManager keys, IBlockchainMonitor monitor,
                                        IOptions<NodeOptions> options)
{
    private readonly ConditionalWeakTable<IUnitOfWork, List<WalletAddressModel>> _staged = new();
    private static readonly SemaphoreSlim s_gate = new(1, 1);
    private readonly HashSet<string> _stagedWatchScripts = new(StringComparer.Ordinal);
    private readonly Network _network = options.Value.BitcoinNetwork.ToNBitcoinNetwork();

    /// <summary>The monitor holds the account allocator gate through its block commit.</summary>
    public static async ValueTask<IDisposable> EnterDiscoveryAsync(CancellationToken ct)
    {
        await s_gate.WaitAsync(ct);
        return new DiscoveryLease();
    }

    private sealed class DiscoveryLease : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) s_gate.Release();
        }
    }

    public async Task<WalletAccountModel> CreateAsync(string name, AddressType type, CancellationToken ct)
    {
        ValidateName(name);
        ValidateType(type);
        await s_gate.WaitAsync(ct);
        try
        {
            if (await uow.WalletAccountDbRepository.GetAsync(name, ct) is not null)
                throw new InvalidOperationException("Account already exists.");
            var accounts = await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).ListAsync(ct);
            var index = checked(accounts.Where(x => !x.WatchOnly && x.AddressType == type)
                .Select(x => x.AccountIndex).DefaultIfEmpty(0u).Max() + 1);
            if (index >= 0x80000000) throw new InvalidOperationException("Account derivation space exhausted.");
            var key = keys.GetDepositAccount(type, index)
                   ?? throw new InvalidOperationException("Account keys unavailable.");
            var account = new WalletAccountModel(name, type, index, key.ExtendedPublicKey,
                key.MasterFingerprint.ToArray(), key.DerivationPath, false, monitor.LastProcessedBlockHeight);
            await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).StageAsync(account, ct);
            var added = StageOwnedWindow(account, false, 0).Concat(StageOwnedWindow(account, true, 0)).ToList();
            await uow.SaveChangesAsync();
            foreach (var address in added) monitor.WatchBitcoinAddress(address);
            return account;
        }
        finally { s_gate.Release(); }
    }

    public async Task<WalletAccountModel> ImportAsync(string name, string xpub, byte[] fingerprint,
                                                     AddressType type, bool dryRun, CancellationToken ct)
    {
        ValidateName(name);
        ValidateType(type);
        if (fingerprint.Length is not (0 or 4)) throw new ArgumentException("Fingerprint must contain four bytes.");
        var decoded = Bip32Encoders.Base58Check.DecodeData(xpub);
        if (decoded.Length != 78 || decoded[4] != 3 || (decoded[9] & 0x80) == 0)
            throw new ArgumentException("Import requires a hardened account-level extended public key.");
        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(decoded);
        var testnet = _network != Network.Main;
        var valid = testnet ? version is 0x043587cf or 0x045f1cf6 : version is 0x0488b21e or 0x04b24746;
        if (!valid) throw new ArgumentException("Extended public key version or network is unsupported.");
        if ((version is 0x045f1cf6 or 0x04b24746) && type != AddressType.P2Wpkh)
            throw new ArgumentException("BIP84 extended public key requires witness pubkey hash addresses.");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(decoded, testnet ? 0x043587cfu : 0x0488b21eu);
        var normalized = Bip32Encoders.Base58Check.EncodeData(decoded);
        _ = ExtPubKey.Parse(normalized, _network);
        var index = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(decoded.AsSpan(9, 4)) & 0x7fffffff;
        // Imported accounts scan from genesis: the RPC supplies no birthday, so assuming the current tip loses deposits.
        var account = new WalletAccountModel(name, type, index, normalized, fingerprint.ToArray(),
            $"m/{(type == AddressType.P2Tr ? 86 : 84)}'/{(testnet ? 1 : 0)}'/{index}'", true, 0);
        if (dryRun) return account;
        await s_gate.WaitAsync(ct);
        try
        {
            if (await uow.WalletAccountDbRepository.GetAsync(name, ct) is not null)
                throw new InvalidOperationException("Account already exists.");
            if ((await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).ListAsync(ct)).Any(a => a.ExtendedPublicKey == normalized)
                || keys.GetDepositAccount(type)?.ExtendedPublicKey == normalized)
                throw new ArgumentException("This account key is already part of the wallet.");
            await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).StageAsync(account, ct);
            await StageWatchWindowAsync(account, false, 0);
            await StageWatchWindowAsync(account, true, 0);
            await uow.SaveChangesAsync();
            return account;
        }
        finally { s_gate.Release(); }
    }

    public async Task<WalletAddressModel> NextAsync(string name, AddressType type, bool change, CancellationToken ct)
    {
        await s_gate.WaitAsync(ct);
        try
        {
            var account = await uow.WalletAccountDbRepository.GetAsync(name, ct)
                       ?? throw new ArgumentException("Account not found.");
            if (account.AddressType != type) throw new ArgumentException("Address type does not match the account scope.");
            var index = change ? account.InternalKeyCount : account.ExternalKeyCount;
            if (index >= 0x80000000 - BitcoinWalletService.GapLimit)
                throw new InvalidOperationException("Account derivation space exhausted.");
            WalletAddressModel address;
            IReadOnlyList<WalletAddressModel> newlyDerived = [];
            if (account.WatchOnly)
            {
                await StageWatchWindowAsync(account, change, index);
                address = new WalletAddressModel(type, index, change, Address(account, change, index))
                { AccountName = name, AccountIndex = account.AccountIndex, DerivationIndex = index, IsReserved = true };
            }
            else
            {
                var added = StageOwnedWindow(account, change, index);
                newlyDerived = added;
                address = uow.WalletAddressesDbRepository.GetAllAddresses().Concat(added).DistinctBy(a => (a.AddressType, a.IsChange, a.Index)).Single(a => a.AccountName == name
                    && a.IsChange == change && a.DerivationIndex == index);
                await uow.WalletAddressesDbRepository.ReserveAsync(address);
            }
            account = change ? account with { InternalKeyCount = checked(index + 1) }
                             : account with { ExternalKeyCount = checked(index + 1) };
            await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).StageAsync(account, ct);
            await uow.SaveChangesAsync();
            foreach (var derived in newlyDerived) monitor.WatchBitcoinAddress(derived);
            return address;
        }
        finally { s_gate.Release(); }
    }

    /// <summary>Stage named-account discovery in the block's own unit of work. The caller saves and watches after commit.</summary>
    public async Task<IReadOnlyList<WalletAddressModel>> StageOwnedDiscoveryAsync(
        IUnitOfWork blockUow, IReadOnlyList<WalletAddressModel> usedAddresses, CancellationToken ct)
    {
        var added = new List<WalletAddressModel>();
        foreach (var account in (await (blockUow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).ListAsync(ct)).Where(a => !a.WatchOnly))
        {
            var updated = account;
            foreach (var change in new[] { false, true })
            {
                var issued = change ? updated.InternalKeyCount : updated.ExternalKeyCount;
                var used = usedAddresses.Where(a => a.AccountName == account.Name && a.IsChange == change)
                    .Select(a => checked((a.DerivationIndex ?? a.Index) + 1)).DefaultIfEmpty(0u).Max();
                var count = Math.Max(issued, used);
                if (count >= 0x80000000 - BitcoinWalletService.GapLimit)
                    throw new InvalidOperationException("Account discovery space exhausted.");
                added.AddRange(StageOwnedWindow(updated, change, count, blockUow));
                updated = change ? updated with { InternalKeyCount = count } : updated with { ExternalKeyCount = count };
            }
            if (updated != account) await (blockUow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).StageAsync(updated, ct);
        }
        return added;
    }

    /// <summary>Discover imported-account gaps using every historical receipt, including outputs already spent.</summary>
    public async Task<bool> ExtendWatchDiscoveryAsync(IReadOnlyList<Transaction> transactions, CancellationToken ct)
    {
        await s_gate.WaitAsync(ct);
        try
        {
            var scripts = transactions.SelectMany(tx => tx.Outputs).Select(o => Convert.ToHexString(o.ScriptPubKey.ToBytes()))
                .ToHashSet(StringComparer.Ordinal);
            var changed = false;
            foreach (var account in (await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).ListAsync(ct)).Where(a => a.WatchOnly))
            {
                var updated = account;
                foreach (var change in new[] { false, true })
                {
                    var count = change ? updated.InternalKeyCount : updated.ExternalKeyCount;
                    var next = count;
                    for (var index = count; index < checked(count + BitcoinWalletService.GapLimit); index++)
                    {
                        var script = BitcoinAddress.Create(Address(account, change, index), _network).ScriptPubKey.ToBytes();
                        if (scripts.Contains(Convert.ToHexString(script))) next = checked(index + 1);
                    }
                    if (next == count) continue;
                    if (next >= 0x80000000 - BitcoinWalletService.GapLimit)
                        throw new InvalidOperationException("Imported account discovery space exhausted.");
                    await StageWatchWindowAsync(updated, change, next);
                    updated = change ? updated with { InternalKeyCount = next } : updated with { ExternalKeyCount = next };
                    changed = true;
                }
                if (updated != account) await (uow.WalletAccountDbRepository ?? NullWalletAccountDbRepository.Instance).StageAsync(updated, ct);
            }
            if (changed) await uow.SaveChangesAsync();
            return changed;
        }
        finally { s_gate.Release(); }
    }

    public string Address(WalletAccountModel account, bool change, uint index) =>
        ExtPubKey.Parse(account.ExtendedPublicKey, _network).Derive(change ? 1u : 0u).Derive(index)
            .PubKey.GetAddress(account.AddressType == AddressType.P2Tr ? ScriptPubKeyType.TaprootBIP86
                                                                     : ScriptPubKeyType.Segwit, _network).ToString();

    private IReadOnlyList<WalletAddressModel> StageOwnedWindow(WalletAccountModel account, bool change, uint first, IUnitOfWork? blockUow = null)
    {
        var targetUow = blockUow ?? uow;
        var staged = _staged.GetOrCreateValue(targetUow);
        var existing = targetUow.WalletAddressesDbRepository.GetAllAddresses().Concat(staged).ToList();
        var physical = existing.Where(a => a.AddressType == account.AddressType && a.IsChange == change)
            .Select(a => a.Index).DefaultIfEmpty(0x7fffffffu).Max();
        physical = Math.Max(physical, 0x7fffffff);
        var added = new List<WalletAddressModel>();
        for (var i = first; i < checked(first + BitcoinWalletService.GapLimit); i++)
        {
            if (existing.Any(a => a.AccountName == account.Name && a.IsChange == change && a.DerivationIndex == i)) continue;
            if (physical == uint.MaxValue) throw new InvalidOperationException("Wallet address storage space exhausted.");
            added.Add(new WalletAddressModel(account.AddressType, ++physical, change, Address(account, change, i))
            { AccountName = account.Name, AccountIndex = account.AccountIndex, DerivationIndex = i });
        }
        targetUow.WalletAddressesDbRepository.AddRange(added);
        staged.AddRange(added);
        return added;
    }

    private async Task StageWatchWindowAsync(WalletAccountModel account, bool change, uint first)
    {
        var knownScripts = (await uow.ImportedTapscriptDbRepository.ListAsync()).Select(s => Convert.ToHexString(s.Script))
            .Concat(_stagedWatchScripts).ToHashSet(StringComparer.Ordinal);
        for (var i = first; i < checked(first + BitcoinWalletService.GapLimit); i++)
        {
            var script = BitcoinAddress.Create(Address(account, change, i), _network).ScriptPubKey.ToBytes();
            var scriptKey = Convert.ToHexString(script);
            if (!knownScripts.Add(scriptKey)) continue;
            if (knownScripts.Count > 1000) throw new InvalidOperationException("Imported script discovery limit reached (1000 scripts).");
            // The tracker persists and replays these scripts without ever creating wallet custody or a signing key.
            _stagedWatchScripts.Add(scriptKey);
            uow.ImportedTapscriptDbRepository.Add(new ImportedTapscript(script, [],
                System.Text.Encoding.UTF8.GetBytes($"account:{account.Name}:{(change ? 1 : 0)}:{i}"), account.BirthdayHeight));
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name is "default" or "imported"
            || name.Any(char.IsControl)) throw new ArgumentException("Invalid or reserved account name.");
    }
    private static void ValidateType(AddressType type)
    {
        if (type is not (AddressType.P2Wpkh or AddressType.P2Tr)) throw new ArgumentException("Unsupported account scope.");
    }
}