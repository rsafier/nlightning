using System.Text;

namespace NLightning.Testing.Cluster.Chain;

using Nodes.BitcoinCore.Rpc;

/// <summary>Timeouts and polling of a <see cref="RegtestChain"/>.</summary>
public sealed record RegtestChainOptions
{
    /// <summary>The default wait (followers at the tip, a transaction in the mempool or a block).</summary>
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Where the chain writes what it does (mines, reorgs, seeds fees); null for nowhere.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>How <see cref="RegtestChain.SeedFeeEstimatesAsync"/> feeds bitcoind's fee estimator.</summary>
public sealed record FeeSeedOptions
{
    /// <summary>Blocks to mine, each with <see cref="TransactionsPerBlock"/> transactions at the target rate.</summary>
    public int Blocks { get; init; } = 8;

    public int TransactionsPerBlock { get; init; } = 5;

    /// <summary>Each seeding transaction's amount (to the wallet itself).</summary>
    public long AmountSat { get; init; } = 10_000;

    /// <summary>The target the returned estimate is read at.</summary>
    public int ConfirmationTarget { get; init; } = 2;

    public FeeEstimateMode Mode { get; init; } = FeeEstimateMode.Economical;
}

/// <summary>
/// The chain helpers of a run (plan R10) against its mining bitcoind: mine, mine and wait until every follower has the
/// tip, wait for a transaction in the mempool or a block, reorgs (<c>invalidateblock</c>/<c>reconsiderblock</c>), and fee
/// control (<c>settxfee</c>, seeding <c>estimatesmartfee</c>). Blocks go to the wallet's mining address.
/// </summary>
public sealed class RegtestChain
{
    private readonly RegtestChainOptions _options;
    private string? _miningAddress;

    public RegtestChain(IBitcoinCoreRpc rpc, RegtestChainOptions? options = null)
    {
        Rpc = rpc ?? throw new ArgumentNullException(nameof(rpc));
        _options = options ?? new RegtestChainOptions();
    }

    /// <summary>The mining bitcoind (its wallet pays the blocks and the sends).</summary>
    public IBitcoinCoreRpc Rpc { get; }

    public Task<ChainTip> GetTipAsync(CancellationToken cancellationToken) => Rpc.GetTipAsync(cancellationToken);

    /// <summary>The wallet address the blocks pay (one per chain, asked once).</summary>
    public async Task<string> GetMiningAddressAsync(CancellationToken cancellationToken) =>
        _miningAddress ??= await Rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Mines <paramref name="blocks"/> blocks (with the mempool) and returns their hashes.</summary>
    public async Task<IReadOnlyList<string>> MineAsync(int blocks, CancellationToken cancellationToken,
                                                       string? address = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blocks);
        if (blocks == 0)
            return [];

        address ??= await GetMiningAddressAsync(cancellationToken).ConfigureAwait(false);
        var hashes = await Rpc.GenerateToAddressAsync(blocks, address, cancellationToken).ConfigureAwait(false);
        Log($"mined {blocks} block(s), tip {hashes[^1]}");
        return hashes;
    }

