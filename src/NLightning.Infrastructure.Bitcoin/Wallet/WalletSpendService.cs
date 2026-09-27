using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Interfaces;
using Networks;

/// <summary>
/// The on-chain <c>withdraw</c> (ClientCommand 25): pays an external address from the wallet's confirmed outputs,
/// through the persisted <see cref="IFeeInputSelector"/> reservations and the reserved-inputs-only wallet signer.
/// </summary>
/// <remarks>
/// <para>Selection: the fee input selector picks the largest unreserved confirmed outputs (never one locked to a
/// channel funding, reserved for a fee or spent by a pending broadcast) for the amount plus the fee, and a P2WPKH
/// change output of a fresh wallet change address when the rest is not dust. The amount is passed as the selector's
/// target, so the reservation's <see cref="FeeInputReservation.Fee"/> is amount plus fee.</para>
/// <para>Anchors reserve (NL-379): after the inputs are reserved (so a concurrent funding or spend sees them taken),
/// the outputs still backing the reserve plus this spend's change must cover it, else the reservation is released and
/// <see cref="AnchorReserveException"/> is thrown. "All" sends everything the selector could spend minus the fee and
/// the reserve, which returns as change.</para>
/// <para>Broadcast: the signed spend is stored as a <see cref="BroadcastPurpose.WalletSend"/> row before it is sent
/// (the chain monitor resends it after every block until it confirms). The reservation is released when anything
/// fails before the row is stored, and kept after (the pending row keeps the outputs out of every other selection
/// either way). Reservations whose inputs have all left the wallet (their spend was processed in a block) are ended
/// with <see cref="IFeeInputSelector.ConfirmAsync"/> at the next withdraw, as the chain monitor does at startup; those
/// none of whose inputs a pending broadcast spends (a crash or an unverifiable save between the reservation and the
/// row) are released then and by <see cref="ReleaseOrphanedReservationsAsync"/>, which the host runs once at startup.
/// Withdrawals run one at a time, so that sweep never sees one between its reservation and its row.</para>
/// </remarks>
public sealed class WalletSpendService : IWalletSpendService
{
    /// <summary>The purpose of the fee input reservations of a withdraw.</summary>
    public const string ReservationPurpose = "withdraw";

    /// <summary>BOLT 3's floor, 1 sat/vB: bitcoind's default minimum relay fee.</summary>
    internal const long MinFeeRatePerKw = 253;

    /// <summary>A sanity cap on a requested fee rate: 1,000 sat/vB.</summary>
    internal const long MaxFeeRatePerKw = 250_000;

    /// <summary>
    /// The weight of everything but the inputs and outputs: version and locktime (8 bytes), the input and output counts
    /// (one byte each up to 252) x 4, plus the segwit marker and flag (2 weight units).
    /// </summary>
    internal const int BaseWeight = (4 + 4 + 1 + 1) * 4 + 2;

    private readonly IAnchorReserveService _anchorReserveService;
    private readonly IBitcoinChainService? _bitcoinChainService;
    private readonly IBlockchainMonitor _blockchainMonitor;
    private readonly IFeeInputSelector _feeInputSelector;
    private readonly IFeeService _feeService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<WalletSpendService> _logger;
    private readonly Network _network;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUtxoMemoryRepository _utxoMemoryRepository;

    public WalletSpendService(IFeeInputSelector feeInputSelector, IAnchorReserveService anchorReserveService,
                              IUtxoMemoryRepository utxoMemoryRepository, ILightningSigner lightningSigner,
                              IBlockchainMonitor blockchainMonitor, IFeeService feeService,
                              IServiceScopeFactory scopeFactory, IOptions<NodeOptions> nodeOptions,
                              ILogger<WalletSpendService> logger, IBitcoinChainService? bitcoinChainService = null)
    {
        _feeInputSelector = feeInputSelector;
        _anchorReserveService = anchorReserveService;
        _utxoMemoryRepository = utxoMemoryRepository;
        _lightningSigner = lightningSigner;
        _blockchainMonitor = blockchainMonitor;
        _feeService = feeService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _bitcoinChainService = bitcoinChainService;
        _network = nodeOptions.Value.BitcoinNetwork.ToNBitcoinNetwork();
    }

