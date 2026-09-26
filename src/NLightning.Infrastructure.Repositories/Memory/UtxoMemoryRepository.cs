using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace NLightning.Infrastructure.Repositories.Memory;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;

public class UtxoMemoryRepository : IUtxoMemoryRepository
{
    private readonly ConcurrentDictionary<(TxId, uint), UtxoModel> _utxoSet = [];

    // Fee input reservations by outpoint (BOLT 5 plan O7-T1). Kept apart from the UtxoModel so a reorg that removes and
    // re-adds an output (NL-293) does not drop its reservation.
    private readonly Dictionary<(TxId, uint), Guid> _feeReservations = [];

    // Serializes every reservation and channel-lock read-check-act, so a fee reservation and a funding never take the
    // same output
    private readonly Lock _reservationLock = new();

    public void Add(UtxoModel utxoModel)
    {
        if (!_utxoSet.TryAdd((utxoModel.TxId, utxoModel.Index), utxoModel))
            throw new InvalidOperationException("Cannot add Utxo");
    }

    public void Spend(UtxoModel utxoModel)
    {
        _utxoSet.TryRemove((utxoModel.TxId, utxoModel.Index), out _);
    }

    public bool TryGetUtxo(TxId txId, uint index, [MaybeNullWhen(false)] out UtxoModel utxoModel)
    {
        return _utxoSet.TryGetValue((txId, index), out utxoModel);
    }

    public LightningMoney GetConfirmedBalance(uint currentBlockHeight)
    {
        return LightningMoney.Satoshis(_utxoSet.Values
                                               .Where(x => x.BlockHeight + 3 <= currentBlockHeight)
                                               .Sum(x => x.Amount.Satoshi));
    }

    public LightningMoney GetUnconfirmedBalance(uint currentBlockHeight)
    {
        return LightningMoney.Satoshis(_utxoSet.Values
                                               .Where(x => x.BlockHeight + 3 > currentBlockHeight)
                                               .Sum(x => x.Amount.Satoshi));
    }

    public LightningMoney GetLockedBalance()
    {
        lock (_reservationLock)
        {
            return LightningMoney.Satoshis(_utxoSet.Values
                                                   .Where(x => x.LockedToChannelId is not null
                                                            || _feeReservations.ContainsKey((x.TxId, x.Index)))
                                                   .Sum(x => x.Amount.Satoshi));
        }
    }

    public void Load(List<UtxoModel> utxoSet)
    {
        foreach (var utxoModel in utxoSet)
            _utxoSet.TryAdd((utxoModel.TxId, utxoModel.Index), utxoModel);
    }

    public LightningMoney GetAvailableConfirmedBalance(uint currentBlockHeight,
                                                       IReadOnlySet<(TxId TxId, uint Index)> excludedOutpoints)
    {
        ArgumentNullException.ThrowIfNull(excludedOutpoints);

        lock (_reservationLock)
        {
            return LightningMoney.Satoshis(GetUnreservedUtxosLocked()
                                          .Where(x => x.BacksAnchorReserve(currentBlockHeight)
                                                   && !excludedOutpoints.Contains((x.TxId, x.Index)))
                                          .Sum(x => x.Amount.Satoshi));
        }
    }

    public List<UtxoModel> LockUtxosToSpendOnChannel(LightningMoney requestFundingAmount, ChannelId channelId)
    {
        lock (_reservationLock)
        {
            // Get available UTXOs (not already locked for other channels nor reserved for a fee)
            var availableUtxos = GetUnreservedUtxosLocked().OrderByDescending(utxo => utxo.Amount.Satoshi).ToList();
            if (availableUtxos.Count == 0)
                throw new InvalidOperationException("No available UTXOs");

            // Try Branch and Bound to find an exact match or minimize inputs
            var selectedUtxos = BranchAndBound(availableUtxos, requestFundingAmount);
            if (selectedUtxos == null || selectedUtxos.Count == 0)
                throw new InvalidOperationException("Insufficient funds");

            LockForChannelLocked(selectedUtxos, channelId);
            return selectedUtxos;
        }
    }