    /// <summary>
    /// Waits until every follower is at the mining node's tip. The tip is re-read on every poll, so a block mined
    /// meanwhile only moves the target.
    /// </summary>
    /// <returns>The tip every follower reached.</returns>
    /// <exception cref="TimeoutException">Some follower did not reach the tip; the message lists every one's state.</exception>
    public async Task<ChainTip> WaitAllAtTipAsync(IEnumerable<ChainFollower> followers,
                                                  CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var list = followers.ToList();
        var deadline = DateTime.UtcNow + (timeout ?? _options.DefaultTimeout);
        var status = new StringBuilder();
        while (true)
        {
            var tip = await Rpc.GetTipAsync(cancellationToken).ConfigureAwait(false);
            var states = await Task.WhenAll(list.Select(f => f.ProbeAsync(tip, cancellationToken)))
                                   .ConfigureAwait(false);
            if (states.All(s => s.AtTip))
                return tip;

            status.Clear().Append("tip ").Append(tip.Height);
            for (var i = 0; i < list.Count; i++)
                status.Append(", ").Append(list[i].Name).Append(' ').Append(states[i].State);

            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Followers not at the chain tip after {timeout ?? _options.DefaultTimeout}: {status}");

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Mines <paramref name="blocks"/> blocks, then waits until every follower has the tip.</summary>
    public async Task<ChainTip> MineAndWaitAsync(int blocks, IEnumerable<ChainFollower> followers,
                                                 CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        await MineAsync(blocks, cancellationToken).ConfigureAwait(false);
        return await WaitAllAtTipAsync(followers, cancellationToken, timeout).ConfigureAwait(false);
    }

    /// <summary>Sends from the wallet (fee rate pinned when given) and returns the txid.</summary>
    public Task<string> SendAsync(string address, long amountSat, CancellationToken cancellationToken,
                                  decimal? feeRateSatPerVb = null) =>
        Rpc.SendToAddressAsync(address, amountSat, feeRateSatPerVb, cancellationToken);

    /// <summary>Waits until the transaction is in the mempool (a peer broadcast it).</summary>
    public Task<TxStatus> WaitForMempoolAsync(string txId, CancellationToken cancellationToken,
                                              TimeSpan? timeout = null) =>
        WaitForStatusAsync(txId, s => s.State == TxState.InMempool, "in the mempool", cancellationToken, timeout);

    /// <summary>
    /// Waits (without mining) until the transaction has <paramref name="confirmations"/> confirmations in the active
    /// chain.
    /// </summary>
    public Task<TxStatus> WaitForConfirmationAsync(string txId, int confirmations,
                                                   CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(confirmations);
        return WaitForStatusAsync(txId, s => s.State == TxState.Confirmed && s.Confirmations >= confirmations,
                                  $"{confirmations} confirmation(s)", cancellationToken, timeout);
    }

    /// <summary>
    /// Waits until the transaction is in the mempool, mines until it has <paramref name="confirmations"/>
    /// confirmations and returns its status.
    /// </summary>
    public async Task<TxStatus> MineUntilConfirmedAsync(string txId, int confirmations,
                                                        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(confirmations);
        var status = await Rpc.GetTransactionStatusAsync(txId, cancellationToken).ConfigureAwait(false);
        if (status.State == TxState.NotFound)
            status = await WaitForMempoolAsync(txId, cancellationToken, timeout).ConfigureAwait(false);

        var missing = confirmations - (int)status.Confirmations;
        if (missing > 0)
            await MineAsync(missing, cancellationToken).ConfigureAwait(false);

        status = await Rpc.GetTransactionStatusAsync(txId, cancellationToken).ConfigureAwait(false);
        if (status.State != TxState.Confirmed || status.Confirmations < confirmations)
            throw new InvalidOperationException(
                $"{txId} has {status.Confirmations} confirmation(s) ({status.State}) after mining {missing} block(s)");

        return status;
    }

    /// <summary>
    /// Reorganizes the last <paramref name="depth"/> blocks: invalidates the first of them (and any other branch that
    /// takes over above the fork), then mines a competing branch on the fork block (by default
    /// <paramref name="depth"/> + 1 blocks, each to a fresh address so no block repeats an invalidated one).
    /// </summary>
    public async Task<ChainReorg> ReorgAsync(int depth, CancellationToken cancellationToken,
                                             ReorgOptions? options = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        options ??= new ReorgOptions();
        var newBlocks = options.NewBlocks ?? depth + 1;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(newBlocks, nameof(options.NewBlocks));

        var oldTip = await Rpc.GetTipAsync(cancellationToken).ConfigureAwait(false);
        if (depth > oldTip.Height)
            throw new ArgumentOutOfRangeException(nameof(depth), depth, $"the chain is only {oldTip.Height} blocks high");

        var forkHeight = oldTip.Height - depth;
        var forkHash = await Rpc.GetBlockHashAsync(forkHeight, cancellationToken).ConfigureAwait(false);
        var disconnected = new List<string>(depth);
        for (var height = forkHeight + 1; height <= oldTip.Height; height++)
            disconnected.Add(await Rpc.GetBlockHashAsync(height, cancellationToken).ConfigureAwait(false));

        await Rpc.InvalidateBlockAsync(disconnected[0], cancellationToken).ConfigureAwait(false);
        var others = await InvalidateOtherBranchesAsync(forkHeight, forkHash, cancellationToken).ConfigureAwait(false);

        var newHashes = new List<string>(newBlocks);
        for (var i = 0; i < newBlocks; i++)
        {
            var address = await Rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(false);
            if (i == 0 && options.FirstBlockTransactions is { } first)
                newHashes.Add(await Rpc.GenerateBlockAsync(address, first, cancellationToken).ConfigureAwait(false));
            else if (options.Transactions == ReorgTransactions.Drop)
                newHashes.Add(await Rpc.GenerateBlockAsync(address, [], cancellationToken).ConfigureAwait(false));
            else
                newHashes.AddRange(await Rpc.GenerateToAddressAsync(1, address, cancellationToken)
                                            .ConfigureAwait(false));
        }

        var newTip = await Rpc.GetTipAsync(cancellationToken).ConfigureAwait(false);
        Log($"reorg at {forkHeight}: {depth} block(s) out ({oldTip}), {newBlocks} in ({newTip})"
          + (others.Count == 0 ? string.Empty : $", {others.Count} other branch(es) invalidated"));
        return new ChainReorg(forkHeight, oldTip, newTip, disconnected, newHashes, others);
    }

    /// <summary>
    /// <c>reconsiderblock</c> of the blocks the reorg invalidated; an old branch is active again only with more work.
    /// </summary>
    /// <returns>The tip after.</returns>
    public async Task<ChainTip> ReconsiderAsync(ChainReorg reorg, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reorg);
        foreach (var hash in reorg.OtherInvalidatedHashes.Prepend(reorg.InvalidatedHash))
            await Rpc.ReconsiderBlockAsync(hash, cancellationToken).ConfigureAwait(false);
        var tip = await Rpc.GetTipAsync(cancellationToken).ConfigureAwait(false);
        Log($"reconsidered {reorg.InvalidatedHash}: tip {tip}");
        return tip;
    }

    /// <summary>
    /// The wallet's fee rate for later sends (<c>settxfee</c>; 0 goes back to the estimate or the fallback). Bitcoin
    /// Core 31 removed <c>settxfee</c>: there, pin the rate per send (<see cref="SendAsync"/>'s
    /// <c>feeRateSatPerVb</c>) or seed the estimator (<see cref="SeedFeeEstimatesAsync"/>).
    /// </summary>
    /// <exception cref="NotSupportedException">This bitcoind has no <c>settxfee</c>.</exception>
    public async Task SetWalletFeeRateAsync(decimal satPerVb, CancellationToken cancellationToken)
    {
        try
        {
            await Rpc.SetTxFeeAsync(satPerVb, cancellationToken).ConfigureAwait(false);
        }
        catch (BitcoinRpcException e) when (e.Code == BitcoinRpcErrorCodes.MethodNotFound)
        {
            throw new NotSupportedException(
                "This bitcoind has no settxfee (removed in Bitcoin Core 31): pin the fee rate per send or seed the "
              + "fee estimator instead", e);
        }
    }

    /// <summary>
    /// Feeds bitcoind's fee estimator until <c>estimatesmartfee</c> answers (regtest has no estimate otherwise): a
    /// fan-out to confirmed wallet outputs, then <see cref="FeeSeedOptions.Blocks"/> blocks each with
    /// <see cref="FeeSeedOptions.TransactionsPerBlock"/> self-sends at <paramref name="satPerVb"/>. Every seeding
    /// transaction spends confirmed outputs only (the estimator ignores transactions with unconfirmed parents). The
    /// estimator decays slowly, so seed a rate once per chain; a later different rate moves the estimate gradually.
    /// Needs a wallet with at least one mature coinbase (mine 101 blocks first).
    /// </summary>
    /// <returns>The estimate at <see cref="FeeSeedOptions.ConfirmationTarget"/>.</returns>
    /// <exception cref="InvalidOperationException">bitcoind still has no estimate afterwards.</exception>
    public async Task<FeeEstimate> SeedFeeEstimatesAsync(decimal satPerVb, CancellationToken cancellationToken,
                                                         FeeSeedOptions? options = null)
    {
        options ??= new FeeSeedOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(satPerVb, 1m);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Blocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TransactionsPerBlock);

        // Confirmed outputs for every seeding transaction of the first block; later blocks also have the change
        var fanOut = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < options.TransactionsPerBlock * 2; i++)
            fanOut[await Rpc.GetNewAddressAsync(cancellationToken).ConfigureAwait(false)] = options.AmountSat * 20;
        await Rpc.SendManyAsync(fanOut, satPerVb, cancellationToken).ConfigureAwait(false);
        await MineAsync(1, cancellationToken).ConfigureAwait(false);

