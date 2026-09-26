using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;

/// <summary>
/// Picks confirmed wallet outputs for a fee, largest first, and reserves them in memory and in the database before it
/// returns them (BOLT 5 plan O7-T1).
/// </summary>
/// <remarks>
/// One reservation at a time per process (the selector is a singleton); the in-memory reservation is taken atomically
/// against channel fundings by <see cref="IUtxoMemoryRepository.TryReserveForFee"/>, and the table's key on the
/// outpoint refuses a second reservation of an output even across processes sharing a database. A failed save releases
/// the in-memory reservation. The chain monitor restores the persisted reservations with the UTXO set at startup.
/// </remarks>
public sealed class FeeInputSelector : IFeeInputSelector
{
    private const int MaxPurposeLength = 128;
    private const int MaxReserveAttempts = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<FeeInputSelector> _logger;
    private readonly Network _network;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public FeeInputSelector(IUtxoMemoryRepository utxoMemoryRepository, IServiceScopeFactory scopeFactory,
                            IOptions<NodeOptions> nodeOptions, ILogger<FeeInputSelector> logger,
                            TimeProvider? timeProvider = null)
    {
        _utxoMemoryRepository = utxoMemoryRepository;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <inheritdoc />
    public async Task<FeeInputReservation> ReserveAsync(LightningMoney targetFee, LightningMoney feeRatePerKw,
                                                        int extraWeight, string purpose,
                                                        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetFee);
        ArgumentNullException.ThrowIfNull(feeRatePerKw);
        ArgumentOutOfRangeException.ThrowIfNegative(extraWeight);
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > MaxPurposeLength)
            throw new ArgumentException($"The purpose must be 1 to {MaxPurposeLength} characters", nameof(purpose));
        if (feeRatePerKw.Satoshi <= 0)
            throw new ArgumentOutOfRangeException(nameof(feeRatePerKw), "The fee rate must be positive");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; attempt <= MaxReserveAttempts; attempt++)
            {
                var selection = Select(GetCandidates(), CeilSatoshis(targetFee), feeRatePerKw.Satoshi, extraWeight);
                var reservationId = Guid.NewGuid();
                var outpoints = selection.Inputs.Select(i => (i.TxId, i.Index)).ToList();

                // A channel funding may have locked one of them since the snapshot: select again
                if (!_utxoMemoryRepository.TryReserveForFee(outpoints, reservationId))
                {
                    _logger.LogDebug("Fee inputs were taken while selecting (attempt {Attempt}); selecting again",
                                     attempt);
                    continue;
                }

                try
                {
                    return await PersistAsync(reservationId, purpose, selection);
                }
                catch
                {
                    _utxoMemoryRepository.ReleaseFeeReservation(reservationId);
                    throw;
                }
            }

            throw new InvalidOperationException("Could not reserve fee inputs: they were taken by other spends");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Database first: if the save fails the outputs stay reserved, which never double-spends
            using (var scope = _scopeFactory.CreateScope())
            {
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                if (await uow.FeeInputReservationDbRepository.DeleteAsync(reservationId))
                    await uow.SaveChangesAsync();
            }

            _utxoMemoryRepository.ReleaseFeeReservation(reservationId);
            _logger.LogInformation("Released fee input reservation {ReservationId}", reservationId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ConfirmAsync(Guid reservationId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var reservation = await uow.FeeInputReservationDbRepository.GetByIdAsync(reservationId);
                if (reservation is null)
                    return;

                // The spend confirmed: its inputs leave the wallet in the same save (a no-op for the ones the chain
                // monitor already removed)
                foreach (var input in reservation.Inputs)
                    uow.TrySpendUtxo(input.TxId, input.Index);

                await uow.FeeInputReservationDbRepository.DeleteAsync(reservationId);
                await uow.SaveChangesAsync();
            }

            _utxoMemoryRepository.ReleaseFeeReservation(reservationId);
            _logger.LogInformation("Fee input reservation {ReservationId} confirmed", reservationId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<FeeInputReservation?> GetAsync(Guid reservationId,
                                                     CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.FeeInputReservationDbRepository.GetByIdAsync(reservationId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeeInputReservation>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return await uow.FeeInputReservationDbRepository.GetAllAsync();
    }

    /// <summary>
    /// Largest first until the inputs pay the target fee plus the fee rate over the extra weight and their own; a change
    /// output is added (and charged) when what is left after it is at least the P2WPKH dust limit, otherwise the excess
    /// goes to the fee.
    /// </summary>
    internal static Selection Select(IReadOnlyList<WalletInput> candidates, long targetFeeSat, long feeRatePerKw,
                                     int extraWeight)
    {
        var inputs = new List<WalletInput>();
        long total = 0;
        long inputWeight = 0;
        foreach (var candidate in candidates)
        {
            inputs.Add(candidate);
            total += candidate.Amount.Satoshi;
            inputWeight += candidate.InputWeight;

            var feeWithChange = targetFeeSat
                              + FeeSat(feeRatePerKw, extraWeight + inputWeight + WalletWeights.P2WpkhOutputWeight);
            if (total - feeWithChange >= WalletWeights.P2WpkhDustLimitSat)
                return new Selection(inputs, feeWithChange, total - feeWithChange);

            var feeWithoutChange = targetFeeSat + FeeSat(feeRatePerKw, extraWeight + inputWeight);
            if (total >= feeWithoutChange)
                return new Selection(inputs, total, 0);
        }

        var required = targetFeeSat + FeeSat(feeRatePerKw, extraWeight + inputWeight
                                                         + (inputs.Count == 0 ? WalletWeights.P2WpkhInputWeight : 0));
        throw new InsufficientFundsException(LightningMoney.Satoshis(required), LightningMoney.Satoshis(total));
    }

    private List<WalletInput> GetCandidates()
    {
        var candidates = new List<WalletInput>();
        foreach (var utxo in _utxoMemoryRepository.GetUnreservedUtxos())
        {
            // Only mined outputs whose script we know
            if (utxo.BlockHeight == 0 || utxo.WalletAddress is null
                                      || utxo.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
                continue;

            Script scriptPubKey;
            try
            {
                scriptPubKey = BitcoinAddress.Create(utxo.WalletAddress.Address, _network).ScriptPubKey;
            }
            catch (FormatException e)
            {
                _logger.LogWarning(e, "Wallet output {TxId}:{Index} has an address of another network; not used",
                                   utxo.TxId, utxo.Index);
                continue;
            }

            candidates.Add(new WalletInput(utxo.TxId, utxo.Index, utxo.Amount, utxo.AddressType,
                                           scriptPubKey.ToBytes(), WalletWeights.GetInputWeight(utxo.AddressType)));
        }

        // Largest first (fewest inputs, so the least weight to pay for); ties in a stable order
        return candidates.OrderByDescending(c => c.Amount.Satoshi)
                         .ThenBy(c => c.TxId.ToString(), StringComparer.Ordinal)
                         .ThenBy(c => c.Index)
                         .ToList();
    }

    private async Task<FeeInputReservation> PersistAsync(Guid reservationId, string purpose, Selection selection)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        BitcoinScript? changeScript = null;
        if (selection.ChangeSat > 0)
        {
            var walletService = scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
            var changeAddress = await walletService.GetUnusedAddressAsync(AddressType.P2Wpkh, true);
            changeScript = BitcoinAddress.Create(changeAddress.Address, _network).ScriptPubKey.ToBytes();
        }

        var reservation = new FeeInputReservation(reservationId, purpose, selection.Inputs,
                                                  LightningMoney.Satoshis(selection.FeeSat),
                                                  LightningMoney.Satoshis(selection.ChangeSat), changeScript);
        uow.FeeInputReservationDbRepository.Add(reservation, _timeProvider.GetUtcNow());
        await uow.SaveChangesAsync();

        if (_logger.IsEnabled(LogLevel.Information))
            _logger.LogInformation(
                "Reserved {Count} fee input(s) worth {Total} sat for {Purpose} (reservation {ReservationId}, fee "
              + "{Fee} sat, change {Change} sat)", reservation.Inputs.Count, reservation.Total.Satoshi, purpose,
                reservationId, selection.FeeSat, selection.ChangeSat);

        return reservation;
    }

    private static long CeilSatoshis(LightningMoney amount) => (long)((amount.MilliSatoshi + 999) / 1000);

    private static long FeeSat(long feeRatePerKw, long weight) => (feeRatePerKw * weight + 999) / 1000;

    internal sealed record Selection(IReadOnlyList<WalletInput> Inputs, long FeeSat, long ChangeSat);
}