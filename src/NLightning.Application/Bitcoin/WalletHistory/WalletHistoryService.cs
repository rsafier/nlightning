using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Application.Bitcoin.WalletHistory;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Infrastructure.Bitcoin.Networks;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Hash = Domain.Crypto.ValueObjects.Hash;

/// <summary>An opt-in, restart-safe historical index. It never stages wallet custody or accounting facts.</summary>
public sealed class WalletHistoryService(IServiceScopeFactory scopes, IBitcoinChainService chain,
    IBlockPrevoutSource prevouts, IWalletHistoryGate gate, ISilentPaymentRecoveryAddressSource addresses,
    IOptions<NodeOptions> nodeOptions, ILogger<WalletHistoryService> logger,
    TimeProvider? timeProvider = null)
    : BackgroundService, IWalletHistoryService
{
    private readonly Network _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private string? _lastError;

    public async Task<WalletHistoryRescanState?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await Work(scope).WalletTransactionDbRepository.GetRescanStateAsync(cancellationToken);
    }

    public async Task<WalletHistoryRescanState> StartRescanAsync(uint fromHeight, uint? toHeight = null,
        bool allowPartial = false, uint addressCount = 30, CancellationToken cancellationToken = default)
    {
        if (addressCount is 0 or > SilentPaymentRecoveryAddressSource.MaximumAddressCount)
            throw new ArgumentOutOfRangeException(nameof(addressCount));
        var tip = await chain.GetCurrentBlockHeightAsync().WaitAsync(cancellationToken);
        var target = toHeight ?? tip;
        if (fromHeight > target || target > tip) throw new ArgumentOutOfRangeException(nameof(fromHeight));
        var floor = await chain.GetBlockDataStartHeightAsync().WaitAsync(cancellationToken);
        if (fromHeight < floor && !allowPartial)
            throw new InvalidOperationException($"History birthday {fromHeight} is pruned (pruneheight {floor}); request an explicit partial scan or use an unpruned node.");
        var available = Math.Max(fromHeight, floor);
        if (available > target) throw new InvalidOperationException("No requested blocks are retained by this node.");
        _ = await chain.GetBlockAsync(available).WaitAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Block {available} unavailable; history recovery requires retained block data.");
        await prevouts.ValidateHeightAsync(available, cancellationToken);
        using var held = await gate.EnterAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var work = Work(scope);
        if ((await work.WalletTransactionDbRepository.GetRescanStateAsync(cancellationToken))?.IsActive == true)
            throw new InvalidOperationException("A wallet history rescan is already active; cancel it before requesting another.");
        var state = new WalletHistoryRescanState(Guid.NewGuid(), fromHeight, available, target,
            available == 0 ? null : available - 1, null, addressCount, true, available != fromHeight);
        await work.WalletTransactionDbRepository.StageRescanStateAsync(state, cancellationToken);
        await work.SaveChangesAsync();
        return state;
    }

    public async Task<WalletHistoryRescanState?> CancelAsync(CancellationToken cancellationToken = default)
    {
        using var held = await gate.EnterAsync(cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var work = Work(scope);
        var state = await work.WalletTransactionDbRepository.GetRescanStateAsync(cancellationToken);
        if (state is null) return null;
        state = state with { IsActive = false };
        await work.WalletTransactionDbRepository.StageRescanStateAsync(state, cancellationToken);
        await work.SaveChangesAsync();
        return state;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await ProcessNextBlockAsync(stoppingToken); if (worked) _lastError = null; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                if (_lastError != exception.Message)
                    logger.LogError(exception, "Wallet history recovery paused; its durable cursor has not advanced");
                _lastError = exception.Message;
                await RecordErrorAsync(exception.Message, stoppingToken);
            }
            await Task.Delay(worked ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(1), _time, stoppingToken);
        }
    }

    /// <summary>One bounded, atomic history commit; exposed internally for restart/failure proofs.</summary>
    internal async Task<bool> ProcessNextBlockAsync(CancellationToken cancellationToken)
    {
        var state = await GetStatusAsync(cancellationToken);
        if (state?.IsActive != true) return false;
        if (state.CursorHeight is { } cursor && state.CursorHash is { Length: 32 } hash &&
            !(await chain.GetBlockHashAsync(cursor).WaitAsync(cancellationToken)).ToBytes().AsSpan().SequenceEqual(hash))
            throw new InvalidOperationException("History cursor is on a disconnected branch; waiting for the live monitor's atomic rewind.");
        var height = state.CursorHeight is { } previous ? checked(previous + 1) : state.AvailableFromHeight;
        if (height > state.TargetHeight)
        {
            using var completedLease = await gate.EnterAsync(cancellationToken);
            await using var completedScope = scopes.CreateAsyncScope();
            var completed = Work(completedScope);
            var current = await completed.WalletTransactionDbRepository.GetRescanStateAsync(cancellationToken);
            if (!SameCheckpoint(state, current)) return false;
            await completed.WalletTransactionDbRepository.StageRescanStateAsync(state with { IsActive = false, Error = null }, cancellationToken);
            await completed.SaveChangesAsync();
            return true;
        }
        await prevouts.ValidateHeightAsync(height, cancellationToken);
        var block = await chain.GetBlockAsync(height).WaitAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Block {height} unavailable; history cannot skip pruned or missing blocks.");
        var value = new BitcoinBlock(block.ToBytes(), new Hash(block.GetHash().ToBytes()), block.Transactions.Count);
        var previousOutputs = await prevouts.GetAllPrevoutsAsync(value, height, cancellationToken);
        using var held = await gate.EnterAsync(cancellationToken);
        await RequireCanonicalAsync(block, height, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var work = Work(scope);
        var currentState = await work.WalletTransactionDbRepository.GetRescanStateAsync(cancellationToken);
        if (!SameCheckpoint(state, currentState)) return false;
        var catalogue = await addresses.StageAddressesAsync(work, state.AddressCount, cancellationToken);
        var scripts = catalogue.Concat(work.WalletAddressesDbRepository.GetAllAddresses())
            .Select(address => BitcoinAddress.Create(address.Address, _network).ScriptPubKey)
            .Select(script => Convert.ToHexString(script.ToBytes())).ToHashSet(StringComparer.Ordinal);
        foreach (var imported in await work.ImportedTapscriptDbRepository.ListAsync())
            scripts.Add(Convert.ToHexString(imported.Script));
        foreach (var output in await work.SilentPaymentDbRepository.GetOutputsAsync(cancellationToken))
            if (!output.Ignored) scripts.Add(Convert.ToHexString((byte[])[0x51, 0x20, .. output.OutputKey]));

        var parentIds = block.Transactions.Where(transaction => !transaction.IsCoinBase)
            .SelectMany(transaction => transaction.Inputs).Select(input => new TxId(input.PrevOut.Hash.ToBytes())).Distinct().ToArray();
        var parents = new Dictionary<TxId, WalletTransactionRecord>();
        foreach (var batch in parentIds.Chunk(500))
            foreach (var parent in await work.WalletTransactionDbRepository.GetByIdsAsync(batch, cancellationToken))
                parents[parent.TxId] = parent;
        foreach (var transaction in block.Transactions)
        {
            var outputs = transaction.Outputs.Select((output, index) => (output, index))
                .Where(pair => scripts.Contains(Convert.ToHexString(pair.output.ScriptPubKey.ToBytes())))
                .Select(pair => (uint)pair.index).ToArray();
            var inputs = new List<WalletTransactionInput>();
            if (!transaction.IsCoinBase)
            {
                var id = new TxId(transaction.GetHash().ToBytes());
                if (!previousOutputs.TryGetValue(id, out var previousInputs) || previousInputs.Count != transaction.Inputs.Count)
                    throw new InvalidOperationException("Historical input ownership cannot be proven; previous outputs are missing.");
                for (var index = 0; index < transaction.Inputs.Count; index++)
                {
                    var previousInput = previousInputs[index];
                    var point = transaction.Inputs[index].PrevOut;
                    var owned = scripts.Contains(Convert.ToHexString((byte[])previousInput.ScriptPubKey));
                    if (!owned && parents.TryGetValue(new TxId(point.Hash.ToBytes()), out var parent))
                        owned = parent.OurOutputs.Contains(point.N);
                    if (owned) inputs.Add(new WalletTransactionInput((uint)index, checked((long)previousInput.AmountSat)));
                }
            }
            if (outputs.Length == 0 && inputs.Count == 0) continue;
            var record = WalletTransactionHistory.Describe(transaction, height, block.GetHash().ToBytes(),
                block.Header.BlockTime, outputs, inputs);
            await work.WalletTransactionDbRepository.StageConfirmedAsync(record);
            parents[record.TxId] = record;
        }
        await RequireCanonicalAsync(block, height, cancellationToken);
        await work.WalletTransactionDbRepository.StageRescanStateAsync(state with
        {
            CursorHeight = height,
            CursorHash = block.GetHash().ToBytes(),
            Error = null,
            IsActive = height < state.TargetHeight
        }, cancellationToken);
        await work.SaveChangesAsync();
        return true;
    }

    private async Task RecordErrorAsync(string message, CancellationToken ct)
    {
        using var held = await gate.EnterAsync(ct);
        await using var scope = scopes.CreateAsyncScope();
        var work = Work(scope);
        var state = await work.WalletTransactionDbRepository.GetRescanStateAsync(ct);
        if (state?.IsActive != true) return;
        var error = message[..Math.Min(message.Length, 1024)];
        if (state.Error == error) return;
        await work.WalletTransactionDbRepository.StageRescanStateAsync(state with { Error = error }, ct);
        await work.SaveChangesAsync();
    }

    private async Task RequireCanonicalAsync(Block block, uint height, CancellationToken ct)
    {
        if (await chain.GetBlockHashAsync(height).WaitAsync(ct) != block.GetHash())
            throw new InvalidOperationException("Chain changed while preparing wallet history; no records or cursor committed.");
    }

    private static bool SameCheckpoint(WalletHistoryRescanState expected, WalletHistoryRescanState? actual) =>
        actual?.IsActive == true && actual.Generation == expected.Generation && actual.CursorHeight == expected.CursorHeight;
    private static IUnitOfWork Work(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
}