    public List<UtxoModel> LockUtxosToSpendOnChannel(LightningMoney requestFundingAmount, ChannelId channelId,
                                                     LightningMoney reserveToKeep,
                                                     IReadOnlySet<(TxId TxId, uint Index)> excludedOutpoints,
                                                     LightningMoney fundingFeeRatePerKw, uint currentBlockHeight)
    {
        ArgumentNullException.ThrowIfNull(requestFundingAmount);
        ArgumentNullException.ThrowIfNull(reserveToKeep);
        ArgumentNullException.ThrowIfNull(excludedOutpoints);
        ArgumentNullException.ThrowIfNull(fundingFeeRatePerKw);

        lock (_reservationLock)
        {
            // Outputs a funding can spend: not locked for other channels, reserved for a fee nor spent by one of our
            // pending broadcasts (NL-385), of a type the funding transaction takes
            var unreserved = GetUnreservedUtxosLocked()
                            .Where(utxo => !excludedOutpoints.Contains((utxo.TxId, utxo.Index)))
                            .ToList();
            var candidates = unreserved.Where(FundingFeeEstimator.CanFund)
                                       .OrderByDescending(utxo => utxo.Amount.Satoshi)
                                       .ToList();
            if (candidates.Count == 0)
                throw new InvalidOperationException("No available UTXOs");

            // The inputs pay the funding transaction's fee too (FundingTransactionModelFactory): select for the amount
            // plus the worst-case fee of as many inputs as the selection needs
            var selectedUtxos = SelectWithFundingFee(candidates, requestFundingAmount, fundingFeeRatePerKw);
            if (selectedUtxos is null || selectedUtxos.Count == 0)
                throw new InvalidOperationException("Insufficient funds");

            // NL-379: what stays in the wallet must cover the anchors reserve, so the CPFP child and the anchors HTLC
            // transactions can still find fee inputs. Only outputs the fee input selector could use count (mined three
            // blocks deep, with a known P2WPKH/P2TR address), plus the least change this funding returns (its inputs
            // minus the funding and the fee; none when the change would be dust)
            if (!reserveToKeep.IsZero)
            {
                var selected = selectedUtxos.Select(utxo => (utxo.TxId, utxo.Index)).ToHashSet();
                var keptSat = unreserved.Where(utxo => !selected.Contains((utxo.TxId, utxo.Index))
                                                    && utxo.BacksAnchorReserve(currentBlockHeight))
                                        .Sum(utxo => utxo.Amount.Satoshi);
                var change = FundingFeeEstimator.GetMinimumChange(selectedUtxos, requestFundingAmount,
                                                                  fundingFeeRatePerKw);
                var remaining = LightningMoney.Satoshis(keptSat) + change;
                if (remaining < reserveToKeep)
                {
                    var availableSat = unreserved.Where(u => u.BacksAnchorReserve(currentBlockHeight))
                                                 .Sum(u => u.Amount.Satoshi);
                    var available = LightningMoney.Satoshis(availableSat);
                    var fee = FundingFeeEstimator.EstimateWorstCaseFee(selectedUtxos.Count, fundingFeeRatePerKw);
                    throw new AnchorReserveException(
                        $"Funding {requestFundingAmount.Satoshi} sat (fee up to {fee.Satoshi} sat) would leave "
                      + $"{remaining.Satoshi} sat in the wallet, below the anchors reserve of {reserveToKeep.Satoshi} "
                      + $"sat (confirmed and available: {available.Satoshi} sat)",
                        requestFundingAmount + fee + reserveToKeep, available, reserveToKeep);
                }
            }

            LockForChannelLocked(selectedUtxos, channelId);
            return selectedUtxos;
        }
    }

    public List<UtxoModel> GetLockedUtxosForChannel(ChannelId channelId)
    {
        return _utxoSet.Values.Where(x => x.LockedToChannelId.HasValue && x.LockedToChannelId.Value.Equals(channelId))
                       .ToList();
    }

    public List<UtxoModel> ReturnUtxosNotSpentOnChannel(ChannelId channelId)
    {
        var utxos = _utxoSet.Values
                            .Where(x => x.LockedToChannelId.HasValue && x.LockedToChannelId.Value.Equals(channelId))
                            .ToList();
        foreach (var utxo in utxos)
        {
            utxo.LockedToChannelId = null;
            _utxoSet[(utxo.TxId, utxo.Index)] = utxo;
        }

        return utxos;
    }

    public void ConfirmSpendOnChannel(ChannelId channelId)
    {
        var utxos = _utxoSet.Values.Where(x => x.LockedToChannelId.HasValue &&
                                               x.LockedToChannelId.Value.Equals(channelId));
        foreach (var utxo in utxos)
            _utxoSet.TryRemove((utxo.TxId, utxo.Index), out _);
    }

    public void UpgradeChannelIdOnLockedUtxos(ChannelId oldChannelId, ChannelId newChannelId)
    {
        var utxos = _utxoSet.Values
                            .Where(x => x.LockedToChannelId.HasValue && x.LockedToChannelId.Value.Equals(oldChannelId))
                            .ToList();
        // If there's no locked utxos, we have a problem
        if (utxos.Count == 0)
            throw new InvalidOperationException("No available UTXOs");

        foreach (var utxo in utxos)
        {
            utxo.LockedToChannelId = newChannelId;
            _utxoSet[(utxo.TxId, utxo.Index)] = utxo;
        }
    }

    public List<UtxoModel> GetUnreservedUtxos()
    {
        lock (_reservationLock)
            return GetUnreservedUtxosLocked();
    }

    public bool TryReserveForFee(IReadOnlyCollection<(TxId TxId, uint Index)> outpoints, Guid reservationId)
    {
        ArgumentNullException.ThrowIfNull(outpoints);
        if (outpoints.Count == 0)
            return false;

        lock (_reservationLock)
        {
            foreach (var outpoint in outpoints)
            {
                if (!_utxoSet.TryGetValue(outpoint, out var utxo) || utxo.LockedToChannelId is not null
                                                                  || _feeReservations.ContainsKey(outpoint))
                    return false;
            }

            foreach (var outpoint in outpoints)
                _feeReservations[outpoint] = reservationId;

            return true;
        }
    }