        var target = await GetMiningAddressAsync(cancellationToken).ConfigureAwait(false);
        for (var block = 0; block < options.Blocks; block++)
        {
            for (var i = 0; i < options.TransactionsPerBlock; i++)
                await Rpc.SendToAddressAsync(target, options.AmountSat, satPerVb, cancellationToken)
                         .ConfigureAwait(false);
            await MineAsync(1, cancellationToken).ConfigureAwait(false);
        }

        var estimate = await Rpc.EstimateSmartFeeAsync(options.ConfirmationTarget, options.Mode, cancellationToken)
                                .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                           $"No fee estimate at target {options.ConfirmationTarget} after {options.Blocks} block(s) "
                         + $"of {options.TransactionsPerBlock} transaction(s) at {satPerVb} sat/vB");
        Log($"fee estimate seeded at {satPerVb} sat/vB: {estimate.SatPerVb} sat/vB for {estimate.Blocks} block(s)");
        return estimate;
    }

    /// <summary>
    /// After the old branch's invalidation the tip must be the fork block. Another valid branch above the fork (one an
    /// earlier reorg left and <c>reconsiderblock</c> brought back) becomes active instead when it is there: invalidate
    /// its first block too, until the fork block is the tip.
    /// </summary>
    private async Task<IReadOnlyList<string>> InvalidateOtherBranchesAsync(long forkHeight, string forkHash,
                                                                           CancellationToken cancellationToken)
    {
        const int maxBranches = 16;
        var others = new List<string>();
        while (true)
        {
            var tip = await Rpc.GetTipAsync(cancellationToken).ConfigureAwait(false);
            if (tip.Hash == forkHash)
                return others;
            if (tip.Height <= forkHeight || others.Count >= maxBranches)
                throw new InvalidOperationException(
                    $"The tip is {tip} after invalidating above the fork {forkHeight} ({forkHash})");

            var other = await Rpc.GetBlockHashAsync(forkHeight + 1, cancellationToken).ConfigureAwait(false);
            await Rpc.InvalidateBlockAsync(other, cancellationToken).ConfigureAwait(false);
            others.Add(other);
        }
    }

    private async Task<TxStatus> WaitForStatusAsync(string txId, Func<TxStatus, bool> done, string what,
                                                    CancellationToken cancellationToken, TimeSpan? timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(txId);
        TxStatus? last = null;
        return await Poll.ForAsync(async ct =>
                                        {
                                            last = await Rpc.GetTransactionStatusAsync(txId, ct)
                                                            .ConfigureAwait(false);
                                            return done(last) ? last : null;
                                        }, timeout ?? _options.DefaultTimeout, _options.PollInterval,
                                        $"{txId} {what}", cancellationToken,
                                        () => $"last {last?.State}, {last?.Confirmations ?? 0} confirmation(s)")
                              .ConfigureAwait(false);
    }

    private void Log(string line) => _options.Log?.Invoke($"[nltg-chain] {line}");
}