    /// <inheritdoc />
    public async Task<WalletWithdrawResult> WithdrawAsync(WalletWithdrawRequest request,
                                                          CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_blockchainMonitor.IsChainProcessingHalted)
            throw new WalletSpendException(WalletSpendError.ChainProcessingHalted,
                                           ChainProcessingHalt.Refusal("withdraw"));

        var destination = ParseAddress(request.Address, _network).ScriptPubKey;
        var feeRatePerKw = await GetFeeRatePerKwAsync(request.FeeRatePerKw, cancellationToken);

        // One withdrawal at a time, so the orphan sweep never sees one between its reservation and its row
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Reservations of earlier withdrawals whose spend a processed block holds, or that lost their spend
            await EndStaleReservationsLockedAsync(cancellationToken);

            return await WithdrawLockedAsync(request, destination, feeRatePerKw, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> ReleaseOrphanedReservationsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await EndStaleReservationsLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WalletWithdrawResult> WithdrawLockedAsync(WalletWithdrawRequest request, Script destination,
                                                                 long feeRatePerKw,
                                                                 CancellationToken cancellationToken)
    {

        var extraWeight = BaseWeight + GetOutputWeight(destination);
        var dustLimit = GetDustThreshold(destination);
        var reserve = _anchorReserveService.GetRequiredReserve();

        long amountSat;
        if (request.Amount is { } requested)
        {
            if (requested.MilliSatoshi % 1_000 != 0)
                throw new WalletSpendException(WalletSpendError.DustAmount,
                                               "The amount must be a whole number of satoshis.");

            amountSat = requested.Satoshi;
            if (amountSat < dustLimit)
                throw new WalletSpendException(WalletSpendError.DustAmount,
                                               $"{amountSat} sat is below the dust limit of the destination "
                                             + $"({dustLimit} sat).");
        }
        else
        {
            amountSat = await GetSendAllAmountAsync(extraWeight, feeRatePerKw, reserve, dustLimit);
        }

        var reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(amountSat),
                                                               LightningMoney.Satoshis(feeRatePerKw), extraWeight,
                                                               ReservationPurpose, cancellationToken);
        var stored = false;
        try
        {
            await EnsureReserveKeptAsync(reservation, amountSat, cancellationToken);

            var signed = BuildAndSign(reservation, destination, amountSat);
            var feeSat = reservation.Total.Satoshi - amountSat - reservation.ChangeAmount.Satoshi;
            var weight = GetWeight(signed.Transaction);

            var row = new BroadcastTransactionModel(signed.Signed, BroadcastPurpose.WalletSend, null,
                                                    _blockchainMonitor.LastProcessedBlockHeight,
                                                    (uint)feeRatePerKw);
            bool published;
            try
            {
                published = await _blockchainMonitor.SaveAndPublishAsync(row);
                stored = true;
            }
            catch
            {
                // The row may have been saved before a later step failed: the pending row then owns the inputs
                stored = await IsStoredAsync(row.TransactionId);
                throw;
            }

            if (published)
                _logger.LogInformation(
                    "Withdrew {Amount} sat to {Address} in {TxId} (fee {Fee} sat, change {Change} sat, {Inputs} input(s))",
                    amountSat, request.Address, signed.Transaction.GetHash(), feeSat, reservation.ChangeAmount.Satoshi,
                    reservation.Inputs.Count);
            else
                _logger.LogWarning(
                    "Withdrawal {TxId} to {Address} was stored but bitcoind refused it; it is sent again after every block",
                    signed.Transaction.GetHash(), request.Address);

            return new WalletWithdrawResult(signed.Signed.TxId, LightningMoney.Satoshis(amountSat),
                                            LightningMoney.Satoshis(feeSat), reservation.ChangeAmount,
                                            LightningMoney.Satoshis(feeRatePerKw), weight,
                                            reservation.Inputs.Count, reserve, published);
        }
        catch
        {
            if (!stored)
                await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// The destination address on <paramref name="network"/>; a <see cref="WalletSpendException"/> tells an address of
    /// another network from text that is no address.
    /// </summary>
    internal static BitcoinAddress ParseAddress(string? address, Network network)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new WalletSpendException(WalletSpendError.InvalidAddress, "No destination address given.");

        address = address.Trim();
        try
        {
            return BitcoinAddress.Create(address, network);
        }
        catch (FormatException)
        {
            // Tell the operator which network the address belongs to
            foreach (var other in new[]
                     {
                         Network.Main, Network.TestNet, Network.RegTest, NBitcoin.Bitcoin.Instance.Signet
                     })
            {
                if (other == network)
                    continue;

                try
                {
                    _ = BitcoinAddress.Create(address, other);
                    throw new WalletSpendException(WalletSpendError.WrongNetwork,
                                                   $"{address} is a {other.ChainName} address; this node runs on "
                                                 + $"{network.ChainName}.");
                }
                catch (FormatException)
                {
                    // Not that network either
                }
            }

            throw new WalletSpendException(WalletSpendError.InvalidAddress,
                                           $"{address} is not a valid Bitcoin address.");
        }
    }

    /// <summary>
    /// Bitcoin Core's dust threshold of an output paying <paramref name="scriptPubKey"/> at the default dust relay fee
    /// (3 sat/vB): P2WPKH 294, P2TR and P2WSH 330, P2SH 540, P2PKH 546 sat.
    /// </summary>
    internal static long GetDustThreshold(Script scriptPubKey)
    {
        var length = scriptPubKey.Length;
        long size = 8 + CompactSizeLength(length) + length;

        // The input that would spend it: outpoint, sequence, script length, and a witness discounted by 4 or a
        // scriptSig of a signature and a key
        size += scriptPubKey.IsScriptType(ScriptType.Witness) || scriptPubKey.IsScriptType(ScriptType.Taproot)
                    ? 32 + 4 + 1 + 107 / 4 + 4
                    : 32 + 4 + 1 + 107 + 4;
        return size * 3;
    }

    /// <summary>The weight of an output paying <paramref name="scriptPubKey"/>.</summary>
    internal static int GetOutputWeight(Script scriptPubKey)
    {
        var length = scriptPubKey.Length;
        return (8 + CompactSizeLength(length) + length) * 4;
    }

    private async Task<long> GetFeeRatePerKwAsync(LightningMoney? requested, CancellationToken cancellationToken)
    {
        long feeRatePerKw;
        if (requested is { } rate)
        {
            feeRatePerKw = rate.Satoshi;
            if (feeRatePerKw < MinFeeRatePerKw)
                throw new WalletSpendException(WalletSpendError.FeeRateTooLow,
                                               $"The fee rate {feeRatePerKw} sat/kw is below the relay minimum of "
                                             + $"{MinFeeRatePerKw} sat/kw (1 sat/vB).");
            if (feeRatePerKw > MaxFeeRatePerKw)
                throw new WalletSpendException(WalletSpendError.FeeRateTooHigh,
                                               $"The fee rate {feeRatePerKw} sat/kw is above the cap of "
                                             + $"{MaxFeeRatePerKw} sat/kw ({MaxFeeRatePerKw / 250} sat/vB).");
        }
        else
        {
            feeRatePerKw = Math.Max(MinFeeRatePerKw,
                                    (await _feeService.GetFeeRatePerKwAsync(cancellationToken)).Satoshi);
        }

        uint? mempoolMin = null;
        if (_bitcoinChainService is not null)
        {
            try
            {
                mempoolMin = await _bitcoinChainService.GetMempoolMinFeeRatePerKwAsync();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _logger.LogDebug(e, "Could not read bitcoind's mempool minimum fee; not checked");
            }
        }

        if (mempoolMin is { } min && feeRatePerKw < min)
        {
            if (requested is not null)
                throw new WalletSpendException(WalletSpendError.FeeRateTooLow,
                                               $"The fee rate {feeRatePerKw} sat/kw is below bitcoind's mempool "
                                             + $"minimum of {min} sat/kw; the transaction would not relay.");

            feeRatePerKw = min;
        }

        return feeRatePerKw;
    }

    /// <summary>
    /// "All": every output the selector could spend, minus the fee of spending them all to the destination and, with a
    /// reserve, the reserve (at least a non-dust change output) that returns as change.
    /// </summary>
    private async Task<long> GetSendAllAmountAsync(int extraWeight, long feeRatePerKw, LightningMoney reserve,
                                                   long dustLimit)
    {
        var candidates = await GetSpendableOutputsAsync();
        var totalSat = candidates.Sum(c => c.AmountSat);
        var keepSat = reserve.IsZero ? 0 : Math.Max(reserve.Satoshi, WalletWeights.P2WpkhDustLimitSat);
        var weight = extraWeight + candidates.Sum(c => (long)c.InputWeight)
                   + (keepSat > 0 ? WalletWeights.P2WpkhOutputWeight : 0);
        var feeSat = FeeSat(feeRatePerKw, weight);
        var amountSat = totalSat - feeSat - keepSat;
        if (amountSat >= dustLimit)
            return amountSat;

        if (keepSat > 0)
            throw new AnchorReserveException(
                $"Nothing to withdraw: {totalSat} sat confirmed and spendable, the fee is {feeSat} sat and the anchors "
              + $"reserve of {reserve.Satoshi} sat stays in the wallet.", LightningMoney.Satoshis(feeSat + keepSat
                                                                                                 + dustLimit),
                LightningMoney.Satoshis(totalSat), reserve);

        throw new InsufficientFundsException(LightningMoney.Satoshis(feeSat + dustLimit),
                                             LightningMoney.Satoshis(totalSat));
    }

    /// <summary>
    /// The outputs <see cref="FeeInputSelector"/> would consider: unreserved, mined, of a known P2WPKH or P2TR wallet
    /// address, and not spent by one of our pending broadcasts.
    /// </summary>
    private async Task<List<(long AmountSat, int InputWeight)>> GetSpendableOutputsAsync()
    {
        HashSet<(TxId TxId, uint Index)> excluded;
        using (var scope = _scopeFactory.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            excluded = await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
        }

        var outputs = new List<(long AmountSat, int InputWeight)>();
        foreach (var utxo in _utxoMemoryRepository.GetUnreservedUtxos())
        {
            if (excluded.Contains((utxo.TxId, utxo.Index)) || utxo.BlockHeight == 0 || utxo.WalletAddress is null
             || utxo.AddressType is not (AddressType.P2Wpkh or AddressType.P2Tr))
                continue;

            outputs.Add((utxo.Amount.Satoshi, WalletWeights.GetInputWeight(utxo.AddressType)));
        }

        return outputs;
    }

    /// <summary>
    /// With the inputs reserved (so they no longer count as available): the outputs still backing the reserve plus
    /// this spend's change must cover it.
    /// </summary>
    private async Task EnsureReserveKeptAsync(FeeInputReservation reservation, long amountSat,
                                              CancellationToken cancellationToken)
    {
        var status = await _anchorReserveService.GetStatusAsync(cancellationToken);
        if (status.RequiredReserve.IsZero)
            return;

        var keptSat = status.AvailableBalance.Satoshi + reservation.ChangeAmount.Satoshi;
        if (keptSat >= status.RequiredReserve.Satoshi)
            return;

        var spendableSat = Math.Max(0, status.AvailableBalance.Satoshi + reservation.Total.Satoshi
                                       - status.RequiredReserve.Satoshi);
        var hint = spendableSat > 0
                       ? $"about {spendableSat} sat minus the fee can be withdrawn (use 'all')"
                       : "nothing can be withdrawn while the channels need it";
        throw new AnchorReserveException(
            $"Withdrawing {amountSat} sat would leave {keptSat} sat in the wallet, below the anchors reserve of "
          + $"{status.RequiredReserve.Satoshi} sat for {status.AnchorsChannelCount} anchors channel(s); {hint}.",
            LightningMoney.Satoshis(amountSat) + status.RequiredReserve,
            status.AvailableBalance + reservation.Total, status.RequiredReserve);
    }

    private (SignedTransaction Signed, Transaction Transaction) BuildAndSign(FeeInputReservation reservation,
                                                                           Script destination, long amountSat)
    {
        var tx = _network.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = LockTime.Zero;
        foreach (var input in reservation.Inputs)
            tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])input.TxId), input.Index))
            {
                // Opt in to replacement (BIP 125)
                Sequence = new Sequence(0xFFFFFFFD)
            });

        tx.Outputs.Add(Money.Satoshis(amountSat), destination);
        if (reservation.ChangeScript is { } changeScript)
            tx.Outputs.Add(Money.Satoshis(reservation.ChangeAmount.Satoshi), new Script((byte[])changeScript));

        var signed = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
        if (!_lightningSigner.SignWalletTransaction(signed, reservation.Id, []))
            throw new InvalidOperationException($"The signer signed no input of withdrawal {tx.GetHash()}");

        // Check every input with the script interpreter, independently of the signer's own check
        var result = Transaction.Load(signed.RawTxBytes, _network);
        var spentOutputs = reservation.Inputs
                                      .Select(i => new TxOut(Money.Satoshis(i.Amount.Satoshi),
                                                             new Script((byte[])i.ScriptPubKey)))
                                      .ToArray();
        var validator = result.CreateValidator(spentOutputs);
        for (var i = 0; i < result.Inputs.Count; i++)
        {
            var check = validator.ValidateInput(i);
            if (check.Error is not (null or ScriptError.OK))
                throw new InvalidOperationException(
                    $"Input {i} of withdrawal {result.GetHash()} does not verify: {check.Error}");
        }

        if (result.GetHash() != tx.GetHash())
            throw new InvalidOperationException("The signer changed the withdrawal's txid");

        return (signed, result);
    }

    /// <summary>
    /// Under <see cref="_gate"/>: ends the withdraw reservations none of whose inputs is still in the wallet (the chain
    /// monitor removed them when it processed the block holding their spend), and releases those none of whose inputs a
    /// pending broadcast spends: a crash, or a failed or unverifiable save, between the reservation and its
    /// <see cref="BroadcastPurpose.WalletSend"/> row left them without a spend, and nothing else would ever free them.
    /// </summary>
    /// <returns>How many reservations were released.</returns>
    private async Task<int> EndStaleReservationsLockedAsync(CancellationToken cancellationToken)
    {
        List<FeeInputReservation> reservations;
        try
        {
            reservations = (await _feeInputSelector.GetAllAsync(cancellationToken))
                          .Where(r => r.Purpose == ReservationPurpose)
                          .ToList();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _logger.LogWarning(e, "Could not read the fee input reservations; completed withdrawals are ended later");
            return 0;
        }

        if (reservations.Count == 0)
            return 0;

        HashSet<(TxId TxId, uint Index)>? pendingSpends;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            pendingSpends = await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unknown: keep every reservation, which never double-spends
            _logger.LogWarning(e, "Could not read the pending broadcasts; withdraw reservations are checked later");
            pendingSpends = null;
        }

        var released = 0;
        foreach (var reservation in reservations)
        {
            if (!reservation.Inputs.Any(i => _utxoMemoryRepository.TryGetUtxo(i.TxId, i.Index, out _)))
            {
                await _feeInputSelector.ConfirmAsync(reservation.Id, cancellationToken);
                continue;
            }

            if (pendingSpends is null || reservation.Inputs.Any(i => pendingSpends.Contains((i.TxId, i.Index))))
                continue;

            _logger.LogWarning(
                "Releasing withdraw reservation {ReservationId} ({Inputs} input(s), {Total} sat): no pending "
              + "withdrawal spends its inputs", reservation.Id, reservation.Inputs.Count, reservation.Total.Satoshi);
            await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
            released++;
        }

        return released;
    }

    private async Task<bool> IsStoredAsync(TxId txId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            return await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(txId) is not null;
        }
        catch (Exception e)
        {
            // Unknown: keep the reservation, which never double-spends
            _logger.LogWarning(e, "Could not check whether withdrawal {TxId} was stored; its inputs stay reserved",
                               txId);
            return true;
        }
    }

    private static int CompactSizeLength(int value) => value < 0xFD ? 1 : value <= 0xFFFF ? 3 : 5;

    private static long FeeSat(long feeRatePerKw, long weight) => (feeRatePerKw * weight + 999) / 1000;

    /// <summary>BIP 141 weight: the size without witnesses x 3 plus the full size.</summary>
    internal static int GetWeight(Transaction transaction) =>
        transaction.GetSerializedSize(TransactionOptions.None) * 3
      + transaction.GetSerializedSize(TransactionOptions.Witness);
}