    public void ReleaseFeeReservation(Guid reservationId)
    {
        lock (_reservationLock)
        {
            foreach (var outpoint in _feeReservations.Where(x => x.Value == reservationId).Select(x => x.Key).ToList())
                _feeReservations.Remove(outpoint);
        }
    }

    public bool TryGetFeeReservation(TxId txId, uint index, out Guid reservationId)
    {
        lock (_reservationLock)
            return _feeReservations.TryGetValue((txId, index), out reservationId);
    }

    public void LoadFeeReservations(IEnumerable<(TxId TxId, uint Index, Guid ReservationId)> reservations)
    {
        ArgumentNullException.ThrowIfNull(reservations);

        lock (_reservationLock)
        {
            foreach (var (txId, index, reservationId) in reservations)
                _feeReservations[(txId, index)] = reservationId;
        }
    }

    private static List<UtxoModel>? SelectWithFundingFee(List<UtxoModel> candidates, LightningMoney fundingAmount,
                                                         LightningMoney feeRatePerKw)
    {
        // The fee depends on the input count: start with one and select again with the count the last selection
        // needed, until a selection needs no more inputs than its fee was estimated for (the count only grows, so at
        // most one round per candidate)
        var inputCount = 1;
        for (var round = 0; round < candidates.Count; round++)
        {
            var target = fundingAmount + FundingFeeEstimator.EstimateWorstCaseFee(inputCount, feeRatePerKw);
            var selection = BranchAndBound(candidates, target);
            if (selection is null || selection.Count <= inputCount)
                return selection;

            inputCount = selection.Count;
        }

        return null;
    }

    private void LockForChannelLocked(List<UtxoModel> selectedUtxos, ChannelId channelId)
    {
        foreach (var selectedUtxo in selectedUtxos)
        {
            selectedUtxo.LockedToChannelId = channelId;
            _utxoSet[(selectedUtxo.TxId, selectedUtxo.Index)] = selectedUtxo;
        }
    }

    private List<UtxoModel> GetUnreservedUtxosLocked() =>
        _utxoSet.Values
                .Where(utxo => utxo.LockedToChannelId is null && !_feeReservations.ContainsKey((utxo.TxId, utxo.Index)))
                .ToList();

    private static List<UtxoModel>? BranchAndBound(List<UtxoModel> utxos, LightningMoney targetAmount)
    {
        const int maxTries = 100_000;
        var tries = 0;

        // Best solution found so far
        List<UtxoModel>? bestSelection = null;
        var bestWaste = long.MaxValue;

        // Current selection being explored
        var targetSatoshis = targetAmount.Satoshi;

        // Stack for depth-first search: (index, includeUtxo)
        var stack = new Stack<(int index, bool include, List<UtxoModel> selection, long value)>();
        stack.Push((0, true, [], 0));
        stack.Push((0, false, [], 0));

        while (stack.Count > 0 && tries < maxTries)
        {
            tries++;
            var (index, include, selection, value) = stack.Pop();

            if (include && index < utxos.Count)
            {
                selection = new List<UtxoModel>(selection) { utxos[index] };
                value += utxos[index].Amount.Satoshi;
            }

            // Check if we found a valid solution
            if (value >= targetSatoshis)
            {
                var waste = value - targetSatoshis;

                // Perfect match (changeless transaction)
                if (waste == 0)
                    return selection;

                // Better solution than the current best
                if (waste < bestWaste ||
                    (waste == bestWaste && selection.Count < (bestSelection?.Count ?? int.MaxValue)))
                {
                    bestSelection = new List<UtxoModel>(selection);
                    bestWaste = waste;
                }

                continue; // Prune this branch
            }

            // Move to the next UTXO
            var nextIndex = index + 1;
            if (nextIndex >= utxos.Count)
                continue;

            // Calculate upper bound (current value + all remaining UTXOs)
            var upperBound = value;
            for (var i = nextIndex; i < utxos.Count; i++)
                upperBound += utxos[i].Amount.Satoshi;

            // Prune if we can't reach the target even with all remaining UTXOs
            if (upperBound < targetSatoshis)
                continue;

            // Explore both branches: include and exclude the next UTXO
            stack.Push((nextIndex, false, [.. selection], value));
            stack.Push((nextIndex, true, [.. selection], value));
        }

        // If no exact match found, return the best solution or fallback to greedy
        // Fallback: simple greedy approach if BnB didn't find a solution
        return bestSelection ?? GreedySelection(utxos, targetAmount);
    }

    private static List<UtxoModel>? GreedySelection(List<UtxoModel> utxos, LightningMoney targetAmount)
    {
        var selected = new List<UtxoModel>();
        long currentSum = 0;
        var targetSatoshis = targetAmount.Satoshi;

        foreach (var utxo in utxos)
        {
            selected.Add(utxo);
            currentSum += utxo.Amount.Satoshi;

            if (currentSum >= targetSatoshis)
                return selected;
        }

        return null; // Insufficient funds
    }
}