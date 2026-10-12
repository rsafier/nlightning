using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;

/// <summary>Ephemeral custody only: each request proves mempool presence, script ownership and current unspent state.</summary>
public sealed class WalletMempoolCatalog(IServiceScopeFactory scopes, IUtxoMemoryRepository wallet,
                                        IBitcoinChainService chain, IOptions<Domain.Node.Options.NodeOptions> options)
    : IWalletMempoolCatalog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Network _network = options.Value.BitcoinNetwork.ToNBitcoinNetwork();

    private readonly HashSet<uint256> _checkedForeign = [];
    private readonly HashSet<uint256> _knownOwned = [];
    private string? _scriptRevision;
    public const int DiscoveryBatchSize = 1_000;

    public Task<IReadOnlyList<UtxoModel>> RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCoreAsync(null, cancellationToken);

    public Task<IReadOnlyList<UtxoModel>> RefreshParentsAsync(IReadOnlyCollection<TxId> parents,
        CancellationToken cancellationToken = default) => RefreshCoreAsync(parents, cancellationToken);

    private async Task<IReadOnlyList<UtxoModel>> RefreshCoreAsync(IReadOnlyCollection<TxId>? requested, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var scripts = uow.WalletAddressesDbRepository.GetAllAddresses()
                .Where(address => address.AddressType is AddressType.P2Wpkh or AddressType.P2Tr)
                .ToDictionary(address => Convert.ToHexStringLower(BitcoinAddress.Create(address.Address, _network).ScriptPubKey.ToBytes()),
                              address => address);
            var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", scripts.Keys.Order()))));
            if (_scriptRevision != revision) { _checkedForeign.Clear(); _knownOwned.Clear(); _scriptRevision = revision; }
            var broadcasts = await uow.BroadcastTransactionDbRepository.GetPendingAsync();
            var ids = new HashSet<uint256>(wallet.GetUnconfirmedUtxos().Select(coin => new uint256((byte[])coin.TxId)));
            foreach (var pending in broadcasts) ids.Add(new uint256((byte[])pending.TransactionId));
            foreach (var reservation in await uow.FeeInputReservationDbRepository.GetAllAsync())
                foreach (var input in reservation.Inputs) ids.Add(new uint256((byte[])input.TxId));
            if (requested is not null) foreach (var id in requested) ids.Add(new uint256((byte[])id));
            HashSet<uint256>? current = null;
            if (requested is null)
            {
                current = (await chain.GetMempoolTransactionIdsAsync()).ToHashSet();
                _checkedForeign.RemoveWhere(id => !current.Contains(id));
                _knownOwned.RemoveWhere(id => !current.Contains(id));
            }
            ids.UnionWith(_knownOwned);
            if (ids.Count > 4_096) throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "too many wallet mempool parents to validate in one request");
            var discoveryIncomplete = false;
            if (requested is null)
            {
                var discoveryBudget = Math.Min(DiscoveryBatchSize, 4_096 - ids.Count);
                var unknown = current!.Where(id => !_checkedForeign.Contains(id) && !ids.Contains(id)).Take(discoveryBudget + 1).ToArray();
                discoveryIncomplete = unknown.Length > discoveryBudget;
                ids.UnionWith(unknown.Take(discoveryBudget));
            }
            var pendingSpenders = new Dictionary<OutPoint, HashSet<uint256>>();
            foreach (var broadcast in broadcasts)
            {
                Transaction child;
                try { child = Transaction.Load(broadcast.RawTransaction, _network); }
                catch (FormatException) { continue; }
                foreach (var input in child.Inputs)
                {
                    if (!pendingSpenders.TryGetValue(input.PrevOut, out var spenders)) pendingSpenders[input.PrevOut] = spenders = [];
                    spenders.Add(child.GetHash());
                }
            }
            var found = new Dictionary<(TxId, uint), UtxoModel>();
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tx = await chain.GetTransactionAsync(id);
                if (tx is null || tx.GetHash() != id || tx.IsCoinBase) { _knownOwned.Remove(id); continue; }
                var owned = tx.Outputs.Select((output, index) => (output, index)).Where(pair =>
                    scripts.ContainsKey(Convert.ToHexStringLower(pair.output.ScriptPubKey.ToBytes()))).ToArray();
                if (owned.Length == 0) { _checkedForeign.Add(id); continue; }
                _knownOwned.Add(id); // Progress survives an incomplete batch clearing signable transient custody.
                if (await chain.GetMempoolEntryAsync(id) is null) { _knownOwned.Remove(id); continue; }
                foreach (var (output, index) in owned)
                {
                    var proven = await chain.GetMempoolUnspentOutputAsync(new OutPoint(id, (uint)index));
                    if (proven is null && pendingSpenders.TryGetValue(new OutPoint(id, (uint)index), out var expected))
                    {
                        var actual = await chain.GetMempoolSpendersAsync([new OutPoint(id, (uint)index)]);
                        if (actual is not null && actual.TryGetValue(new OutPoint(id, (uint)index), out var spender) && expected.Contains(spender))
                            proven = output; // Kept only for exact leased RBF; ordinary selection excludes pending spends.
                    }
                    if (proven is null || proven.Value != output.Value || proven.ScriptPubKey != output.ScriptPubKey) continue;
                    var txid = new TxId(id.ToBytes());
                    if (wallet.TryGetUtxo(txid, (uint)index, out var confirmed) && confirmed.BlockHeight != 0) continue;
                    var model = new UtxoModel(txid, (uint)index, LightningMoney.Satoshis(output.Value.Satoshi), 0,
                        scripts[Convert.ToHexStringLower(output.ScriptPubKey.ToBytes())]);
                    found[(txid, (uint)index)] = model;
                }
            }
            foreach (var existing in wallet.GetUnconfirmedUtxos())
                if (!found.ContainsKey((existing.TxId, existing.Index)))
                    wallet.RemoveUnconfirmed(existing.TxId, existing.Index);
            foreach (var model in found.Values) wallet.AddUnconfirmed(model);
            if (discoveryIncomplete)
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition,
                    "unconfirmed wallet discovery is incomplete; retry to advance discovery or specify exact input parents");
            return found.Values.ToArray();
        }
        catch
        {
            // Never leave stale signable custody after a failed validation. Reservation identities survive eviction.
            foreach (var existing in wallet.GetUnconfirmedUtxos()) wallet.RemoveUnconfirmed(existing.TxId, existing.Index);
            throw;
        }
        finally { _gate.Release(); }
    }
}